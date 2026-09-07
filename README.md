# CTI Self-Hosted

CTI Self-Hosted collects reviewed cybersecurity feeds, validates article URLs and content, stores normalized records in PostgreSQL, and optionally uses an operator-selected AI API for bounded article analysis and weekly reporting. The dashboard, database, and n8n workflows remain on infrastructure controlled by the operator.

## Requirements

- Docker Engine with Docker Compose v2
- an AI API credential only when AI analysis/reporting will be enabled
- a Telegram bot only if Telegram delivery or query workflows will be used
- a stable public HTTPS route to n8n only for the interactive Telegram query workflow

The guided setup can deploy a local n8n container automatically. An existing n8n 2.x installation is also supported. The bundled workflow exports currently contain a Gemini model node, but the prompt and validated JSON contract are provider-independent; operators may replace that node with another AI provider.

## Guided installation from a release

The examples below target `0.1.0-rc.7`. If those assets are not yet listed in
GitHub Releases, use the latest published tag and its matching documentation;
the `main` branch can contain unreleased preparation work.

### Windows graphical installer

Download `CTI-Setup-<version>-win-x64.exe` and its `.sha256` file from GitHub Releases. Verify the checksum, then start the EXE. The graphical installer:

1. checks that Docker Desktop, Docker Compose v2, and the Docker engine are available;
2. lets the user choose a safe installation directory;
3. offers a managed local n8n container or an existing n8n instance;
4. extracts the matching, versioned CTI package embedded in the EXE;
5. runs the same reviewed `setup.ps1` installation path and streams its progress;
6. preserves an existing `.env` during an update and refuses an unrelated non-empty destination;
7. provides local dashboard and n8n links after a successful installation.

The installer runs as the current user and does not request administrator elevation. Docker Desktop may independently require privileges during its own installation. The EXE is currently unsigned, so Windows SmartScreen may show an unrecognized-app warning; verify the published SHA-256 checksum before running it.

### ZIP installation and non-Windows systems

On a Linux server, download the versioned terminal installer and its checksum from GitHub Releases:

```sh
version=0.1.0-rc.7
base="https://github.com/emecyildiz/CTI/releases/download/v$version"
curl -fsSLO "$base/CTI-Setup-$version-linux.sh"
curl -fsSLO "$base/CTI-Setup-$version-linux.sh.sha256"
sha256sum -c "CTI-Setup-$version-linux.sh.sha256"
chmod +x "CTI-Setup-$version-linux.sh"
./CTI-Setup-$version-linux.sh
```

The Linux installer supports x86_64 and ARM64 hosts. It checks Docker access, downloads the matching release ZIP, verifies its SHA-256 checksum, asks for the installation directory and n8n mode, preserves an existing `.env`, and then runs the reviewed `setup.sh` path. On a remote server it prints an SSH port-forward command so the dashboard and n8n can remain bound to loopback.

During an interactive managed-n8n installation, Telegram query support is presented as a separate opt-in feature. Leaving it disabled does not affect collection, the dashboard, or outbound Telegram reports and alerts. Enabling it requires the public HTTPS base URL that will route webhook traffic to n8n; the installer records this URL but does not create DNS, TLS, reverse-proxy, or tunnel configuration.

For a non-interactive managed-n8n installation using the default directory:

```sh
./CTI-Setup-0.1.0-rc.7-linux.sh --non-interactive
```

For a non-interactive server installation where a public HTTPS route already exists:

```sh
./CTI-Setup-0.1.0-rc.7-linux.sh \
  --non-interactive \
  --telegram-webhook-url https://hooks.example.com/
```

`--n8n-proxy-hops N` can override the default trusted reverse-proxy count of `1`. It must match the actual proxy path; do not increase it without understanding that path.

Use `--existing-n8n` to install only the database and dashboard. The installer never opens the dashboard or n8n directly to the public Internet.

Use `--prepare-only` to download, verify, and copy the package without starting any services. This allows the files to be reviewed before `setup.sh` is run manually.

Advanced offline or internal-mirror installations may set `CTI_RELEASE_BASE` to an HTTPS location or a local `file://` directory containing the matching ZIP and checksum. Plain HTTP mirrors are rejected.

Alternatively, download and extract the release ZIP. Do not run a setup script from inside the ZIP archive.

On Windows, start `setup.cmd`. On Linux or macOS, run:

```sh
chmod +x setup.sh scripts/*.sh
./setup.sh
```

The guided installer:

1. checks Docker and Docker Compose before changing anything;
2. explains where to install a missing prerequisite and stops safely;
3. generates separate local database, dashboard, and n8n secrets;
4. starts PostgreSQL, the dashboard, and an optional managed n8n container;
5. waits for service health checks;
6. imports all CTI workflows in a disabled state and avoids duplicate imports.

The user still creates the first local n8n owner account. The protected setup page can then select from the reviewed source catalog and create and map the operator-owned PostgreSQL, bundled Gemini, and optional Telegram credentials. Workflows are never activated automatically.

Published GitHub releases contain a versioned ZIP, its SHA-256 checksum, a JSON manifest, a self-contained Windows x64 installer, and a version-pinned Linux installer. Both installers have separate checksums. The release pipeline is triggered only by a matching version tag and builds every installation format from that exact tagged commit.

## Manual installation or existing n8n

Clone the repository, copy `.env.example` to `.env`, and replace all three password placeholders with different random values:

```sh
git clone https://github.com/emecyildiz/CTI.git
cd CTI
cp .env.example .env
```

Then run:

```sh
sh ./scripts/preflight.sh
sh ./scripts/install.sh
```

Open `http://127.0.0.1:8080`. The dashboard intentionally listens only on localhost.
The protected `/setup` page provides an installation and source-health check plus a provider-neutral AI profile. The CSRF-protected profile form stores only provider, adapter, model, and optional base-URL metadata.

The same page performs a secret-free n8n handoff preflight against `CTI_N8N_API_URL`. It checks the health endpoint, confirms the credential schema route exists, and verifies that an unauthenticated request is rejected. The probe never sends an n8n API key or AI credential.

For the bundled Gemini adapter, the one-time handoff form accepts an n8n API key with `credential:list`, `credential:create`, `credential:update`, `workflow:list`, `workflow:read`, and `workflow:update` scopes plus the Gemini API key. Both values remain request-scoped: the dashboard resolves the reserved `CTI Self-Hosted - Google Gemini` credential by name and creates or updates it directly through the n8n public API. Neither key is written to PostgreSQL, reflected in HTML, or placed in a URL.

The separate workflow-mapping action binds that reserved credential only to the expected Gemini nodes in `CTI Article Analysis` and `CTI Weekly Report`. It refuses missing or duplicate workflow names, unexpected Gemini-node layouts, archived workflows, and active or published workflows. A successful mapping updates draft workflow data only; it never calls the n8n activation API.

The PostgreSQL handoff follows the same boundary. The user supplies the generated `CTI_APP_PASSWORD` once; the dashboard creates or updates `CTI Self-Hosted - PostgreSQL` for the restricted `cti_n8n` role at `cti-db:5432`. A separate guarded action maps it to exactly 31 expected database nodes across seven bundled workflows. Neither action stores the password in the CTI schema, and mapping refuses any unexpected workflow structure or active/published target.

Optional Telegram setup creates the reserved `CTI Self-Hosted - Telegram` credential directly in n8n, then maps it to exactly five expected nodes in three disabled workflows. The operator supplies one numeric private user/chat ID: the query workflow requires both sender and chat IDs to match it, while weekly delivery and workflow-error alerts use the same destination. Public exports contain placeholders rather than a personal Telegram identifier. Outbound delivery works without exposing n8n. Interactive menu/query support remains disabled unless `CTI_TELEGRAM_QUERY_ENABLED=true` and `N8N_WEBHOOK_URL` contains a non-local HTTPS base URL.

The final setup step is a read-only activation-readiness audit. Supply an n8n API key with only `credential:list`, `workflow:list`, and `workflow:read`, then select AI, outbound Telegram delivery, and interactive Telegram queries independently. The audit confirms the reserved credentials, expected node mappings, shared private Telegram destination, disabled workflow state, and the required query webhook configuration. It does not claim that DNS or route reachability has been tested, and it never activates a workflow.

Source selection is limited to the catalog bundled with the release. The setup page can enable any non-empty subset of the six compatible reviewed sources, but it cannot accept arbitrary feed URLs or modify host allowlists, selectors, or trust scores. Dark Reading and SecurityWeek remain visible but cannot be enabled while their unattended article retrieval is incompatible.

### Connect n8n

Attach the n8n container to the CTI network, or declare the network as external in the n8n Compose file:

```yaml
services:
  n8n:
    networks:
      - default
      - cti

networks:
  cti:
    external: true
    name: cti-self-hosted
```

The guided `/setup` page can create and map this credential. For a manual setup, create an n8n PostgreSQL credential with:

- Name: `CTI Self-Hosted - PostgreSQL`
- Host: `cti-db`
- Database: `cti`
- User: `cti_n8n`
- Password: the `CTI_APP_PASSWORD` value from `.env`
- Port: `5432`
- SSL: disabled for the private Docker network

Import the files in `workflows/`, assign the PostgreSQL credential to database nodes if the guided mapping was not used, and assign AI/Telegram credentials only to workflows that use them. Imported workflows are disabled by default.

The exports have been import-tested with n8n `2.30.5`. Set `N8N_CONTAINER` in `.env`, then follow [N8N-SETUP.md](N8N-SETUP.md) for the guarded one-time import and credential mapping. Importing through the n8n UI is also supported.

Recommended activation order:

1. CTI Source Collection
2. CTI Article Analysis
3. CTI Vulnerability Enrichment
4. CTI Retention Maintenance
5. CTI Weekly Report
6. optional Telegram query/delivery and error alert workflows

### Verify

```sh
docker compose exec -T cti-db \
  psql -U cti_owner -d cti -Atc \
  "SELECT max(version) FROM cti.schema_versions;"
curl --fail http://127.0.0.1:8080/health/ready
```

The schema version must be `29` or newer on the current main branch. Database upgrades are shipped as versioned migrations rather than by recreating the volume.

## Data and AI behavior

- Feed collection does not call AI.
- Article analysis claims a bounded queue item and records token usage.
- Telegram searches and dashboard filtering do not call AI.
- KEV/EPSS enrichment uses public vulnerability data and does not call AI.
- PostgreSQL data remains in the `cti_pgdata` Docker volume.

## Included sources

Six reviewed sources are enabled by default:

- The Hacker News
- CISA Cybersecurity Advisories
- Microsoft Security Blog
- BleepingComputer
- Cisco Talos
- Krebs on Security

Dark Reading and SecurityWeek definitions are included but disabled because their RSS feeds work while automated article retrieval currently returns HTTP 403. Keeping them disabled prevents permanent failures in the analysis queue. They can be enabled after a compatible, policy-respecting content adapter is configured.

## Backup and restore

Create a PostgreSQL custom-format backup and SHA-256 checksum:

```sh
sh ./scripts/backup.sh
```

Before a restore, disable all imported CTI workflows so they cannot write during replacement. Restore requires two explicit confirmations and creates an additional safety backup first:

```sh
CTI_RESTORE_CONFIRM=RESTORE_CTIDB \
CTI_RESTORE_WORKFLOWS_DISABLED=YES \
sh ./scripts/restore.sh ./backups/cti-YYYYMMDDTHHMMSSZ-XXXXXX.dump
```

The dashboard is stopped during `pg_restore` and restarted afterward. Restore runs in a single database transaction: an SQL or archive-read failure rolls back the database changes. Backup filenames include a unique suffix so a same-second safety backup cannot overwrite the selected input. If a checksum file exists beside the dump, it is verified before any database change.

## Exposure warning

Local mode has no user login: it relies on the loopback binding and accepts only `localhost`, `127.0.0.1`, or `[::1]` Host headers, including custom SSH-forwarding ports. This blocks browser DNS rebinding through an unrelated hostname. Never publish the dashboard port directly to the internet. For remote access, prefer an SSH tunnel. An authenticated reverse proxy must also send a permitted loopback Host upstream; Cloudflare mode has its separate authentication configuration.

## Development validation

Run `sh tests/setup-inputs.sh`, `powershell -NoProfile -File tests/setup-inputs.ps1` on Windows, and `dotnet run --project tests/dashboard-boundary/DashboardBoundary.Tests.csproj`. These cover setup input injection, configuration preservation, local Host checks, public webhook URL shape, and modified Telegram authorization workflows.

`sh scripts/tests/recovery.sh` requires Docker Compose and uses a unique temporary project with an internal network and no published ports. It runs each SQL scenario against a separate database cloned from the current schema, then checks backup-name collisions, failed-restore rollback, and successful restore/role grants. Its dashboard container is a lifecycle stub; this test does not claim to verify dashboard HTTP behavior, external routing, or a complete n8n installation. Temporary test containers, volumes and files are removed on exit. The CI validation workflow runs these regressions and gates release builds.

## License

Copyright © 2026 Emeç Yıldız.

The original code, workflows, configuration, and documentation in this repository are licensed under the [GNU Affero General Public License v3.0 only](LICENSE). If you modify the software and make that modified version available to users over a network, you must offer those users the corresponding source code as required by the license.

CTI Self-Hosted may communicate with separately installed software and third-party services such as n8n, PostgreSQL, AI providers, Telegram, and external intelligence feeds. Those components, services, and their data remain subject to their own licenses and terms; they are not relicensed by this repository.
