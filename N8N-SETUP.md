# n8n credential and activation guide

All public workflow exports are deliberately disabled and contain no credential identifiers. Import them only once, then map operator-owned credentials in the n8n editor.

Before activation, use the protected dashboard `/setup` page to choose at least one source from the bundled reviewed catalog. This action changes only the enabled state of known source definitions; it does not accept URLs or alter their host allowlists, selectors, or trust scores.

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

- Name: `CTI Self-Hosted - PostgreSQL`
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

## Telegram integration (optional)

The protected dashboard `/setup` page can create the bot credential, insert one authorized private user/chat ID, and map all expected Telegram nodes without activating a workflow. Public exports contain no personal chat identifier.

Use Telegram only if these optional workflows are needed:

- `CTI Telegram Query`
- `CTI Weekly Telegram Delivery`
- `n8n Workflow Error Alerts`

Review the configured private user/chat ID before activation. The query workflow requires both sender ID and private chat ID to match this value; group chats are intentionally excluded. Never publish the query bot without this authorization guard.

Telegram has two different network requirements:

- `CTI Weekly Telegram Delivery` and `n8n Workflow Error Alerts` make outbound API calls. They need the Telegram credential and destination, but no inbound public route.
- `CTI Telegram Query` uses a Telegram Trigger. Telegram must be able to send updates to a stable public HTTPS URL routed to n8n.

For managed n8n, configure the query URL during installation:

```sh
sh ./setup.sh --telegram-webhook-url https://hooks.example.com/
```

This records `CTI_TELEGRAM_QUERY_ENABLED=true`, sets n8n's `WEBHOOK_URL`, and defaults `N8N_PROXY_HOPS` to `1`. The URL must already be routed through a correctly configured TLS reverse proxy or tunnel. Keep the n8n editor private; expose only the production webhook path needed by Telegram. Do not place an interactive user-login challenge in front of Telegram's webhook requests.

For an existing n8n installation, set `WEBHOOK_URL` and the correct `N8N_PROXY_HOPS` value in that n8n deployment, then set the matching `CTI_TELEGRAM_QUERY_ENABLED` and `N8N_WEBHOOK_URL` values in CTI's `.env` so the dashboard can report readiness. The dashboard validates configuration shape but does not create or probe external routing.

## Read-only activation audit

Return to the protected dashboard `/setup` page after credential handoff and workflow mapping. In **Activation readiness**, enter a temporary n8n API key with only these scopes:

- `credential:list`
- `workflow:list`
- `workflow:read`

Select AI, outbound Telegram delivery, and interactive Telegram query only when those optional components will be activated. The audit verifies:

- the reserved PostgreSQL credential and all 31 expected database-node mappings;
- both Gemini model-node mappings when AI is selected;
- all five Telegram mappings, the direct private-chat guard, and matching delivery destinations when either Telegram mode is selected;
- a configured non-local HTTPS webhook base when interactive Telegram query is selected;
- all eight bundled workflows remain disabled, unpublished, and unarchived.

The key is request-scoped and is not stored, reflected in HTML, or placed in a URL. The audit performs GET requests only. It never updates or activates a workflow.

Telegram query mapping and readiness accept the reviewed seven-node graph and exact authorization script, allowing only the private numeric ID and line-ending changes. Disabled nodes, error-continuation settings on the guard, extra entry points, altered connections, or edited guard code cause a refusal. If you intentionally customize this workflow, review it independently rather than treating the bundled audit as certification. A repository guard-template change must update its fingerprint and regression fixtures together.

Webhook configuration uses HTTPS with a DNS hostname and a URL-safe path; IP literals, local-name suffixes, credentials, whitespace, queries, and fragments are rejected consistently by the guided setup and dashboard. This checks configuration shape, not DNS resolution or public reachability. The setup opt-in and readiness checks do not prevent an operator from manually activating workflows directly in n8n.

## Activation order

PostgreSQL workflow mapping also replaces the seven CTI drafts' imported
`settings.errorWorkflow` references with the unique local ID of
`n8n Workflow Error Alerts`. Import all eight workflows first and keep them
disabled, unpublished and unarchived while mapping. A missing, duplicate or
unsafe error-handler draft blocks mapping before writes. The update response
must confirm each new reference, and readiness rejects stale or missing routes.
The handler must retain its three enabled nodes and have no chained error route.
This links error routing; it does not send a test alert or verify Telegram
delivery. Configure the Telegram credential and private destination separately.

Activate one workflow at a time and inspect its first execution:

1. `CTI Source Collection`
2. `CTI Article Analysis`
3. `CTI Vulnerability Enrichment`
4. `CTI Retention Maintenance`
5. `CTI Weekly Report`
6. optional Telegram workflows
7. optional n8n error alerts

After Source Collection runs, verify the dashboard and confirm that all six enabled sources have a recent successful check. Article Analysis should be activated only after the PostgreSQL and selected AI-provider nodes both show valid credentials.
