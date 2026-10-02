import assert from 'node:assert/strict';
import {readFileSync,readdirSync} from 'node:fs';
import vm from 'node:vm';
import {makePlan} from '../managed-bootstrap-plan.mjs';
import {makeTelegramPlan} from '../managed-telegram-plan.mjs';
const root = new URL('../../workflows/',import.meta.url);
const templates = readdirSync(root).filter(f=>f.endsWith('.json')).map(f=>JSON.parse(readFileSync(new URL(f,root),'utf8')));
const bootstrap = makePlan(templates,{databasePassword:'x'.repeat(43),aiKey:'synthetic-preserved-ai-key'});
const original = JSON.stringify(bootstrap);
const config = {botToken:`123456789:${'A'.repeat(35)}`,chatId:'123456789'};
const plan = makeTelegramPlan(templates,bootstrap.workflows,bootstrap.credentials,config);
assert.equal(plan.workflows.length,3);
assert.equal(plan.workflows.flatMap(w=>w.nodes).filter(n=>n.credentials?.telegramApi).length,5);
assert.equal(plan.credential.data.accessToken,config.botToken);
assert.equal(JSON.stringify(bootstrap),original);
for (const update of plan.workflows) {
  const before = bootstrap.workflows.find(w=>w.id===update.id);
  assert.deepEqual(update.settings,before.settings); assert.deepEqual(update.connections,before.connections);
  for (const node of update.nodes.filter(n=>n.type==='n8n-nodes-base.postgres')) assert.deepEqual(node,before.nodes.find(n=>n.id===node.id));
}
const query = plan.workflows.find(w=>w.name==='CTI Telegram Query');
const guard = query.nodes.find(n=>n.name==='Authorize and Parse Request').parameters.jsCode;
for (const update of [
  {message:{from:{id:123456789},chat:{id:123456789},text:'/menu'}},
  {callback_query:{id:'test',from:{id:123456789},message:{chat:{id:123456789}},data:'cti:menu'}}
]) assert.equal(vm.runInNewContext(`(function(){${guard}})()`,{$input:{first:()=>({json:update})}}).length,1);
for (const update of [
  {message:{from:{id:999999999},chat:{id:123456789},text:'/menu'}},
  {message:{from:{id:123456789},chat:{id:-100123456789},text:'/menu'}},
  {callback_query:{from:{id:999999999},message:{chat:{id:123456789}},data:'cti:menu'}},
  {}
]) assert.equal(vm.runInNewContext(`(function(){${guard}})()`,{$input:{first:()=>({json:update})}}).length,0);
const connected = structuredClone(bootstrap.workflows);
for (const update of plan.workflows) connected[connected.findIndex(w=>w.id===update.id)] = update;
const updated = makeTelegramPlan(templates,connected,[...bootstrap.credentials,plan.credential],{...config,chatId:'987654321'});
assert.equal(updated.credential.id,plan.credential.id);
const changedGuard=updated.workflows.find(w=>w.name==='CTI Telegram Query').nodes.find(n=>n.name==='Authorize and Parse Request').parameters.jsCode;
assert.equal(vm.runInNewContext(`(function(){${changedGuard}})()`,{$input:{first:()=>({json:{message:{from:{id:123456789},chat:{id:123456789},text:'/menu'}}})}}).length,0);
assert.equal(vm.runInNewContext(`(function(){${changedGuard}})()`,{$input:{first:()=>({json:{message:{from:{id:987654321},chat:{id:987654321},text:'/menu'}}})}}).length,1);
const windowsLines=structuredClone(connected);
windowsLines.find(w=>w.name==='CTI Telegram Query').nodes.find(n=>n.name==='Authorize and Parse Request').parameters.jsCode=guard.replace(/\n/g,'\r\n');
assert.equal(makeTelegramPlan(templates,windowsLines,[...bootstrap.credentials,plan.credential],{...config,chatId:'987654321'}).workflows.find(w=>w.name==='CTI Telegram Query').nodes.find(n=>n.name==='Authorize and Parse Request').parameters.jsCode,changedGuard);
for (const workflow of updated.workflows.filter(w=>w.name!=='CTI Telegram Query'))
  assert.equal(workflow.nodes.find(n=>n.type==='n8n-nodes-base.telegram').parameters.chatId,'987654321');
for (const mutate of [
  w=>w[0].active=true, w=>w[0].activeVersionId='published', w=>w[0].activeVersion={}, w=>w[0].isArchived=true,
  w=>w.pop(), w=>w[1].id=w[0].id,
  w=>w.find(x=>x.name==='CTI Telegram Query').connections={},
  w=>w.find(x=>x.name==='CTI Telegram Query').nodes.find(n=>n.name==='Authorize and Parse Request').disabled=true,
  w=>w.find(x=>x.name==='CTI Telegram Query').nodes.find(n=>n.name==='Authorize and Parse Request').parameters.jsCode='return $input.all();',
  w=>w.find(x=>x.name==='CTI Telegram Query').nodes.find(n=>n.name==='Authorize and Parse Request').onError='continueRegularOutput',
  w=>w.find(x=>x.name==='CTI Weekly Telegram Delivery').nodes.find(n=>n.type==='n8n-nodes-base.telegram').parameters.chatId='={{ $json.untrusted }}',
  w=>w.find(x=>x.name==='CTI Telegram Query').settings.errorWorkflow='foreign-handler',
  w=>w.find(x=>x.name==='CTI Weekly Telegram Delivery').settings.timezone='unexpected',
  w=>w.find(x=>x.name==='n8n Workflow Error Alerts').nodes[0].parameters={injected:true},
  w=>w.find(x=>x.name==='CTI Telegram Query').nodes[0].credentials={telegramApi:{id:'foreign',name:'foreign'}}
]) { const changed=structuredClone(bootstrap.workflows); mutate(changed); assert.throws(()=>makeTelegramPlan(templates,changed,bootstrap.credentials,config)); }
for (const bad of [undefined,{}, {...config,chatId:'-100123456789'}, {...config,chatId:'1234'}, {...config,chatId:"12345';return [];"}, {...config,botToken:'invalid'}, {...config,botToken:config.botToken+'\n'}])
  assert.throws(()=>makeTelegramPlan(templates,bootstrap.workflows,bootstrap.credentials,bad));
assert.throws(()=>makeTelegramPlan(templates,bootstrap.workflows,[plan.credential,plan.credential],config));
assert.throws(()=>makeTelegramPlan(templates,bootstrap.workflows,[{...plan.credential,type:'unexpected'}],config));
console.log('PASS: Telegram three-task/five-node scope, private message/callback guard, destination update, preserved DB/AI/error routes and unsafe configuration refusal.');
