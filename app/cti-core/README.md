# CTI — RSS first-use preview

This is a separate public CTI development preview. It does not install,
migrate, or read an rc.10 installation and does not use the private VPS.

From the repository root, with the .NET 8 SDK or later and the .NET 8
ASP.NET Core runtime installed:

```sh
dotnet run --project app/cti-core -- --port 8091
```

Open **http://127.0.0.1:8091** and click **Haberleri getir**. The first
click reads the Microsoft Security Blog feed; startup itself does not
contact the publisher. The screen shows saved articles, not generated
summaries. Another click does not duplicate the same article, and restarting
the process keeps what was saved. If the feed is unavailable, existing
articles remain visible.

Data defaults to `Emecworks/CTI-Core` under the current user's local
application data directory. Pass `--data-dir PATH` to use another directory.
This preview listens on IPv4 loopback only; on a server, use a private SSH
forward to access it. Do not expose it through a reverse proxy: there is no
multi-user authentication in this package. It makes no AI or Telegram calls
and requires neither Docker, n8n nor a separate database service.

The publisher URL and allowed article origin are fixed by the reviewed
catalog. The collector does not follow redirects or fetch article pages.
It limits response size, time and item count, forbids XML DTDs, and stores
only canonical links, titles, sources and dates. Titles are always rendered
as text. The panel limits its display to the 100 newest saved articles.

## Verification

```sh
dotnet run --project tests/cti-core --configuration Release
# Optional: exactly one live request to the catalog feed, using temporary data.
dotnet run --project tests/cti-core --configuration Release -- --live-feed
```

Next increment: add opt-in periodic collection, persist enabled sources and
show last success/failure in plain language. Expand the reviewed catalog
only after the first end-to-end path is stable. AI summaries and Telegram
stay optional; installer and authenticated server packaging are later work.
