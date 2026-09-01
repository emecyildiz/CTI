using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
{
    var configuredConnectionString = builder.Configuration.GetConnectionString("Cti");
    var connectionString = configuredConnectionString ?? new NpgsqlConnectionStringBuilder
    {
        Host = builder.Configuration["CtiDatabase:Host"] ?? "cti-db",
        Port = 5432,
        Database = builder.Configuration["CtiDatabase:Name"] ?? "cti",
        Username = builder.Configuration["CtiDatabase:Username"] ?? "cti_dashboard",
        Password = builder.Configuration["CTI_DASHBOARD_PASSWORD"]
            ?? throw new InvalidOperationException("CTI_DASHBOARD_PASSWORD is required."),
        MaxPoolSize = 5,
        Timeout = 5,
        CommandTimeout = 10,
        ApplicationName = builder.Configuration["CTI_APPLICATION_NAME"] ?? "cti-dashboard"
    }.ConnectionString;
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    return dataSourceBuilder.Build();
});
builder.Services.AddSingleton(_ => new N8nHandoffProbe(
    builder.Configuration["CTI_N8N_API_URL"] ?? "http://cti-n8n:5678/api/v1"));

var app = builder.Build();
var environment = app.Environment;
var authenticationMode = (builder.Configuration["CTI_AUTH_MODE"] ??
        (environment.IsDevelopment() ? "local" : "cloudflare"))
    .Trim()
    .ToLowerInvariant();
if (authenticationMode is not ("cloudflare" or "local"))
{
    throw new InvalidOperationException("CTI_AUTH_MODE must be either 'cloudflare' or 'local'.");
}

var expectedAccessEmail = authenticationMode == "cloudflare"
    ? builder.Configuration["CTI_ACCESS_EMAIL"]
        ?? throw new InvalidOperationException(
            "CTI_ACCESS_EMAIL is required when CTI_AUTH_MODE is 'cloudflare'.")
    : null;

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    if (authenticationMode == "cloudflare")
    {
        var authenticatedEmail = context.Request.Headers["Cf-Access-Authenticated-User-Email"]
            .FirstOrDefault();
        if (!string.Equals(authenticatedEmail, expectedAccessEmail, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Cloudflare Access authentication is required.");
            return;
        }
    }

    context.Response.Headers["Cache-Control"] = "private, no-store, max-age=0";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = static context =>
    {
        context.Context.Response.Headers["Cache-Control"] = "private, max-age=3600";
        context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
});

app.MapGet("/health/ready", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    await using var command = dataSource.CreateCommand("SELECT 1;");
    await command.ExecuteScalarAsync(cancellationToken);
    return Results.Text("ready", "text/plain; charset=utf-8");
});

app.MapGet("/setup", async (
    HttpContext context,
    NpgsqlDataSource dataSource,
    N8nHandoffProbe n8nProbe,
    string? result,
    CancellationToken cancellationToken) =>
{
    await using var command = dataSource.CreateCommand("""
        SELECT schema_version, enabled_source_count, total_source_count,
               checked_source_count, failing_source_count, last_source_success_at
        FROM cti.dashboard_system_status;
        """);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        throw new InvalidOperationException("The CTI system status is unavailable.");
    }

    var status = new SetupSystemStatus(
        reader.GetInt32(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetInt64(3),
        reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetDateTime(5),
        authenticationMode);
    await reader.CloseAsync();

    await using var providerCommand = dataSource.CreateCommand("""
        SELECT profile_defined, provider_key, provider_label, adapter_key,
               model_identifier, api_base_url, adapter_status, updated_at
        FROM cti.dashboard_ai_provider_status;
        """);
    await using var providerReader = await providerCommand.ExecuteReaderAsync(cancellationToken);
    if (!await providerReader.ReadAsync(cancellationToken))
    {
        throw new InvalidOperationException("The CTI AI provider status is unavailable.");
    }

    var aiProvider = new AiProviderStatus(
        providerReader.GetBoolean(0),
        providerReader.IsDBNull(1) ? null : providerReader.GetString(1),
        providerReader.IsDBNull(2) ? null : providerReader.GetString(2),
        providerReader.IsDBNull(3) ? null : providerReader.GetString(3),
        providerReader.IsDBNull(4) ? null : providerReader.GetString(4),
        providerReader.IsDBNull(5) ? null : providerReader.GetString(5),
        providerReader.GetString(6),
        providerReader.IsDBNull(7) ? null : providerReader.GetDateTime(7));
    var csrfToken = GetOrCreateSetupCsrfToken(context);
    var n8nStatus = await n8nProbe.CheckAsync(cancellationToken);

    return Results.Content(
        HtmlPages.Setup(
            status,
            aiProvider,
            n8nStatus,
            csrfToken,
            string.Equals(result, "saved", StringComparison.Ordinal),
            GetAuthenticatedIdentity(context)),
        "text/html; charset=utf-8");
});

app.MapPost("/setup/ai-profile", async (
    HttpContext context,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    if (!context.Request.HasFormContentType)
    {
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }

    if (context.Request.ContentLength is null or <= 0 or > 8192)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var form = await context.Request.ReadFormAsync(cancellationToken);
    if (!IsSameSetupOrigin(context.Request) || !HasValidSetupCsrfToken(context, form))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var providerType = form["provider_type"].FirstOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty;
    if (providerType is not ("google_gemini" or "openai_compatible" or "custom"))
    {
        return Results.BadRequest("Unsupported AI provider type.");
    }

    var modelIdentifier = form["model_identifier"].FirstOrDefault()?.Trim() ?? string.Empty;
    if (modelIdentifier.Length is < 1 or > 200 || modelIdentifier.Any(char.IsControl))
    {
        return Results.BadRequest("Invalid AI model identifier.");
    }

    var apiBaseUrl = form["api_base_url"].FirstOrDefault()?.Trim();
    if (string.IsNullOrEmpty(apiBaseUrl))
    {
        apiBaseUrl = null;
    }
    else if (apiBaseUrl.Length > 500 ||
             !Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var endpoint) ||
             (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
             endpoint.UserInfo.Length > 0 ||
             endpoint.Query.Length > 0 ||
             endpoint.Fragment.Length > 0)
    {
        return Results.BadRequest("Invalid AI API base URL.");
    }

    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
    await using (var writeMode = new NpgsqlCommand(
                     "SET TRANSACTION READ WRITE;",
                     connection,
                     transaction))
    {
        await writeMode.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var command = new NpgsqlCommand(
                     "SELECT cti.configure_ai_provider_profile(@provider, @model, @url);",
                     connection,
                     transaction))
    {
        command.Parameters.AddWithValue("provider", providerType);
        command.Parameters.AddWithValue("model", modelIdentifier);
        command.Parameters.AddWithValue("url", (object?)apiBaseUrl ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    await transaction.CommitAsync(cancellationToken);
    return Results.Redirect("/setup?result=saved");
});

app.MapGet("/", async (
    HttpContext context,
    NpgsqlDataSource dataSource,
    string? q,
    string? source,
    string? category,
    string? severity,
    int? page,
    CancellationToken cancellationToken) =>
{
    const int pageSize = 20;
    var currentPage = Math.Clamp(page ?? 1, 1, 1000);
    var normalizedQuery = NormalizeFilter(q, 120);
    var normalizedSource = NormalizeFilter(source, 200);
    var normalizedCategory = NormalizeEnum(category,
        ["malware", "vulnerability", "data_breach", "threat_intelligence", "other"]);
    var normalizedSeverity = NormalizeEnum(severity,
        ["critical", "high", "medium", "low", "unknown"]);

    var availableSources = new List<string>();
    await using (var sourceCommand = dataSource.CreateCommand("""
        SELECT DISTINCT source_name
        FROM cti.dashboard_articles
        CROSS JOIN LATERAL unnest(source_names) AS source_name
        ORDER BY source_name;
        """))
    await using (var sourceReader = await sourceCommand.ExecuteReaderAsync(cancellationToken))
    {
        while (await sourceReader.ReadAsync(cancellationToken))
        {
            availableSources.Add(sourceReader.GetString(0));
        }
    }

    await using var usageCommand = dataSource.CreateCommand("""
        SELECT today_requests, today_tokens, month_requests, month_prompt_tokens,
               month_output_tokens, month_total_tokens, month_article_requests,
               month_report_requests, month_failed_requests, last_requested_at
        FROM cti.dashboard_ai_usage;
        """);
    await using var usageReader = await usageCommand.ExecuteReaderAsync(cancellationToken);
    if (!await usageReader.ReadAsync(cancellationToken))
    {
        throw new InvalidOperationException("The CTI AI usage summary is unavailable.");
    }
    var aiUsage = new AiUsageSummary(
        usageReader.GetInt64(0),
        usageReader.GetInt64(1),
        usageReader.GetInt64(2),
        usageReader.GetInt64(3),
        usageReader.GetInt64(4),
        usageReader.GetInt64(5),
        usageReader.GetInt64(6),
        usageReader.GetInt64(7),
        usageReader.GetInt64(8),
        usageReader.IsDBNull(9) ? null : usageReader.GetDateTime(9));

    await using var command = dataSource.CreateCommand("""
        SELECT id, title, category, severity, summary_tr, canonical_url, published_at, source_names,
               cve_ids, kev_cve_ids, max_epss_score, max_epss_percentile,
               count(*) OVER() AS total_count
        FROM cti.dashboard_articles
        WHERE (@query = '' OR title ILIKE '%' || @query || '%' OR summary_tr ILIKE '%' || @query || '%')
          AND (@source = '' OR @source = ANY(source_names))
          AND (@category = '' OR category = @category)
          AND (@severity = '' OR severity = @severity)
        ORDER BY published_at DESC, id DESC
        LIMIT @limit OFFSET @offset;
        """);
    command.Parameters.AddWithValue("query", normalizedQuery);
    command.Parameters.AddWithValue("source", normalizedSource);
    command.Parameters.AddWithValue("category", normalizedCategory);
    command.Parameters.AddWithValue("severity", normalizedSeverity);
    command.Parameters.AddWithValue("limit", pageSize);
    command.Parameters.AddWithValue("offset", (currentPage - 1) * pageSize);

    var articles = new List<ArticleListItem>();
    long totalCount = 0;
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
    {
        while (await reader.ReadAsync(cancellationToken))
        {
            totalCount = reader.GetInt64(12);
            articles.Add(new ArticleListItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetDateTime(6),
                reader.GetFieldValue<string[]>(7),
                reader.GetFieldValue<string[]>(8),
                reader.GetFieldValue<string[]>(9),
                reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                reader.IsDBNull(11) ? null : reader.GetDecimal(11)));
        }
    }

    var model = new ArticleIndexModel(
        articles,
        normalizedQuery,
        normalizedSource,
        availableSources,
        normalizedCategory,
        normalizedSeverity,
        currentPage,
        (int)Math.Ceiling(totalCount / (double)pageSize),
        totalCount,
        aiUsage,
        GetAuthenticatedIdentity(context));
    return Results.Content(HtmlPages.Index(model), "text/html; charset=utf-8");
});

app.MapGet("/articles/{id:long}", async (
    HttpContext context,
    NpgsqlDataSource dataSource,
    long id,
    CancellationToken cancellationToken) =>
{
    await using var command = dataSource.CreateCommand("""
        SELECT id, title, category, severity, summary_tr, canonical_url, published_at,
               analyzed_at, source_names, cve_ids, kev_cve_ids, max_epss_score,
               max_epss_percentile
        FROM cti.dashboard_articles
        WHERE id = @id;
        """);
    command.Parameters.AddWithValue("id", id);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.NotFound();
    }

    var article = new ArticleDetail(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetDateTime(6),
        reader.IsDBNull(7) ? null : reader.GetDateTime(7),
        reader.GetFieldValue<string[]>(8),
        reader.GetFieldValue<string[]>(9),
        reader.GetFieldValue<string[]>(10),
        reader.IsDBNull(11) ? null : reader.GetDecimal(11),
        reader.IsDBNull(12) ? null : reader.GetDecimal(12));
    return Results.Content(HtmlPages.Article(article, GetAuthenticatedIdentity(context)), "text/html; charset=utf-8");
});

app.MapGet("/reports", async (
    HttpContext context,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    await using var command = dataSource.CreateCommand("""
        SELECT id, title, content, status, window_start, window_end, generated_at, sent_at
        FROM cti.dashboard_reports
        ORDER BY generated_at DESC, id DESC
        LIMIT 24;
        """);
    var reports = new List<ReportItem>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        reports.Add(new ReportItem(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetDateTime(4),
            reader.GetDateTime(5),
            reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7)));
    }

    return Results.Content(
        HtmlPages.Reports(reports, GetAuthenticatedIdentity(context)),
        "text/html; charset=utf-8");
});

app.Run();

static string GetOrCreateSetupCsrfToken(HttpContext context)
{
    const string cookieName = "cti_setup_csrf";
    var existing = context.Request.Cookies[cookieName];
    if (IsValidSetupCsrfToken(existing)) return existing!;

    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    context.Response.Cookies.Append(cookieName, token, new CookieOptions
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        Path = "/setup",
        MaxAge = TimeSpan.FromMinutes(30)
    });
    return token;
}

static bool HasValidSetupCsrfToken(HttpContext context, IFormCollection form)
{
    var cookieToken = context.Request.Cookies["cti_setup_csrf"];
    var formToken = form["_csrf"].FirstOrDefault();
    if (!IsValidSetupCsrfToken(cookieToken) || !IsValidSetupCsrfToken(formToken)) return false;

    return CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(cookieToken!),
        Encoding.ASCII.GetBytes(formToken!));
}

static bool IsValidSetupCsrfToken(string? token) =>
    token is { Length: 43 } && token.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

static bool IsSameSetupOrigin(HttpRequest request)
{
    var origin = request.Headers.Origin.FirstOrDefault();
    return Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
           string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
           (string.Equals(originUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}

static string NormalizeFilter(string? value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    var normalized = value.Trim();
    return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
}

static string NormalizeEnum(string? value, IReadOnlyCollection<string> allowed)
{
    var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
    return allowed.Contains(normalized) ? normalized : string.Empty;
}

static string GetAuthenticatedIdentity(HttpContext context) =>
    string.IsNullOrWhiteSpace(context.Request.Headers["Cf-Access-Authenticated-User-Email"].FirstOrDefault())
        ? "Local development"
        : "Authenticated owner";

internal sealed record ArticleListItem(
    long Id, string Title, string Category, string Severity, string Summary,
    string Url, DateTime PublishedAt, string[] Sources, string[] Cves,
    string[] KevCves, decimal? MaxEpssScore, decimal? MaxEpssPercentile);

internal sealed record ArticleIndexModel(
    IReadOnlyList<ArticleListItem> Articles, string Query, string Source,
    IReadOnlyList<string> AvailableSources, string Category, string Severity,
    int Page, int TotalPages, long TotalCount, AiUsageSummary AiUsage,
    string AuthenticatedEmail);

internal sealed record AiUsageSummary(
    long TodayRequests, long TodayTokens, long MonthRequests,
    long MonthPromptTokens, long MonthOutputTokens, long MonthTotalTokens,
    long MonthArticleRequests, long MonthReportRequests, long MonthFailedRequests,
    DateTime? LastRequestedAt);

internal sealed record ArticleDetail(
    long Id, string Title, string Category, string Severity, string Summary,
    string Url, DateTime PublishedAt, DateTime? AnalyzedAt, string[] Sources,
    string[] Cves, string[] KevCves, decimal? MaxEpssScore,
    decimal? MaxEpssPercentile);

internal sealed record ReportItem(
    long Id, string Title, string Content, string Status, DateTime WindowStart,
    DateTime WindowEnd, DateTime GeneratedAt, DateTime? SentAt);

internal sealed record SetupSystemStatus(
    int SchemaVersion, long EnabledSourceCount, long TotalSourceCount,
    long CheckedSourceCount, long FailingSourceCount,
    DateTime? LastSourceSuccessAt, string AuthenticationMode);

internal sealed record AiProviderStatus(
    bool ProfileDefined, string? ProviderKey, string? ProviderLabel,
    string? AdapterKey, string? ModelIdentifier, string? ApiBaseUrl,
    string AdapterStatus, DateTime? UpdatedAt);

internal sealed record N8nHandoffStatus(
    bool Reachable,
    bool HealthReady,
    bool CredentialApiDetected,
    bool AuthenticationRequired,
    string ApiBaseUrl,
    string Detail);

internal sealed class N8nHandoffProbe : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri healthUri;
    private readonly Uri credentialSchemaUri;

    internal N8nHandoffProbe(string configuredApiUrl)
    {
        if (!Uri.TryCreate(configuredApiUrl.TrimEnd('/') + "/", UriKind.Absolute, out var apiUri) ||
            (!string.Equals(apiUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(apiUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            apiUri.UserInfo.Length > 0 ||
            apiUri.Query.Length > 0 ||
            apiUri.Fragment.Length > 0 ||
            !apiUri.AbsolutePath.TrimEnd('/').EndsWith("/api/v1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CTI_N8N_API_URL must be an HTTP(S) URL ending in /api/v1 without credentials or query data.");
        }

        ApiBaseUrl = apiUri.AbsoluteUri.TrimEnd('/');
        credentialSchemaUri = new Uri(apiUri, "credentials/schema/googlePalmApi");
        var apiPath = apiUri.AbsolutePath.TrimEnd('/');
        var prefix = apiPath[..^"/api/v1".Length];
        var healthBuilder = new UriBuilder(apiUri)
        {
            Path = prefix + "/healthz",
            Query = string.Empty,
            Fragment = string.Empty
        };
        healthUri = healthBuilder.Uri;
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    internal string ApiBaseUrl { get; }

    internal async Task<N8nHandoffStatus> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var healthResponse = await client.GetAsync(
                healthUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            using var schemaRequest = new HttpRequestMessage(HttpMethod.Get, credentialSchemaUri);
            using var schemaResponse = await client.SendAsync(
                schemaRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var healthReady = healthResponse.IsSuccessStatusCode;
            var authenticationRequired = schemaResponse.StatusCode is
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
            var apiDetected = authenticationRequired || schemaResponse.IsSuccessStatusCode;
            var detail = !healthReady
                ? $"n8n responded, but health returned HTTP {(int)healthResponse.StatusCode}."
                : authenticationRequired
                    ? "Credential API detected; unauthenticated access was rejected."
                    : schemaResponse.IsSuccessStatusCode
                        ? "Credential schema was exposed without API authentication; handoff remains disabled."
                        : $"Credential schema endpoint returned HTTP {(int)schemaResponse.StatusCode}.";

            return new N8nHandoffStatus(
                true,
                healthReady,
                apiDetected,
                authenticationRequired,
                ApiBaseUrl,
                detail);
        }
        catch (Exception exception) when (
            (exception is HttpRequestException || exception is TaskCanceledException) &&
            !cancellationToken.IsCancellationRequested)
        {
            return new N8nHandoffStatus(
                false,
                false,
                false,
                false,
                ApiBaseUrl,
                "n8n is not reachable from the dashboard network.");
        }
    }

    public void Dispose() => client.Dispose();
}

internal static class HtmlPages
{
    private static string E(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    private static string D(DateTime value) => value.ToUniversalTime().ToString("dd MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

    internal static string Index(ArticleIndexModel model)
    {
        var cards = model.Articles.Count == 0
            ? "<div class=\"empty\">No matching analyzed articles were found.</div>"
            : string.Join("", model.Articles.Select(article => $$"""
                <article class="record">
                  <div class="record-meta"><span class="tag {{E(article.Category)}}">{{E(Label(article.Category))}}</span><span class="severity {{E(article.Severity)}}">{{E(article.Severity)}}</span>{{EnrichmentBadges(article.Cves, article.KevCves, article.MaxEpssScore, article.MaxEpssPercentile)}}<span>{{E(string.Join(", ", article.Sources))}}</span><time>{{D(article.PublishedAt)}}</time></div>
                  <h2><a href="/articles/{{article.Id}}">{{E(article.Title)}}</a></h2>
                  <p>{{E(article.Summary)}}</p>
                  <a class="source" href="{{E(article.Url)}}" target="_blank" rel="noopener noreferrer">Open original source ↗</a>
                </article>
                """));
        var previous = model.Page > 1 ? PageLink("Previous", model, model.Page - 1) : string.Empty;
        var next = model.Page < model.TotalPages ? PageLink("Next", model, model.Page + 1) : string.Empty;
        var usage = model.AiUsage;
        var lastRequest = usage.LastRequestedAt is null ? "No recorded request" : D(usage.LastRequestedAt.Value);
        return Layout("CTI Intelligence", model.AuthenticatedEmail, $$"""
            <section class="hero"><p class="eyebrow">PRIVATE THREAT INTELLIGENCE</p><h1>Intelligence inbox</h1><p>Analyzed security news retained for the current research window.</p></section>
            <section class="usage-summary" aria-label="AI usage summary">
              <div><span>Today</span><strong>{{usage.TodayRequests.ToString("N0", CultureInfo.InvariantCulture)}} requests</strong><small>{{usage.TodayTokens.ToString("N0", CultureInfo.InvariantCulture)}} tokens</small></div>
              <div><span>UTC month</span><strong>{{usage.MonthRequests.ToString("N0", CultureInfo.InvariantCulture)}} requests</strong><small>{{usage.MonthTotalTokens.ToString("N0", CultureInfo.InvariantCulture)}} tokens</small></div>
              <div><span>Token split</span><strong>{{usage.MonthPromptTokens.ToString("N0", CultureInfo.InvariantCulture)}} input</strong><small>{{usage.MonthOutputTokens.ToString("N0", CultureInfo.InvariantCulture)}} output</small></div>
              <div><span>Workload</span><strong>{{usage.MonthArticleRequests.ToString("N0", CultureInfo.InvariantCulture)}} articles · {{usage.MonthReportRequests.ToString("N0", CultureInfo.InvariantCulture)}} reports</strong><small>{{usage.MonthFailedRequests.ToString("N0", CultureInfo.InvariantCulture)}} failed/rate-limited · {{E(lastRequest)}}</small></div>
            </section>
            <p class="count">Token totals include provider metadata captured after usage tracking was enabled; historical requests are not reconstructed.</p>
            <form class="filters" method="get">
              <label>Search<input type="search" name="q" value="{{E(model.Query)}}" maxlength="120" placeholder="Title or executive summary"></label>
              <label>Source<select name="source">{{SourceOptions(model.AvailableSources, model.Source)}}</select></label>
              <label>Category<select name="category">{{Options(CategoryOptions, model.Category)}}</select></label>
              <label>Severity<select name="severity">{{Options(SeverityOptions, model.Severity)}}</select></label>
              <button type="submit">Apply filters</button><a class="reset" href="/">Reset</a>
            </form>
            <div class="count">{{model.TotalCount}} analyzed records</div>
            <section class="records">{{cards}}</section>
            <nav class="pagination">{{previous}}<span>Page {{model.Page}} / {{Math.Max(model.TotalPages, 1)}}</span>{{next}}</nav>
            """);
    }

    internal static string Article(ArticleDetail article, string email) => Layout(article.Title, email, $$"""
        <a class="back" href="/">← Back to intelligence inbox</a>
        <article class="detail">
          <div class="record-meta"><span class="tag {{E(article.Category)}}">{{E(Label(article.Category))}}</span><span class="severity {{E(article.Severity)}}">{{E(article.Severity)}}</span>{{EnrichmentBadges(article.Cves, article.KevCves, article.MaxEpssScore, article.MaxEpssPercentile)}}</div>
          <h1>{{E(article.Title)}}</h1>
          <p class="summary">{{E(article.Summary)}}</p>
          <dl><div><dt>Published</dt><dd>{{D(article.PublishedAt)}}</dd></div><div><dt>Analyzed</dt><dd>{{(article.AnalyzedAt is null ? "Unknown" : D(article.AnalyzedAt.Value))}}</dd></div><div><dt>Sources</dt><dd>{{E(string.Join(", ", article.Sources))}}</dd></div><div><dt>CVEs</dt><dd>{{(article.Cves.Length == 0 ? "None detected" : E(string.Join(", ", article.Cves)))}}</dd></div><div><dt>CISA KEV</dt><dd>{{(article.KevCves.Length == 0 ? "No catalog match" : E(string.Join(", ", article.KevCves)))}}</dd></div><div><dt>EPSS</dt><dd>{{E(EpssLabel(article.MaxEpssScore, article.MaxEpssPercentile))}}</dd></div></dl>
          <a class="primary-link" href="{{E(article.Url)}}" target="_blank" rel="noopener noreferrer">Read original report ↗</a>
        </article>
        """);

    internal static string Reports(IReadOnlyList<ReportItem> reports, string email)
    {
        var body = reports.Count == 0
            ? "<div class=\"empty\">No completed reports are available.</div>"
            : string.Join("", reports.Select(report => $$"""
                <details class="report">
                  <summary><span><strong>{{E(report.Title)}}</strong><small>{{D(report.WindowStart)}} — {{D(report.WindowEnd)}}</small></span><span class="status">{{E(report.Status)}}</span></summary>
                  <div class="report-body"><div class="report-markdown">{{RenderReportMarkdown(report.Content, report.Title)}}</div><p class="report-timestamps">Generated {{D(report.GeneratedAt)}}{{(report.SentAt is null ? "" : $" · Sent {D(report.SentAt.Value)}")}}</p></div>
                </details>
                """));
        return Layout("Weekly reports", email, $$"""
            <section class="hero"><p class="eyebrow">PRIVATE REPORT ARCHIVE</p><h1>Weekly assessments</h1><p>Generated reports are retained for eight weeks.</p></section>
            <section class="reports">{{body}}</section>
            """);
    }

    internal static string Setup(
        SetupSystemStatus status,
        AiProviderStatus aiProvider,
        N8nHandoffStatus n8nStatus,
        string csrfToken,
        bool profileSaved,
        string email)
    {
        var sourceHealthClass = status.FailingSourceCount > 0 ? "warning" : "ready";
        var sourceHealthLabel = status.FailingSourceCount > 0
            ? $"{status.FailingSourceCount} source needs attention"
            : status.CheckedSourceCount == 0
                ? "Waiting for the first collection"
                : "No current source failures";
        var lastCollection = status.LastSourceSuccessAt is null
            ? "Not recorded yet"
            : D(status.LastSourceSuccessAt.Value);
        var accessLabel = status.AuthenticationMode == "cloudflare"
            ? "Cloudflare Access"
            : "Local/private binding";
        var providerLabel = aiProvider.ProfileDefined
            ? aiProvider.ProviderLabel ?? aiProvider.ProviderKey ?? "Configured provider"
            : "Not selected";
        var modelLabel = aiProvider.ModelIdentifier ?? "Not selected";
        var endpointLabel = aiProvider.ApiBaseUrl ?? "Provider default";
        var adapterLabel = aiProvider.AdapterStatus switch
        {
            "bundled" => "Bundled adapter",
            "manual_adapter_required" => "Manual adapter required",
            _ => "Not configured"
        };
        var profileUpdated = aiProvider.UpdatedAt is null
            ? "No saved profile"
            : D(aiProvider.UpdatedAt.Value);
        var selectedProvider = aiProvider.ProviderKey ?? "google_gemini";
        var selectedModel = aiProvider.ModelIdentifier ?? "models/gemini-3.6-flash";
        var selectedEndpoint = aiProvider.ApiBaseUrl ?? string.Empty;
        var savedNotice = profileSaved
            ? "<aside class=\"setup-result\"><strong>AI profile saved.</strong><span>No credential or workflow state was changed.</span></aside>"
            : string.Empty;
        var handoffReady = n8nStatus.HealthReady &&
                           n8nStatus.CredentialApiDetected &&
                           n8nStatus.AuthenticationRequired;

        return Layout("Guided setup", email, $$"""
            <section class="hero setup-hero"><p class="eyebrow">GUIDED CONFIGURATION</p><h1>Setup</h1><p>Check the installation before adding provider credentials or activating automation.</p></section>
            <aside class="setup-notice"><strong>Credential-safe profile</strong><span>This step saves metadata only. API keys are neither accepted nor stored in the CTI database.</span></aside>
            {{savedNotice}}
            <ol class="setup-steps" aria-label="Setup progress">
              <li class="complete"><span>01</span><strong>System check</strong><small>Passed</small></li>
              <li class="active"><span>02</span><strong>AI provider</strong><small>Profile model ready</small></li>
              <li><span>03</span><strong>Telegram &amp; sources</strong><small>Planned</small></li>
              <li><span>04</span><strong>Review &amp; activate</strong><small>Planned</small></li>
            </ol>
            <section class="setup-panel">
              <div class="setup-heading"><div><p class="eyebrow">STEP 01</p><h2>System check</h2></div><span class="check-state ready">Ready</span></div>
              <div class="system-grid">
                <article><span>Dashboard</span><strong>Online</strong><small>The private interface is responding.</small></article>
                <article><span>Database schema</span><strong>Version {{status.SchemaVersion}}</strong><small>The restricted status view is available.</small></article>
                <article><span>Reviewed sources</span><strong>{{status.EnabledSourceCount}} enabled / {{status.TotalSourceCount}} defined</strong><small>{{status.CheckedSourceCount}} enabled sources have completed a successful check.</small></article>
                <article><span>Source health</span><strong class="{{sourceHealthClass}}">{{E(sourceHealthLabel)}}</strong><small>Last successful collection: {{E(lastCollection)}}</small></article>
                <article><span>Access boundary</span><strong>{{E(accessLabel)}}</strong><small>The setup route uses the same protection as the dashboard.</small></article>
                <article><span>Configuration writes</span><strong>Profile metadata only</strong><small>No secrets or workflow state can be changed in this package.</small></article>
              </div>
            </section>
            <section class="setup-panel ai-panel">
              <div class="setup-heading"><div><p class="eyebrow">STEP 02</p><h2>AI provider profile</h2></div><span class="check-state {{(aiProvider.ProfileDefined ? "ready" : "warning")}}">{{(aiProvider.ProfileDefined ? "Profile defined" : "Not configured")}}</span></div>
              <div class="ai-profile-grid">
                <article><span>Provider</span><strong>{{E(providerLabel)}}</strong><small>Stable key: {{E(aiProvider.ProviderKey ?? "—")}}</small></article>
                <article><span>Model</span><strong>{{E(modelLabel)}}</strong><small>Stored as provider-neutral metadata.</small></article>
                <article><span>API endpoint</span><strong>{{E(endpointLabel)}}</strong><small>Optional for compatible or private endpoints.</small></article>
                <article><span>Workflow adapter</span><strong>{{E(adapterLabel)}}</strong><small>Adapter key: {{E(aiProvider.AdapterKey ?? "—")}}</small></article>
                <article><span>Credential boundary</span><strong>External credential store</strong><small>API keys are intentionally excluded from this schema.</small></article>
                <article><span>Profile updated</span><strong>{{E(profileUpdated)}}</strong><small>No workflow is activated by defining a profile.</small></article>
              </div>
              <form class="ai-form" method="post" action="/setup/ai-profile">
                <input type="hidden" name="_csrf" value="{{E(csrfToken)}}">
                <label>Provider<select name="provider_type" required>{{Options(ProviderOptions, selectedProvider)}}</select></label>
                <label>Model identifier<input name="model_identifier" value="{{E(selectedModel)}}" minlength="1" maxlength="200" required autocomplete="off"></label>
                <label>API base URL (optional)<input type="url" name="api_base_url" value="{{E(selectedEndpoint)}}" maxlength="500" placeholder="https://api.example.com/v1" autocomplete="off"></label>
                <div class="form-actions"><p>Saving updates only the non-secret provider profile. It does not test the API or alter n8n.</p><button type="submit">Save AI profile</button></div>
              </form>
              <div class="adapter-section">
                <p class="eyebrow">ADAPTER TARGETS</p>
                <div class="adapter-options">
                  <article><strong>Google Gemini</strong><span class="check-state ready">Bundled</span><p>The included article-analysis and weekly-report workflows currently use the Gemini node.</p></article>
                  <article><strong>OpenAI-compatible</strong><span class="check-state warning">Manual mapping</span><p>The profile can represent the provider and endpoint, but its n8n model node must be mapped before activation.</p></article>
                  <article><strong>Custom provider</strong><span class="check-state warning">Manual mapping</span><p>The validated prompt and JSON contract can be reused after a compatible workflow adapter is supplied.</p></article>
                </div>
              </div>
              <div class="handoff-section">
                <div class="setup-heading"><div><p class="eyebrow">CREDENTIAL HANDOFF PREFLIGHT</p><h3>n8n boundary check</h3></div><span class="check-state {{(handoffReady ? "ready" : "warning")}}">{{(handoffReady ? "Compatible" : "Not ready")}}</span></div>
                <div class="handoff-grid">
                  <article><span>Network reachability</span><strong>{{(n8nStatus.Reachable ? "Reachable" : "Unavailable")}}</strong><small>The probe sends no API key or credential data.</small></article>
                  <article><span>Health endpoint</span><strong>{{(n8nStatus.HealthReady ? "Ready" : "Not ready")}}</strong><small>Checked through the configured internal n8n URL.</small></article>
                  <article><span>Credential API</span><strong>{{(n8nStatus.CredentialApiDetected ? "Detected" : "Not detected")}}</strong><small>{{(n8nStatus.AuthenticationRequired ? "Unauthenticated access rejected." : "Authentication boundary not confirmed.")}}</small></article>
                  <article><span>Configured API URL</span><strong>{{E(n8nStatus.ApiBaseUrl)}}</strong><small>{{E(n8nStatus.Detail)}}</small></article>
                </div>
              </div>
              <div class="setup-next"><div><strong>Next: protected credential handoff</strong><p>The following package will accept one-time credential input and hand it directly to n8n without placing it in PostgreSQL.</p></div><button type="button" disabled aria-disabled="true">Credential handoff is next</button></div>
            </section>
            """);
    }

    private static string RenderReportMarkdown(string content, string reportTitle)
    {
        var output = new StringBuilder();
        var paragraph = new List<string>();
        var firstMeaningfulLine = true;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            output.Append("<p>").Append(E(string.Join(" ", paragraph))).Append("</p>");
            paragraph.Clear();
        }

        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            var headingLevel = 0;
            while (headingLevel < line.Length && headingLevel < 3 && line[headingLevel] == '#')
            {
                headingLevel++;
            }

            if (headingLevel > 0 && line.Length > headingLevel && line[headingLevel] == ' ')
            {
                FlushParagraph();
                var heading = line[(headingLevel + 1)..].Trim();
                if (!(firstMeaningfulLine && string.Equals(heading, reportTitle, StringComparison.Ordinal)))
                {
                    var htmlLevel = headingLevel + 1;
                    output.Append("<h").Append(htmlLevel).Append('>')
                        .Append(E(heading))
                        .Append("</h").Append(htmlLevel).Append('>');
                }

                firstMeaningfulLine = false;
                continue;
            }

            if (line.Length > 2 && line[0] == '<' && line[^1] == '>' &&
                Uri.TryCreate(line[1..^1], UriKind.Absolute, out var link) &&
                string.Equals(link.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                FlushParagraph();
                var safeUrl = E(link.AbsoluteUri);
                output.Append("<p class=\"report-source\"><a href=\"")
                    .Append(safeUrl)
                    .Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
                    .Append(safeUrl)
                    .Append("</a></p>");
                firstMeaningfulLine = false;
                continue;
            }

            paragraph.Add(line);
            firstMeaningfulLine = false;
        }

        FlushParagraph();
        return output.ToString();
    }

    private static string Layout(string title, string email, string content) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex,nofollow,noarchive"><meta name="theme-color" content="#18211d"><title>{{E(title)}} — CTI Self-Hosted</title><link rel="icon" type="image/svg+xml" href="/favicon.svg"><link rel="stylesheet" href="/app.css"><link rel="stylesheet" href="/reports.css"><link rel="stylesheet" href="/setup.css"></head>
        <body><header><a class="brand" href="/"><b>CTI Self-Hosted</b><span>CTI OPERATIONS</span></a><nav><a href="/">Articles</a><a href="/reports">Reports</a><a href="/setup">Setup</a></nav><span class="identity">{{E(email)}}</span></header><main>{{content}}</main><footer>Content remains untrusted until independently verified · <a href="https://github.com/emecyildiz/CTI" target="_blank" rel="noopener noreferrer">Source (AGPL-3.0)</a> · No warranty</footer></body></html>
        """;

    private static string EnrichmentBadges(
        string[] cves,
        string[] kevCves,
        decimal? epssScore,
        decimal? epssPercentile)
    {
        var output = new StringBuilder();
        if (kevCves.Length > 0)
        {
            output.Append("<span class=\"severity critical\">CISA KEV</span>");
        }
        if (epssScore is not null)
        {
            output.Append("<span class=\"tag\">")
                .Append(E(EpssLabel(epssScore, epssPercentile)))
                .Append("</span>");
        }
        if (cves.Length > 0)
        {
            output.Append("<span>")
                .Append(E(string.Join(", ", cves)))
                .Append("</span>");
        }
        return output.ToString();
    }

    private static string EpssLabel(decimal? score, decimal? percentile)
    {
        if (score is null) return "Not scored";
        var probability = (score.Value * 100m).ToString("0.0", CultureInfo.InvariantCulture);
        if (percentile is null) return $"EPSS {probability}%";
        var rank = (percentile.Value * 100m).ToString("0.0", CultureInfo.InvariantCulture);
        return $"EPSS {probability}% · percentile {rank}%";
    }

    private static string PageLink(string label, ArticleIndexModel model, int page)
    {
        var values = new Dictionary<string, string>
        {
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["q"] = model.Query,
            ["source"] = model.Source,
            ["category"] = model.Category,
            ["severity"] = model.Severity
        };
        var query = string.Join("&", values.Where(x => x.Value.Length > 0)
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        return $"<a href=\"/?{E(query)}\">{E(label)}</a>";
    }

    private static string Options(IEnumerable<(string Value, string Label)> options, string selected) =>
        string.Join("", options.Select(option =>
            $"<option value=\"{E(option.Value)}\"{(option.Value == selected ? " selected" : "")}>{E(option.Label)}</option>"));

    private static string SourceOptions(IEnumerable<string> sources, string selected) =>
        Options(
            new[] { (Value: string.Empty, Label: "All sources") }
                .Concat(sources.Select(source => (Value: source, Label: source))),
            selected);

    private static string Label(string value) => value switch
    {
        "data_breach" => "Data breach",
        "threat_intelligence" => "Threat intelligence",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '))
    };

    private static readonly (string Value, string Label)[] CategoryOptions =
    [
        ("", "All categories"), ("malware", "Malware"), ("vulnerability", "Vulnerability"),
        ("data_breach", "Data breach"), ("threat_intelligence", "Threat intelligence"), ("other", "Other")
    ];

    private static readonly (string Value, string Label)[] SeverityOptions =
    [
        ("", "All severities"), ("critical", "Critical"), ("high", "High"),
        ("medium", "Medium"), ("low", "Low"), ("unknown", "Unknown")
    ];

    private static readonly (string Value, string Label)[] ProviderOptions =
    [
        ("google_gemini", "Google Gemini — bundled adapter"),
        ("openai_compatible", "OpenAI-compatible — manual mapping"),
        ("custom", "Custom provider — manual mapping")
    ];
}
