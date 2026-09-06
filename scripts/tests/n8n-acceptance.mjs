// Real, offline n8n acceptance test. Never activates or executes workflows.
// Requires Node 22+ and Docker Compose; all state is an ephemeral unique project.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomBytes } from 'node:crypto';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const temp = mkdtempSync(join(tmpdir(), 'cti-n8n-acceptance-'));
const project = `cti-acceptance-${randomBytes(6).toString('hex')}`;
const container = `${project}-n8n`;
const password = randomBytes(24).toString('hex');
const env = { ...process.env };
for (const key of Object.keys(env)) {
  if (/^(CTI_|N8N_|POSTGRES_|COMPOSE_)/.test(key)) delete env[key];
}
const composeArgs = ['compose', '--project-directory', root, '--env-file', join(temp, '.env'),
  '-p', project, '-f', join(root, 'compose.yml'), '-f', join(temp, 'override.yml'),
  '--profile', 'managed-n8n'];
const docker = (...args) => execFileSync('docker', args, {
  cwd: root, env, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'],
  timeout: 300_000, maxBuffer: 8 * 1024 * 1024,
});
const compose = (...args) => docker(...composeArgs, ...args);
const pass = message => console.log(`PASS: ${message}`);
const safe = value => String(value).split(password).join('[REDACTED]');
writeFileSync(join(temp, '.env'), [
  `CTI_COMPOSE_PROJECT_NAME=${project}`, `CTI_NETWORK_NAME=${project}`, `N8N_CONTAINER=${container}`,
  'POSTGRES_DB=cti', 'POSTGRES_USER=cti_owner', `POSTGRES_PASSWORD=owner-${password}`,
  `CTI_APP_PASSWORD=app-${password}`, `CTI_DASHBOARD_PASSWORD=dashboard-${password}`,
  `N8N_ENCRYPTION_KEY=${randomBytes(32).toString('hex')}`, 'CTI_TELEGRAM_QUERY_ENABLED=false',
].join('\n') + '\n', { mode: 0o600 });
writeFileSync(join(temp, 'override.yml'), `services:
  cti-db:
    restart: "no"
  cti-dashboard:
    restart: "no"
    ports: !reset []
  cti-n8n:
    restart: "no"
    ports: !reset []
    environment:
      N8N_VERSION_NOTIFICATIONS_ENABLED: "false"
      N8N_TEMPLATES_ENABLED: "false"
networks:
  cti:
    internal: true
`);

try {
  console.log(`Starting isolated acceptance project ${project}`);
  for (const file of readdirSync(join(root, 'workflows')).filter(f => f.endsWith('.json'))) {
    const workflow = JSON.parse(readFileSync(join(root, 'workflows', file), 'utf8'));
    assert.equal(workflow.active, false, `${file} must be disabled before import`);
  }
  compose('up', '-d', '--build');
  assert.equal(JSON.parse(docker('network', 'inspect', project))[0].Internal, true);
  pass('Docker network has no external egress; no host ports published');
  docker('cp', join(root, 'scripts/tests/n8n-acceptance-driver.mjs'), `${container}:/tmp/acceptance.mjs`);
  console.log(compose('exec', '-T', 'cti-n8n', 'node', '/tmp/acceptance.mjs', '--bootstrap').trim());
  const currentVersion = Number(compose('exec', '-T', 'cti-db', 'psql', '-U', 'cti_owner', '-d', 'cti',
    '-Atc', 'SELECT COALESCE(max(version), 0) FROM cti.schema_versions;').trim());
  assert.ok(Number.isInteger(currentVersion));
  for (const file of readdirSync(join(root, 'app/cti/migrations')).filter(f => /^\d{3}-.*\.sql$/.test(f)).sort()) {
    if (Number(file.slice(0, 3)) <= currentVersion) continue;
    compose('exec', '-T', 'cti-db', 'psql', '-U', 'cti_owner', '-d', 'cti',
      '-v', 'ON_ERROR_STOP=1', '-f', `/opt/cti/migrations/${file}`);
  }
  docker('cp', join(root, 'workflows'), `${container}:/tmp/cti-acceptance-workflows`);
  compose('exec', '-T', 'cti-n8n', 'n8n', 'import:workflow', '--separate', '--input=/tmp/cti-acceptance-workflows');
  const result = execFileSync('docker', [...composeArgs, 'exec', '-T', 'cti-n8n', 'node', '/tmp/acceptance.mjs'], {
    cwd: root, env, encoding: 'utf8', input: JSON.stringify({ databasePassword: `app-${password}` }),
    timeout: 240_000, maxBuffer: 2 * 1024 * 1024,
  });
  console.log(result.trim());
} catch (error) {
  console.error(safe(error.message));
  if (error.stdout) console.error(safe(error.stdout).slice(-4000));
  if (error.stderr) console.error(safe(error.stderr).slice(-3000));
  process.exitCode = 1;
} finally {
  try {
    compose('down', '--volumes', '--remove-orphans');
    pass('isolated containers, network and test volumes removed');
    rmSync(temp, { recursive: true, force: true });
  } catch (error) {
    console.error(`Cleanup requires attention for ${project}: ${safe(error.message)}`);
    process.exitCode = 1;
  }
}
