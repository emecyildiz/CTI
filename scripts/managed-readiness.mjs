import { execFileSync } from 'node:child_process';
import { readFileSync,writeFileSync,readdirSync,mkdtempSync,rmSync,existsSync } from 'node:fs';
import { join } from 'node:path';
import { auditManagedReadiness } from './managed-readiness-plan.mjs';
const work=mkdtempSync('/dev/shm/cti-readiness-');
const lock='/home/node/.n8n/.cti-managed-connection-lock-v1';
let ownsLock=false;
function exported(kind,name) {
  const file=join(work,name);
  execFileSync('n8n',[`export:${kind}`,'--all',`--output=${file}`],{stdio:['ignore','pipe','pipe'],timeout:120000,maxBuffer:4*1024*1024});
  return JSON.parse(readFileSync(file,'utf8'));
}
try {
  if (['bootstrap','ai','telegram'].some(name=>existsSync(`/home/node/.n8n/.cti-managed-${name}-pending-v1`))) throw new Error('Interrupted preparation.');
  const receipt=JSON.parse(readFileSync('/home/node/.n8n/.cti-managed-bootstrap-v1','utf8'));
  if (receipt.schema!==1 || receipt.database!==true || receipt.workflows!==8) throw new Error('Unknown bootstrap.');
  const input=readFileSync(0,'utf8');
  if (Buffer.byteLength(input)>8192) throw new Error('Oversized options.');
  const options=JSON.parse(input);
  if (typeof options.includeAi!=='boolean' || typeof options.includeTelegram!=='boolean') throw new Error('Invalid options.');
  writeFileSync(lock,'',{mode:0o600,flag:'wx'}); ownsLock=true;
  const templates=readdirSync('/tmp/cti-self-hosted-workflows').filter(f=>f.endsWith('.json')).map(f=>JSON.parse(readFileSync(join('/tmp/cti-self-hosted-workflows',f),'utf8')));
  const workflows=exported('workflow','workflows.json');
  const credentials=exported('credentials','credentials.json'); // encrypted export only
  const result=auditManagedReadiness(templates,workflows,credentials,options);
  // Detect edits during this snapshot; the lock does not block manual n8n edits.
  if (JSON.stringify(workflows)!==JSON.stringify(exported('workflow','workflows-after.json')) ||
      JSON.stringify(credentials)!==JSON.stringify(exported('credentials','credentials-after.json'))) throw new Error('State changed during audit.');
  console.log('CTI_MANAGED_AUDIT_RESULT:v1:'+JSON.stringify(result));
} catch {
  console.error('Preparation check could not finish. No tasks, credentials or messages were changed. Keep the installation folder and review any interrupted preparation.');
  process.exitCode=1;
} finally {
  rmSync(work,{recursive:true,force:true});
  if (ownsLock) rmSync(lock,{force:true});
}
