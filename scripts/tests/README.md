# Isolated acceptance tests

Run the real n8n integration test from the repository root:

```sh
node scripts/tests/n8n-acceptance.mjs
```

Requires Node.js 22 or later and a running Linux Docker engine with Compose
support for `!reset`. The runner builds the actual dashboard and uses the
PostgreSQL and n8n images pinned in `compose.yml`. Downloading/building those
images may require network access; the running test services use an internal
Docker network with no published host ports.

Each run creates a uniquely named project, disposable owner account, short-lived
n8n API key, and fake AI/Telegram credentials. It never uses the repository's
`.env`, activates workflows, or executes them. The driver operates inside the
n8n container and sends the same local Host/Origin headers as the loopback UI.
The `finally` cleanup removes only this run's containers, volumes, network and
temporary files. If the process is forcibly killed, use the printed project
name to inspect and remove that project's resources; never run a global prune.

The test covers clean CLI import, three credential create/update paths,
idempotent workflow mapping, API readback of 31 PostgreSQL / 2 AI / 5 Telegram
bindings, readiness before and after configuration, disabled-guard rejection,
draft restoration, and zero executions. This is an integration regression test,
not a complete release approval or an external Telegram/AI connectivity test.

## Error-workflow routing regression

The exports retain a source-installation `settings.errorWorkflow` ID. PostgreSQL
mapping replaces it with the local imported handler ID. The test asserts all
seven references, deliberately introduces a stale route, checks readiness refusal,
then repairs it through mapping while the database credentials are already mapped.
A disabled error trigger must block both relinking and readiness. All changes
remain disabled drafts; no actual error workflow or Telegram request is executed.
