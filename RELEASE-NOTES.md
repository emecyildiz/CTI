# 0.1.0-rc.5

Fifth release candidate adds a version-pinned terminal installer for Linux servers and brings the POSIX setup options in line with the Windows setup paths.

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

The Linux bootstrap refuses unsupported operating systems and architectures, missing Docker/Compose access, unrelated non-empty destinations, checksum failures, unexpected archive layouts, and package-version mismatches. It never installs Docker silently or exposes CTI services to a public interface.

The Windows installer checks Docker Desktop, Docker Compose v2, and the Docker engine before enabling installation. It offers a managed n8n container or an existing n8n instance, refuses unrelated non-empty destination folders, preserves an existing `.env` during updates, streams setup progress, and links to the local dashboard after completion.

The protected setup dashboard now separates installation, reviewed source selection, credential mapping, and activation readiness. Source selection can enable any non-empty subset of the six compatible bundled sources without allowing changes to URLs, host allowlists, selectors, or trust scores. The write operation is available only to the dashboard database role; the n8n role cannot call it.

The six compatible sources are The Hacker News, CISA Cybersecurity Advisories, Microsoft Security Blog, BleepingComputer, Cisco Talos, and Krebs on Security. Dark Reading and SecurityWeek remain visible but compatibility-blocked because unattended article retrieval currently returns HTTP 403.

Workflows are never activated automatically. The operator must review the readiness results and deliberately enable the required workflows in n8n. Secrets are not included in the repository or release archive.

The Windows installer is not code-signed in this release. Windows SmartScreen may therefore display an unrecognized-app warning; verify the adjacent `.sha256` file before running it. The installer runs as the current user and does not request elevation.

The repository is licensed under `AGPL-3.0-only`, and the dashboard provides a visible link to the corresponding source code.
