# 0.1.0-rc.3

Third release candidate focused on safe post-install configuration and activation readiness.

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
- release ZIP generation with required-file inspection, SHA-256 checksum, and JSON manifest.

The protected setup dashboard now separates installation, reviewed source selection, credential mapping, and activation readiness. Source selection can enable any non-empty subset of the six compatible bundled sources without allowing changes to URLs, host allowlists, selectors, or trust scores. The write operation is available only to the dashboard database role; the n8n role cannot call it.

The six compatible sources are The Hacker News, CISA Cybersecurity Advisories, Microsoft Security Blog, BleepingComputer, Cisco Talos, and Krebs on Security. Dark Reading and SecurityWeek remain visible but compatibility-blocked because unattended article retrieval currently returns HTTP 403.

Workflows are never activated automatically. The operator must review the readiness results and deliberately enable the required workflows in n8n. Secrets are not included in the repository or release archive.

The repository is licensed under `AGPL-3.0-only`, and the dashboard provides a visible link to the corresponding source code.
