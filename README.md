# CTI Self-Hosted

CTI Self-Hosted collects reviewed cybersecurity feeds, validates article URLs and content, stores normalized records in PostgreSQL, and optionally uses Gemini for bounded article analysis and weekly reporting. The dashboard, database, and n8n workflows remain on infrastructure controlled by the operator.

## Requirements

- Docker Engine with Docker Compose v2
- an existing n8n installation
- a Gemini API credential for AI analysis (optional workflows can remain disabled)
- a Telegram bot only if Telegram delivery/query workflows will be used

## 1. Configure and start the database/dashboard

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

## 2. Connect n8n

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

Import the files in `workflows/`, assign the PostgreSQL credential to database nodes, and assign Gemini/Telegram credentials only to the workflows that use them. Imported workflows are disabled by default.

The exports have been import-tested with n8n `2.30.5`. Set `N8N_CONTAINER` in `.env`, then follow [N8N-SETUP.md](N8N-SETUP.md) for the guarded one-time import and credential mapping. Importing through the n8n UI is also supported.

Recommended activation order:

1. CTI Source Collection
2. CTI Article Analysis
3. CTI Vulnerability Enrichment
4. CTI Retention Maintenance
5. CTI Weekly Report
6. optional Telegram query/delivery and error alert workflows

## 3. Verify

```sh
docker compose exec -T cti-db \
  psql -U cti_owner -d cti -Atc \
  "SELECT max(version) FROM cti.schema_versions;"
curl --fail http://127.0.0.1:8080/health/ready
```

The schema version must be `22` or newer for this release candidate. Database upgrades will be shipped as versioned migrations rather than by recreating the volume.

## Data and AI behavior

- Feed collection does not call AI.
- Article analysis claims a bounded queue item and records token usage.
- Telegram searches and dashboard filtering do not call AI.
- KEV/EPSS enrichment uses public vulnerability data and does not call AI.
- PostgreSQL data remains in the `cti_pgdata` Docker volume.

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

CTI Self-Hosted may communicate with separately installed software and third-party services such as n8n, PostgreSQL, Gemini, Telegram, and external intelligence feeds. Those components, services, and their data remain subject to their own licenses and terms; they are not relicensed by this repository.
