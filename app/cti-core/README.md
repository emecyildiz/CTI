# CTI — RSS first-use preview

This is a separate public CTI development preview. It does not install,
migrate, or read an rc.10 installation and does not use the private VPS.

From the repository root, with the .NET 8 SDK or later and the .NET 8
ASP.NET Core runtime installed:

```sh
dotnet run --project app/cti-core -- --port 8091
```

Open **http://127.0.0.1:8091** and click **Haberleri getir** on a source.
Microsoft Security Blog is initially enabled for manual collection.
BleepingComputer (security only), Krebs on Security, The Hacker News,
and SecurityWeek start disabled. Open a row under **Haber kaynakları**
to see its collection and settings buttons. Each row shows whether the
source is enabled, its automatic mode, and the latest attempt result.
Automatic collection is off until explicitly enabled for a source.
The screen shows saved headlines and, where available, a short
publisher-provided RSS description (up to 360 characters). It does not
generate or translate summaries. Another click does not duplicate the same article, and restarting
the process keeps what was saved. If the feed is unavailable, existing
articles remain visible.

Under **Haber kaynakları**, choose **Otomatik toplamayı aç** on a source
and save to check that feed about once an hour while CTI is running. The first check runs on
the next minute tick unless a manual or automatic attempt already finished
in the past hour. A failed attempt also waits about an hour before an
automatic retry; manual refresh remains available. The page shows the
last attempt, its result, and the last success separately for each source
(UTC). One source's failure does not overwrite another source's status.
The scheduler runs at most two automatic requests at once, and still checks
the other due sources. An error does not erase a previous success.

Unchecking **Bu kaynaktan haber topla** and saving stops new automatic
and manual checks for that source. If a request was already sent, it may
finish, but its result is not saved if the setting changed in flight.
Already saved articles are not removed. Source and scheduling preferences
persist across restarts. Run one CTI process per data directory.

Under **Son haberler**, choose **Tümü** or a specific source and optionally
enter text in **Haber başlığında ara**. Search ignores case and accents and
matches text in locally saved titles only. Use **Önceki** and **Sonraki** to
browse 100 results at a time. Searching and filtering do not contact
publishers or change which sources collect. Source actions retain the
current filter, search and page; a new search or source filter starts at the
first page. Disabled sources still show their previously saved news.

Data defaults to `Emecworks/CTI-Core` under the current user's local
application data directory. Pass `--data-dir PATH` to use another directory.
This preview listens on IPv4 loopback only; on a server, use a private SSH
forward to access it. Do not expose it through a reverse proxy: there is no
multi-user authentication in this package. It makes no AI or Telegram calls
and requires neither Docker, n8n nor a separate database service.

The publisher URLs and allowed article paths are fixed by the reviewed
catalog. The collector does not follow redirects or fetch article pages.
Only `/news/security/` stories from the BleepingComputer news feed are kept.
The Hacker News feed is fetched from its reviewed FeedBurner URL, but only
valid `thehackernews.com` article URLs are accepted as links.
It limits response size, time and item count, forbids XML DTDs, and stores
canonical links, titles, sources, dates and optional short excerpts. The
description HTML is parsed locally with no browser/resource loader; markup
and external assets are not stored or rendered. Existing rows get an excerpt
only if the same article reappears in a normal collection; old titles and
dates are left unchanged. The panel displays at most 100 results per page.

## Verification

```sh
dotnet run --project tests/cti-core --configuration Release
# Optional: one live request per catalog source, using temporary data.
dotnet run --project tests/cti-core --configuration Release -- --live-feed
```

The first security catalog has five reviewed sources. AI summaries and
Telegram stay optional; additional topic catalogs, installer and
authenticated server packaging are later work.
