// Execute the real read-only helper using in-memory n8n/fs fixtures.
import assert from 'node:assert/strict';
import { readFileSync,readdirSync } from 'node:fs';
import { posix } from 'node:path';
import vm from 'node:vm';
import { makePlan } from '../managed-bootstrap-plan.mjs';
import { auditManagedReadiness } from '../managed-readiness-plan.mjs';
const root=new URL('../../workflows/',import.meta.url);
const templates=readdirSync(root).filter(f=>f.endsWith('.json')).map(f=>JSON.parse(readFileSync(new URL(f,root),'utf8')));
const source=readFileSync(new URL('../managed-readiness.mjs',import.meta.url),'utf8').replace(/^import .*;\r?\n/gm,'');
const prefix='/home/node/.n8n/'; const lock=prefix+'.cti-managed-connection-lock-v1';
function run(options={}) {
  const boot=makePlan(templates,{databasePassword:'x'.repeat(43),aiKey:'synthetic-provider-key-not-real'});
  const workflows=boot.workflows;
  const credentials=boot.credentials.map(c=>({...c,data:'encrypted-only-fixture'}));
  const before=JSON.stringify({workflows,credentials});
  const files=new Map([[prefix+'.cti-managed-bootstrap-v1',JSON.stringify({schema:1,database:true,workflows:8})]]);
  if (options.marker) files.set(options.marker,'existing');
  if (options.noReceipt) files.delete(prefix+'.cti-managed-bootstrap-v1');
  const writes=[]; const calls=[]; const output=[]; const errors=[]; const process={exitCode:0};
  let workflowExports=0;
  vm.runInNewContext(source,{
    readFileSync:path=>{
      if (path===0) return options.rawInput??JSON.stringify(options.configuration??{includeAi:true,includeTelegram:false});
      const template=/^\/tmp\/cti-self-hosted-workflows\/([0-7])\.json$/.exec(path);
      if (template) return JSON.stringify(templates[Number(template[1])]);
      if (!files.has(path)) throw new Error('Missing fixture file');
      return files.get(path);
    },
    writeFileSync:(path,value,settings)=>{
      assert.equal(path,lock); assert.equal(settings.mode,0o600); assert.equal(settings.flag,'wx');
      if (files.has(path)) throw new Error('Existing fixture lock');
      files.set(path,value); writes.push(path);
    },
    existsSync:path=>files.has(path),
    readdirSync:()=>templates.map((_,i)=>`${i}.json`),
    mkdtempSync:()=>'/dev/shm/cti-readiness-fixture',
    rmSync:(path,settings={})=>{for (const name of [...files.keys()]) if(name===path || (settings.recursive && name.startsWith(path+'/'))) files.delete(name);},
    join:posix.join,Buffer,process,auditManagedReadiness,
    console:{log:message=>output.push(message),error:message=>errors.push(message)},
    execFileSync:(command,args)=>{
      assert.equal(command,'n8n'); assert.ok(['export:workflow','export:credentials'].includes(args[0]));
      assert.ok(!args.includes('--decrypted')); calls.push(args[0]);
      if (options.failExport) throw new Error('Fixture encrypted secret should never be echoed');
      const file=args.find(a=>a.startsWith('--output=')).slice(9);
      if (args[0]==='export:workflow') {
        workflowExports++;
        files.set(file,JSON.stringify(options.changedSnapshot && workflowExports===2 ? workflows.slice(1) : workflows));
      } else files.set(file,JSON.stringify(credentials));
    }
  },{timeout:5000});
  assert.equal(JSON.stringify({workflows,credentials}),before);
  assert.ok(![...files.keys()].some(p=>p.startsWith('/dev/shm/cti-readiness-fixture/')));
  assert.ok(!JSON.stringify([...output,...errors]).includes('encrypted-only-fixture'));
  assert.ok(!JSON.stringify([...output,...errors]).includes('synthetic-provider-key-not-real'));
  if (options.marker!==lock) assert.ok(!files.has(lock));
  return {process,files,writes,calls,output,errors};
}
const success=run();
assert.equal(success.process.exitCode,0); assert.equal(success.calls.length,4);
assert.equal(success.output.length,1);
const result=JSON.parse(success.output[0].slice('CTI_MANAGED_AUDIT_RESULT:v1:'.length));
assert.equal(result.ready,true); assert.equal(result.telegram,null);
const incomplete=run({configuration:{includeAi:true,includeTelegram:true}});
assert.equal(incomplete.process.exitCode,0);
assert.equal(JSON.parse(incomplete.output[0].slice('CTI_MANAGED_AUDIT_RESULT:v1:'.length)).ready,false);
for (const marker of [lock,...['bootstrap','ai','telegram'].map(n=>`${prefix}.cti-managed-${n}-pending-v1`)]) {
  const denied=run({marker}); assert.equal(denied.process.exitCode,1); assert.equal(denied.calls.length,0);
  assert.equal(denied.files.get(marker),'existing'); assert.deepEqual(denied.output,[]);
}
for (const options of [{noReceipt:true},{rawInput:'x'.repeat(8193)},{configuration:{includeAi:'true',includeTelegram:false}},{failExport:true},{changedSnapshot:true}]) {
  const denied=run(options); assert.equal(denied.process.exitCode,1); assert.deepEqual(denied.output,[]);
  assert.ok(!denied.errors.join('').includes('Fixture encrypted secret'));
}
console.log('PASS: actual read-only audit helper, encrypted exports only, no imports/activation/network, exclusive lock, interrupted-operation refusal, consistent snapshot checks and scratch cleanup.');
