// Runs only inside the pinned, installer-owned n8n container. No HTTP/API key.
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, readdirSync, mkdtempSync, rmSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { makePlan } from './managed-bootstrap-plan.mjs';

const work = mkdtempSync('/dev/shm/cti-bootstrap-');
const marker = '/home/node/.n8n/.cti-managed-bootstrap-v1';
const pending = '/home/node/.n8n/.cti-managed-bootstrap-pending-v1';
let stage = 'validation';
function cli(args) {
  stage = args[0];
  // Never expose CLI stderr/stdout: import errors can contain credential data.
  execFileSync('n8n', args, { stdio: ['ignore', 'pipe', 'pipe'], timeout: 120000, maxBuffer: 2 * 1024 * 1024 });
}
try {
  if (existsSync(marker) || existsSync(pending)) throw new Error('Already configured or interrupted.');
  const input = readFileSync(0, 'utf8');
  if (Buffer.byteLength(input) > 8192) throw new Error('Input too large.');
  const templates = readdirSync('/tmp/cti-self-hosted-workflows').filter(f => f.endsWith('.json'))
    .map(f => JSON.parse(readFileSync(join('/tmp/cti-self-hosted-workflows', f), 'utf8')));
  const plan = makePlan(templates, JSON.parse(input));
  for (const kind of ['workflow', 'credentials']) {
    const file = join(work, `${kind}-before.json`);
    try { cli([`export:${kind}`, '--all', `--output=${file}`]); }
    catch (error) {
      const expected = kind === 'workflow' ? 'No workflows found with specified filters' : 'No credentials found with specified filters';
      const lines = `${error.stdout ?? ''}\n${error.stderr ?? ''}`.trim().split(/\r?\n/);
      if (error.status === 1 && lines.at(-1) === expected && !existsSync(file)) continue;
      throw new Error('Could not establish empty instance.');
    }
    const items = JSON.parse(readFileSync(file, 'utf8'));
    if (!Array.isArray(items) || items.length) throw new Error('Instance is not empty.');
  }
  const credentialFile = join(work, 'credentials.json');
  writeFileSync(pending, '', { mode: 0o600, flag: 'wx' });
  writeFileSync(credentialFile, JSON.stringify(plan.credentials), { mode: 0o600 });
  cli(['import:credentials', `--input=${credentialFile}`]);
  rmSync(credentialFile);
  const savedCredentials = join(work, 'saved-credentials.json');
  cli(['export:credentials', '--all', `--output=${savedCredentials}`]);
  const credentialRecords = JSON.parse(readFileSync(savedCredentials, 'utf8'));
  if (credentialRecords.length !== plan.credentials.length || plan.credentials.some(expected =>
    !credentialRecords.some(actual => actual.id === expected.id && actual.name === expected.name &&
      actual.type === expected.type && typeof actual.data === 'string' && actual.data.length > 0)))
    throw new Error('Credential verification failed.');
  const workflowFile = join(work, 'workflows.json');
  writeFileSync(workflowFile, JSON.stringify(plan.workflows), { mode: 0o600 });
  cli(['import:workflow', `--input=${workflowFile}`]);
  const verified = join(work, 'verified.json');
  cli(['export:workflow', '--all', `--output=${verified}`]);
  const actual = JSON.parse(readFileSync(verified, 'utf8'));
  if (actual.length !== 8 || actual.some(w => w.active || w.activeVersionId)) throw new Error('Import verification failed.');
  for (const expected of plan.workflows) {
    const saved = actual.find(w => w.id === expected.id);
    if (!saved || saved.settings?.errorWorkflow !== expected.settings.errorWorkflow) throw new Error('Routing verification failed.');
    for (const node of expected.nodes) {
      const savedNode = saved.nodes.find(n => n.id === node.id);
      if (!savedNode || JSON.stringify(savedNode.credentials ?? {}) !== JSON.stringify(node.credentials ?? {})) throw new Error('Binding verification failed.');
    }
  }
  writeFileSync(marker, JSON.stringify({ schema: 1, database: true, ai: plan.credentials.length === 2, workflows: 8, active: false }), { mode: 0o600, flag: 'wx' });
  writeFileSync('/home/node/.n8n/.cti-workflows-imported-v1', '', { mode: 0o600 });
  rmSync(pending);
  console.log('Managed CTI data connection prepared. Eight tasks remain disabled.');
} catch {
  console.error(`Managed connection was not completed at ${stage}. Existing data was not overwritten. A partial import may remain; do not retry over it automatically. No tasks were activated.`);
  process.exitCode = 1;
} finally {
  rmSync(work, { recursive: true, force: true });
}
