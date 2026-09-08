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
