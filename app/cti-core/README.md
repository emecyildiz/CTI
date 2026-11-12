# CTI — RSS first-use preview

This is a separate public CTI development preview. It does not install,
migrate, or read an rc.10 installation and does not use the private VPS.

From the repository root, with the .NET 10 SDK or later and the .NET 10
ASP.NET Core runtime installed:

```sh
dotnet run --project app/cti-core -- --port 8091
```

### Windows x64 preview ZIP

On Windows, build an unsigned, self-contained ZIP with the .NET 10 SDK:

```powershell
.\scripts\build-cti-core-preview.ps1
```

The script unpacks the result, starts the included executable against a
temporary data directory and checks the panel, embedded runtime and SQLite
before writing the ZIP and SHA-256 file to `dist/cti-core-preview`. To build
with a workspace-local SDK, pass its `dotnet.exe` using `-DotnetExe`.

To run the ZIP, extract it and double-click **START-CTI.cmd**. It opens the
loopback panel in the default browser after CTI starts. It requires no
installer, separate .NET runtime, Docker, or administrator access. Use
Ctrl+C in its console to stop. For a custom port, run
`CTI-Core.exe --port 8092 --open-browser` instead. No server-side deployment,
release signing or background Windows service is included.

### Using the panel

Open **http://127.0.0.1:8091** and click **Haberleri getir** on a source.
Microsoft Security Blog is initially enabled for manual collection.
BleepingComputer (security only), Krebs on Security, The Hacker News,
and SecurityWeek start disabled. Open a row under **Haber kaynakları**
to see its collection and settings buttons. Each row shows whether the
source is enabled, its automatic mode, and the latest attempt result.
Automatic collection is off until explicitly enabled for a source.
The screen shows saved headlines and, where available, a short
publisher-provided RSS description (up to 360 characters). This publisher
text is distinct from the optional AI summary below. Another click does not duplicate the same article, and restarting
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
multi-user authentication in this package. It requires neither Docker, n8n
nor a separate database service. It does not use Telegram.

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

## Optional Turkish summary with Gemini

By default there are no AI calls. To opt in, set `CTI_GEMINI_API_KEY` in the
app's environment and restart it. For example, from PowerShell 7:

```powershell
$env:CTI_GEMINI_API_KEY = Read-Host -MaskInput 'Gemini API key'
dotnet run --project app/cti-core -- --port 8091
Remove-Item Env:CTI_GEMINI_API_KEY
```

Use a [Gemini API key](https://ai.google.dev/gemini-api/docs/api-key) for
your own project. Do not put the key in the repository or share it. CTI
does not write the key to SQLite or send it to publishers. The default
text model is `gemini-3.5-flash-lite`; optionally set `CTI_GEMINI_MODEL`
to a [Gemini model ID](https://ai.google.dev/gemini-api/docs/models) that
supports text generation and is available to your project.

Click **Türkçe AI özeti oluştur** on a saved article with an RSS description.
Only that article's saved title and short RSS description are sent to
Google. CTI does not download the article, attach its URL, or send other
records. A request may count against your Gemini quota or incur charges.
There is no automatic AI processing, background batch, or retry. Completed
summaries are saved locally and reused; removing the key hides the controls
but keeps previously saved summaries. Summaries are based only on RSS and
are not verified technical analyses.

## Verification

```sh
dotnet run --project tests/cti-core --configuration Release
# Optional: one live request per catalog source, using temporary data.
dotnet run --project tests/cti-core --configuration Release -- --live-feed
```

The first security catalog has five reviewed sources. The default test
suite exercises Gemini with a mock HTTP provider and does not require a
key. Additional topic catalogs, Telegram, installer and authenticated
server packaging are later work.

On the development branch, **Validate CTI Core preview** runs these
fixture tests on Windows x64 and Ubuntu x64 when Core files change.
The Windows job also unpacks and starts a freshly built preview ZIP and
verifies its checksum. This workflow is independent of the original CTI
installer workflows; it does not contact live RSS or Gemini, upload a
binary, or create a GitHub Release. The interactive launcher and first-use
experience still need a person to test them.
