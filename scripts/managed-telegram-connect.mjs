// Container-side only. A trusted host bridge must verify ownership before stdin handoff.
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, readdirSync, mkdtempSync, rmSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { makeTelegramPlan } from './managed-telegram-plan.mjs';
const work = mkdtempSync('/dev/shm/cti-telegram-connect-');
const pending = '/home/node/.n8n/.cti-managed-telegram-pending-v1';
const lock = '/home/node/.n8n/.cti-managed-connection-lock-v1';
let ownsLock = false;
let stage = 'validation';
function cli(args) { stage=args[0]; execFileSync('n8n',args,{stdio:['ignore','pipe','pipe'],timeout:120000,maxBuffer:4*1024*1024}); }
function exported(kind,name) { const path=join(work,name); cli([`export:${kind}`,'--all',`--output=${path}`]); return JSON.parse(readFileSync(path,'utf8')); }
try {
  if ([pending,'/home/node/.n8n/.cti-managed-bootstrap-pending-v1','/home/node/.n8n/.cti-managed-ai-pending-v1'].some(existsSync)) throw new Error('Interrupted operation.');
  const receipt = JSON.parse(readFileSync('/home/node/.n8n/.cti-managed-bootstrap-v1','utf8'));
  if (receipt.schema !== 1 || receipt.database !== true || receipt.workflows !== 8) throw new Error('Bootstrap not confirmed.');
  const input = readFileSync(0,'utf8');
  if (Buffer.byteLength(input) > 8192) throw new Error('Oversized request.');
  const configuration = JSON.parse(input);
  writeFileSync(lock,'',{mode:0o600,flag:'wx'}); ownsLock = true;
  const templates = readdirSync('/tmp/cti-self-hosted-workflows').filter(f=>f.endsWith('.json')).map(f=>JSON.parse(readFileSync(join('/tmp/cti-self-hosted-workflows',f),'utf8')));
  const before = exported('workflow','workflows-before.json');
  const credentials = exported('credentials','credentials-before.json'); // encrypted only
  const plan = makeTelegramPlan(templates,before,credentials,configuration);
  writeFileSync(pending,'',{mode:0o600,flag:'wx'});
  const credentialFile = join(work,'telegram-credential.json');
  writeFileSync(credentialFile,JSON.stringify([plan.credential]),{mode:0o600});
  cli(['import:credentials',`--input=${credentialFile}`]);
  rmSync(credentialFile);
  const workflowFile = join(work,'telegram-workflows.json');
  writeFileSync(workflowFile,JSON.stringify(plan.workflows),{mode:0o600});
  cli(['import:workflow',`--input=${workflowFile}`]);
  const after = exported('workflow','workflows-after.json');
  const saved = exported('credentials','credentials-after.json');
  const check = makeTelegramPlan(templates,after,saved,configuration);
  if (check.credential.id !== plan.credential.id || saved.length !== credentials.length+(credentials.some(c=>c.id===plan.credential.id)?0:1) ||
      !saved.some(c=>c.id===plan.credential.id && typeof c.data==='string' && c.data.length>0)) throw new Error('Credential readback mismatch.');
  for (const expected of plan.workflows) {
    const actual = after.find(w=>w.id===expected.id);
    if (!actual || JSON.stringify(actual.nodes) !== JSON.stringify(expected.nodes) ||
        JSON.stringify(actual.connections) !== JSON.stringify(expected.connections) ||
        JSON.stringify(actual.settings) !== JSON.stringify(expected.settings) || actual.active !== false)
      throw new Error('Telegram binding or private destination readback mismatch.');
  }
  for (const untouched of before.filter(w=>!plan.workflows.some(p=>p.id===w.id))) {
    if (JSON.stringify(untouched) !== JSON.stringify(after.find(w=>w.id===untouched.id))) throw new Error('Unrelated workflow changed.');
  }
  for (const untouched of credentials.filter(c=>c.id!==plan.credential.id)) {
    if (JSON.stringify(untouched) !== JSON.stringify(saved.find(c=>c.id===untouched.id))) throw new Error('Unrelated credential changed.');
  }
  rmSync(pending);
  console.log('CTI_MANAGED_TELEGRAM_RESULT:v1:prepared');
} catch {
  console.error(`Managed Telegram connection did not finish at ${stage}. A partial update may remain; automatic retry is blocked after mutation. No tasks were activated or messages sent.`);
  process.exitCode=1;
} finally {
  rmSync(work,{recursive:true,force:true});
  if (ownsLock) rmSync(lock,{force:true});
}
