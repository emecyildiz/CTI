using System.Net;
using System.Text.RegularExpressions;
using CtiCore;
using Microsoft.Data.Sqlite;

const string fixture = """
    <rss version="2.0"><channel>
    <item><title>&lt;script&gt;not code&lt;/script&gt;</title>
      <link>https://www.microsoft.com/en-us/security/blog/2026/11/01/example?utm_source=rss</link>
      <pubDate>Sun, 01 Nov 2026 13:00:00 +0000</pubDate></item>
    <item><title>Duplicate</title>
      <link>https://www.microsoft.com/en-us/security/blog/2026/11/01/example/#copy</link></item>
    <item><title>Off-catalog</title><link>https://example.test/story</link></item>
    <item><title>Insecure</title><link>http://www.microsoft.com/en-us/security/blog/fake/</link></item>
    <item><title>Credential link</title><link>https://user@www.microsoft.com/en-us/security/blog/fake/</link></item>
    <item><title>Custom port</title><link>https://www.microsoft.com:444/en-us/security/blog/fake/</link></item>
    </channel></rss>
    """;

var temp = Directory.CreateTempSubdirectory("emecworks-cti-core-test-");
try
{
    var mode = "normal";
    int fetched = 0;
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var fake = new HttpClient(new Stub(async (req, ct) =>
    {
        Require(req.RequestUri == FeedSource.Microsoft.FeedUri && req.Method == HttpMethod.Get,
            "Only the reviewed source URL is used.");
        Interlocked.Increment(ref fetched);
        if (mode == "blocked")
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        }
        if (mode == "redirect")
            return new HttpResponseMessage(HttpStatusCode.Redirect)
                { Headers = { Location = new Uri("http://127.0.0.1:9/") } };
        HttpContent content = mode switch
        {
            "DTD" => new StringContent("""<!DOCTYPE rss [<!ENTITY probe SYSTEM "file:///private">]><rss><channel>&probe;</channel></rss>"""),
            "oversize" => new StreamContent(new LargeStream(FeedClient.MaximumBytes + 1)),
            "unsupported" => new StringContent("<rss><channel><item><title>No link</title></item></channel></rss>"),
            _ => new StringContent(fixture)
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }));
    var options = new CoreOptions(0, temp.FullName);
    await using (var app = CoreApp.Create(options, fake))
    {
        await app.StartAsync();
        using var browser = Browser(app.Urls.Single());
        using var ready = await browser.GetAsync("/");
        var page = await ready.Content.ReadAsStringAsync();
        var token = Token(page);
        Require(fetched == 0 && WebUtility.HtmlDecode(page).Contains("Henüz haber yok"), "An empty startup makes no network request.");
        Require(ready.Headers.GetValues("Content-Security-Policy").Single().Contains("frame-ancestors 'none'"),
            "Frame protection is enabled.");
        using var badHost = new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = "outside.test" } };
        using var hostResponse = await browser.SendAsync(badHost);
        Require(hostResponse.StatusCode == HttpStatusCode.BadRequest, "Unexpected Host is rejected.");
        Require((await Submit(browser, token, origin: "http://outside.test")).Status == 400, "Cross-origin POST rejected.");
        Require((await Submit(browser, token, omitOrigin: true)).Status == 400, "Missing Origin rejected.");
        Require((await Submit(browser, "bad-token")).Status == 400, "Missing/mismatched token rejected.");
        Require((await Submit(browser, token, source: "https://example.test/feed")).Status == 400 && fetched == 0,
            "Unknown source cannot initiate a fetch.");
        Require(await Submit(browser, token) == (303, "/?status=1"), "One unique article is added.");
        Require(await Submit(browser, token) == (303, "/?status=0"), "Refetch never duplicates an article.");
        page = await browser.GetStringAsync("/");
        Require(page.Contains("&lt;script&gt;not code&lt;/script&gt;") && !page.Contains("<script>")
            && page.Contains("/2026/11/01/example/") && !page.Contains("utm_source") && !page.Contains("Off-catalog"),
            "Titles are escaped, URLs canonicalized, other hosts rejected.");
        foreach (var failure in new[] { "redirect", "DTD", "oversize", "unsupported" })
        {
            mode = failure;
            Require(await Submit(browser, token) == (303, "/?status=failed"), $"Feed failure is recoverable: {failure}.");
        }
        mode = "blocked";
        var first = Submit(browser, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { Require(await Submit(browser, token) == (303, "/?status=busy"), "One fetch at a time."); }
        finally { release.TrySetResult(); }
        Require(await first == (303, "/?status=0"), "The first fetch still completes.");
        await app.StopAsync();
    }

    await using (var reopened = CoreApp.Create(options, fake))
    {
        await reopened.StartAsync();
        using var browser = Browser(reopened.Urls.Single());
        var page = await browser.GetStringAsync("/");
        Require(Regex.Matches(page, "/2026/11/01/example/").Count == 1 && fetched == 7,
            "Reopening the app preserves the single stored article without contacting the publisher.");
        await reopened.StopAsync();
    }
    Console.WriteLine("PASS: loopback HTTP, form guards, allowlist, redirect/DTD/oversize rejection, concurrent refresh, SQLite dedup and restart.");

    var legacy = Directory.CreateDirectory(Path.Combine(temp.FullName, "previous-preview"));
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(legacy.FullName, "articles.sqlite3"), Pooling = false
    }.ToString()))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE articles (
                url TEXT PRIMARY KEY NOT NULL, source TEXT NOT NULL, title TEXT NOT NULL,
                published_unix INTEGER, first_seen_unix INTEGER NOT NULL
            );
            INSERT INTO articles VALUES (
                'https://www.microsoft.com/en-us/security/blog/legacy/', 'Microsoft Security Blog',
                'Saved in the first preview', NULL, 0
            );
            """;
        cmd.ExecuteNonQuery();
    }
    var migrated = new ArticleStore(legacy.FullName);
    Require(migrated.ReadSource() is { Enabled: true, Automatic: false, LastAttempt: null }
        && migrated.Latest().Single().Title == "Saved in the first preview",
        "Adding settings to an existing preview database preserves its articles and never opts in.");
    Console.WriteLine("PASS: first-preview SQLite upgrade preserves articles and starts in manual mode.");

    var scheduledOptions = new CoreOptions(0, Path.Combine(temp.FullName, "scheduled"));
    var clock = new TestClock(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
    var poll = TimeSpan.FromMilliseconds(80);
    var scheduleStore = new ArticleStore(scheduledOptions.DataDirectory);
    var scheduleMode = 0;
    var requested = 0;
    var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var scheduleFeed = new HttpClient(new Stub(async (_, ct) =>
    {
        Interlocked.Increment(ref requested);
        var attemptMode = Volatile.Read(ref scheduleMode);
        if (attemptMode == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (attemptMode == 2)
        {
            inFlight.TrySetResult();
            await allowCompletion.Task.WaitAsync(ct);
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(attemptMode == 2
                ? fixture.Replace("/example", "/in-flight", StringComparison.Ordinal)
                : fixture)
        };
    }));
    await using (var app = CoreApp.Create(scheduledOptions, scheduleFeed, clock, poll))
    {
        await app.StartAsync();
        using var browser = Browser(app.Urls.Single());
        var page = await browser.GetStringAsync("/");
        var token = Token(page);
        await Task.Delay(poll * 4);
        Require(requested == 0 && page.Contains("Otomatik toplama kapalı"), "Automatic collection is off on a fresh install.");
        Require((await Settings(browser, token, enabled: true, automatic: true, origin: "http://outside.test")).Status == 400
            && (await Settings(browser, "invalid", enabled: true, automatic: true)).Status == 400
            && (await Settings(browser, token, enabled: true, automatic: true, source: "unlisted")).Status == 400
            && !scheduleStore.ReadSource().Automatic, "Settings rejects cross-origin, missing token, and unsupported sources.");
        Require((await Submit(browser, token, path: "/settings/source", fields: [new("enabled", "on"), new("enabled", "off")])).Status == 400,
            "Ambiguous checkbox values are rejected.");
        Require(await Settings(browser, token, enabled: true, automatic: true) == (303, "/?status=saved"), "Automatic collection opt-in saves.");
        await WaitUntil(() => Task.FromResult(scheduleStore.ReadSource().LastSuccess == clock.GetUtcNow()), "First automatic attempt");
        var success = scheduleStore.ReadSource();
        page = await browser.GetStringAsync("/");
        Require(success is { LastAttemptSucceeded: true, LastAdded: 1 } && scheduleStore.Latest().Count == 1
            && page.Contains("Son başarılı toplama") && page.Contains("10.11.2026 12:00 UTC") && requested == 1,
            "Automatic collection stores articles, result and displayed timestamp.");

        clock.Advance(TimeSpan.FromMinutes(59));
        await Task.Delay(poll * 4);
        Require(requested == 1, "Automatic collection does not run before the interval.");
        Volatile.Write(ref scheduleMode, 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntil(() => Task.FromResult(scheduleStore.ReadSource().LastAttemptSucceeded == false), "Scheduled failure");
        var failure = scheduleStore.ReadSource();
        page = await browser.GetStringAsync("/");
        Require(failure.LastAttempt == clock.GetUtcNow() && failure.LastSuccess == success.LastSuccess
            && failure.LastAdded is null && scheduleStore.Latest().Count == 1
            && page.Contains("Önceden kaydedilen haberler duruyor."),
            "A failed collection preserves both saved articles and the last successful time.");
        await app.StopAsync();
    }
    await using (var app = CoreApp.Create(scheduledOptions, scheduleFeed, clock, poll))
    {
        await app.StartAsync();
        using var browser = Browser(app.Urls.Single());
        var token = Token(await browser.GetStringAsync("/"));
        await Task.Delay(poll * 4);
        Require(requested == 2 && scheduleStore.ReadSource() is { Automatic: true, LastAttemptSucceeded: false },
            "Schedule and failed-attempt cooldown survive restart.");
        Volatile.Write(ref scheduleMode, 2);
        clock.Advance(TimeSpan.FromHours(1));
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disabledAt = scheduleStore.ReadSource().LastAttempt;
        try
        {
            Require(await Settings(browser, token, enabled: false, automatic: true) == (303, "/?status=saved"),
                "Turning off the source also turns off automatic collection.");
        }
        finally { allowCompletion.TrySetResult(); }
        await WaitUntil(async () => await Submit(browser, token) == (303, "/?status=disabled"), "In-flight request released");
        Require(scheduleStore.ReadSource() is { Enabled: false, Automatic: false } disabled
            && disabled.LastAttempt == disabledAt && scheduleStore.Latest().Count == 1,
            "A disabled source cannot fetch manually or commit an in-flight result.");
        await app.StopAsync();
    }
    await using (var app = CoreApp.Create(scheduledOptions, scheduleFeed, clock, poll))
    {
        await app.StartAsync();
        using var browser = Browser(app.Urls.Single());
        var page = await browser.GetStringAsync("/");
        var token = Token(page);
        clock.Advance(TimeSpan.FromHours(3));
        await Task.Delay(poll * 4);
        Require(requested == 3 && page.Contains("Kaynak kapalı") && page.Contains("<button type=\"submit\" disabled>"),
            "Disabled state and articles survive restart without a new request.");
        Volatile.Write(ref scheduleMode, 0);
        Require(await Settings(browser, token, enabled: true, automatic: false) == (303, "/?status=saved")
            && await Submit(browser, token) == (303, "/?status=0"), "Manual collection works with automatic collection off.");
        await app.StopAsync();
    }
    Console.WriteLine("PASS: opt-in scheduler, hourly interval, persisted status and settings, and in-flight disable protection.");

    if (args.SequenceEqual(new[] { "--live-feed" }))
    {
        using var publisher = new HttpClient(FeedClient.CreateTransport());
        await using var live = CoreApp.Create(new CoreOptions(0, Path.Combine(temp.FullName, "live")), publisher);
        await live.StartAsync();
        using var browser = Browser(live.Urls.Single());
        var token = Token(await browser.GetStringAsync("/"));
        var result = await Submit(browser, token);
        var store = new ArticleStore(Path.Combine(temp.FullName, "live"));
        var n = store.Latest().Count;
        Require(result == (303, "/?status=" + n) && n > 0, "The real publisher feed loads and stores articles.");
        await live.StopAsync();
        Console.WriteLine($"PASS: Microsoft RSS through the actual preview, {n} news records stored in the temporary fixture.");
    }
}
finally
{
    // Remove only the disposable directory created here, never a user-provided path.
    if (temp.Parent?.FullName == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
        && temp.Name.StartsWith("emecworks-cti-core-test-", StringComparison.Ordinal))
        temp.Delete(recursive: true);
}

static void Require(bool value, string scenario)
{
    if (!value) throw new InvalidOperationException(scenario);
}

static string Token(string page)
{
    var match = Regex.Match(page, """name="__RequestVerificationToken" value="([^"]+)""");
    Require(match.Success, "The form has an antiforgery token.");
    return WebUtility.HtmlDecode(match.Groups[1].Value);
}

static HttpClient Browser(string address) =>
    new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri(address) };

static Task<(int Status, string? Redirect)> Settings(HttpClient browser, string token, bool enabled, bool automatic,
    string? origin = null, string source = "microsoft-security")
{
    var fields = new List<KeyValuePair<string, string>>();
    if (enabled) fields.Add(new("enabled", "on"));
    if (automatic) fields.Add(new("automatic", "on"));
    return Submit(browser, token, source, origin, path: "/settings/source", fields: fields);
}

static async Task<(int Status, string? Redirect)> Submit(HttpClient browser, string token,
    string source = "microsoft-security", string? origin = null, bool omitOrigin = false,
    string path = "/refresh", IEnumerable<KeyValuePair<string, string>>? fields = null)
{
    List<KeyValuePair<string, string>> values = [new("source", source), new("__RequestVerificationToken", token)];
    if (fields is not null) values.AddRange(fields);
    using var request = new HttpRequestMessage(HttpMethod.Post, path)
    {
        Content = new FormUrlEncodedContent(values)
    };
    if (!omitOrigin) request.Headers.TryAddWithoutValidation("Origin",
        origin ?? browser.BaseAddress!.GetLeftPart(UriPartial.Authority));
    using var response = await browser.SendAsync(request);
    return ((int)response.StatusCode, response.Headers.Location?.OriginalString);
}

static async Task WaitUntil(Func<Task<bool>> completed, string scenario)
{
    var time = System.Diagnostics.Stopwatch.StartNew();
    while (time.Elapsed < TimeSpan.FromSeconds(5))
    {
        if (await completed()) return;
        await Task.Delay(25);
    }
    throw new InvalidOperationException($"Timed out: {scenario}.");
}

sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private long ticks = start.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
}

sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}

sealed class LargeStream(int remaining) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(remaining, count);
        Array.Clear(buffer, offset, n);
        remaining -= n;
        return n;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
