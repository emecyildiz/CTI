import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { makePlan } from '../managed-bootstrap-plan.mjs';
const root = new URL('../../workflows/', import.meta.url);
const templates = readdirSync(root).filter(f => f.endsWith('.json')).map(f => JSON.parse(readFileSync(new URL(f, root), 'utf8')));
const original = JSON.stringify(templates);
const secret = 'x'.repeat(43);
for (const aiKey of [undefined, 'test-ai-key-not-a-real-secret']) {
  const plan = makePlan(templates, { databasePassword: secret, aiKey });
  assert.equal(plan.credentials.length, aiKey ? 2 : 1);
  assert.equal(plan.credentials[0].data.user, 'cti_n8n');
  assert.equal(plan.workflows.length, 8);
  assert.equal(new Set(plan.workflows.map(w => w.id)).size, 8);
  assert.ok(plan.workflows.every(w => w.active === false && !w.activeVersionId));
  const bindings = plan.workflows.flatMap(w => w.nodes).filter(n => n.credentials?.postgres);
  assert.equal(bindings.length, 31);
  assert.ok(bindings.every(n => n.credentials.postgres.id === plan.credentials[0].id));
  assert.equal(plan.workflows.flatMap(w => w.nodes).filter(n => n.credentials?.googlePalmApi).length, aiKey ? 2 : 0);
  assert.equal(plan.workflows.flatMap(w => w.nodes).filter(n => n.credentials?.telegramApi).length, 0);
}
assert.equal(JSON.stringify(templates), original);
assert.throws(() => makePlan(templates, { databasePassword: 'bad' }));
assert.throws(() => makePlan(templates.slice(1), { databasePassword: secret }));
const active = structuredClone(templates); active[0].active = true;
assert.throws(() => makePlan(active, { databasePassword: secret }));
assert.throws(() => makePlan(templates, { databasePassword: secret, aiKey: 'bad\nkey' }));
console.log('PASS: managed bootstrap plan, bindings, input refusal and unchanged templates.');
