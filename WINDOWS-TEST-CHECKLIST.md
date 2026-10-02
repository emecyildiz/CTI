# Windows candidate test checklist

This is a user acceptance checklist for the **rc.10 test prerelease**. Use its matching EXE from the [official release](https://github.com/emecyildiz/CTI/releases/tag/v0.1.0-rc.10), not an older rc.9 EXE. If the assets are not listed yet, wait for publication; do not substitute another version for this checklist. Passing automated tests or **Check setup** does not replace these user checks. Do not enter secrets in screenshots or bug reports.

## 1. Keep the existing installation safe

- Keep your working CTI folder and its Docker data intact. Do not choose **Purge** there.
- Verify the candidate's SHA-256 using [the verification guide](VERIFY-RELEASE.md).
- Docker Desktop must be installed **and running**, with its Linux engine available. Installing Docker alone is not enough. A VM may need nested virtualization, depending on its host configuration.
- For a fresh managed-path test, choose a separate normal local folder, for example `%LOCALAPPDATA%\Emecworks\CTI-RC-Test`. Avoid OneDrive and linked folders.
- Choose unused ports, for example dashboard `18085` and managed n8n `15679`. These are examples, not reserved ports. If they are busy, choose different ones; do not stop another application to free them.

An existing installation is a different scenario. Repair/update preserves its `.env`, ports, n8n credentials and stored workflows; it does not automatically replace those workflows or adopt an old/manual installation into the new keyless path. Do not edit ownership/bootstrap records to force that path.

## 2. Fresh managed installation

1. Select **Install and manage a local n8n container**, review the new folder and ports, then install.
2. Wait for success. Confirm that the dashboard and n8n links use your selected ports.
3. Open n8n and create its first owner account yourself. Keep the setup app open, or reopen the same candidate and select the same folder.
4. Use **Open setup** and select the sources you want. Save, reload and confirm the selection persists without a 403 error.
5. AI is optional. If wanted, use **Connect AI** in the setup app. The bundled connection prepares Gemini; advanced provider/model metadata does not convert the workflow to another provider. Cancel should leave preparation unchanged.
6. Telegram is optional. If wanted, use **Connect Telegram (optional)** with a bot token and your private numeric user/chat ID. Do not use a group ID. Cancel should leave preparation unchanged.
7. Use **Check setup**. Both optional boxes start unchecked. Select only the connections you prepared; with neither selected it checks database/task preparation only.
8. Review n8n: all eight imported CTI tasks should still be disabled. No provider request, Telegram message or webhook activation should have been triggered by these preparation actions.

**Check setup** checks structure, mappings and private Telegram guards. It cannot prove that a key/password is accepted, that Telegram delivers messages, or that public HTTPS routing works. A successful check is not permission to activate every optional task. See [activation order](N8N-SETUP.md#activation-order) when you deliberately decide to run tasks. Collection works without AI; AI summaries/reports do not.

Incoming Telegram queries need a working public HTTPS webhook route. Outbound reports/error alerts do not need an inbound route. Keep the n8n editor private.

## 3. Dialog and keyboard checks

- Open the AI, Telegram and preparation dialogs. Check labels, explanatory text, inputs and buttons for clipping or overlap.
- Tab should move through inputs and actions sensibly. Escape cancels; Enter validates instead of accepting an empty secret.
- Secrets stay masked. Never use a real key to test screenshot/layout reporting.
- If you already use Windows display scaling at 100%, 150% or 200%, report the scale and screen resolution when a layout problem occurs. A maintainer component/font-scale test is **not** a physical monitor-DPI test.
- When using multiple monitors, check moving the setup app between them at your existing scales. Actual display/DPI behavior and small-screen acceptance remain manual checks; do not change system security settings for a test.

## 4. Reopen and existing-installation checks

For the disposable fresh installation, close/reopen the setup app and select its same folder. Confirm the saved ports and management actions reappear. A same-version repair must not duplicate the eight tasks, reset credentials or activate anything.

Only test repair on your older real installation if you have reviewed package conflicts and backed up its data separately. Package-file backups are not database/n8n backups. Old/manual/external installations should keep manual setup forms unless a reviewed migration has been performed; absence of keyless buttons there is not a reason to erase their data.

## 5. Stop and clean up the disposable test only

- **Stop** keeps data and files. Re-running setup in the same folder can start it again.
- **Remove services** removes owned containers/network but retains database/n8n volumes and configuration for reuse.
- **Purge** permanently deletes owned data and eligible package files. Choose it only for the explicitly disposable test folder after checking the displayed project/folder. Backups, modified/unknown files, Docker/WSL and shared images may remain.

Do not use broad Docker prune commands, delete ownership records, or remove the working installation to clean up this test.

## Report a result

Include candidate version, Windows version, fresh/repair scenario, selected ports, action taken, exact error text and whether Docker was running. For layout issues include display scale/resolution and a redacted screenshot. Exclude API keys, bot tokens, passwords, `.env` contents and personal Telegram IDs. Say which checks were skipped; a skipped provider or webhook test is not a pass.
