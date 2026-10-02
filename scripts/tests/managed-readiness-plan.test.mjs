import assert from 'node:assert/strict';
import { readFileSync,readdirSync } from 'node:fs';
import { makePlan } from '../managed-bootstrap-plan.mjs';
import { makeTelegramPlan } from '../managed-telegram-plan.mjs';
import { auditManagedReadiness } from '../managed-readiness-plan.mjs';
const root=new URL('../../workflows/',import.meta.url);
const templates=readdirSync(root).filter(f=>f.endsWith('.json')).map(f=>JSON.parse(readFileSync(new URL(f,root),'utf8')));
const boot=makePlan(templates,{databasePassword:'x'.repeat(43),aiKey:'synthetic-provider-key-not-real'});
const configuration={botToken:'123456789:'+'A'.repeat(35),chatId:'123456789'};
const telegram=makeTelegramPlan(templates,boot.workflows,boot.credentials,configuration);
const workflows=structuredClone(boot.workflows);
for (const update of telegram.workflows) workflows[workflows.findIndex(w=>w.id===update.id)]=update;
const credentials=[...boot.credentials,telegram.credential].map(c=>({...c,data:'encrypted-fixture-only'}));
const before=JSON.stringify({templates,workflows,credentials});
const result=auditManagedReadiness(templates,workflows,credentials,{includeAi:true,includeTelegram:true});
assert.deepEqual(result,{schema:1,drafts:true,database:true,errorRouting:true,ai:true,telegram:true,ready:true});
assert.equal(JSON.stringify({templates,workflows,credentials}),before);
assert.equal(auditManagedReadiness(templates,boot.workflows,credentials,{includeAi:true,includeTelegram:true}).telegram,false);
const skipped=auditManagedReadiness(templates,boot.workflows,credentials,{includeAi:false,includeTelegram:false});
assert.equal(skipped.ready,true); assert.equal(skipped.ai,null); assert.equal(skipped.telegram,null);
for (const mutate of [
  w=>w[0].active=true,
  w=>w[0].activeVersion={id:'published'},
  w=>w[0].isArchived=true,
  w=>w[0].connections={},
  w=>w.find(x=>x.name==='CTI Source Collection').nodes[0].parameters={unreviewed:true},
  w=>w.find(x=>x.name==='CTI Telegram Query').nodes.find(n=>n.name==='Authorize and Parse Request').parameters.jsCode='return $input.all();',
  w=>w.find(x=>x.name==='CTI Weekly Telegram Delivery').nodes.find(n=>n.type==='n8n-nodes-base.telegram').parameters.chatId='987654321',
  w=>w[0].settings.errorWorkflow='foreign',
  w=>w.flatMap(x=>x.nodes).find(n=>n.type==='n8n-nodes-base.postgres').credentials.postgres.id='foreign',
  w=>w.flatMap(x=>x.nodes).find(n=>n.type==='@n8n/n8n-nodes-langchain.googleGemini').credentials.googlePalmApi.id='foreign',
  w=>w.flatMap(x=>x.nodes).find(n=>n.type==='n8n-nodes-base.telegram').credentials.telegramApi.id='foreign',
  w=>w[0].nodes[0].credentials={injected:{id:'foreign'}},
  w=>w.pop()
]) {
  const changed=structuredClone(workflows); mutate(changed);
  assert.equal(auditManagedReadiness(templates,changed,credentials,{includeAi:true,includeTelegram:true}).ready,false);
}
for (const changed of [credentials.slice(1),[...credentials,credentials[0]],credentials.map(c=>({...c,data:{decrypted:'not-accepted'}})),[...credentials,{...credentials[0],id:'extra'}]])
  assert.equal(auditManagedReadiness(templates,workflows,changed,{includeAi:true,includeTelegram:true}).ready,false);
assert.throws(()=>auditManagedReadiness(templates,workflows,credentials,{includeAi:'true'}));
assert.equal(JSON.stringify(result).includes(configuration.chatId),false);
assert.equal(JSON.stringify(result).includes(configuration.botToken),false);
console.log('PASS: read-only readiness snapshot, selected optional connections, all reviewed graphs, 31 DB/2 AI/5 Telegram bindings, private guard/destinations, encrypted-only metadata and unsafe state refusal.');
