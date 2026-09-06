// Internal driver; invoked only inside the disposable n8n container by the runner.
import assert from 'node:assert/strict';
import { request } from 'node:http';
import { readFileSync, writeFileSync } from 'node:fs';
import { randomBytes } from 'node:crypto';

const n8n = 'http://127.0.0.1:5678';
const dashboard = 'http://cti-dashboard:8080';
const origin = 'http://localhost:8080';
const pass = message => console.log(`PASS: ${message}`);
const secrets = [];
const safe = value => secrets.reduce((text, secret) => text.split(secret).join('[REDACTED]'), String(value));
function http(url, { method = 'GET', headers = {}, body } = {}) {
  return new Promise((resolve, reject) => {
    if (body !== undefined) headers['Content-Length'] = Buffer.byteLength(body);
    const req = request(url, { method, headers, timeout: 60_000 }, res => {
      let text = '';
      res.setEncoding('utf8');
      res.on('data', chunk => { text += chunk; });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, text }));
    });
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('HTTP acceptance timeout')));
    req.end(body);
  });
}
async function waitHttp(url, headers = {}) {
  for (let i = 0; i < 90; i++) {
    try { if ((await http(url, { headers })).status === 200) return; } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error(`Service did not become ready: ${url}`);
}
let cookie = '';
async function rest(path, body) {
  const response = await http(`${n8n}/rest/${path}`, {
    method: body ? 'POST' : 'GET', headers: { 'Content-Type': 'application/json', Cookie: cookie },
    body: body ? JSON.stringify(body) : undefined,
  });
  const cookies = response.headers['set-cookie'];
  if (cookies?.length) cookie = cookies.map(c => c.split(';')[0]).join('; ');
  assert.equal(response.status, 200, `n8n bootstrap ${path}: HTTP ${response.status}: ${safe(response.text).slice(0, 400)}`);
  return JSON.parse(response.text).data;
}

try {
  // Liveness can precede controller registration during a fresh migration.
  await waitHttp(`${n8n}/rest/settings`);
  if (process.argv.includes('--bootstrap')) {
    const password = `A1!${randomBytes(24).toString('hex')}`;
    secrets.push(password);
    await rest('owner/setup', { email: 'acceptance@example.invalid', firstName: 'Isolated',
      lastName: 'Test', password });
    writeFileSync('/tmp/acceptance-login.json', JSON.stringify({ emailOrLdapLoginId: 'acceptance@example.invalid', password }), { mode: 0o600 });
    pass('fresh n8n owner initialized with disposable local credentials');
  } else {
    const { databasePassword } = JSON.parse(readFileSync(0, 'utf8'));
    secrets.push(databasePassword);
    await waitHttp(`${dashboard}/health/ready`, { Host: 'localhost:8080' });
    const login = JSON.parse(readFileSync('/tmp/acceptance-login.json', 'utf8'));
    secrets.push(login.password);
    await rest('login', login);
    const key = await rest('api-keys', { label: 'Ephemeral acceptance only',
      expiresAt: Math.floor(Date.now() / 1000) + 3600, scopes: [
      'credential:list', 'credential:create', 'credential:update',
      'workflow:list', 'workflow:read', 'workflow:update', 'execution:list',
    ] });
    const apiKey = key.rawApiKey;
    assert.equal(typeof apiKey, 'string');
    secrets.push(apiKey);
    const api = async (path, body) => {
      const response = await http(`${n8n}/api/v1/${path}`, {
        method: body ? 'PUT' : 'GET', headers: { 'X-N8N-API-KEY': apiKey, 'Content-Type': 'application/json' },
        body: body ? JSON.stringify(body) : undefined,
      });
      assert.equal(response.status, 200, `n8n API ${path}: HTTP ${response.status}: ${safe(response.text).slice(0, 400)}`);
      return JSON.parse(response.text);
    };
    let workflows = (await api('workflows?limit=100')).data;
    assert.equal(workflows.length, 8);
    assert.ok(workflows.every(w => w.active === false && !w.activeVersionId));
    pass('clean CLI import produced 8 inactive, unpublished workflows');
    const setup = await http(`${dashboard}/setup`, { headers: { Host: 'localhost:8080' } });
    const csrf = setup.text.match(/name="_csrf"[^>]*value="([^"]+)"/)?.[1];
    assert.ok(csrf, 'setup CSRF field is present');
    const setupCookie = setup.headers['set-cookie'].map(c => c.split(';')[0]).join('; ');
    const post = async (path, fields = {}, expected = 302) => {
      const response = await http(`${dashboard}/setup/${path}`, {
        method: 'POST', headers: { Host: 'localhost:8080', Cookie: setupCookie, Origin: origin,
          'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({ _csrf: csrf, n8n_api_key: apiKey, ...fields }).toString(),
      });
      assert.equal(response.status, expected, `${path}: ${safe(response.text).slice(0, 800)}`);
      assert.ok(!response.text.includes(apiKey), 'API key must never be reflected');
      return { text: response.text, location: response.headers.location };
    };
    assert.match((await post('readiness', {}, 200)).text, /Configuration needs attention/);
    pass('unmapped installation fails readiness');
    await post('ai-profile', { provider_type: 'google_gemini', model_identifier: 'gemini-2.5-flash' });
    const handoffs = [
      ['postgres-credential-handoff', { database_password: databasePassword }],
      ['credential-handoff', { ai_api_key: `dummy-ai-${'A'.repeat(40)}` }],
      ['telegram-credential-handoff', { bot_token: `123456789:${'A'.repeat(35)}` }],
    ];
    for (const [path, fields] of handoffs) {
      assert.match((await post(path, fields)).location, /credential_created$/);
      assert.match((await post(path, fields)).location, /credential_updated$/);
      pass(`${path}: create and repeat update`);
    }
    for (const path of ['postgres-workflow-mapping', 'workflow-mapping', 'telegram-workflow-mapping']) {
      const fields = path.startsWith('telegram') ? { authorized_chat_id: '123456789' } : {};
      assert.match((await post(path, fields)).location, /workflows_mapped$/);
      assert.match((await post(path, fields)).location, /workflows_already_mapped$/);
      pass(`${path}: mapping and idempotent readback`);
    }
    const all = { include_ai: 'true', include_telegram_delivery: 'true' };
    assert.match((await post('readiness', all, 200)).text, /Ready for controlled activation/);
    assert.match((await post('readiness', { ...all, include_telegram_query: 'true' }, 200)).text,
      /Configuration needs attention/);
    pass('AI/delivery readiness passes; query without public HTTPS configuration stays blocked');
    workflows = (await api('workflows?limit=100')).data;
    const detailed = await Promise.all(workflows.map(w => api(`workflows/${w.id}`)));
    const counts = {};
    for (const w of detailed) for (const node of w.nodes) {
      for (const [type, value] of Object.entries(node.credentials ?? {})) {
        assert.ok(value.id && !value.id.includes('REPLACE'), `credential ${type} must be mapped`);
        counts[type] = (counts[type] ?? 0) + 1;
      }
    }
    assert.equal(counts.postgres, 31);
    assert.equal(counts.googlePalmApi, 2);
    assert.equal(counts.telegramApi, 5);
    pass('API readback: 31 PostgreSQL, 2 AI and 5 Telegram credential bindings');
    const query = detailed.find(w => w.name === 'CTI Telegram Query');
    assert.ok(query);
    // n8n readback adds settings not accepted by its public update schema.
    // Match the dashboard's writable setting boundary, not the export shape.
    const settingKeys = ['saveExecutionProgress', 'saveManualExecutions',
      'saveDataErrorExecution', 'saveDataSuccessExecution', 'executionTimeout',
      'errorWorkflow', 'timezone', 'executionOrder', 'callerPolicy', 'callerIds',
      'timeSavedPerExecution', 'redactionPolicy', 'availableInMCP', 'customTelemetryTags'];
    const payload = ({ name, nodes, connections, settings }) => ({ name, nodes, connections,
      settings: Object.fromEntries(Object.entries(settings).filter(([key]) => settingKeys.includes(key))) });
    const original = structuredClone(query);
    query.nodes.find(n => n.name === 'Authorize and Parse Request').disabled = true;
    await api(`workflows/${query.id}`, payload(query));
    await post('telegram-workflow-mapping', { authorized_chat_id: '123456789' }, 409);
    assert.match((await post('readiness', all, 200)).text, /Configuration needs attention/);
    await api(`workflows/${query.id}`, payload(original));
    assert.match((await post('readiness', all, 200)).text, /Ready for controlled activation/);
    pass('disabled authorization guard blocks mapping/readiness; restoring draft recovers');
    assert.ok((await api('workflows?limit=100')).data.every(w => !w.active && !w.activeVersionId));
    assert.equal((await api('executions?limit=100')).data.length, 0);
    pass('all workflows remained disabled; zero executions and no external API calls');
    const ids = new Set(detailed.map(w => w.id));
    const dangling = detailed.filter(w => w.settings?.errorWorkflow && !ids.has(w.settings.errorWorkflow));
    if (dangling.length) console.log(`KNOWN GAP: ${dangling.length} imported error-workflow references do not resolve; configure them before activation.`);
  }
} catch (error) {
  console.error(safe(error.message));
  process.exitCode = 1;
}
