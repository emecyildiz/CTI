import assert from 'node:assert/strict';
import {readFileSync,readdirSync} from 'node:fs';
import {makePlan} from '../managed-bootstrap-plan.mjs';
import {makeAiPlan} from '../managed-ai-plan.mjs';
const root=new URL('../../workflows/',import.meta.url);
const templates=readdirSync(root).filter(f=>f.endsWith('.json')).map(f=>JSON.parse(readFileSync(new URL(f,root),'utf8')));
const bootstrap=makePlan(templates,{databasePassword:'x'.repeat(43)});
const input=JSON.stringify(bootstrap);
const key='synthetic-ai-key-not-real';
const plan=makeAiPlan(templates,bootstrap.workflows,bootstrap.credentials,key);
assert.equal(plan.workflows.length,2);
assert.equal(plan.workflows.flatMap(w=>w.nodes).filter(n=>n.credentials?.googlePalmApi).length,2);
for(const updated of plan.workflows) {
  const original=bootstrap.workflows.find(w=>w.id===updated.id);
  assert.deepEqual(updated.connections,original.connections);
  assert.deepEqual(updated.settings,original.settings);
  for(const node of updated.nodes.filter(n=>n.type!=='@n8n/n8n-nodes-langchain.googleGemini')) assert.deepEqual(node,original.nodes.find(n=>n.id===node.id));
}
assert.equal(JSON.stringify(bootstrap),input);
const connected=structuredClone(bootstrap.workflows);
for(const update of plan.workflows) connected[connected.findIndex(w=>w.id===update.id)]=update;
assert.equal(makeAiPlan(templates,connected,[...bootstrap.credentials,plan.credential],key).credential.id,plan.credential.id);
for(const mutation of [
  w=>w[0].active=true,
  w=>w[0].activeVersionId='published',
  w=>w[0].activeVersion={id:'published'},
  w=>w[0].isArchived=true,
  w=>w[0].nodes[0].parameters={injected:true},
  w=>w[0].nodes[0].disabled=true,
  w=>w[0].connections={unexpected:[]},
  w=>w[0].settings.timezone='unreviewed-timezone',
  w=>w[1].id=w[0].id,
  w=>w.pop()
]) { const changed=structuredClone(bootstrap.workflows); mutation(changed); assert.throws(()=>makeAiPlan(templates,changed,bootstrap.credentials,key)); }
assert.throws(()=>makeAiPlan(templates,bootstrap.workflows,[{id:'reserved',name:plan.credential.name,type:'unexpected'}],key));
assert.throws(()=>makeAiPlan(templates,bootstrap.workflows,[plan.credential,plan.credential],key));
assert.throws(()=>makeAiPlan(templates,bootstrap.workflows,bootstrap.credentials,'bad\nkey'));
assert.throws(()=>makeAiPlan(templates,bootstrap.workflows,bootstrap.credentials,key+'\n'));
console.log('PASS: managed AI plan, two-node scope, preserved database/settings, stable credential and modified/active refusal.');
