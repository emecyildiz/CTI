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
BleepingComputer (security only) and Krebs on Security start disabled;
enable their source cards to collect them. Automatic collection is off until explicitly enabled
for a source. The screen shows saved articles, not generated
summaries. Another click does not duplicate the same article, and restarting
the process keeps what was saved. If the feed is unavailable, existing
articles remain visible.

Under **Haber kaynakları**, choose **Otomatik toplamayı aç** on a source
and save to check that feed about once an hour while CTI is running. The first check runs on
the next minute tick unless a manual or automatic attempt already finished
in the past hour. A failed attempt also waits about an hour before an
automatic retry; manual refresh remains available. The page shows the
last attempt, its result, and the last success separately for each source
(UTC). A slow or failed source does not block the other source's collection
or overwrite its status. An error does not erase a previous success.

Unchecking **Bu kaynaktan haber topla** and saving stops new automatic
and manual checks for that source. If a request was already sent, it may
finish, but its result is not saved if the setting changed in flight.
Already saved articles are not removed. Source and scheduling preferences
persist across restarts. Run one CTI process per data directory.

Under **Son haberler**, choose **Tümü** or a specific source. This filters
saved news before applying the 100-item display limit; it does not contact
the publisher or change which sources collect. The selection stays active
when you refresh a source or save its settings. Disabled sources still show
their previously saved news.

Data defaults to `Emecworks/CTI-Core` under the current user's local
application data directory. Pass `--data-dir PATH` to use another directory.
This preview listens on IPv4 loopback only; on a server, use a private SSH
forward to access it. Do not expose it through a reverse proxy: there is no
multi-user authentication in this package. It makes no AI or Telegram calls
and requires neither Docker, n8n nor a separate database service.

The publisher URLs and allowed article paths are fixed by the reviewed
catalog. The collector does not follow redirects or fetch article pages.
Only `/news/security/` stories from the BleepingComputer news feed are kept.
It limits response size, time and item count, forbids XML DTDs, and stores
only canonical links, titles, sources and dates. Titles are always rendered
as text. The panel limits its display to the 100 newest saved articles.

## Verification

```sh
dotnet run --project tests/cti-core --configuration Release
# Optional: one live request per catalog source, using temporary data.
dotnet run --project tests/cti-core --configuration Release -- --live-feed
```

Next increment: make a larger catalog easy to browse and add the remaining
reviewed security sources. AI summaries and Telegram stay optional;
installer and authenticated server packaging are later work.
