# CTI — public RSS-first preview

The current public CTI is a lightweight, local news reader. It stores reviewed
security feeds in SQLite and runs without Docker, n8n or PostgreSQL. This
development version is on the **public-core/rss-first** branch.

## Try the Windows preview

Download the matching **CTI-Core-Windows-x64-preview.zip** and its SHA-256 file.
The tested package is attached to a successful Windows run under
[Actions → Validate CTI Core preview](https://github.com/emecyildiz/CTI/actions/workflows/cti-core-validation.yml?query=branch%3Apublic-core%2Frss-first),
in the **Artifacts** section. GitHub may require sign-in to download it.
Artifacts are development downloads, expire after 14 days, and are not Releases.
Extract the ZIP, then double-click **START-CTI.cmd**. The panel opens in your
browser. No installer, separate .NET runtime or administrator access is required.
Stop the app with Ctrl+C in its console.

The preview is unsigned. Verify the checksum before running it; it is not a
stable release. News and settings stay under
`%LOCALAPPDATA%\Emecworks\CTI-Core` when the app is closed or the ZIP is replaced.
Do not delete that data directory just to update the executable.

## Included

- Five reviewed security sources, manual collection and opt-in automatic checks.
- Persistent source preferences; disabling a source does not delete its history.
- Headline search, source filters and numbered pagination, at most 12 news items
  per page. A new search or source filter starts at the first page.
- A responsive sidebar interface with local appearance settings: change or hide
  the subtitle below CTI, or restore the default **Emecworks**.
- Optional, user-triggered Turkish Gemini summaries of the saved RSS excerpt.
  AI is off unless you explicitly supply an API key.

This preview binds only to this computer's loopback address. It has no multi-user
authentication and must not be exposed to the public Internet. It does not read,
upgrade or remove an old n8n-based installation or connect to the private VPS.

See [Core usage, build and test instructions](app/cti-core/README.md).

## Older n8n-based edition

The original installer, Compose files, workflows and recovery tools are retained
for existing releases. They are not dependencies of the new Core executable and
are not bundled in its Windows preview ZIP. Do not use `setup.cmd` or the old
`CTI-Setup-*.exe` to launch Core.

- [Legacy installation documentation](LEGACY-N8N.md)
- [Repository layout and conservative cleanup review](REPOSITORY-LAYOUT.md)

The root `VERSION`, old release workflow and historical release notes still
describe the legacy edition. They are not Core's release version.
