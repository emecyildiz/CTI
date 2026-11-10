using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CtiCore;

internal sealed record FeedSource(string Id, string Name, Uri FeedUri, string ArticlePath,
    bool EnabledByDefault, string? OtherNewsPath = null, Regex? ArticleShape = null,
    string? ArticleHost = null, bool DirectoryPermalinks = true)
{
    internal static readonly FeedSource Microsoft = new("microsoft-security", "Microsoft Security Blog",
        new Uri("https://www.microsoft.com/en-us/security/blog/feed/"), "/en-us/security/blog/", true);
    internal static readonly FeedSource BleepingComputer = new("bleepingcomputer-security", "BleepingComputer · Güvenlik",
        new Uri("https://www.bleepingcomputer.com/feed/"), "/news/security/", false, "/news/");
    internal static readonly FeedSource Krebs = new("krebs-on-security", "Krebs on Security",
        new Uri("https://krebsonsecurity.com/feed/"), "/", false, ArticleShape:
        PathPattern(@"\A/[0-9]{4}/(?:0[1-9]|1[0-2])/[^/]+/?\z"));
    internal static readonly FeedSource HackerNews = new("the-hacker-news", "The Hacker News",
        new Uri("https://feeds.feedburner.com/TheHackersNews"), "/", false, ArticleShape:
        PathPattern(@"\A/[0-9]{4}/(?:0[1-9]|1[0-2])/[a-z0-9][a-z0-9-]*\.html/?\z"),
        ArticleHost: "thehackernews.com", DirectoryPermalinks: false);
    internal static readonly FeedSource SecurityWeek = new("securityweek", "SecurityWeek",
        new Uri("https://www.securityweek.com/feed/"), "/", false, ArticleShape:
        PathPattern(@"\A/[a-z0-9]+(?:-[a-z0-9]+)+/?\z"));
    internal static readonly IReadOnlyList<FeedSource> Catalog =
        Array.AsReadOnly(new[] { Microsoft, BleepingComputer, Krebs, HackerNews, SecurityWeek });

    internal static FeedSource? Find(string? id) => Catalog.FirstOrDefault(source => source.Id == id);

    // The feed can contain other publisher categories; those are not errors or security items.
    public bool IsOtherNews(string text) => OtherNewsPath is { } prefix && PublisherUrl(text) is { } uri
        && IsUnder(uri.AbsolutePath, prefix) && !uri.AbsolutePath.StartsWith(ArticlePath, StringComparison.Ordinal);

    public string? CanonicalLink(string text)
    {
        if (PublisherUrl(text) is not { } uri || !IsUnder(uri.AbsolutePath, ArticlePath)
            || (ArticleShape is not null && !ArticleShape.IsMatch(uri.AbsolutePath)))
            return null;
        var path = uri.AbsolutePath.TrimEnd('/') + (DirectoryPermalinks ? "/" : "");
        return new UriBuilder(uri) { Path = path, Query = "", Fragment = "" }
            .Uri.AbsoluteUri;
    }

    private Uri? PublisherUrl(string text)
    {
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != (ArticleHost ?? FeedUri.Host) || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            return null;
        return uri;
    }

    private static bool IsUnder(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) && path.TrimEnd('/').Length >= prefix.Length;

    private static Regex PathPattern(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}

internal sealed record Article(string Url, string Source, string Title, DateTimeOffset? Published, string? Excerpt = null);

internal sealed class FeedClient(HttpClient http)
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    internal const int MaximumItems = 100;

    internal static SocketsHttpHandler CreateTransport() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public async Task<IReadOnlyList<Article>> Read(FeedSource source, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, source.FeedUri);
        request.Headers.UserAgent.ParseAdd("Emecworks-CTI-Core/0.1");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("RSS publisher did not return HTTP 200.");
        if (response.Content.Headers.ContentLength > MaximumBytes)
            throw new InvalidDataException("RSS response exceeds the size limit.");

        await using var input = await response.Content.ReadAsStreamAsync(budget.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int size = await input.ReadAsync(buffer, budget.Token);
            if (size == 0) break;
            if (bytes.Length + size > MaximumBytes)
                throw new InvalidDataException("RSS response exceeds the size limit.");
            bytes.Write(buffer, 0, size);
        }

        bytes.Position = 0;
        using var reader = XmlReader.Create(bytes, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes,
            MaxCharactersFromEntities = 1024
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != "rss" || root.Element("channel") is not XElement channel)
            throw new InvalidDataException("Expected an RSS channel.");

        var articles = new List<Article>();
        var excerpts = new FeedExcerpt();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int scanned = 0, otherNews = 0;
        foreach (var item in channel.Elements("item").Take(MaximumItems))
        {
            budget.Token.ThrowIfCancellationRequested();
            scanned++;
            var title = item.Element("title")?.Value.Trim();
            var link = item.Element("link")?.Value.Trim() ?? "";
            if (source.IsOtherNews(link))
            {
                otherNews++;
                continue;
            }
            var url = source.CanonicalLink(link);
            if (title is null || title.Length is 0 or > 500 || url is null || !seen.Add(url))
                continue;

            var date = DateTimeOffset.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var published) ? published.ToUniversalTime() : (DateTimeOffset?)null;
            articles.Add(new Article(url, source.Name, title, date, excerpts.Read(item.Element("description"))));
        }

        if (scanned > otherNews && articles.Count == 0)
            throw new InvalidDataException("The RSS items have no supported article links.");
        return articles;
    }
}
