# Repository layout and cleanup review

Reviewed on 2026-11-21 for the RSS-first development branch. This inventory
separates current dependencies from retained history; it is not permission to
delete a user's installation, database, credentials or backups.

## Current Core files

| Paths | Purpose |
| --- | --- |
| `app/cti-core/` | Application, reviewed source catalog, SQLite storage, summaries, dashboard and Windows launch assets |
| `tests/cti-core/` | Core's offline fixture and HTTP boundary tests |
| `scripts/build-cti-core-preview.ps1` | Publishes only the Core project and explicitly selects the Windows ZIP contents |
| `.github/workflows/cti-core-validation.yml` | Windows/Linux Core checks, separate from legacy installer checks |
| `LICENSE`, `.gitattributes`, `.gitignore` | Shared repository controls; keep |

The Core project has no project reference to the old dashboard, installer or
workflow directory. Its tests reference only Core. Its packaging script publishes
`app/cti-core/CtiCore.csproj`; it does not use `git archive` over the repository.

## Retained legacy edition — do not delete yet

| Paths | Why they remain |
| --- | --- |
| `app/cti-dashboard/`, `app/cti/` | The Compose application and database schema/migrations |
| `workflows/` | Imported by legacy setup and used by n8n acceptance tests |
| `installer/`, `setup.*`, `manage.ps1`, `compose.yml`, `.env.example` | Existing installer, ownership/lifecycle rules and configuration |
| Other `scripts/` and non-Core `tests/` | Legacy setup, backup, restore, release and integration contracts; not generic unused files |
| `.github/workflows/validate.yml`, `.github/workflows/release.yml` | Existing legacy CI/release path; the release workflow calls the validation workflow and old builders |
| `VERSION`, `RELEASE-NOTES.md`, `VERIFY-RELEASE.md`, `N8N-SETUP.md`, `WINDOWS-TEST-CHECKLIST.md`, `LEGACY-N8N.md` | Documentation and versioning of old packages |

Evidence: `compose.yml` uses the legacy app/schema; setup scripts call workflow
import and migrations; the tagged release builds Linux/Windows installers; the
legacy tests exercise these files. Removing only part of this group would break
those paths even though Core itself does not use them.

Once Core has passed user acceptance and the old edition's support policy is
decided, archive the legacy edition at an explicit, verified commit/tag and
remove it as a coordinated change on this branch. Keep the old release assets,
their checksums and history. Do not delete branches/tags/releases as cleanup.

## Local generated or personal files

- `dist/` and `**/bin/`, `**/obj/` are ignored build/cache/test output. Old
  SDK/NuGet directories are still used for building here; they are not redundant
  just because they are absent from GitHub.
- Existing `dist/rc*` acceptance/download directories may contain evidence for old
  releases. Retain pending a separate contents/backup review.
- Removed the known `dist/ui-integration-oz6hy392ms/` database and its empty
  folder after exact-path, file-type, database-content and no-live-process
  checks confirmed only the previous UI test's 30 synthetic articles and no AI
  summaries. No recursive deletion was used.
- Removed the empty `.cti-recovery-test.UXNto8/` directory after confirming its
  contents and inspecting the OneDrive cloud-directory reparse tag (not a
  symlink or junction).
- The untracked `OBSİDİAN-DEVAM-2026-09-05.md` is a personal note. Do not delete
  it or include it in the public package or commit.

No live database, user settings, old installer, migration, backup, branch, tag or
release is deleted by the Core package build.
