# 0.1.0-rc.2

Second release candidate focused on guided installation and source expansion.

Verified on 30 August 2026 with:

- PostgreSQL 16 Alpine (pinned image digest);
- n8n 2.30.5 (pinned image digest for the import test);
- ASP.NET Core 8 dashboard;
- clean Docker Compose installation;
- schema version 25, six enabled sources, and two compatibility-disabled source definitions;
- eight sanitized workflows imported into an empty n8n data store.
- custom-format backup, checksum verification, safety backup, and full restore.
- Windows and POSIX guided setup with prerequisite detection, secret generation, managed n8n deployment, and duplicate-safe disabled workflow import.
- guarded one-time n8n import plus PostgreSQL, provider-selectable AI, and Telegram credential mapping guidance.
- release ZIP generation with required-file inspection, SHA-256 checksum, and JSON manifest.

The installer checks for Docker before making changes. If Docker is present, it can start a local n8n service and import the workflows automatically. Workflows remain disabled and require operator-owned PostgreSQL, optional AI, and optional Telegram credentials.

BleepingComputer was already included. Krebs on Security is now enabled. Dark Reading and SecurityWeek are included in disabled state because their RSS endpoints are healthy but their article pages currently reject unattended retrieval with HTTP 403.

The repository is licensed under `AGPL-3.0-only`, and the dashboard provides a visible link to the corresponding source code.
