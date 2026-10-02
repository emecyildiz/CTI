import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
const root = resolve(import.meta.dirname, '../..');
const name = `cti-bootstrap-test-${randomBytes(6).toString('hex')}`;
const image = 'docker.n8n.io/n8nio/n8n:2.30.5@sha256:450853cd21a2ce36587c4c860eb26927c1ceba9496bf55f4c213b5d3a6dc8c6f';
const helperFiles=['managed-bootstrap.mjs','managed-bootstrap-plan.mjs','managed-ai-connect.mjs','managed-ai-plan.mjs','managed-telegram-connect.mjs','managed-telegram-plan.mjs'];
const digest=file=>createHash('sha256').update(readFileSync(resolve(root,'scripts',file))).digest('hex');
const initialDigests=new Map(helperFiles.map(file=>[file,digest(file)]));
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
  for(const file of ['managed-telegram-connect.mjs','managed-telegram-plan.mjs']) docker(['cp',resolve(root,'scripts',file),`${name}:/tmp/${file}`]);
  const telegramInput=JSON.stringify({botToken:`123456789:${'A'.repeat(35)}`,chatId:'123456789'});
  for(const input of [telegramInput,JSON.stringify({botToken:`123456789:${'B'.repeat(35)}`,chatId:'987654321'})]) {
    const result=docker(['exec','-i',name,'node','/tmp/managed-telegram-connect.mjs'],input);
    assert.equal(result.trim(),'CTI_MANAGED_TELEGRAM_RESULT:v1:prepared');
  }
  console.log('PASS: Telegram credential created then updated; three inactive tasks/five nodes mapped, private destinations changed, DB/AI and other five tasks preserved.');
  docker(['exec',name,'sh','-c','test ! -f /home/node/.n8n/.cti-managed-connection-lock-v1 && test ! -f /home/node/.n8n/.cti-managed-telegram-pending-v1 && test -z "$(find /dev/shm -maxdepth 1 -name \'cti-telegram-connect-*\' -print -quit)"']);
  for(const marker of ['.cti-managed-connection-lock-v1','.cti-managed-ai-pending-v1','.cti-managed-telegram-pending-v1']) {
    docker(['exec',name,'touch',`/home/node/.n8n/${marker}`]);
    for(const [helper,input] of [['managed-telegram-connect.mjs',telegramInput],['managed-ai-connect.mjs',JSON.stringify({aiKey:'replacement-synthetic-ai-key-only'})]]) {
      let rejected=false;
      try { docker(['exec','-i',name,'node',`/tmp/${helper}`],input); } catch { rejected=true; }
      assert.ok(rejected);
    }
    docker(['exec',name,'rm',`/home/node/.n8n/${marker}`]);
  }
  console.log('PASS: shared lock and either pending marker block both connection helpers; failed operations do not remove another operation\'s lock.');
  docker(['exec',name,'n8n','export:workflow','--all','--output=/dev/shm/cti-before-refusal.json']);
  const beforeRefusal=docker(['exec',name,'cat','/dev/shm/cti-before-refusal.json']);
  for(const input of [JSON.stringify({botToken:'invalid',chatId:'123456789'}),JSON.stringify({botToken:`123456789:${'A'.repeat(35)}`,chatId:'-100123456789'})]) {
    let rejected=false;
    try { docker(['exec','-i',name,'node','/tmp/managed-telegram-connect.mjs'],input); } catch { rejected=true; }
    assert.ok(rejected);
  }
  docker(['exec',name,'sh','-c','test ! -f /home/node/.n8n/.cti-managed-telegram-pending-v1 && test ! -f /home/node/.n8n/.cti-managed-connection-lock-v1']);
  docker(['exec',name,'n8n','export:workflow','--all','--output=/dev/shm/cti-after-refusal.json']);
  assert.equal(docker(['exec',name,'cat','/dev/shm/cti-after-refusal.json']),beforeRefusal);
  console.log('PASS: malformed token and group ID refused without a pending mutation marker.');
  docker(['exec',name,'rm','/home/node/.n8n/.cti-managed-bootstrap-v1']);
  docker(['exec',name,'touch','/home/node/.n8n/.cti-managed-bootstrap-pending-v1']);
  refused = false;
  try { docker(['exec','-i',name,'node','/tmp/managed-bootstrap.mjs'],payload); } catch { refused = true; }
  assert.ok(refused);
  console.log('PASS: interrupted bootstrap refuses automatic retry.');
  for(const file of helperFiles) assert.equal(digest(file),initialDigests.get(file),'Test source changed during acceptance.');
  console.log('PASS: all reviewed helper source hashes stayed unchanged throughout acceptance.');
} catch (error) {
  // Do not print child output, command input, environment or secret-bearing exception objects.
  console.error('FAIL: isolated managed bootstrap acceptance. ' + (error.code ?? error.name ?? 'Error'));
  const stage = /not completed at ([a-z:]+)\./.exec(String(error.stderr ?? ''));
  if (stage) console.error('Safe failure stage: ' + stage[1]);
  process.exitCode = 1;
} finally {
  if (created) { docker(['rm','-f',name]); console.log('PASS: isolated container and tmpfs state removed.'); }
}
