# CTI Self-Hosted

CTI Self-Hosted collects reviewed cybersecurity feeds, validates article URLs and content, stores normalized records in PostgreSQL, and optionally uses an operator-selected AI API for bounded article analysis and weekly reporting. The dashboard, database, and n8n workflows remain on infrastructure controlled by the operator.

## Requirements

- Docker Engine with Docker Compose v2
- an AI API credential only when AI analysis/reporting will be enabled
- a Telegram bot only if Telegram delivery/query workflows will be used

The guided setup can deploy a local n8n container automatically. An existing n8n 2.x installation is also supported. The bundled workflow exports currently contain a Gemini model node, but the prompt and validated JSON contract are provider-independent; operators may replace that node with another AI provider.

## Guided installation from a release

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

Download and extract the release ZIP. Do not run a setup script from inside the ZIP archive.

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

Published GitHub releases contain a versioned ZIP, its SHA-256 checksum, a JSON manifest, and a self-contained Windows x64 installer with a separate checksum. The release pipeline is triggered only by a matching version tag and builds both installation formats from that exact tagged commit.

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

Optional Telegram setup creates the reserved `CTI Self-Hosted - Telegram` credential directly in n8n, then maps it to exactly five expected nodes in three disabled workflows. The operator supplies one numeric private user/chat ID: the query workflow requires both sender and chat IDs to match it, while weekly delivery and workflow-error alerts use the same destination. Public exports contain placeholders rather than a personal Telegram identifier.

The final setup step is a read-only activation-readiness audit. Supply an n8n API key with only `credential:list`, `workflow:list`, and `workflow:read`, then select whether AI and Telegram will be enabled. The audit confirms the reserved credentials, all expected node mappings, the shared private Telegram destination when selected, and the disabled/unpublished/unarchived state of all eight bundled workflows. It does not update credentials, modify workflows, or activate anything; activation remains a deliberate one-workflow-at-a-time action in n8n.

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
sh ./scripts/restore.sh ./backups/cti-YYYYMMDDTHHMMSSZ.dump
```

The dashboard is stopped during `pg_restore` and restarted afterward. If a checksum file exists beside the dump, it is verified before any database change.

## Exposure warning

The local authentication mode trusts the loopback binding. Never publish the dashboard port directly to the internet. Use an authenticated reverse proxy or VPN when remote access is required.

## License

Copyright © 2026 Emeç Yıldız.

The original code, workflows, configuration, and documentation in this repository are licensed under the [GNU Affero General Public License v3.0 only](LICENSE). If you modify the software and make that modified version available to users over a network, you must offer those users the corresponding source code as required by the license.

CTI Self-Hosted may communicate with separately installed software and third-party services such as n8n, PostgreSQL, AI providers, Telegram, and external intelligence feeds. Those components, services, and their data remain subject to their own licenses and terms; they are not relicensed by this repository.
