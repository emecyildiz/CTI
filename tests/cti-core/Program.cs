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
const string otherNews = """
    <item><title>Non-security story</title>
      <link>https://www.bleepingcomputer.com/news/technology/non-security/</link></item>
    """;
const string securityNews = """
    <item><title>BleepingComputer security story</title>
      <link>https://www.bleepingcomputer.com/news/security/safe-story/?utm_source=rss</link></item>
    <item><title>Not a BleepingComputer story</title>
      <link>https://www.microsoft.com/en-us/security/blog/cross-source/</link></item>
    <item><title>Encoded separator</title>
      <link>https://www.bleepingcomputer.com/news/security/story%2felsewhere/</link></item>
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
        Require(Regex.Matches(page, "<details class=\"source-card\"").Count == 5
            && Regex.Matches(page, "<details class=\"source-card\" open>").Count == 1
            && page.Contains("5 kaynak · 1 açık")
            && page.Contains("id=\"label-the-hacker-news\"") && page.Contains("id=\"label-securityweek\""),
            "The initial catalog shows all five sources compactly with only the default source expanded.");
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
        Require(await Submit(browser, token) == Redirect("1"), "One unique article is added.");
        Require(await Submit(browser, token) == Redirect("0"), "Refetch never duplicates an article.");
        page = await browser.GetStringAsync("/");
        Require(page.Contains("&lt;script&gt;not code&lt;/script&gt;") && !page.Contains("<script>")
            && page.Contains("/2026/11/01/example/") && !page.Contains("utm_source") && !page.Contains("Off-catalog"),
            "Titles are escaped, URLs canonicalized, other hosts rejected.");
        foreach (var failure in new[] { "redirect", "DTD", "oversize", "unsupported" })
        {
            mode = failure;
            Require(await Submit(browser, token) == Redirect("failed"), $"Feed failure is recoverable: {failure}.");
        }
        mode = "blocked";
        var first = Submit(browser, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { Require(await Submit(browser, token) == Redirect("busy"), "One fetch at a time."); }
        finally { release.TrySetResult(); }
        Require(await first == Redirect("0"), "The first fetch still completes.");
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
    Require(migrated.ReadSource(FeedSource.Microsoft) is { Enabled: true, Automatic: false, LastAttempt: null }
        && migrated.ReadSource(FeedSource.BleepingComputer) is { Enabled: false, Automatic: false, LastAttempt: null }
        && migrated.ReadSource(FeedSource.Krebs) is { Enabled: false, Automatic: false, LastAttempt: null }
        && migrated.ReadSource(FeedSource.HackerNews) is { Enabled: false, Automatic: false, LastAttempt: null }
        && migrated.ReadSource(FeedSource.SecurityWeek) is { Enabled: false, Automatic: false, LastAttempt: null }
        && migrated.Latest().Single().Title == "Saved in the first preview",
        "Adding settings to an existing preview database preserves its articles and never opts in.");
    Console.WriteLine("PASS: first-preview SQLite upgrade preserves articles and starts in manual mode.");

    const string legacyMarkup = """
        <p>Patch&nbsp;<strong>now</strong> &amp; check.</p><p>More &lt;notes&gt;.</p>
        <script>alert('not text')</script><style>.hidden{color:red}</style>
        <iframe src="https://example.test/tracker">hidden frame</iframe><img src="https://example.test/pixel" onerror="alert(1)">
        <p hidden>secret text</p><p aria-hidden="true">hidden badge</p>
        """;
    var longMarkup = string.Concat(Enumerable.Repeat("🙂", FeedExcerpt.MaximumMarkup));
    int excerptPass = 0;
    using var excerptFeed = new HttpClient(new Stub((request, _) =>
    {
        Require(request.RequestUri == FeedSource.Microsoft.FeedUri, "Only the RSS feed is requested, not article or asset URLs.");
        var first = Interlocked.Increment(ref excerptPass) == 1;
        var description = first ? legacyMarkup : "<p>The publisher changed its excerpt.</p>";
        var missing = first ? "<template><p>not visible</p></template>" : "<p>Description became available later.</p>";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"""
                <rss><channel>
                <item><title>Changed publisher title</title><link>https://www.microsoft.com/en-us/security/blog/legacy/</link>
                  <description><![CDATA[{description}]]></description></item>
                <item><title>Long excerpt</title><link>https://www.microsoft.com/en-us/security/blog/long-excerpt/</link>
                  <description><![CDATA[{longMarkup}]]></description></item>
                <item><title>Without excerpt</title><link>https://www.microsoft.com/en-us/security/blog/no-excerpt/</link>
                  <description><![CDATA[{missing}]]></description></item>
                <item><title>Nested XML excerpt</title><link>https://www.microsoft.com/en-us/security/blog/nested-description/</link>
                  <description><p>Nested text</p><p>Other paragraph</p><script>not visible</script></description></item>
                </channel></rss>
                """)
        });
    }));
    await using (var app = CoreApp.Create(new CoreOptions(0, legacy.FullName), excerptFeed))
    {
        await app.StartAsync();
        using var browser = Browser(app.Urls.Single());
        var beforeExcerpt = await browser.GetStringAsync("/");
        var token = Token(beforeExcerpt);
        Require(migrated.Latest().Single().Excerpt is null && !beforeExcerpt.Contains("class=\"excerpt\""),
            "Existing records start with an optional, empty excerpt.");
        Require(await Submit(browser, token) == Redirect("3"), "Backfilling an existing article does not count as a new article.");
        var stored = migrated.Latest();
        var preserved = stored.Single(item => item.Url.EndsWith("/legacy/", StringComparison.Ordinal));
        var longText = stored.Single(item => item.Title == "Long excerpt").Excerpt;
        Require(preserved is { Title: "Saved in the first preview", Published: null, Excerpt: "Patch now & check. More <notes>." }
            && stored.Single(item => item.Title == "Nested XML excerpt").Excerpt == "Nested text Other paragraph"
            && stored.Single(item => item.Title == "Without excerpt").Excerpt is null
            && longText is { Length: <= FeedExcerpt.MaximumLength } && longText.EndsWith('…') && !longText.Contains('\uFFFD'),
            "Descriptions become bounded readable text; existing article metadata and Unicode text are preserved.");
        var page = await browser.GetStringAsync("/");
        Require(page.Contains("class=\"excerpt-note\"") && page.Contains("Patch now &amp; check. More &lt;notes&gt;.")
            && !page.Contains("<script>") && !page.Contains("<img") && !page.Contains("<iframe")
            && !page.Contains("example.test") && !page.Contains("secret text") && !page.Contains("hidden badge"),
            "The preview contains only escaped visible text, with no external assets or hidden markup.");
        Require(await Submit(browser, token) == Redirect("0") && excerptPass == 2, "A second fetch preserves the article count.");
        var reread = new ArticleStore(legacy.FullName).Latest();
        Require(reread.Count == 4 && reread.Single(item => item.Url == preserved.Url).Excerpt == preserved.Excerpt
            && reread.Single(item => item.Title == "Without excerpt").Excerpt == "Description became available later.",
            "Refresh fills missing excerpts without replacing ones already saved.");
        await app.StopAsync();
    }
    Console.WriteLine("PASS: RSS excerpt cleanup, limits, escaped rendering, old SQLite migration, and duplicate-safe backfill.");

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
            && !scheduleStore.ReadSource(FeedSource.Microsoft).Automatic, "Settings rejects cross-origin, missing token, and unsupported sources.");
        Require((await Submit(browser, token, path: "/settings/source", fields: [new("enabled", "on"), new("enabled", "off")])).Status == 400,
            "Ambiguous checkbox values are rejected.");
        Require(await Settings(browser, token, enabled: true, automatic: true) == Redirect("saved"), "Automatic collection opt-in saves.");
        await WaitUntil(() => Task.FromResult(scheduleStore.ReadSource(FeedSource.Microsoft).LastSuccess == clock.GetUtcNow()), "First automatic attempt");
        var success = scheduleStore.ReadSource(FeedSource.Microsoft);
        page = await browser.GetStringAsync("/");
        Require(success is { LastAttemptSucceeded: true, LastAdded: 1 } && scheduleStore.Latest().Count == 1
            && page.Contains("Son başarılı toplama") && page.Contains("10.11.2026 12:00 UTC") && requested == 1,
            "Automatic collection stores articles, result and displayed timestamp.");

        clock.Advance(TimeSpan.FromMinutes(59));
        await Task.Delay(poll * 4);
        Require(requested == 1, "Automatic collection does not run before the interval.");
        Volatile.Write(ref scheduleMode, 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntil(() => Task.FromResult(scheduleStore.ReadSource(FeedSource.Microsoft).LastAttemptSucceeded == false), "Scheduled failure");
        var failure = scheduleStore.ReadSource(FeedSource.Microsoft);
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
        Require(requested == 2 && scheduleStore.ReadSource(FeedSource.Microsoft) is { Automatic: true, LastAttemptSucceeded: false },
            "Schedule and failed-attempt cooldown survive restart.");
        Volatile.Write(ref scheduleMode, 2);
        clock.Advance(TimeSpan.FromHours(1));
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disabledAt = scheduleStore.ReadSource(FeedSource.Microsoft).LastAttempt;
        try
        {
            Require(await Settings(browser, token, enabled: false, automatic: true) == Redirect("saved"),
                "Turning off the source also turns off automatic collection.");
        }
        finally { allowCompletion.TrySetResult(); }
        await WaitUntil(async () => await Submit(browser, token) == Redirect("disabled"), "In-flight request released");
        Require(scheduleStore.ReadSource(FeedSource.Microsoft) is { Enabled: false, Automatic: false } disabled
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
        Require(await Settings(browser, token, enabled: true, automatic: false) == Redirect("saved")
            && await Submit(browser, token) == Redirect("0"), "Manual collection works with automatic collection off.");
        await app.StopAsync();
    }
    Console.WriteLine("PASS: opt-in scheduler, hourly interval, persisted status and settings, and in-flight disable protection.");

    var ms = FeedSource.Microsoft;
    var bc = FeedSource.BleepingComputer;
    var catalogOptions = new CoreOptions(0, Path.Combine(temp.FullName, "catalog"));
    var catalogStore = new ArticleStore(catalogOptions.DataDirectory);
    var catalogClock = new TestClock(new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero));
    var msStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var completeMs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int msRequests = 0, bcRequests = 0, otherOnly = 0;
    using var catalogFeed = new HttpClient(new Stub(async (request, ct) =>
    {
        if (request.RequestUri == ms.FeedUri)
        {
            Interlocked.Increment(ref msRequests);
            msStarted.TrySetResult();
            await completeMs.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        Require(request.RequestUri == bc.FeedUri, "Only a catalog feed can be requested.");
        Interlocked.Increment(ref bcRequests);
        var items = otherNews + (Volatile.Read(ref otherOnly) == 1 ? "" : securityNews);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<rss><channel>" + items + "</channel></rss>") };
    }));
    await using (var app = CoreApp.Create(catalogOptions, catalogFeed, catalogClock, poll))
    {
        await app.StartAsync();
        using var browser = Browser(app.Urls.Single());
        var token = Token(await browser.GetStringAsync("/"));
        Require(await Submit(browser, token, source: bc.Id) == Redirect("disabled", bc.Id) && bcRequests == 0,
            "The new source starts disabled and makes no request.");
        Require(await Settings(browser, token, enabled: true, automatic: true) == Redirect("saved"), "Microsoft scheduling is enabled.");
        await msStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Require(await Settings(browser, token, enabled: true, automatic: true, source: bc.Id) == Redirect("saved", bc.Id),
                "Scheduling is configured for the second source independently.");
            // Manual collection must work even when the other publisher has a request in flight.
            Require(await Submit(browser, token, source: bc.Id) == Redirect("1", bc.Id), "The other source can collect independently.");
            Require(catalogStore.ReadSource(ms).LastAttempt is null && catalogStore.ReadSource(bc) is { LastAdded: 1, LastAttemptSucceeded: true },
                "Collection status updates only the source that finished.");
        }
        finally { completeMs.TrySetResult(); }
        await WaitUntil(() => Task.FromResult(catalogStore.ReadSource(ms).LastAttemptSucceeded == false), "Microsoft failure is persisted");
        Require(catalogStore.ReadSource(bc).LastAttemptSucceeded == true && bcRequests == 1 && msRequests == 1,
            "A failure from one publisher does not overwrite another publisher's success.");
        var page = await browser.GetStringAsync(Redirect("1", bc.Id).Redirect);
        var msCard = page.Split("id=\"source-microsoft-security\"", 2)[1].Split("</article>", 2)[0];
        var bcCard = page.Split("id=\"source-bleepingcomputer-security\"", 2)[1].Split("</article>", 2)[0];
        Require(!msCard.Contains("role=\"status\"") && bcCard.Contains("role=\"status\"")
            && page.Contains("/news/security/safe-story/") && !page.Contains("/news/technology/non-security/")
            && !page.Contains("Not a BleepingComputer story") && !page.Contains("Encoded separator"),
            "Feedback is attached to the chosen source and only eligible security stories are linked.");
        Volatile.Write(ref otherOnly, 1);
        Require(await Submit(browser, token, source: bc.Id) == Redirect("0", bc.Id)
            && catalogStore.ReadSource(bc) is { LastAdded: 0, LastAttemptSucceeded: true },
            "A feed with only another category is valid but contributes no security articles.");
        catalogClock.Advance(TimeSpan.FromHours(1));
        await WaitUntil(() => Task.FromResult(catalogStore.ReadSource(ms).LastAttempt == catalogClock.GetUtcNow()
            && catalogStore.ReadSource(bc).LastAttempt == catalogClock.GetUtcNow()), "Both sources are scheduled independently");
        Require(msRequests == 2 && bcRequests == 3 && catalogStore.ReadSource(bc).LastAttemptSucceeded == true,
            "Both due sources are attempted even when one fails.");
        Require(await Settings(browser, token, enabled: false, automatic: true, source: bc.Id) == Redirect("saved", bc.Id),
            "Turning off BleepingComputer does not require touching Microsoft.");
        await app.StopAsync();
    }
    var catalogReopened = new ArticleStore(catalogOptions.DataDirectory);
    Require(catalogReopened.ReadSource(ms) is { Enabled: true, Automatic: true, LastAttemptSucceeded: false }
        && catalogReopened.ReadSource(bc) is { Enabled: false, Automatic: false, LastAttemptSucceeded: true }
        && catalogReopened.Latest().Single().Source == bc.Name,
        "Each source preserves its own settings, history, and saved articles.");
    Console.WriteLine("PASS: multi-source catalog, independent settings and fetches, isolated failure, and security-category filtering.");

    var krebs = FeedSource.Krebs;
    var filterOptions = new CoreOptions(0, Path.Combine(temp.FullName, "filters"));
    var filterStore = new ArticleStore(filterOptions.DataDirectory);
    var day = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    var recent = Enumerable.Range(1, 101).Select(i => new Article(
        $"https://www.microsoft.com/en-us/security/blog/recent-{i:000}/", ms.Name, $"Recent Microsoft {i:000}", day)).ToArray();
    Require(filterStore.RecordCollection(ms, filterStore.ReadSource(ms).Revision, false, day, recent[..100]) == 100
        && filterStore.RecordCollection(ms, filterStore.ReadSource(ms).Revision, false, day, recent[100..]) == 1,
        "The combined list fixture contains more than 100 recent articles.");
    const string krebsFeed = """
        <rss version="2.0"><channel>
        <item><title>Older Krebs investigation</title>
          <link>https://krebsonsecurity.com/2025/01/fixture-story/?utm_source=rss</link>
          <pubDate>Wed, 01 Jan 2025 00:00:00 +0000</pubDate></item>
        <item><title>Repeated story</title><link>https://krebsonsecurity.com/2025/01/fixture-story#duplicate</link></item>
        <item><title>Not a permalink</title><link>https://krebsonsecurity.com/feed/</link></item>
        <item><title>Invalid month</title><link>https://krebsonsecurity.com/2025/13/not-a-story/</link></item>
        <item><title>Other origin</title><link>https://sub.krebsonsecurity.com/2025/01/elsewhere/</link></item>
        </channel></rss>
        """;
    int krebsRequests = 0;
    using var filterFeed = new HttpClient(new Stub((request, _) =>
    {
        Require(request.RequestUri == krebs.FeedUri, "The article filter never selects an outbound feed.");
        Interlocked.Increment(ref krebsRequests);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(krebsFeed) });
    }));
    await using (var app = CoreApp.Create(filterOptions, filterFeed))
    {
        await app.StartAsync();
        using var browser = Browser(app.Urls.Single());
        var filterUrl = "/?filter=" + krebs.Id;
        var filteredEmpty = await browser.GetStringAsync(filterUrl);
        var token = Token(filteredEmpty);
        Require(filteredEmpty.Contains("Bu kaynaktan henüz haber kaydedilmedi.") && krebsRequests == 0,
            "Filtering a disabled source is read-only.");
        using var bad = await browser.GetAsync("/?filter=unknown");
        using var repeated = await browser.GetAsync(filterUrl + "&filter=" + ms.Id);
        Require(bad.StatusCode == HttpStatusCode.BadRequest && repeated.StatusCode == HttpStatusCode.BadRequest
            && (await Submit(browser, token, source: krebs.Id, fields: [new("filter", "https://example.test/feed")])).Status == 400
            && krebsRequests == 0, "Unknown and duplicated filter IDs do not trigger reads from any publisher.");
        Require(await Settings(browser, token, enabled: true, automatic: false, source: krebs.Id, filter: krebs.Id)
            == Redirect("saved", krebs.Id, krebs.Id), "Settings keeps the selected filter.");
        var result = await Submit(browser, token, source: krebs.Id, fields: [new("filter", krebs.Id)]);
        Require(result == Redirect("1", krebs.Id, krebs.Id) && krebsRequests == 1, "Collection keeps the selected filter.");
        var page = await browser.GetStringAsync(result.Redirect);
        var all = await browser.GetStringAsync("/");
        Require(page.Contains($"/?filter={krebs.Id}#articles-heading\" aria-current=\"page\"")
            && page.Contains($"name=\"filter\" value=\"{krebs.Id}\"")
            && page.Split($"id=\"source-{krebs.Id}\"", 2)[1].Split("</article>", 2)[0]
                .Contains("<details class=\"source-card\" open>")
            && page.Contains("Older Krebs investigation") && page.Contains("https://krebsonsecurity.com/2025/01/fixture-story/")
            && !page.Contains("Recent Microsoft") && !page.Contains("Repeated story") && !page.Contains("Invalid month")
            && !page.Contains("Other origin") && !page.Contains("Not a permalink"),
            "The selected source renders only its own valid articles and retains navigation state.");
        Require(filterStore.Latest().Count == 100 && all.Contains("Recent Microsoft") && !all.Contains("Older Krebs investigation")
            && filterStore.Latest(krebs).Single().Title == "Older Krebs investigation",
            "SQLite filters before the 100-row limit, so newer articles from other sources cannot hide the selected source.");
        Require(await Settings(browser, token, enabled: false, automatic: false, source: krebs.Id, filter: krebs.Id)
            == Redirect("saved", krebs.Id, krebs.Id) && (await browser.GetStringAsync(filterUrl)).Contains("Older Krebs investigation")
            && krebsRequests == 1, "Past records remain visible after a source is disabled.");
        var second = await browser.GetStringAsync("/?page=2");
        Require(all.Contains("/?page=2#articles-heading\" rel=\"next\"") && !all.Contains("Recent Microsoft 101")
            && second.Contains("Recent Microsoft 101") && second.Contains("Older Krebs investigation")
            && second.Contains("Sayfa 2") && second.Contains("rel=\"prev\"") && !second.Contains("rel=\"next\"")
            && !second.Contains("Recent Microsoft 100"),
            "The second page shows older records, with no duplicates at the boundary.");

        var found = WebUtility.HtmlDecode(await browser.GetStringAsync("/?filter=" + ms.Id + "&q=rEcEnT%20MiCrOsOfT"));
        var foundMore = await browser.GetStringAsync("/?filter=" + ms.Id + "&q=rEcEnT%20MiCrOsOfT&page=2");
        Require(found.Contains($"/?filter={ms.Id}&q=rEcEnT%20MiCrOsOfT&page=2#articles-heading\" rel=\"next\"")
            && foundMore.Contains("Recent Microsoft 101") && !foundMore.Contains("Older Krebs investigation")
            && !foundMore.Contains("rel=\"next\""),
            "Search is case-insensitive, combines with a source filter, and carries both parameters across pages.");
        Require(filterStore.RecordCollection(ms, filterStore.ReadSource(ms).Revision, false, day,
            [new("https://www.microsoft.com/en-us/security/blog/unicode/", ms.Name, "İSTANBUL Güvenlik %_ notu", day.AddYears(-2))]) == 1,
            "Add a headline with Turkish letters and literal wildcard characters.");
        string matched = WebUtility.HtmlDecode(await browser.GetStringAsync("/?q=istanbul%20guvenlik"));
        string literal = WebUtility.HtmlDecode(await browser.GetStringAsync("/?q=%25_"));
        Require(matched.Contains("İSTANBUL Güvenlik %_ notu") && literal.Contains("İSTANBUL Güvenlik %_ notu")
            && !matched.Contains("Recent Microsoft") && !literal.Contains("Recent Microsoft"),
            "Unicode folding finds Turkish titles while SQL wildcard characters remain literal.");
        var noMatch = await browser.GetStringAsync("/?q=%22%3E%3Cscript%3E");
        Require(noMatch.Contains("Aramana uyan haber bulunamadı.") && !noMatch.Contains("<script>")
            && noMatch.Contains("&lt;script&gt;"), "Search input is safely escaped and empty results are clear.");
        foreach (var url in new[] { "/?q=a&q=b", "/?q=%0Aexample", "/?q=" + new string('x', 121),
                     "/?page=0", "/?page=1001", "/?page=2&page=3" })
        {
            using var response = await browser.GetAsync(url);
            Require(response.StatusCode == HttpStatusCode.BadRequest, "Invalid search or page arguments are rejected.");
        }
        var savedWithSearch = await Submit(browser, token, source: krebs.Id, path: "/settings/source",
            fields: [new("filter", ms.Id), new("q", "recent Microsoft"), new("page", "2")]);
        Require(savedWithSearch == (303, $"/?source={krebs.Id}&status=saved&filter={ms.Id}&q=recent%20Microsoft&page=2#source-{krebs.Id}")
            && (await browser.GetStringAsync(savedWithSearch.Redirect)).Contains("Recent Microsoft 101") && krebsRequests == 1,
            "A source action preserves the search and page without starting a network request.");
        await app.StopAsync();
    }
    Console.WriteLine("PASS: source filter before list limit, catalog-only input, retained selection, disabled-source history, and Krebs permalinks.");
    Console.WriteLine("PASS: title search, Turkish characters, literal symbols, HTML escaping, pagination, and search context across settings.");

    var thn = FeedSource.HackerNews;
    var sw = FeedSource.SecurityWeek;
    Require(thn.CanonicalLink("https://thehackernews.com/2026/11/security-news.html/?utm=1#example")
        == "https://thehackernews.com/2026/11/security-news.html"
        && thn.CanonicalLink("https://feeds.feedburner.com/2026/11/security-news.html") is null
        && thn.CanonicalLink("https://thehackernews.com/2026/11/not-an-article/") is null
        && sw.CanonicalLink("https://www.securityweek.com/security-news?utm=1") == "https://www.securityweek.com/security-news/"
        && sw.CanonicalLink("https://www.securityweek.com/feed/") is null
        && sw.CanonicalLink("https://www.securityweek.com/category/security-news/") is null,
        "Feed and article hosts may differ, and each publisher keeps its own canonical link shape.");

    var waveOptions = new CoreOptions(0, Path.Combine(temp.FullName, "scheduler-wave"));
    var waveStore = new ArticleStore(waveOptions.DataDirectory);
    foreach (var source in FeedSource.Catalog)
        waveStore.SetCollectionPolicy(source, enabled: true, automatic: true);
    var readyWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int waveRequests = 0, inFlightWave = 0, excess = 0;
    using var waveFeed = new HttpClient(new Stub(async (_, ct) =>
    {
        int running = Interlocked.Increment(ref inFlightWave);
        if (running > 2) Interlocked.Increment(ref excess);
        if (Interlocked.Increment(ref waveRequests) >= 2) readyWave.TrySetResult();
        try { await releaseWave.Task.WaitAsync(ct); }
        finally { Interlocked.Decrement(ref inFlightWave); }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<rss><channel/></rss>") };
    }));
    await using (var app = CoreApp.Create(waveOptions, waveFeed, clock, poll))
    {
        await app.StartAsync();
        try
        {
            await readyWave.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(poll * 3);
            Require(waveRequests == 2, "Two in-flight requests hold the remaining automatic publishers in the queue.");
        }
        finally { releaseWave.TrySetResult(); }
        await WaitUntil(() => Task.FromResult(FeedSource.Catalog.All(source => waveStore.ReadSource(source).LastAttemptSucceeded == true)),
            "Every due source in the five-source catalog is eventually checked");
        Require(waveRequests == 5 && excess == 0, "A five-source automatic wave stays within the two-request limit.");
        await app.StopAsync();
    }
    Console.WriteLine("PASS: compact five-source catalog, independent publisher link formats, and bounded automatic concurrency.");

    await SummaryTests.Run(temp.FullName);

    if (args.SequenceEqual(new[] { "--live-feed" }))
    {
        using var publisher = new HttpClient(FeedClient.CreateTransport());
        await using var live = CoreApp.Create(new CoreOptions(0, Path.Combine(temp.FullName, "live")), publisher);
        await live.StartAsync();
        using var browser = Browser(live.Urls.Single());
        var token = Token(await browser.GetStringAsync("/"));
        var store = new ArticleStore(Path.Combine(temp.FullName, "live"));
        int totalExcerpts = 0;
        foreach (var source in FeedSource.Catalog)
        {
            if (!source.EnabledByDefault)
                Require(await Settings(browser, token, enabled: true, automatic: false, source: source.Id) == Redirect("saved", source.Id),
                    "Opt-in to the live publisher.");
            var result = await Submit(browser, token, source: source.Id);
            var articles = store.Latest(source);
            var n = articles.Count;
            var excerpts = articles.Count(article => article.Excerpt is not null);
            totalExcerpts += excerpts;
            Require(result == Redirect(n.ToString(System.Globalization.CultureInfo.InvariantCulture), source.Id) && n > 0,
                "The real publisher feed loads and stores articles: " + source.Id);
            Require(articles.All(article => article.Excerpt is null || article.Excerpt.Length <= FeedExcerpt.MaximumLength),
                "Every live excerpt satisfies the display limit.");
            Console.WriteLine($"PASS: {source.Id} RSS through the actual preview; {n} news records, {excerpts} with a publisher excerpt.");
        }
        Require(totalExcerpts > 0, "At least one publisher supplied an excerpt in its RSS feed.");
        await live.StopAsync();
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

static (int Status, string Redirect) Redirect(string status, string source = "microsoft-security", string? filter = null) =>
    (303, $"/?source={source}&status={status}{(filter is null ? "" : "&filter=" + filter)}#source-{source}");

static Task<(int Status, string? Redirect)> Settings(HttpClient browser, string token, bool enabled, bool automatic,
    string? origin = null, string source = "microsoft-security", string? filter = null)
{
    var fields = new List<KeyValuePair<string, string>>();
    if (enabled) fields.Add(new("enabled", "on"));
    if (automatic) fields.Add(new("automatic", "on"));
    if (filter is not null) fields.Add(new("filter", filter));
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
