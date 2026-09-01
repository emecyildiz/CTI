# CTI Self-Hosted

CTI Self-Hosted collects reviewed cybersecurity feeds, validates article URLs and content, stores normalized records in PostgreSQL, and optionally uses an operator-selected AI API for bounded article analysis and weekly reporting. The dashboard, database, and n8n workflows remain on infrastructure controlled by the operator.

## Requirements

- Docker Engine with Docker Compose v2
- an AI API credential only when AI analysis/reporting will be enabled
- a Telegram bot only if Telegram delivery/query workflows will be used

The guided setup can deploy a local n8n container automatically. An existing n8n 2.x installation is also supported. The bundled workflow exports currently contain a Gemini model node, but the prompt and validated JSON contract are provider-independent; operators may replace that node with another AI provider.

## Guided installation from a release

Download and extract the release ZIP. Do not run the installer from inside the ZIP archive.

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

The user still creates the first local n8n owner account and maps operator-owned PostgreSQL, AI, and optional Telegram credentials. Workflows are never activated automatically.

Published GitHub releases contain a versioned ZIP, SHA-256 checksum, and JSON manifest. The release pipeline is triggered only by a matching version tag, validates the package, and builds the downloadable archive automatically.

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
The protected `/setup` page provides an installation and source-health check plus a provider-neutral AI profile. The CSRF-protected profile form stores only provider, adapter, model, and optional base-URL metadata. It never accepts an API key and does not modify credentials, workflows, or activation state.

The same page performs a secret-free n8n handoff preflight against `CTI_N8N_API_URL`. It checks the health endpoint, confirms the credential schema route exists, and verifies that an unauthenticated request is rejected. The probe never sends an n8n API key or AI credential.

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

Create an n8n PostgreSQL credential with:

- Host: `cti-db`
- Database: `cti`
- User: `cti_n8n`
- Password: the `CTI_APP_PASSWORD` value from `.env`
- Port: `5432`
- SSL: disabled for the private Docker network

Import the files in `workflows/`, assign the PostgreSQL credential to database nodes, and assign AI/Telegram credentials only to workflows that use them. Imported workflows are disabled by default.

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

The schema version must be `28` or newer on the current main branch. Database upgrades are shipped as versioned migrations rather than by recreating the volume.

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
