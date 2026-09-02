# n8n credential and activation guide

All public workflow exports are deliberately disabled and contain no credential identifiers. Import them only once, then map operator-owned credentials in the n8n editor.

## One-time import

Set `N8N_CONTAINER` in `.env`, start CTI Self-Hosted, then run:

```sh
CTI_IMPORT_CONFIRM=IMPORT_DISABLED_WORKFLOWS \
sh ./scripts/import-workflows.sh
```

The script verifies Docker, n8n 2.x, password separation, and the CTI network. It connects the existing n8n container to the CTI network when necessary, copies the sanitized exports into a temporary container directory, imports them disabled, and removes the temporary files. Running it again creates duplicate workflows.

## PostgreSQL credential

The protected dashboard `/setup` page can create this credential and map it to all expected PostgreSQL nodes without activating a workflow. The following values remain available for a manual installation or review:

Create one PostgreSQL credential in n8n and assign it to every PostgreSQL node:

- Name: `CTI PostgreSQL`
- Host: `cti-db`
- Port: `5432`
- Database: `cti`
- User: `cti_n8n`
- Password: `CTI_APP_PASSWORD` from `.env`
- SSL: disabled inside the private Docker network

This role is not the database owner and cannot read internal event-clustering tables directly.

## AI credential and provider adapter

AI access is optional. The bundled workflow exports currently use a Google Gemini model node in:

- `CTI Article Analysis` → `Analyze With Gemini`
- `CTI Weekly Report` → its Gemini model node

An operator can replace those model nodes with another AI provider while preserving the prepared prompt and the validated JSON response contract used by the following nodes. Provider credentials must remain operator-owned and must never be committed to the repository.

Feed collection, dashboard filtering, Telegram queries, retention, KEV synchronization, and EPSS enrichment do not require an AI API.

## Telegram credential (optional)

The protected dashboard `/setup` page can create the bot credential, insert one authorized private user/chat ID, and map all expected Telegram nodes without activating a workflow. Public exports contain no personal chat identifier.

Use Telegram only if these optional workflows are needed:

- `CTI Telegram Query`
- `CTI Weekly Telegram Delivery`
- `n8n Workflow Error Alerts`

Review the configured private user/chat ID before activation. The query workflow requires both sender ID and private chat ID to match this value; group chats are intentionally excluded. Never publish the query bot without this authorization guard.

## Activation order

Activate one workflow at a time and inspect its first execution:

1. `CTI Source Collection`
2. `CTI Article Analysis`
3. `CTI Vulnerability Enrichment`
4. `CTI Retention Maintenance`
5. `CTI Weekly Report`
6. optional Telegram workflows
7. optional n8n error alerts

After Source Collection runs, verify the dashboard and confirm that all six enabled sources have a recent successful check. Article Analysis should be activated only after the PostgreSQL and selected AI-provider nodes both show valid credentials.
