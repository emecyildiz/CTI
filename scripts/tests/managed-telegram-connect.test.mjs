// Execute the real container helper against in-memory fs/n8n fixtures. No Docker/network.
import assert from 'node:assert/strict';
import {readFileSync,readdirSync} from 'node:fs';
import {posix} from 'node:path';
import vm from 'node:vm';
import {makePlan} from '../managed-bootstrap-plan.mjs';
import {makeTelegramPlan} from '../managed-telegram-plan.mjs';
const root=new URL('../../workflows/',import.meta.url);
const templates=readdirSync(root).filter(f=>f.endsWith('.json')).map(f=>JSON.parse(readFileSync(new URL(f,root),'utf8')));
const source=readFileSync(new URL('../managed-telegram-connect.mjs',import.meta.url),'utf8').replace(/^import .*;\r?\n/gm,'');
const prefix='/home/node/.n8n/';
const pending=prefix+'.cti-managed-telegram-pending-v1';
const lock=prefix+'.cti-managed-connection-lock-v1';
const config={botToken:`123456789:${'A'.repeat(35)}`,chatId:'123456789'};
function run(options={}) {
  const bootstrap=makePlan(templates,{databasePassword:'x'.repeat(43),aiKey:'synthetic-preserved-ai-key'});
  let workflows=options.workflows ?? bootstrap.workflows;
  let credentials=bootstrap.credentials.map(c=>({...c,data:'encrypted-fixture'}));
  const files=new Map([[prefix+'.cti-managed-bootstrap-v1',JSON.stringify({schema:1,database:true,workflows:8})]]);
  if(options.marker) files.set(options.marker,'');
  if(options.noReceipt) files.delete(prefix+'.cti-managed-bootstrap-v1');
  const output=[]; const errors=[]; const calls=[]; const writes=[];
  let mutations=0;
  function read(path) {
    if(path===0) return JSON.stringify(options.configuration ?? config);
    const template=/^\/tmp\/cti-self-hosted-workflows\/([0-7])\.json$/.exec(path);
    if(template) return JSON.stringify(templates[Number(template[1])]);
    if(!files.has(path)) throw new Error('Fixture file missing');
    return files.get(path);
  }
  function write(path,value,settings={}) {
    if(settings.flag==='wx' && files.has(path)) throw new Error('Exclusive fixture file exists');
    files.set(path,value); writes.push({path,mode:settings.mode});
  }
  const before=JSON.stringify(workflows);
  const process={exitCode:0};
  vm.runInNewContext(source,{
    readFileSync:read,writeFileSync:write,
    readdirSync:()=>templates.map((_,i)=>`${i}.json`),
    mkdtempSync:()=>'/dev/shm/cti-telegram-connect-fixture',
    existsSync:path=>files.has(path),
    rmSync:(path,settings={})=>{
      for(const name of [...files.keys()]) if(name===path || (settings.recursive && name.startsWith(path+'/'))) files.delete(name);
    },
    join:posix.join,
    makeTelegramPlan,structuredClone,Buffer,
    process,console:{log:message=>output.push(message),error:message=>errors.push(message)},
    execFileSync:(command,args)=>{
      assert.equal(command,'n8n'); calls.push(args[0]);
      assert.ok(!JSON.stringify(args).includes(config.botToken));
      const fileArg=args.find(x=>x.startsWith('--output=') || x.startsWith('--input='));
      const file=fileArg?.slice(fileArg.indexOf('=')+1);
      if(args[0]==='export:workflow') files.set(file,JSON.stringify(workflows));
      else if(args[0]==='export:credentials') { assert.ok(!args.includes('--decrypted')); files.set(file,JSON.stringify(credentials)); }
      else if(args[0]==='import:credentials') {
        mutations++;
        const [record]=JSON.parse(read(file));
        assert.equal(record.data.accessToken,config.botToken);
        credentials=credentials.filter(c=>c.id!==record.id).concat({...record,data:'encrypted-new-fixture'});
        if(options.failImport) throw new Error('Fixture interrupted after credential import');
      } else if(args[0]==='import:workflow') {
        mutations++;
        for(const update of JSON.parse(read(file))) workflows=workflows.map(w=>w.id===update.id?update:w);
        if(options.corruptReadback) workflows.find(w=>w.name==='CTI Weekly Telegram Delivery').nodes.find(n=>n.type==='n8n-nodes-base.telegram').parameters.chatId='999999999';
        if(options.corruptOtherCredential) credentials[0].name='changed-unrelated-credential';
      } else throw new Error('Unexpected fixture command');
    },
  },{timeout:5000});
  assert.ok(!JSON.stringify([...output,...errors]).includes(config.botToken));
  assert.ok(![...files.keys()].some(path=>path.startsWith('/dev/shm/cti-telegram-connect-fixture/')));
  if(!options.marker || options.marker!==lock) assert.ok(!files.has(lock));
  return {process,files,output,errors,calls,writes,mutations,before,after:JSON.stringify(workflows)};
}
const success=run();
assert.equal(success.process.exitCode,0);
assert.deepEqual(success.output,['CTI_MANAGED_TELEGRAM_RESULT:v1:prepared']);
assert.equal(success.mutations,2);
assert.ok(!success.files.has(pending));
assert.ok(success.writes.filter(w=>w.path.endsWith('telegram-credential.json') || w.path.endsWith('telegram-workflows.json')).every(w=>w.mode===0o600));
for(const marker of [lock,pending,prefix+'.cti-managed-ai-pending-v1',prefix+'.cti-managed-bootstrap-pending-v1']) {
  const refused=run({marker});
  assert.equal(refused.process.exitCode,1); assert.equal(refused.mutations,0);
  assert.deepEqual(refused.output,[]); assert.ok(refused.files.has(marker));
}
for(const options of [{noReceipt:true},{configuration:{...config,chatId:'-100123456789'}},{configuration:{...config,botToken:'invalid'}}]) {
  const refused=run(options);
  assert.equal(refused.process.exitCode,1); assert.equal(refused.mutations,0);
  assert.ok(!refused.files.has(pending)); assert.equal(refused.after,refused.before);
}
for(const options of [{failImport:true},{corruptReadback:true},{corruptOtherCredential:true}]) {
  const partial=run(options);
  assert.equal(partial.process.exitCode,1); assert.ok(partial.files.has(pending));
  assert.deepEqual(partial.output,[]);
}
console.log('PASS: real Telegram helper with in-memory CLI, encrypted exports, private stdin, mode 0600, shared lock, preflight refusals, partial/readback failure markers and scratch cleanup.');
