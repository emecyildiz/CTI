import { normalizeTelegramNode } from './managed-telegram-plan.mjs';

const names = ['CTI Article Analysis','CTI Retention Maintenance','CTI Source Collection','CTI Telegram Query',
  'CTI Vulnerability Enrichment','CTI Weekly Report','CTI Weekly Telegram Delivery','n8n Workflow Error Alerts'];
const telegramNames = new Set(['CTI Telegram Query','CTI Weekly Telegram Delivery','n8n Workflow Error Alerts']);
const aiType = '@n8n/n8n-nodes-langchain.googleGemini';
const telegramTypes = new Set(['n8n-nodes-base.telegram','n8n-nodes-base.telegramTrigger']);
const idValid = id => typeof id === 'string' && /^[A-Za-z0-9_-]{1,64}(?![\s\S])/.test(id);
function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(k=>[k,canonical(value[k])]));
  return value;
}
const equal = (a,b) => JSON.stringify(canonical(a)) === JSON.stringify(canonical(b));
function normalized(node,name) {
  if (telegramNames.has(name)) return normalizeTelegramNode(node,name);
  const result=structuredClone(node); delete result.credentials; return result;
}
function credential(credentials,name,type) {
  const found=credentials.filter(c=>c.name===name);
  return found.length===1 && found[0].type===type && idValid(found[0].id) && typeof found[0].data==='string' && found[0].data.length>0 ? found[0] : null;
}
function bindings(nodes,type,key,stored,expectedCount) {
  const matching=nodes.filter(n=>type(n.type));
  return stored !== null && matching.length===expectedCount && matching.every(n=>equal(n.credentials,{[key]:{id:stored.id,name:stored.name}}));
}

// Structural preparation audit only. No credentials are decrypted, no provider is
// contacted, no tasks are activated, and no input objects are modified.
export function auditManagedReadiness(templates,workflows,credentials,{includeAi=true,includeTelegram=false}={}) {
  if (typeof includeAi!=='boolean' || typeof includeTelegram!=='boolean') throw new Error('Invalid audit options.');
  const result={schema:1,drafts:false,database:false,errorRouting:false,ai:includeAi?false:null,telegram:includeTelegram?false:null,ready:false};
  if (!Array.isArray(templates) || templates.length!==8 || new Set(templates.map(w=>w.name)).size!==8 || templates.some(w=>!names.includes(w.name)) ||
      !Array.isArray(workflows) || workflows.length!==8 || new Set(workflows.map(w=>w.name)).size!==8 || new Set(workflows.map(w=>w.id)).size!==8 ||
      workflows.some(w=>!names.includes(w.name) || !idValid(w.id)) || !Array.isArray(credentials) ||
      credentials.some(c=>!idValid(c.id)) || new Set(credentials.map(c=>c.id)).size!==credentials.length) return result;
  try {
    result.drafts=workflows.every(w=>{
      const template=templates.find(t=>t.name===w.name);
      if (w.active!==false || w.activeVersionId || w.activeVersion || w.isArchived || !Array.isArray(w.nodes) ||
          w.nodes.length!==template.nodes.length || new Set(w.nodes.map(n=>n.id)).size!==w.nodes.length || !equal(w.connections,template.connections)) return false;
      for (const [key,value] of Object.entries(template.settings??{}))
        if (key!=='errorWorkflow' && !equal(w.settings?.[key],value)) return false;
      return w.nodes.every(n=>{
        const expected=template.nodes.find(t=>t.id===n.id);
        const allowed=n.type==='n8n-nodes-base.postgres'?'postgres':n.type===aiType?'googlePalmApi':telegramTypes.has(n.type)?'telegramApi':null;
        if (n.credentials && (allowed===null || Object.keys(n.credentials).length!==1 || !Object.hasOwn(n.credentials,allowed))) return false;
        return expected && equal(normalized(n,w.name),normalized(expected,w.name));
      });
    });
    const nodes=workflows.flatMap(w=>w.nodes);
    result.database=bindings(nodes,t=>t==='n8n-nodes-base.postgres','postgres',credential(credentials,'CTI Self-Hosted - PostgreSQL','postgres'),31);
    const handler=workflows.find(w=>w.name==='n8n Workflow Error Alerts');
    result.errorRouting=!handler.settings?.errorWorkflow && workflows.filter(w=>w.id!==handler.id).every(w=>w.settings?.errorWorkflow===handler.id);
    if (includeAi) result.ai=bindings(nodes,t=>t===aiType,'googlePalmApi',credential(credentials,'CTI Self-Hosted - Google Gemini','googlePalmApi'),2);
    if (includeTelegram) {
      const guard=workflows.find(w=>w.name==='CTI Telegram Query').nodes.find(n=>n.name==='Authorize and Parse Request');
      const ids=[...guard.parameters.jsCode.replace(/\r\n/g,'\n').matchAll(/^const allowedId = '([0-9]{5,20})';$/gm)].map(m=>m[1]);
      const delivery=workflows.filter(w=>w.name==='CTI Weekly Telegram Delivery' || w.name==='n8n Workflow Error Alerts')
        .flatMap(w=>w.nodes.filter(n=>n.type==='n8n-nodes-base.telegram').map(n=>n.parameters.chatId));
      result.telegram=ids.length===1 && delivery.length===2 && delivery.every(id=>id===ids[0]) &&
        bindings(nodes,t=>telegramTypes.has(t),'telegramApi',credential(credentials,'CTI Self-Hosted - Telegram','telegramApi'),5);
    }
    result.ready=result.drafts && result.database && result.errorRouting && result.ai!==false && result.telegram!==false;
  } catch { result.drafts=false; result.ready=false; }
  return result;
}
