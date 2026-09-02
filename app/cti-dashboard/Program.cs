using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    builder.Configuration["CTI_N8N_API_URL"] ?? "http://cti-n8n:5678/api/v1",
    builder.Configuration["CtiDatabase:Name"] ?? "cti"));

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
            result,
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

app.MapPost("/setup/credential-handoff", async (
    HttpContext context,
    NpgsqlDataSource dataSource,
    N8nHandoffProbe n8nProbe,
    CancellationToken cancellationToken) =>
{
    if (!context.Request.HasFormContentType)
    {
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }

    if (context.Request.ContentLength is null or <= 0 or > 16384)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var form = await context.Request.ReadFormAsync(cancellationToken);
    if (!IsSameSetupOrigin(context.Request) || !HasValidSetupCsrfToken(context, form))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    await using (var command = dataSource.CreateCommand("""
                     SELECT profile_defined, provider_key, adapter_key
                     FROM cti.dashboard_ai_provider_status;
                     """))
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
    {
        if (!await reader.ReadAsync(cancellationToken) ||
            !reader.GetBoolean(0) ||
            !string.Equals(reader.GetString(1), "google_gemini", StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(2), "google_gemini", StringComparison.Ordinal))
        {
            return Results.Text(
                "Save the bundled Google Gemini profile before handing off a credential.",
                "text/plain; charset=utf-8",
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    var n8nApiKey = form["n8n_api_key"].FirstOrDefault() ?? string.Empty;
    var aiApiKey = form["ai_api_key"].FirstOrDefault() ?? string.Empty;
    if (!IsValidSecretInput(n8nApiKey, 20, 4096) ||
        !IsValidSecretInput(aiApiKey, 20, 512))
    {
        return Results.BadRequest("Invalid API key format.");
    }

    var handoff = await n8nProbe.UpsertGoogleGeminiCredentialAsync(
        n8nApiKey,
        aiApiKey,
        cancellationToken);

    return handoff.Status switch
    {
        N8nCredentialHandoffStatus.Created =>
            Results.Redirect("/setup?result=credential_created"),
        N8nCredentialHandoffStatus.Updated =>
            Results.Redirect("/setup?result=credential_updated"),
        N8nCredentialHandoffStatus.AuthenticationFailed => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status401Unauthorized),
        N8nCredentialHandoffStatus.Conflict => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status409Conflict),
        N8nCredentialHandoffStatus.Unavailable => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status502BadGateway)
    };
});

app.MapPost("/setup/workflow-mapping", async (
    HttpContext context,
    NpgsqlDataSource dataSource,
    N8nHandoffProbe n8nProbe,
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

    await using (var command = dataSource.CreateCommand("""
                     SELECT profile_defined, provider_key, adapter_key
                     FROM cti.dashboard_ai_provider_status;
                     """))
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
    {
        if (!await reader.ReadAsync(cancellationToken) ||
            !reader.GetBoolean(0) ||
            !string.Equals(reader.GetString(1), "google_gemini", StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(2), "google_gemini", StringComparison.Ordinal))
        {
            return Results.Text(
                "Save the bundled Google Gemini profile before mapping workflows.",
                "text/plain; charset=utf-8",
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    var n8nApiKey = form["n8n_api_key"].FirstOrDefault() ?? string.Empty;
    if (!IsValidSecretInput(n8nApiKey, 20, 4096))
    {
        return Results.BadRequest("Invalid n8n API key format.");
    }

    var mapping = await n8nProbe.MapGoogleGeminiWorkflowsAsync(
        n8nApiKey,
        cancellationToken);

    return mapping.Status switch
    {
        N8nWorkflowMappingStatus.Mapped =>
            Results.Redirect("/setup?result=workflows_mapped"),
        N8nWorkflowMappingStatus.AlreadyMapped =>
            Results.Redirect("/setup?result=workflows_already_mapped"),
        N8nWorkflowMappingStatus.AuthenticationFailed => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status401Unauthorized),
        N8nWorkflowMappingStatus.Missing or N8nWorkflowMappingStatus.Conflict => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status409Conflict),
        N8nWorkflowMappingStatus.Unavailable => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status502BadGateway)
    };
});

app.MapPost("/setup/postgres-credential-handoff", async (
    HttpContext context,
    N8nHandoffProbe n8nProbe,
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

    var n8nApiKey = form["n8n_api_key"].FirstOrDefault() ?? string.Empty;
    var databasePassword = form["database_password"].FirstOrDefault() ?? string.Empty;
    if (!IsValidSecretInput(n8nApiKey, 20, 4096) ||
        !IsValidSecretInput(databasePassword, 16, 512))
    {
        return Results.BadRequest("Invalid credential input format.");
    }

    var handoff = await n8nProbe.UpsertPostgresCredentialAsync(
        n8nApiKey,
        databasePassword,
        cancellationToken);

    return handoff.Status switch
    {
        N8nCredentialHandoffStatus.Created =>
            Results.Redirect("/setup?result=postgres_credential_created"),
        N8nCredentialHandoffStatus.Updated =>
            Results.Redirect("/setup?result=postgres_credential_updated"),
        N8nCredentialHandoffStatus.AuthenticationFailed => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status401Unauthorized),
        N8nCredentialHandoffStatus.Conflict => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status409Conflict),
        N8nCredentialHandoffStatus.Unavailable => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status502BadGateway)
    };
});

app.MapPost("/setup/postgres-workflow-mapping", async (
    HttpContext context,
    N8nHandoffProbe n8nProbe,
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

    var n8nApiKey = form["n8n_api_key"].FirstOrDefault() ?? string.Empty;
    if (!IsValidSecretInput(n8nApiKey, 20, 4096))
    {
        return Results.BadRequest("Invalid n8n API key format.");
    }

    var mapping = await n8nProbe.MapPostgresWorkflowsAsync(
        n8nApiKey,
        cancellationToken);

    return mapping.Status switch
    {
        N8nWorkflowMappingStatus.Mapped =>
            Results.Redirect("/setup?result=postgres_workflows_mapped"),
        N8nWorkflowMappingStatus.AlreadyMapped =>
            Results.Redirect("/setup?result=postgres_workflows_already_mapped"),
        N8nWorkflowMappingStatus.AuthenticationFailed => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status401Unauthorized),
        N8nWorkflowMappingStatus.Missing or N8nWorkflowMappingStatus.Conflict => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status409Conflict),
        N8nWorkflowMappingStatus.Unavailable => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status502BadGateway)
    };
});

app.MapPost("/setup/telegram-credential-handoff", async (
    HttpContext context,
    N8nHandoffProbe n8nProbe,
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

    var n8nApiKey = form["n8n_api_key"].FirstOrDefault() ?? string.Empty;
    var botToken = form["bot_token"].FirstOrDefault() ?? string.Empty;
    if (!IsValidSecretInput(n8nApiKey, 20, 4096) ||
        !IsValidTelegramBotToken(botToken))
    {
        return Results.BadRequest("Invalid credential input format.");
    }

    var handoff = await n8nProbe.UpsertTelegramCredentialAsync(
        n8nApiKey,
        botToken,
        cancellationToken);

    return handoff.Status switch
    {
        N8nCredentialHandoffStatus.Created =>
            Results.Redirect("/setup?result=telegram_credential_created"),
        N8nCredentialHandoffStatus.Updated =>
            Results.Redirect("/setup?result=telegram_credential_updated"),
        N8nCredentialHandoffStatus.AuthenticationFailed => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status401Unauthorized),
        N8nCredentialHandoffStatus.Conflict => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status409Conflict),
        N8nCredentialHandoffStatus.Unavailable => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Text(
            handoff.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status502BadGateway)
    };
});

app.MapPost("/setup/telegram-workflow-mapping", async (
    HttpContext context,
    N8nHandoffProbe n8nProbe,
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

    var n8nApiKey = form["n8n_api_key"].FirstOrDefault() ?? string.Empty;
    var authorizedChatId = form["authorized_chat_id"].FirstOrDefault() ?? string.Empty;
    if (!IsValidSecretInput(n8nApiKey, 20, 4096) ||
        !IsValidTelegramPrivateId(authorizedChatId))
    {
        return Results.BadRequest("Invalid Telegram mapping input format.");
    }

    var mapping = await n8nProbe.MapTelegramWorkflowsAsync(
        n8nApiKey,
        authorizedChatId,
        cancellationToken);

    return mapping.Status switch
    {
        N8nWorkflowMappingStatus.Mapped =>
            Results.Redirect("/setup?result=telegram_workflows_mapped"),
        N8nWorkflowMappingStatus.AlreadyMapped =>
            Results.Redirect("/setup?result=telegram_workflows_already_mapped"),
        N8nWorkflowMappingStatus.AuthenticationFailed => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status401Unauthorized),
        N8nWorkflowMappingStatus.Missing or N8nWorkflowMappingStatus.Conflict => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status409Conflict),
        N8nWorkflowMappingStatus.Unavailable => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Text(
            mapping.Message,
            "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status502BadGateway)
    };
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

static bool IsValidSecretInput(string value, int minimumLength, int maximumLength) =>
    value.Length >= minimumLength &&
    value.Length <= maximumLength &&
    value.All(character => char.IsAscii(character) &&
                           !char.IsControl(character) &&
                           !char.IsWhiteSpace(character));

static bool IsValidTelegramBotToken(string value) =>
    IsValidSecretInput(value, 26, 160) &&
    Regex.IsMatch(
        value,
        @"^[0-9]{5,20}:[A-Za-z0-9_-]{20,128}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

static bool IsValidTelegramPrivateId(string value) =>
    Regex.IsMatch(
        value,
        @"^[0-9]{5,20}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

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

internal enum N8nCredentialHandoffStatus
{
    Created,
    Updated,
    AuthenticationFailed,
    Conflict,
    Missing,
    Rejected,
    Unavailable
}

internal sealed record N8nCredentialHandoffResult(
    N8nCredentialHandoffStatus Status,
    string Message);

internal enum N8nWorkflowMappingStatus
{
    Mapped,
    AlreadyMapped,
    AuthenticationFailed,
    Missing,
    Conflict,
    Rejected,
    Unavailable
}

internal sealed record N8nWorkflowMappingResult(
    N8nWorkflowMappingStatus Status,
    string Message);

internal sealed record N8nWorkflowTarget(
    string WorkflowName,
    string CredentialType,
    string CredentialName,
    IReadOnlyDictionary<string, string> NodeTypesByName);

internal sealed record N8nPreparedWorkflow(
    string WorkflowId,
    string WorkflowName,
    string CredentialType,
    string CredentialName,
    IReadOnlyDictionary<string, string> NodeTypesByName,
    JsonObject Payload,
    bool NeedsUpdate,
    string? AuthorizedTelegramChatId = null);

internal sealed class N8nWorkflowSafetyException(string message) : Exception(message);

internal sealed class N8nHandoffProbe : IDisposable
{
    private const string GeminiCredentialName = "CTI Self-Hosted - Google Gemini";
    private const string GeminiCredentialType = "googlePalmApi";
    private const string GeminiHost = "https://generativelanguage.googleapis.com";
    private const string GeminiNodeType = "@n8n/n8n-nodes-langchain.googleGemini";
    private const string PostgresCredentialName = "CTI Self-Hosted - PostgreSQL";
    private const string PostgresCredentialType = "postgres";
    private const string PostgresNodeType = "n8n-nodes-base.postgres";
    private const string TelegramCredentialName = "CTI Self-Hosted - Telegram";
    private const string TelegramCredentialType = "telegramApi";
    private const string TelegramNodeType = "n8n-nodes-base.telegram";
    private const string TelegramTriggerNodeType = "n8n-nodes-base.telegramTrigger";
    private const int MaximumResponseBytes = 262144;
    private static readonly Regex TelegramAllowedIdAssignment = new(
        @"const allowedId = '(?:__CTI_TELEGRAM_ALLOWED_ID__|[0-9]{5,20})';",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex TelegramChatIdValue = new(
        @"^(?:__CTI_TELEGRAM_CHAT_ID__|[0-9]{5,20})$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> WritableWorkflowSettings = new(StringComparer.Ordinal)
    {
        "saveExecutionProgress",
        "saveManualExecutions",
        "saveDataErrorExecution",
        "saveDataSuccessExecution",
        "executionTimeout",
        "errorWorkflow",
        "timezone",
        "executionOrder",
        "callerPolicy",
        "callerIds",
        "timeSavedPerExecution",
        "redactionPolicy",
        "availableInMCP",
        "customTelemetryTags"
    };
    private static readonly N8nWorkflowTarget[] GeminiWorkflowTargets =
    [
        SingleTypeTarget("CTI Article Analysis", GeminiNodeType, GeminiCredentialType,
            GeminiCredentialName, "Analyze With Gemini"),
        SingleTypeTarget("CTI Weekly Report", GeminiNodeType, GeminiCredentialType,
            GeminiCredentialName, "Generate Weekly Assessment")
    ];
    private static readonly N8nWorkflowTarget[] PostgresWorkflowTargets =
    [
        SingleTypeTarget("CTI Article Analysis", PostgresNodeType, PostgresCredentialType,
            PostgresCredentialName,
            "Claim One Analysis Job", "Record Rule Triage", "Complete Analysis",
             "Defer Unsafe Claim", "Defer Fetch Failure", "Defer Extraction Failure",
             "Defer Invalid Content", "Defer Rule Triage Failure", "Defer Gemini Failure",
             "Defer Invalid AI Output"),
        SingleTypeTarget("CTI Retention Maintenance", PostgresNodeType, PostgresCredentialType,
            PostgresCredentialName, "Apply Retention Policy"),
        SingleTypeTarget("CTI Source Collection", PostgresNodeType, PostgresCredentialType,
            PostgresCredentialName,
            "Load Active Sources", "Start Source Check", "Store Recent Metadata",
             "Record Source Success", "Record Rejected Feed Item",
             "Record Source Read Failure", "Record Source Store Failure"),
        SingleTypeTarget("CTI Telegram Query", PostgresNodeType, PostgresCredentialType,
            PostgresCredentialName, "Lookup Recent CTI Articles"),
        SingleTypeTarget("CTI Vulnerability Enrichment", PostgresNodeType, PostgresCredentialType,
            PostgresCredentialName,
            "Store CISA KEV Catalog", "Select EPSS Lookup Batch", "Store FIRST EPSS Scores"),
        SingleTypeTarget("CTI Weekly Report", PostgresNodeType, PostgresCredentialType,
            PostgresCredentialName,
            "Claim Weekly Report", "Store Weekly Report", "Record Gemini Failure",
             "Record Invalid Output"),
        SingleTypeTarget("CTI Weekly Telegram Delivery", PostgresNodeType, PostgresCredentialType,
            PostgresCredentialName,
            "Claim Weekly Telegram Delivery", "Complete Telegram Delivery",
             "Record Preparation Failure", "Record Ambiguous Send Failure",
             "Record Invalid Receipt")
    ];
    private static readonly N8nWorkflowTarget[] TelegramWorkflowTargets =
    [
        new("CTI Telegram Query", TelegramCredentialType, TelegramCredentialName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Private CTI Telegram Trigger"] = TelegramTriggerNodeType,
                ["Answer Callback Query"] = TelegramNodeType,
                ["Send Private CTI Response"] = TelegramNodeType
            }),
        SingleTypeTarget("CTI Weekly Telegram Delivery", TelegramNodeType,
            TelegramCredentialType, TelegramCredentialName, "Send Weekly Report to Telegram"),
        SingleTypeTarget("n8n Workflow Error Alerts", TelegramNodeType,
            TelegramCredentialType, TelegramCredentialName, "Send a text message")
    ];

    private static N8nWorkflowTarget SingleTypeTarget(
        string workflowName,
        string nodeType,
        string credentialType,
        string credentialName,
        params string[] nodeNames) =>
        new(
            workflowName,
            credentialType,
            credentialName,
            nodeNames.ToDictionary(name => name, _ => nodeType, StringComparer.Ordinal));

    private readonly HttpClient client;
    private readonly Uri apiBaseUri;
    private readonly Uri healthUri;
    private readonly Uri credentialSchemaUri;
    private readonly string ctiDatabaseName;
    private readonly SemaphoreSlim credentialLock = new(1, 1);

    internal N8nHandoffProbe(string configuredApiUrl, string configuredDatabaseName = "cti")
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
        if (string.IsNullOrWhiteSpace(configuredDatabaseName) ||
            configuredDatabaseName.Length > 63 ||
            !configuredDatabaseName.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
        {
            throw new InvalidOperationException("The configured CTI database name is invalid.");
        }

        ctiDatabaseName = configuredDatabaseName;
        apiBaseUri = apiUri;
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

    internal async Task<N8nCredentialHandoffResult> UpsertGoogleGeminiCredentialAsync(
        string n8nApiKey,
        string geminiApiKey,
        CancellationToken cancellationToken) =>
        await UpsertCredentialAsync(
            n8nApiKey,
            GeminiCredentialName,
            GeminiCredentialType,
            new JsonObject
            {
                ["host"] = GeminiHost,
                ["apiKey"] = geminiApiKey
            },
            "Gemini",
            cancellationToken);

    internal async Task<N8nCredentialHandoffResult> UpsertPostgresCredentialAsync(
        string n8nApiKey,
        string databasePassword,
        CancellationToken cancellationToken) =>
        await UpsertCredentialAsync(
            n8nApiKey,
            PostgresCredentialName,
            PostgresCredentialType,
            new JsonObject
            {
                ["host"] = "cti-db",
                ["database"] = ctiDatabaseName,
                ["user"] = "cti_n8n",
                ["password"] = databasePassword,
                ["maxConnections"] = 10,
                ["allowUnauthorizedCerts"] = false,
                ["ssl"] = "disable",
                ["port"] = 5432
            },
            "PostgreSQL",
            cancellationToken);

    internal async Task<N8nCredentialHandoffResult> UpsertTelegramCredentialAsync(
        string n8nApiKey,
        string botToken,
        CancellationToken cancellationToken) =>
        await UpsertCredentialAsync(
            n8nApiKey,
            TelegramCredentialName,
            TelegramCredentialType,
            new JsonObject
            {
                ["accessToken"] = botToken,
                ["baseUrl"] = "https://api.telegram.org"
            },
            "Telegram",
            cancellationToken);

    private async Task<N8nCredentialHandoffResult> UpsertCredentialAsync(
        string n8nApiKey,
        string credentialName,
        string credentialType,
        JsonObject credentialData,
        string credentialLabel,
        CancellationToken cancellationToken)
    {
        await credentialLock.WaitAsync(cancellationToken);
        try
        {
            var lookup = await FindManagedCredentialAsync(
                n8nApiKey,
                credentialName,
                credentialType,
                cancellationToken);
            if (lookup.Error is not null)
            {
                return lookup.Error;
            }

            if (lookup.CredentialId is not null)
            {
                var updated = await WriteCredentialAsync(
                    HttpMethod.Patch,
                    new Uri(apiBaseUri, $"credentials/{Uri.EscapeDataString(lookup.CredentialId)}"),
                    n8nApiKey,
                    credentialName,
                    credentialType,
                    credentialData,
                    credentialLabel,
                    N8nCredentialHandoffStatus.Updated,
                    cancellationToken);
                if (updated.Status != N8nCredentialHandoffStatus.Missing)
                {
                    return updated;
                }
            }

            return await WriteCredentialAsync(
                HttpMethod.Post,
                new Uri(apiBaseUri, "credentials"),
                n8nApiKey,
                credentialName,
                credentialType,
                credentialData,
                credentialLabel,
                N8nCredentialHandoffStatus.Created,
                cancellationToken);
        }
        catch (Exception exception) when (
            (exception is HttpRequestException ||
             exception is TaskCanceledException ||
             exception is JsonException ||
             exception is InvalidDataException) &&
            !cancellationToken.IsCancellationRequested)
        {
            return exception is HttpRequestException or TaskCanceledException
                ? new N8nCredentialHandoffResult(
                    N8nCredentialHandoffStatus.Unavailable,
                    "n8n could not be reached. No credential was stored in PostgreSQL.")
                : new N8nCredentialHandoffResult(
                    N8nCredentialHandoffStatus.Rejected,
                    "n8n returned an invalid credential response.");
        }
        finally
        {
            credentialLock.Release();
        }
    }

    internal async Task<N8nWorkflowMappingResult> MapGoogleGeminiWorkflowsAsync(
        string n8nApiKey,
        CancellationToken cancellationToken) =>
        await MapCredentialWorkflowsAsync(
            n8nApiKey,
            GeminiCredentialName,
            GeminiCredentialType,
            GeminiWorkflowTargets,
            "Create the reserved Google Gemini credential before mapping workflows.",
            "The bundled Gemini workflow nodes already use the reserved credential.",
            "The reserved Gemini credential was mapped to two disabled bundled workflows.",
            cancellationToken);

    internal async Task<N8nWorkflowMappingResult> MapPostgresWorkflowsAsync(
        string n8nApiKey,
        CancellationToken cancellationToken) =>
        await MapCredentialWorkflowsAsync(
            n8nApiKey,
            PostgresCredentialName,
            PostgresCredentialType,
            PostgresWorkflowTargets,
            "Create the reserved PostgreSQL credential before mapping workflows.",
            "All bundled PostgreSQL nodes already use the reserved credential.",
            "The reserved PostgreSQL credential was mapped to 31 nodes in seven disabled workflows.",
            cancellationToken);

    internal async Task<N8nWorkflowMappingResult> MapTelegramWorkflowsAsync(
        string n8nApiKey,
        string authorizedChatId,
        CancellationToken cancellationToken) =>
        await MapCredentialWorkflowsAsync(
            n8nApiKey,
            TelegramCredentialName,
            TelegramCredentialType,
            TelegramWorkflowTargets,
            "Create the reserved Telegram credential before mapping workflows.",
            "All bundled Telegram nodes and authorized chat targets already use the configured values.",
            "The Telegram credential and authorized private chat were mapped to five nodes in three disabled workflows.",
            cancellationToken,
            authorizedChatId);

    private async Task<N8nWorkflowMappingResult> MapCredentialWorkflowsAsync(
        string n8nApiKey,
        string credentialName,
        string credentialType,
        IReadOnlyList<N8nWorkflowTarget> workflowTargets,
        string missingMessage,
        string alreadyMappedMessage,
        string mappedMessage,
        CancellationToken cancellationToken,
        string? authorizedTelegramChatId = null)
    {
        await credentialLock.WaitAsync(cancellationToken);
        try
        {
            var credentialLookup = await FindManagedCredentialAsync(
                n8nApiKey,
                credentialName,
                credentialType,
                cancellationToken);
            if (credentialLookup.Error is not null)
            {
                return ConvertCredentialError(credentialLookup.Error);
            }

            if (credentialLookup.CredentialId is null)
            {
                return new N8nWorkflowMappingResult(
                    N8nWorkflowMappingStatus.Missing,
                    missingMessage);
            }

            var preparedWorkflows = new List<N8nPreparedWorkflow>(workflowTargets.Count);
            foreach (var target in workflowTargets)
            {
                var workflowLookup = await FindWorkflowByNameAsync(
                    target,
                    n8nApiKey,
                    cancellationToken);
                if (workflowLookup.Error is not null)
                {
                    return workflowLookup.Error;
                }

                var prepared = PrepareWorkflowMapping(
                    workflowLookup.Workflow!,
                    target,
                    credentialLookup.CredentialId);
                if (authorizedTelegramChatId is not null)
                {
                    prepared = ApplyTelegramConfiguration(
                        prepared,
                        authorizedTelegramChatId);
                }

                preparedWorkflows.Add(prepared);
            }

            if (preparedWorkflows.All(workflow => !workflow.NeedsUpdate))
            {
                return new N8nWorkflowMappingResult(
                    N8nWorkflowMappingStatus.AlreadyMapped,
                    alreadyMappedMessage);
            }

            foreach (var workflow in preparedWorkflows.Where(workflow => workflow.NeedsUpdate))
            {
                var updateResult = await UpdateWorkflowAsync(
                    workflow,
                    credentialLookup.CredentialId,
                    n8nApiKey,
                    cancellationToken);
                if (updateResult is not null)
                {
                    return updateResult;
                }
            }

            return new N8nWorkflowMappingResult(
                N8nWorkflowMappingStatus.Mapped,
                mappedMessage);
        }
        catch (N8nWorkflowSafetyException exception)
        {
            return new N8nWorkflowMappingResult(
                N8nWorkflowMappingStatus.Conflict,
                exception.Message);
        }
        catch (Exception exception) when (
            (exception is HttpRequestException ||
             exception is TaskCanceledException ||
             exception is JsonException ||
             exception is InvalidDataException) &&
            !cancellationToken.IsCancellationRequested)
        {
            return exception is HttpRequestException or TaskCanceledException
                ? new N8nWorkflowMappingResult(
                    N8nWorkflowMappingStatus.Unavailable,
                    "n8n could not be reached while mapping workflows.")
                : new N8nWorkflowMappingResult(
                    N8nWorkflowMappingStatus.Rejected,
                    "n8n returned an invalid workflow response.");
        }
        finally
        {
            credentialLock.Release();
        }
    }

    private async Task<(JsonObject? Workflow, N8nWorkflowMappingResult? Error)>
        FindWorkflowByNameAsync(
            N8nWorkflowTarget target,
            string n8nApiKey,
            CancellationToken cancellationToken)
    {
        var listUri = new Uri(
            apiBaseUri,
            $"workflows?name={Uri.EscapeDataString(target.WorkflowName)}&limit=100&excludePinnedData=true");
        using var listRequest = CreateAuthorizedRequest(HttpMethod.Get, listUri, n8nApiKey);
        using var listResponse = await client.SendAsync(
            listRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var listError = WorkflowApiError(listResponse, "workflow:list");
        if (listError is not null)
        {
            return (null, listError);
        }

        var listPayload = await ReadLimitedContentAsync(listResponse.Content, cancellationToken);
        using var listDocument = JsonDocument.Parse(listPayload);
        if (!listDocument.RootElement.TryGetProperty("data", out var workflows) ||
            workflows.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Workflow list does not contain a data array.");
        }

        var workflowIds = new List<string>();
        foreach (var workflow in workflows.EnumerateArray())
        {
            if (!workflow.TryGetProperty("name", out var nameElement) ||
                !string.Equals(nameElement.GetString(), target.WorkflowName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!workflow.TryGetProperty("id", out var idElement) ||
                !IsSafeCredentialId(idElement.GetString()))
            {
                throw new JsonException("Workflow identity is invalid.");
            }

            workflowIds.Add(idElement.GetString()!);
        }

        if (workflowIds.Count == 0)
        {
            return (null, new N8nWorkflowMappingResult(
                N8nWorkflowMappingStatus.Missing,
                $"The bundled workflow '{target.WorkflowName}' was not found in n8n."));
        }

        if (workflowIds.Count > 1)
        {
            return (null, new N8nWorkflowMappingResult(
                N8nWorkflowMappingStatus.Conflict,
                $"Multiple n8n workflows use the reserved name '{target.WorkflowName}'."));
        }

        using var getRequest = CreateAuthorizedRequest(
            HttpMethod.Get,
            new Uri(apiBaseUri, $"workflows/{Uri.EscapeDataString(workflowIds[0])}?excludePinnedData=true"),
            n8nApiKey);
        using var getResponse = await client.SendAsync(
            getRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var getError = WorkflowApiError(getResponse, "workflow:read");
        if (getError is not null)
        {
            return (null, getError);
        }

        var workflowPayload = await ReadLimitedContentAsync(getResponse.Content, cancellationToken);
        var workflowObject = JsonNode.Parse(workflowPayload) as JsonObject;
        if (workflowObject is null)
        {
            throw new JsonException("Workflow response is not an object.");
        }

        return (workflowObject, null);
    }

    private static N8nPreparedWorkflow PrepareWorkflowMapping(
        JsonObject workflow,
        N8nWorkflowTarget target,
        string credentialId)
    {
        var workflowId = RequiredString(workflow, "id");
        var workflowName = RequiredString(workflow, "name");
        if (!IsSafeCredentialId(workflowId) ||
            !string.Equals(workflowName, target.WorkflowName, StringComparison.Ordinal))
        {
            throw new JsonException("Workflow identity did not match the mapping target.");
        }

        if (!RequiredBoolean(workflow, "active", out var active) || active ||
            !RequiredBoolean(workflow, "isArchived", out var archived) || archived ||
            workflow["activeVersion"] is not null)
        {
            throw new N8nWorkflowSafetyException(
                $"Workflow '{target.WorkflowName}' must be disabled, unpublished, and unarchived before mapping.");
        }

        if (workflow["nodes"] is not JsonArray nodes ||
            workflow["connections"] is not JsonObject ||
            workflow["settings"] is not JsonObject settings)
        {
            throw new JsonException("Workflow structure is incomplete.");
        }

        var expectedNodeTypes = target.NodeTypesByName.Values.ToHashSet(StringComparer.Ordinal);
        var credentialNodes = nodes
            .OfType<JsonObject>()
            .Where(node => expectedNodeTypes.Contains(OptionalString(node, "type") ?? string.Empty))
            .ToList();
        var actualNodeNames = credentialNodes
            .Select(node => RequiredString(node, "name"))
            .ToList();
        if (actualNodeNames.Count != target.NodeTypesByName.Count ||
            actualNodeNames.Distinct(StringComparer.Ordinal).Count() != actualNodeNames.Count ||
            credentialNodes.Any(node =>
                !target.NodeTypesByName.TryGetValue(RequiredString(node, "name"), out var expectedType) ||
                !string.Equals(OptionalString(node, "type"), expectedType, StringComparison.Ordinal)))
        {
            throw new N8nWorkflowSafetyException(
                $"Workflow '{target.WorkflowName}' does not contain exactly the expected credential nodes.");
        }

        var alreadyMapped = true;
        foreach (var node in credentialNodes)
        {
            JsonObject credentials;
            if (node["credentials"] is null)
            {
                credentials = new JsonObject();
                node["credentials"] = credentials;
            }
            else if (node["credentials"] is JsonObject existingCredentials)
            {
                credentials = existingCredentials;
            }
            else
            {
                throw new JsonException("Workflow node credentials are not an object.");
            }

            var existingCredential = credentials[target.CredentialType] as JsonObject;
            alreadyMapped &= existingCredential is not null &&
                             string.Equals(OptionalString(existingCredential, "id"), credentialId, StringComparison.Ordinal) &&
                             string.Equals(OptionalString(existingCredential, "name"), target.CredentialName, StringComparison.Ordinal);
            credentials[target.CredentialType] = new JsonObject
            {
                ["id"] = credentialId,
                ["name"] = target.CredentialName
            };
        }

        var payload = new JsonObject();
        foreach (var property in new[]
                 {
                     "name", "description", "nodes", "connections", "nodeGroups",
                     "staticData", "pinData"
                 })
        {
            if (workflow.TryGetPropertyValue(property, out var value))
            {
                payload[property] = value?.DeepClone();
            }
        }

        var writableSettings = new JsonObject();
        foreach (var setting in settings)
        {
            if (WritableWorkflowSettings.Contains(setting.Key))
            {
                writableSettings[setting.Key] = setting.Value?.DeepClone();
            }
        }

        payload["settings"] = writableSettings;

        if (payload["name"] is null ||
            payload["nodes"] is null ||
            payload["connections"] is null ||
            payload["settings"] is null)
        {
            throw new JsonException("Workflow update payload is incomplete.");
        }

        return new N8nPreparedWorkflow(
            workflowId,
            workflowName,
            target.CredentialType,
            target.CredentialName,
            target.NodeTypesByName,
            payload,
            !alreadyMapped);
    }

    private static N8nPreparedWorkflow ApplyTelegramConfiguration(
        N8nPreparedWorkflow workflow,
        string authorizedChatId)
    {
        if (workflow.Payload["nodes"] is not JsonArray nodes)
        {
            throw new JsonException("Telegram workflow nodes are missing.");
        }

        var changed = workflow.WorkflowName switch
        {
            "CTI Telegram Query" => ConfigureTelegramAuthorization(nodes, authorizedChatId),
            "CTI Weekly Telegram Delivery" => ConfigureTelegramChatTarget(
                nodes,
                "Send Weekly Report to Telegram",
                authorizedChatId),
            "n8n Workflow Error Alerts" => ConfigureTelegramChatTarget(
                nodes,
                "Send a text message",
                authorizedChatId),
            _ => throw new N8nWorkflowSafetyException(
                $"Workflow '{workflow.WorkflowName}' is not an approved Telegram target.")
        };

        return workflow with
        {
            NeedsUpdate = workflow.NeedsUpdate || changed,
            AuthorizedTelegramChatId = authorizedChatId
        };
    }

    private static bool ConfigureTelegramAuthorization(
        JsonArray nodes,
        string authorizedChatId)
    {
        var node = RequiredWorkflowNode(
            nodes,
            "Authorize and Parse Request",
            "n8n-nodes-base.code");
        if (node["parameters"] is not JsonObject parameters)
        {
            throw new JsonException("Telegram authorization parameters are missing.");
        }

        var source = RequiredString(parameters, "jsCode");
        var matches = TelegramAllowedIdAssignment.Matches(source);
        if (matches.Count != 1)
        {
            throw new N8nWorkflowSafetyException(
                "CTI Telegram Query does not contain the expected private-chat authorization guard.");
        }

        var replacement = $"const allowedId = '{authorizedChatId}';";
        if (string.Equals(matches[0].Value, replacement, StringComparison.Ordinal))
        {
            return false;
        }

        parameters["jsCode"] = TelegramAllowedIdAssignment.Replace(source, replacement, 1);
        return true;
    }

    private static bool ConfigureTelegramChatTarget(
        JsonArray nodes,
        string nodeName,
        string authorizedChatId)
    {
        var node = RequiredWorkflowNode(nodes, nodeName, TelegramNodeType);
        if (node["parameters"] is not JsonObject parameters)
        {
            throw new JsonException("Telegram send parameters are missing.");
        }

        var currentChatId = RequiredString(parameters, "chatId");
        if (!TelegramChatIdValue.IsMatch(currentChatId))
        {
            throw new N8nWorkflowSafetyException(
                $"Telegram node '{nodeName}' contains an unexpected chat target.");
        }

        if (string.Equals(currentChatId, authorizedChatId, StringComparison.Ordinal))
        {
            return false;
        }

        parameters["chatId"] = authorizedChatId;
        return true;
    }

    private static JsonObject RequiredWorkflowNode(
        JsonArray nodes,
        string nodeName,
        string nodeType)
    {
        var matches = nodes.OfType<JsonObject>().Where(node =>
            string.Equals(OptionalString(node, "name"), nodeName, StringComparison.Ordinal) &&
            string.Equals(OptionalString(node, "type"), nodeType, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
        {
            throw new N8nWorkflowSafetyException(
                $"Expected workflow node '{nodeName}' was not found exactly once.");
        }

        return matches[0];
    }

    private async Task<N8nWorkflowMappingResult?> UpdateWorkflowAsync(
        N8nPreparedWorkflow workflow,
        string credentialId,
        string n8nApiKey,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(workflow.Payload);
        try
        {
            using var request = CreateAuthorizedRequest(
                HttpMethod.Put,
                new Uri(apiBaseUri, $"workflows/{Uri.EscapeDataString(workflow.WorkflowId)}"),
                n8nApiKey);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var error = WorkflowApiError(response, "workflow:update");
            if (error is not null)
            {
                return error;
            }

            var responsePayload = await ReadLimitedContentAsync(response.Content, cancellationToken);
            var updatedWorkflow = JsonNode.Parse(responsePayload) as JsonObject;
            if (updatedWorkflow is null ||
                !string.Equals(RequiredString(updatedWorkflow, "id"), workflow.WorkflowId, StringComparison.Ordinal) ||
                !string.Equals(RequiredString(updatedWorkflow, "name"), workflow.WorkflowName, StringComparison.Ordinal) ||
                !RequiredBoolean(updatedWorkflow, "active", out var active) || active ||
                !RequiredBoolean(updatedWorkflow, "isArchived", out var archived) || archived ||
                updatedWorkflow["activeVersion"] is not null ||
                !WorkflowNodesUseCredential(updatedWorkflow, workflow, credentialId) ||
                !TelegramConfigurationMatches(updatedWorkflow, workflow))
            {
                return new N8nWorkflowMappingResult(
                    N8nWorkflowMappingStatus.Rejected,
                    $"n8n did not confirm a safe disabled mapping for '{workflow.WorkflowName}'.");
            }

            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static bool WorkflowNodesUseCredential(
        JsonObject workflow,
        N8nPreparedWorkflow expected,
        string credentialId)
    {
        if (workflow["nodes"] is not JsonArray nodes)
        {
            return false;
        }

        var expectedNodeTypes = expected.NodeTypesByName.Values.ToHashSet(StringComparer.Ordinal);
        var matchingNodes = nodes.OfType<JsonObject>().Where(candidate =>
            expectedNodeTypes.Contains(OptionalString(candidate, "type") ?? string.Empty)).ToList();
        var actualNames = matchingNodes.Select(node => RequiredString(node, "name")).ToList();
        if (actualNames.Count != expected.NodeTypesByName.Count ||
            actualNames.Distinct(StringComparer.Ordinal).Count() != actualNames.Count ||
            matchingNodes.Any(node =>
                !expected.NodeTypesByName.TryGetValue(RequiredString(node, "name"), out var expectedType) ||
                !string.Equals(OptionalString(node, "type"), expectedType, StringComparison.Ordinal)))
        {
            return false;
        }

        return matchingNodes.All(node =>
        {
            var credential = node["credentials"]?[expected.CredentialType] as JsonObject;
            return credential is not null &&
                   string.Equals(OptionalString(credential, "id"), credentialId, StringComparison.Ordinal) &&
                   string.Equals(OptionalString(credential, "name"), expected.CredentialName, StringComparison.Ordinal);
        });
    }

    private static bool TelegramConfigurationMatches(
        JsonObject workflow,
        N8nPreparedWorkflow expected)
    {
        var chatId = expected.AuthorizedTelegramChatId;
        if (chatId is null)
        {
            return true;
        }

        if (workflow["nodes"] is not JsonArray nodes)
        {
            return false;
        }

        if (string.Equals(expected.WorkflowName, "CTI Telegram Query", StringComparison.Ordinal))
        {
            var node = RequiredWorkflowNode(
                nodes,
                "Authorize and Parse Request",
                "n8n-nodes-base.code");
            if (node["parameters"] is not JsonObject parameters)
            {
                return false;
            }

            var source = OptionalString(parameters, "jsCode") ?? string.Empty;
            var matches = TelegramAllowedIdAssignment.Matches(source);
            return matches.Count == 1 &&
                   string.Equals(matches[0].Value, $"const allowedId = '{chatId}';", StringComparison.Ordinal);
        }

        var targetNodeName = string.Equals(
            expected.WorkflowName,
            "CTI Weekly Telegram Delivery",
            StringComparison.Ordinal)
            ? "Send Weekly Report to Telegram"
            : "Send a text message";
        var targetNode = RequiredWorkflowNode(nodes, targetNodeName, TelegramNodeType);
        return targetNode["parameters"] is JsonObject sendParameters &&
               string.Equals(OptionalString(sendParameters, "chatId"), chatId, StringComparison.Ordinal);
    }

    private static N8nWorkflowMappingResult? WorkflowApiError(
        HttpResponseMessage response,
        string requiredScope)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new N8nWorkflowMappingResult(
                N8nWorkflowMappingStatus.AuthenticationFailed,
                $"n8n API authentication failed or the key lacks {requiredScope} permission.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new N8nWorkflowMappingResult(
                N8nWorkflowMappingStatus.Missing,
                "A required n8n workflow no longer exists.");
        }

        return response.IsSuccessStatusCode
            ? null
            : new N8nWorkflowMappingResult(
                N8nWorkflowMappingStatus.Rejected,
                $"n8n rejected workflow mapping with HTTP {(int)response.StatusCode}.");
    }

    private static N8nWorkflowMappingResult ConvertCredentialError(
        N8nCredentialHandoffResult error) => error.Status switch
        {
            N8nCredentialHandoffStatus.AuthenticationFailed => new(
                N8nWorkflowMappingStatus.AuthenticationFailed,
                "n8n API authentication failed or the key lacks credential:list permission."),
            N8nCredentialHandoffStatus.Conflict => new(
                N8nWorkflowMappingStatus.Conflict,
                error.Message),
            N8nCredentialHandoffStatus.Unavailable => new(
                N8nWorkflowMappingStatus.Unavailable,
                error.Message),
            _ => new(N8nWorkflowMappingStatus.Rejected, error.Message)
        };

    private static string RequiredString(JsonObject value, string property)
    {
        var result = OptionalString(value, property);
        return string.IsNullOrEmpty(result)
            ? throw new JsonException($"Required string '{property}' is missing.")
            : result;
    }

    private static string? OptionalString(JsonObject value, string property) =>
        value[property] is JsonValue jsonValue &&
        jsonValue.TryGetValue<string>(out var result)
            ? result
            : null;

    private static bool RequiredBoolean(JsonObject value, string property, out bool result)
    {
        result = false;
        return value[property] is JsonValue jsonValue && jsonValue.TryGetValue(out result);
    }

    private async Task<(string? CredentialId, N8nCredentialHandoffResult? Error)>
        FindManagedCredentialAsync(
            string n8nApiKey,
            string credentialName,
            string credentialType,
            CancellationToken cancellationToken)
    {
        string? cursor = null;
        string? matchedId = null;
        for (var page = 0; page < 10; page++)
        {
            var relativeUrl = cursor is null
                ? "credentials?limit=100"
                : $"credentials?limit=100&cursor={Uri.EscapeDataString(cursor)}";
            using var request = CreateAuthorizedRequest(
                HttpMethod.Get,
                new Uri(apiBaseUri, relativeUrl),
                n8nApiKey);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (null, new N8nCredentialHandoffResult(
                    N8nCredentialHandoffStatus.AuthenticationFailed,
                    "n8n API authentication failed or the key lacks credential:list permission."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, new N8nCredentialHandoffResult(
                    N8nCredentialHandoffStatus.Rejected,
                    $"n8n rejected credential discovery with HTTP {(int)response.StatusCode}."));
            }

            var payload = await ReadLimitedContentAsync(response.Content, cancellationToken);
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("data", out var credentials) ||
                credentials.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Credential list does not contain a data array.");
            }

            foreach (var credential in credentials.EnumerateArray())
            {
                if (!credential.TryGetProperty("name", out var nameElement) ||
                    !string.Equals(nameElement.GetString(), credentialName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!credential.TryGetProperty("type", out var typeElement) ||
                    !string.Equals(typeElement.GetString(), credentialType, StringComparison.Ordinal) ||
                    !credential.TryGetProperty("id", out var idElement) ||
                    !IsSafeCredentialId(idElement.GetString()))
                {
                    return (null, new N8nCredentialHandoffResult(
                        N8nCredentialHandoffStatus.Conflict,
                        "A credential with the reserved CTI name exists but is not compatible."));
                }

                if (matchedId is not null)
                {
                    return (null, new N8nCredentialHandoffResult(
                        N8nCredentialHandoffStatus.Conflict,
                        "Multiple credentials use the reserved CTI name; resolve them in n8n first."));
                }

                matchedId = idElement.GetString();
            }

            cursor = document.RootElement.TryGetProperty("nextCursor", out var cursorElement) &&
                     cursorElement.ValueKind == JsonValueKind.String
                ? cursorElement.GetString()
                : null;
            if (string.IsNullOrEmpty(cursor))
            {
                return (matchedId, null);
            }
        }

        return (null, new N8nCredentialHandoffResult(
            N8nCredentialHandoffStatus.Conflict,
            "Credential discovery exceeded the safe pagination limit."));
    }

    private async Task<N8nCredentialHandoffResult> WriteCredentialAsync(
        HttpMethod method,
        Uri endpoint,
        string n8nApiKey,
        string credentialName,
        string credentialType,
        JsonObject credentialData,
        string credentialLabel,
        N8nCredentialHandoffStatus successStatus,
        CancellationToken cancellationToken)
    {
        var payload = method == HttpMethod.Patch
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                name = credentialName,
                type = credentialType,
                data = credentialData,
                isPartialData = false
            })
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                name = credentialName,
                type = credentialType,
                data = credentialData
            });

        try
        {
            using var request = CreateAuthorizedRequest(method, endpoint, n8nApiKey);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new N8nCredentialHandoffResult(
                    N8nCredentialHandoffStatus.AuthenticationFailed,
                    "n8n API authentication failed or the key lacks credential create/update permission.");
            }

            if (method == HttpMethod.Patch && response.StatusCode == HttpStatusCode.NotFound)
            {
                return new N8nCredentialHandoffResult(
                    N8nCredentialHandoffStatus.Missing,
                    "The managed n8n credential no longer exists.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new N8nCredentialHandoffResult(
                    N8nCredentialHandoffStatus.Rejected,
                    $"n8n rejected the credential handoff with HTTP {(int)response.StatusCode}.");
            }

            var responsePayload = await ReadLimitedContentAsync(response.Content, cancellationToken);
            using var document = JsonDocument.Parse(responsePayload);
            if (!document.RootElement.TryGetProperty("id", out var idElement) ||
                !IsSafeCredentialId(idElement.GetString()) ||
                !document.RootElement.TryGetProperty("name", out var nameElement) ||
                !string.Equals(nameElement.GetString(), credentialName, StringComparison.Ordinal) ||
                !document.RootElement.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), credentialType, StringComparison.Ordinal))
            {
                throw new JsonException("Credential response identity did not match the request.");
            }

            return new N8nCredentialHandoffResult(
                successStatus,
                successStatus == N8nCredentialHandoffStatus.Created
                    ? $"{credentialLabel} credential created in n8n."
                    : $"{credentialLabel} credential updated in n8n.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        Uri endpoint,
        string n8nApiKey)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.TryAddWithoutValidation("X-N8N-API-KEY", n8nApiKey);
        return request;
    }

    private static bool IsSafeCredentialId(string? value) =>
        value is { Length: >= 1 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static async Task<byte[]> ReadLimitedContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("n8n response exceeded the size limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("n8n response exceeded the size limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

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
        string? result,
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
        var savedNotice = result switch
        {
            "saved" => "<aside class=\"setup-result\"><strong>AI profile saved.</strong><span>No credential or workflow state was changed.</span></aside>",
            "credential_created" => "<aside class=\"setup-result\"><strong>Gemini credential created in n8n.</strong><span>The API keys were not stored in PostgreSQL or returned to the page.</span></aside>",
            "credential_updated" => "<aside class=\"setup-result\"><strong>Gemini credential updated in n8n.</strong><span>The existing reserved CTI credential was reused.</span></aside>",
            "workflows_mapped" => "<aside class=\"setup-result\"><strong>Gemini workflow mapping completed.</strong><span>Two bundled workflow drafts were updated and remained disabled.</span></aside>",
            "workflows_already_mapped" => "<aside class=\"setup-result\"><strong>Gemini workflows were already mapped.</strong><span>No workflow write or activation was performed.</span></aside>",
            "postgres_credential_created" => "<aside class=\"setup-result\"><strong>PostgreSQL credential created in n8n.</strong><span>The database password was not stored in the dashboard database or returned to the page.</span></aside>",
            "postgres_credential_updated" => "<aside class=\"setup-result\"><strong>PostgreSQL credential updated in n8n.</strong><span>The existing reserved CTI credential was reused.</span></aside>",
            "postgres_workflows_mapped" => "<aside class=\"setup-result\"><strong>PostgreSQL workflow mapping completed.</strong><span>Thirty-one database nodes in seven workflow drafts were updated and remained disabled.</span></aside>",
            "postgres_workflows_already_mapped" => "<aside class=\"setup-result\"><strong>PostgreSQL workflows were already mapped.</strong><span>No workflow write or activation was performed.</span></aside>",
            "telegram_credential_created" => "<aside class=\"setup-result\"><strong>Telegram credential created in n8n.</strong><span>The bot token was not stored in the dashboard database or returned to the page.</span></aside>",
            "telegram_credential_updated" => "<aside class=\"setup-result\"><strong>Telegram credential updated in n8n.</strong><span>The existing reserved CTI credential was reused.</span></aside>",
            "telegram_workflows_mapped" => "<aside class=\"setup-result\"><strong>Telegram workflow mapping completed.</strong><span>Five Telegram nodes and the private-chat authorization guard were configured in three disabled workflows.</span></aside>",
            "telegram_workflows_already_mapped" => "<aside class=\"setup-result\"><strong>Telegram workflows were already mapped.</strong><span>No workflow write or activation was performed.</span></aside>",
            _ => string.Empty
        };
        var handoffReady = n8nStatus.HealthReady &&
                           n8nStatus.CredentialApiDetected &&
                           n8nStatus.AuthenticationRequired;
        var credentialFormReady = handoffReady &&
                                  aiProvider.ProfileDefined &&
                                  string.Equals(aiProvider.ProviderKey, "google_gemini", StringComparison.Ordinal) &&
                                  string.Equals(aiProvider.AdapterKey, "google_gemini", StringComparison.Ordinal);
        var credentialForm = credentialFormReady
            ? $$"""
                <form class="credential-form" method="post" action="/setup/credential-handoff" autocomplete="off">
                  <input type="hidden" name="_csrf" value="{{E(csrfToken)}}">
                  <label>n8n API key<input type="password" name="n8n_api_key" minlength="20" maxlength="4096" required autocomplete="new-password" spellcheck="false" autocapitalize="off"></label>
                  <label>Google Gemini API key<input type="password" name="ai_api_key" minlength="20" maxlength="512" required autocomplete="new-password" spellcheck="false" autocapitalize="off"></label>
                  <div class="credential-guidance"><p>The setup key needs <code>credential:list</code>, <code>credential:create</code>, <code>credential:update</code>, <code>workflow:list</code>, <code>workflow:read</code>, and <code>workflow:update</code> scopes. The Gemini key is written directly to n8n's encrypted credential store.</p><button type="submit">Create or update credential</button></div>
                </form>
                """
            : $"<div class=\"credential-unavailable\"><strong>Credential input is locked.</strong><p>{E(aiProvider.ProviderKey == "google_gemini" ? "Complete the n8n boundary check first." : "Save the bundled Google Gemini profile first.")}</p></div>";
        var workflowMappingForm = credentialFormReady
            ? $$"""
                <form class="credential-form workflow-mapping-form" method="post" action="/setup/workflow-mapping" autocomplete="off">
                  <input type="hidden" name="_csrf" value="{{E(csrfToken)}}">
                  <label>n8n API key<input type="password" name="n8n_api_key" minlength="20" maxlength="4096" required autocomplete="new-password" spellcheck="false" autocapitalize="off"></label>
                  <div class="credential-guidance"><p>The reserved Gemini credential must already exist. Mapping is limited to <code>CTI Article Analysis</code> and <code>CTI Weekly Report</code>; both must be disabled, unpublished, and structurally compatible.</p><button type="submit">Map disabled workflows</button></div>
                </form>
                """
            : "<div class=\"credential-unavailable\"><strong>Workflow mapping is locked.</strong><p>Complete the Gemini profile and n8n boundary check first.</p></div>";
        var postgresCredentialForm = handoffReady
            ? $$"""
                <form class="credential-form" method="post" action="/setup/postgres-credential-handoff" autocomplete="off">
                  <input type="hidden" name="_csrf" value="{{E(csrfToken)}}">
                  <label>n8n API key<input type="password" name="n8n_api_key" minlength="20" maxlength="4096" required autocomplete="new-password" spellcheck="false" autocapitalize="off"></label>
                  <label>CTI application database password<input type="password" name="database_password" minlength="16" maxlength="512" required autocomplete="new-password" spellcheck="false" autocapitalize="off"></label>
                  <div class="credential-guidance"><p>Use <code>CTI_APP_PASSWORD</code> from the private <code>.env</code> file. The credential is fixed to <code>cti_n8n@cti-db:5432</code> with SSL disabled inside the private Docker network.</p><button type="submit">Create or update PostgreSQL credential</button></div>
                </form>
                """
            : "<div class=\"credential-unavailable\"><strong>Database credential input is locked.</strong><p>Complete the n8n boundary check first.</p></div>";
        var postgresMappingForm = handoffReady
            ? $$"""
                <form class="credential-form workflow-mapping-form" method="post" action="/setup/postgres-workflow-mapping" autocomplete="off">
                  <input type="hidden" name="_csrf" value="{{E(csrfToken)}}">
                  <label>n8n API key<input type="password" name="n8n_api_key" minlength="20" maxlength="4096" required autocomplete="new-password" spellcheck="false" autocapitalize="off"></label>
                  <div class="credential-guidance"><p>Mapping is limited to 31 expected PostgreSQL nodes in seven bundled workflows. Every target workflow must remain disabled, unpublished, unarchived, and structurally unchanged.</p><button type="submit">Map PostgreSQL workflow drafts</button></div>
                </form>
                """
            : "<div class=\"credential-unavailable\"><strong>Database workflow mapping is locked.</strong><p>Complete the n8n boundary check first.</p></div>";
        var telegramCredentialForm = handoffReady
            ? $$"""
                <form class="credential-form" method="post" action="/setup/telegram-credential-handoff" autocomplete="off">
                  <input type="hidden" name="_csrf" value="{{E(csrfToken)}}">
                  <label>n8n API key<input type="password" name="n8n_api_key" minlength="20" maxlength="4096" required autocomplete="new-password" spellcheck="false" autocapitalize="off"></label>
                  <label>Telegram bot token<input type="password" name="bot_token" minlength="26" maxlength="160" required autocomplete="new-password" spellcheck="false" autocapitalize="off"></label>
                  <div class="credential-guidance"><p>Obtain the token from BotFather. It is written directly to n8n's encrypted credential store under the reserved CTI name.</p><button type="submit">Create or update Telegram credential</button></div>
                </form>
                """
            : "<div class=\"credential-unavailable\"><strong>Telegram credential input is locked.</strong><p>Complete the n8n boundary check first.</p></div>";
        var telegramMappingForm = handoffReady
            ? $$"""
                <form class="credential-form workflow-mapping-form" method="post" action="/setup/telegram-workflow-mapping" autocomplete="off">
                  <input type="hidden" name="_csrf" value="{{E(csrfToken)}}">
                  <label>n8n API key<input type="password" name="n8n_api_key" minlength="20" maxlength="4096" required autocomplete="new-password" spellcheck="false" autocapitalize="off"></label>
                  <label>Authorized private Telegram user/chat ID<input inputmode="numeric" pattern="[0-9]{5,20}" name="authorized_chat_id" minlength="5" maxlength="20" required autocomplete="off" spellcheck="false"></label>
                  <div class="credential-guidance"><p>The query bot accepts only a direct private chat where the sender ID and chat ID both equal this value. The same destination receives weekly reports and workflow-error alerts.</p><button type="submit">Configure Telegram workflow drafts</button></div>
                </form>
                """
            : "<div class=\"credential-unavailable\"><strong>Telegram workflow mapping is locked.</strong><p>Complete the n8n boundary check first.</p></div>";

        return Layout("Guided setup", email, $$"""
            <section class="hero setup-hero"><p class="eyebrow">GUIDED CONFIGURATION</p><h1>Setup</h1><p>Check the installation before adding provider credentials or activating automation.</p></section>
            <aside class="setup-notice"><strong>One-time credential handoff</strong><span>API keys are accepted only by the protected handoff form, forwarded directly to n8n, and never stored in the CTI database.</span></aside>
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
                <article><span>CTI database writes</span><strong>Profile metadata only</strong><small>Credential material remains outside the PostgreSQL schema.</small></article>
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
                {{credentialForm}}
              </div>
              <div class="workflow-mapping-section">
                <div class="setup-heading"><div><p class="eyebrow">WORKFLOW CREDENTIAL MAPPING</p><h3>Gemini workflow drafts</h3></div><span class="check-state warning">Manual confirmation</span></div>
                {{workflowMappingForm}}
              </div>
            </section>
            <section class="setup-panel database-panel">
              <div class="setup-heading"><div><p class="eyebrow">STEP 03</p><h2>PostgreSQL workflow access</h2></div><span class="check-state warning">Manual confirmation</span></div>
              <p class="section-intro">The generated application-role password is handed directly to n8n. The database owner and dashboard roles are never used by workflow nodes.</p>
              <div class="handoff-section database-handoff-section">
                <div class="setup-heading"><div><p class="eyebrow">DATABASE CREDENTIAL HANDOFF</p><h3>Restricted CTI application role</h3></div></div>
                {{postgresCredentialForm}}
              </div>
              <div class="workflow-mapping-section">
                <div class="setup-heading"><div><p class="eyebrow">DATABASE WORKFLOW MAPPING</p><h3>Bundled workflow drafts</h3></div></div>
                {{postgresMappingForm}}
              </div>
            </section>
            <section class="setup-panel telegram-panel">
              <div class="setup-heading"><div><p class="eyebrow">STEP 03 · OPTIONAL</p><h2>Private Telegram delivery</h2></div><span class="check-state warning">Disabled by default</span></div>
              <p class="section-intro">Telegram remains optional. The public exports contain no personal chat identifier, and the query workflow authorizes one direct private user/chat before activation.</p>
              <div class="handoff-section database-handoff-section">
                <div class="setup-heading"><div><p class="eyebrow">TELEGRAM CREDENTIAL HANDOFF</p><h3>Operator-owned bot token</h3></div></div>
                {{telegramCredentialForm}}
              </div>
              <div class="workflow-mapping-section">
                <div class="setup-heading"><div><p class="eyebrow">PRIVATE CHAT AUTHORIZATION</p><h3>Three bundled workflow drafts</h3></div></div>
                {{telegramMappingForm}}
              </div>
              <div class="setup-next"><div><strong>Next: review and controlled activation</strong><p>Credentials and targets must be reviewed in n8n before workflows are activated one at a time.</p></div><button type="button" disabled aria-disabled="true">Activation review is next</button></div>
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
