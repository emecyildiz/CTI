# 0.1.0-rc.1

Initial technical release candidate for self-hosted evaluation.

Verified on 30 August 2026 with:

- PostgreSQL 16 Alpine (pinned image digest);
- n8n 2.30.5 (pinned image digest for the import test);
- ASP.NET Core 8 dashboard;
- clean Docker Compose installation;
- schema version 22 and five enabled sources;
- eight sanitized workflows imported into an empty n8n data store.
- custom-format backup, checksum verification, safety backup, and full restore.
- guarded one-time n8n import plus PostgreSQL, Gemini, and Telegram credential mapping guidance.
- release ZIP generation with required-file inspection, SHA-256 checksum, and JSON manifest.

This release candidate is intended for technical users. Workflows are imported disabled and require operator-owned PostgreSQL, Gemini, and optional Telegram credentials.

The repository is licensed under `AGPL-3.0-only`, and the dashboard provides a visible link to the corresponding source code.
