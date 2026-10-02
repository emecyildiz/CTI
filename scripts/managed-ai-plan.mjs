import { randomBytes } from 'node:crypto';
const targetNames = ['CTI Article Analysis', 'CTI Weekly Report'];
const credentialName = 'CTI Self-Hosted - Google Gemini';
const aiType = '@n8n/n8n-nodes-langchain.googleGemini';
function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(k => [k, canonical(value[k])]));
  return value;
}
const equal = (a,b) => JSON.stringify(canonical(a)) === JSON.stringify(canonical(b));
function withoutCredentials(node) { const result=structuredClone(node); delete result.credentials; return result; }

// Pure, read-only planning. Inputs are reviewed templates and encrypted CLI exports.
export function makeAiPlan(templates, workflows, credentials, aiKey) {
  if (typeof aiKey !== 'string' || !/^[\x21-\x7e]{20,512}(?![\s\S])/.test(aiKey)) throw new Error('Unsupported AI key format.');
  if (!Array.isArray(workflows) || workflows.length !== 8 || !Array.isArray(templates) || templates.length !== 8 ||
      new Set(workflows.map(w=>w.name)).size !== 8 || new Set(workflows.map(w=>w.id)).size !== 8 ||
      workflows.some(w=>!templates.some(t=>t.name===w.name) || typeof w.id!=='string' || !/^[A-Za-z0-9_-]{1,64}$/.test(w.id) || w.active !== false || w.activeVersionId || w.activeVersion || w.isArchived))
    throw new Error('Installation is not the expected inactive package.');
  if (!Array.isArray(credentials)) throw new Error('Credential export unavailable.');
  const matching=credentials.filter(c=>c.name===credentialName);
  if (matching.length>1 || matching.some(c=>c.type!=='googlePalmApi' || typeof c.id!=='string' || !/^[A-Za-z0-9_-]{1,64}$/.test(c.id)))
    throw new Error('Reserved AI credential conflict.');
  const id=matching[0]?.id ?? randomBytes(12).toString('hex');
  if (credentials.some(c=>c.id===id && c.name!==credentialName)) throw new Error('Credential identity conflict.');
  const planned=[];
  let count=0;
  for (const name of targetNames) {
    const expected=templates.find(w=>w.name===name);
    const current=workflows.find(w=>w.name===name);
    if (!expected || !current || !Array.isArray(current.nodes) ||
        !equal(current.connections,expected.connections) ||
        current.nodes.length!==expected.nodes.length || new Set(current.nodes.map(n=>n.id)).size!==current.nodes.length)
      throw new Error('Customized AI workflow refused.');
    for (const [setting,value] of Object.entries(expected.settings ?? {})) {
      if (setting !== 'errorWorkflow' && !equal(current.settings?.[setting],value)) throw new Error('Changed reviewed AI setting refused.');
    }
    for (const node of current.nodes) {
      const reviewed=expected.nodes.find(n=>n.id===node.id);
      if (!reviewed || !equal(withoutCredentials(node),withoutCredentials(reviewed))) throw new Error('Customized AI node refused.');
      if (node.type===aiType && node.credentials &&
          (!matching.length || !equal(node.credentials,{googlePalmApi:{id,name:credentialName}})))
        throw new Error('Unrecognized AI credential binding.');
    }
    const update=structuredClone(current);
    delete update.versionId;
    delete update.activeVersionId;
    for (const node of update.nodes) if (node.type===aiType) {
      node.credentials={googlePalmApi:{id,name:credentialName}}; count++;
    }
    planned.push(update);
  }
  if (count!==2) throw new Error('Unexpected AI node count.');
  return { workflows:planned, credential:{id,name:credentialName,type:'googlePalmApi',data:{apiKey:aiKey,host:'https://generativelanguage.googleapis.com'}} };
}
