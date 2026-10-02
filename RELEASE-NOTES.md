# 0.1.0-rc.10 — Setup recovery and guided configuration

- Unreleased Windows source: fresh managed database bootstrap, keyless **Connect AI**, **Connect Telegram (optional)** and **Check setup** dialogs. Trusted embedded ownership/pinned-image checks precede local CLI access; inactive reviewed drafts and unrelated data are preserved. No automatic activation, provider calls, or Telegram messages.
- Add a read-only encrypted-export preparation audit with selected optional connections, all eight reviewed graphs, database/error/AI/private-Telegram binding checks, shared exclusive lock and consistent-snapshot refusal. Preparation is not presented as a live connection or public-webhook test.
- Align the browser wizard with the easy Windows actions: optional AI, source selection in the browser, connections/checks in the setup app, and advanced manual fallbacks. Keep provider metadata advanced and make clear that it does not reconfigure the actual model node. A separate guarded capability record avoids confusing bootstrap history with current Windows UI support.
- Improve native connection dialogs with visible API-key labels, explicit keyboard order/mnemonics and accessible field/action descriptions. Optional AI/Telegram preparation checks default to unselected; database-only setup does not accidentally require AI. Add Windows-only 100/150/200% component/font layout tests and a backend-free preview tool. Physical monitor DPI and full installer integration remain separate acceptance work.
- Tighten trailing-newline input rejection and published-version safety checks. Native dialog/DPI and released-EXE acceptance remain pending; published rc.9 is unchanged.
- Fix the setup Referrer-Policy / native form Origin conflict while preserving mandatory Origin, CSRF and local Host checks. Rejected setup requests now explain that no changes were saved and how to retry safely.
- Fresh Windows installations offer Continue setup on the selected port. Repair/update keeps Open dashboard and also provides Open setup. PowerShell prints the setup URL separately.
- Add a next-step guide and section links. Saved AI profiles and source selections are not presented as verified credentials or activated workflows. Non-bundled AI providers still require a compatible workflow adapter.
- Replace misleading ready/passed labels with observed connection and configuration states.
- Add repeatable isolated real-PostgreSQL setup-record acceptance (PowerShell 7 and local Docker Desktop). No real provider credentials or workflow activation are needed.

Validation: dashboard boundary/guide tests, 52 installer assertions, Windows mock setup flow and warning-free Windows build passed. Real PostgreSQL acceptance confirmed AI/source persistence, rejected-request state preservation and unavailable-n8n HTTP 503. Native-browser form policy behavior was separately verified using an isolated fixture. These do not replace graphical installer acceptance or successful real n8n credential/readiness validation.

2 October source validation: 133 installer assertions (OS-privileged symlink case skipped), dashboard guide/boundary tests, PowerShell 5.1 mock setup and warning-free builds passed. Both managed-guided and manual real-PostgreSQL HTTP acceptance passed. Full isolated Windows setup confirmed schema 29, database bootstrap, the separate guided capability, eight disabled tasks without duplicates, reinstall preservation, Remove→setup data/key/port preservation and fixture cleanup. Browser AI/review layouts were visually checked on an isolated fixture. These checks do not replace native dialog/DPI, clean-machine or published-EXE acceptance, nor actual provider delivery tests.

Existing configuration/data preservation and update consent rules remain unchanged. The browser has a guided five-step setup flow; source selection and manual/external-n8n configuration remain separate from the new Windows actions. There is no automatic activation or new topic/source expansion in this candidate. The EXE remains unsigned; this is not stable-release approval.

# 0.1.0-rc.9 — Windows update and repair safety

- Installed/attempted version display with Install, Repair and Update actions.
- Numeric RC/stable version comparison and downgrade refusal before package overwrite/setup; pending updates retain a high-water version receipt.
- Default-No review of updates/repairs. Locally edited or untracked colliding files require explicit backup-and-replace consent. All replaced existing package files are backed up first; edits are not silently discarded or automatically merged.
- A fresh preflight invalidates stale confirmation plans after source, target or metadata edits.
- Successful custom installation folders appear in a local recent-folder list, without disk scanning or storing credentials.
- Existing .env, Docker data, n8n mode and stored workflows are retained. Stored n8n workflows are explicitly NOT automatically upgraded, merged or re-imported.
- Purge understands the pending-update receipt; package backups remain retained.
- Cross-platform headless update tests and Windows lifecycle regression coverage are included in CI.

Local Windows validation: 41 update assertions passed; symbolic-link creation was skipped because the host does not grant that privilege. Windows build is warning-free. Linux CI separately runs the link guard. These are isolated filesystem tests, not a real graphical upgrade, database rollback or clean-machine acceptance claim.

Use the new installer for these protections: previously downloaded rc.8 and older EXEs cannot be retroactively hardened. Backups cover package files, not database/n8n volumes. Failed updates are resumable with the same/newer package but are not automatically rolled back. The EXE remains unsigned and this remains a release candidate for user testing.

# 0.1.0-rc.8 — Windows ports and installation management

- Windows GUI and PowerShell dashboard/managed-n8n port selection with loopback-only bindings, numeric/distinct-port checks, availability checks and saved setting preservation.
- Unique resource names and directory/engine-bound ownership records for new Windows installations.
- Preview + confirmation for Stop, Remove services/keep data, and Purge owned data/package files. External n8n, Docker/WSL, images, backups and modified/unknown files remain untouched.
- Legacy ownership is not guessed. Source paths through junctions/symlinks are refused; deletion uses an allowlisted hash-checked package manifest, never recursive installation-folder deletion.
- Offline PowerShell tests cover setup flow, ownership/refusal paths, data preservation and partial cleanup failure. Windows installer compilation is checked in CI.

Synthetic real-Docker lifecycle acceptance passed on local Windows Docker Desktop on 7 September: custom loopback ports, stop/restart, remove/recreate with both volumes preserved, shared-resource refusal, and purge with a separate sentinel project/backup preserved. All fixture resources were removed. This uses Node/Alpine stand-ins, not PostgreSQL/n8n application acceptance.

Actual CTI setup acceptance also passed on 7 September: PostgreSQL schema 29, dashboard and n8n readiness on custom ports, eight disabled workflows without duplicates, repeat setup preserving keys/ports, and Remove followed by setup preserving real database data. The test purged its isolated Docker resources successfully. No AI calls or Telegram delivery were activated.

The development GUI was visually checked for layout, missing-Docker-engine blocking, external-n8n port disabling, and the three management choices. Full graphical installation/purge click-through, clean-machine installation, and Linux/macOS management parity remain pending. This is a release candidate for user testing, not a stable-release approval.

# 0.1.0-rc.7

Seventh release candidate: setup boundary hardening, verified n8n credential/error
routing, and recovery/startup reliability. This is release preparation, not a
stable-release approval.

## Changes since rc.6

- Local dashboard Host validation limits browser DNS-rebinding exposure.
- Setup scripts reject unsafe webhook URLs and preserve dotenv values safely;
  Windows preflight checks password separation and loopback bindings.
- Telegram query mapping validates the reviewed authorization code and graph.
- PostgreSQL mapping links seven error-workflow routes to the newly imported
  handler. Readiness and mapping readback reject stale or unsafe references.
- Unique backup names avoid same-second collisions; restore uses one database
  transaction to preserve the prior state when an archive fails partway through.
- PostgreSQL readiness waits for the final TCP server, not its temporary
  socket-only initialization server.
- CI covers application boundaries, Windows setup, isolated SQL/recovery, and
  real n8n 2.30.5 import/mapping with fake credentials and zero executions.
- Release archives, version labels and manifests are built from the same clean
  Git commit. No separate ZIP executable is required.

## Verification and remaining acceptance

Source CI for `4e16123` passed all three jobs on 6 September 2026, including
11 isolated SQL scenarios, backup/restore, and real n8n credential/error routing.
The release-preparation commit and generated artifacts require their own checks.
Clean-machine installation, versioned-archive recovery and real Telegram delivery
are still pending. No claim is made that those acceptance tests have passed.

The package remains self-hosted. Eight workflows are imported disabled; the
operator supplies credentials and chooses when to activate them. The bundled AI
nodes use Gemini; other providers require adapting those nodes. Incoming Telegram
queries require an operator-configured public HTTPS route; outbound delivery does
not. The installer does not create DNS, TLS or tunnel routes.

The six compatible sources remain The Hacker News, CISA, Microsoft Security Blog,
BleepingComputer, Cisco Talos and Krebs on Security. Dark Reading and SecurityWeek
remain compatibility-blocked. The Windows executable remains unsigned.

## Previous candidate verification record (rc.6)

Sixth release candidate makes interactive Telegram support an explicit, guarded opt-in instead of implying that a VPS installation alone is sufficient.

Verified on 3 September 2026 with:

- PostgreSQL 16 Alpine using the pinned image digest;
- n8n 2.30.5 using the pinned image digest;
- ASP.NET Core 8 dashboard;
- clean Docker Compose installation and schema version 29;
- eight sanitized workflows imported into an empty n8n data store in disabled state;
- custom-format backup, SHA-256 verification, safety backup, and full restore;
- Windows and POSIX guided setup with prerequisite detection, secret generation, managed n8n deployment, and duplicate-safe workflow import;
- guarded creation and mapping of operator-owned PostgreSQL, provider-selectable AI, and optional Telegram credentials;
- a read-only activation-readiness audit using only `credential:list`, `workflow:list`, and `workflow:read` n8n API scopes;
- guarded reviewed-source selection that rejects arbitrary URLs, unknown sources, duplicate values, and an empty selection;
- release ZIP generation with required-file inspection, SHA-256 checksum, and JSON manifest;
- a single-file Windows x64 installer built from the exact tagged release ZIP;
- embedded-payload extraction and package-copy smoke testing from the compiled EXE;
- separate SHA-256 verification for the Windows installer;
- a Linux x86_64/ARM64 bootstrap that downloads and verifies the exact tagged release ZIP;
- interactive installation-directory and managed/existing-n8n choices plus non-interactive and prepare-only modes;
- update-safe `.env` preservation and remote-server SSH tunnel guidance;
- separate SHA-256 verification for the Linux installer.
- separate readiness choices for outbound Telegram reports/alerts and inbound Telegram menu/query handling;
- validated `WEBHOOK_URL` and bounded `N8N_PROXY_HOPS` configuration for managed n8n;
- Linux installer prompting and non-interactive flags for an existing public HTTPS webhook route;
- dashboard visibility for disabled, invalid, and configured Telegram query states;
- an activation-readiness failure when interactive queries are selected without the required HTTPS configuration.

The Linux bootstrap refuses unsupported operating systems and architectures, missing Docker/Compose access, unrelated non-empty destinations, checksum failures, unexpected archive layouts, and package-version mismatches. It never installs Docker silently or exposes CTI services to a public interface.

The installer does not create DNS, TLS, Cloudflare Tunnel, or reverse-proxy rules. Outbound Telegram delivery remains usable without a public route. Interactive Telegram queries remain disabled unless the operator explicitly supplies a non-local HTTPS webhook base and confirms that the route reaches n8n.

The Windows installer checks Docker Desktop, Docker Compose v2, and the Docker engine before enabling installation. It offers a managed n8n container or an existing n8n instance, refuses unrelated non-empty destination folders, preserves an existing `.env` during updates, streams setup progress, and links to the local dashboard after completion.

The protected setup dashboard now separates installation, reviewed source selection, credential mapping, and activation readiness. Source selection can enable any non-empty subset of the six compatible bundled sources without allowing changes to URLs, host allowlists, selectors, or trust scores. The write operation is available only to the dashboard database role; the n8n role cannot call it.

The six compatible sources are The Hacker News, CISA Cybersecurity Advisories, Microsoft Security Blog, BleepingComputer, Cisco Talos, and Krebs on Security. Dark Reading and SecurityWeek remain visible but compatibility-blocked because unattended article retrieval currently returns HTTP 403.

Workflows are never activated automatically. The operator must review the readiness results and deliberately enable the required workflows in n8n. Secrets are not included in the repository or release archive.

The Windows installer is not code-signed in this release. Windows SmartScreen may therefore display an unrecognized-app warning; verify the adjacent `.sha256` file before running it. The installer runs as the current user and does not request elevation.

The repository is licensed under `AGPL-3.0-only`, and the dashboard provides a visible link to the corresponding source code.
