import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { resolve } from 'node:path';
const root = resolve(import.meta.dirname, '../..');
const name = `cti-bootstrap-test-${randomBytes(6).toString('hex')}`;
const image = 'docker.n8n.io/n8nio/n8n:2.30.5@sha256:450853cd21a2ce36587c4c860eb26927c1ceba9496bf55f4c213b5d3a6dc8c6f';
const docker = (args, input) => execFileSync('docker', args, { cwd: root, input, encoding: 'utf8', timeout: 240000, maxBuffer: 2 * 1024 * 1024, stdio: ['pipe','pipe','pipe'] });
let created = false;
try {
  docker(['run','-d','--name',name,'--network','none','--tmpfs','/home/node/.n8n:uid=1000,gid=1000,mode=700','--entrypoint','sleep',image,'900']);
  created = true;
  for (const file of ['managed-bootstrap.mjs','managed-bootstrap-plan.mjs']) docker(['cp',resolve(root,'scripts',file),`${name}:/tmp/${file}`]);
  docker(['cp',resolve(root,'workflows'),`${name}:/tmp/cti-self-hosted-workflows`]);
  const payload = JSON.stringify({ databasePassword: randomBytes(32).toString('base64url'), aiKey: 'synthetic-provider-key-not-real' });
  const result = docker(['exec','-i',name,'node','/tmp/managed-bootstrap.mjs'],payload);
  assert.match(result,/Eight tasks remain disabled/);
  const receipt = JSON.parse(docker(['exec',name,'cat','/home/node/.n8n/.cti-managed-bootstrap-v1']));
  assert.equal(receipt.database,true); assert.equal(receipt.ai,true); assert.equal(receipt.active,false);
  docker(['exec',name,'sh','-c','test ! -f /home/node/.n8n/.cti-managed-bootstrap-pending-v1']);
  console.log('PASS: real pinned n8n CLI prepared database + AI bindings without API key, owner password or external network.');
  let refused = false;
  try { docker(['exec','-i',name,'node','/tmp/managed-bootstrap.mjs'],payload); } catch { refused = true; }
  assert.ok(refused);
  console.log('PASS: repeat bootstrap refused; existing setup not overwritten.');
  for(const file of ['managed-ai-connect.mjs','managed-ai-plan.mjs']) docker(['cp',resolve(root,'scripts',file),`${name}:/tmp/${file}`]);
  const aiResult=docker(['exec','-i',name,'node','/tmp/managed-ai-connect.mjs'],JSON.stringify({aiKey:'replacement-synthetic-ai-key-only'}));
  assert.match(aiResult,/CTI_MANAGED_AI_RESULT:v1:prepared/);
  console.log('PASS: managed AI credential updated and two tasks mapped without n8n API key; unrelated tasks preserved.');
  docker(['exec',name,'rm','/home/node/.n8n/.cti-managed-bootstrap-v1']);
  docker(['exec',name,'touch','/home/node/.n8n/.cti-managed-bootstrap-pending-v1']);
  refused = false;
  try { docker(['exec','-i',name,'node','/tmp/managed-bootstrap.mjs'],payload); } catch { refused = true; }
  assert.ok(refused);
  console.log('PASS: interrupted bootstrap refuses automatic retry.');
} catch (error) {
  // Do not print child output, command input, environment or secret-bearing exception objects.
  console.error('FAIL: isolated managed bootstrap acceptance. ' + (error.code ?? error.name ?? 'Error'));
  const stage = /not completed at ([a-z:]+)\./.exec(String(error.stderr ?? ''));
  if (stage) console.error('Safe failure stage: ' + stage[1]);
  process.exitCode = 1;
} finally {
  if (created) { docker(['rm','-f',name]); console.log('PASS: isolated container and tmpfs state removed.'); }
}
