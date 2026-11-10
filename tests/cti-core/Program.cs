using System.Net;
using System.Text.RegularExpressions;
using CtiCore;

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

static async Task<(int Status, string? Redirect)> Submit(HttpClient browser, string token,
    string source = "microsoft-security", string? origin = null, bool omitOrigin = false)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/refresh")
    {
        Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["source"] = source, ["__RequestVerificationToken"] = token })
    };
    if (!omitOrigin) request.Headers.TryAddWithoutValidation("Origin",
        origin ?? browser.BaseAddress!.GetLeftPart(UriPartial.Authority));
    using var response = await browser.SendAsync(request);
    return ((int)response.StatusCode, response.Headers.Location?.OriginalString);
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
