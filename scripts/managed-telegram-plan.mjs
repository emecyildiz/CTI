import { randomBytes, createHash } from 'node:crypto';
const credentialName = 'CTI Self-Hosted - Telegram';
const types = new Set(['n8n-nodes-base.telegram', 'n8n-nodes-base.telegramTrigger']);
const targets = ['CTI Telegram Query', 'CTI Weekly Telegram Delivery', 'n8n Workflow Error Alerts'];
const guardName = 'Authorize and Parse Request';
const guardPlaceholder = '__CTI_TELEGRAM_ALLOWED_ID__';
const chatPlaceholder = '__CTI_TELEGRAM_CHAT_ID__';
const guardHash = '4111e8444d2eace31175136ccbf7819a437287dd832fed6c325c3359ba221df0';
const assignment = /^const allowedId = '(__CTI_TELEGRAM_ALLOWED_ID__|[0-9]{5,20})';$/gm;
function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(k => [k, canonical(value[k])]));
  return value;
}
const equal = (a,b) => JSON.stringify(canonical(a)) === JSON.stringify(canonical(b));
export function normalizeTelegramNode(node, workflowName) {
  const copy = structuredClone(node);
  delete copy.credentials;
  if (workflowName === 'CTI Telegram Query' && node.name === guardName) {
    const source = copy.parameters?.jsCode;
    if (typeof source !== 'string' || source.length > 10000) throw new Error('Missing private authorization guard.');
    const normalized = source.replace(/\r\n/g, '\n').trim();
    if ([...normalized.matchAll(assignment)].length !== 1) throw new Error('Ambiguous private authorization guard.');
    copy.parameters.jsCode = normalized.replace(assignment, `const allowedId = '${guardPlaceholder}';`);
    if (createHash('sha256').update(copy.parameters.jsCode).digest('hex') !== guardHash) throw new Error('Modified authorization guard.');
  }
  if ((workflowName === 'CTI Weekly Telegram Delivery' && node.name === 'Send Weekly Report to Telegram') ||
      (workflowName === 'n8n Workflow Error Alerts' && node.name === 'Send a text message')) {
    if (typeof copy.parameters?.chatId !== 'string' || !/^(__CTI_TELEGRAM_CHAT_ID__|[0-9]{5,20})$/.test(copy.parameters.chatId))
      throw new Error('Unexpected private delivery target.');
    copy.parameters.chatId = chatPlaceholder;
  }
  return copy;
}

// Pure planning: reviewed inactive drafts only. No provider/network calls or mutation.
export function makeTelegramPlan(templates, workflows, credentials, { botToken, chatId } = {}) {
  if (typeof botToken !== 'string' || !/^[0-9]{5,20}:[A-Za-z0-9_-]{20,128}(?![\s\S])/.test(botToken) ||
      typeof chatId !== 'string' || !/^[0-9]{5,20}(?![\s\S])/.test(chatId)) throw new Error('Invalid private Telegram configuration.');
  if (!Array.isArray(templates) || templates.length !== 8 || new Set(templates.map(w=>w.name)).size !== 8 ||
      !Array.isArray(workflows) || workflows.length !== 8 || new Set(workflows.map(w=>w.name)).size !== 8 ||
      new Set(workflows.map(w=>w.id)).size !== 8 || workflows.some(w=>!templates.some(t=>t.name===w.name) ||
        typeof w.id !== 'string' || !/^[A-Za-z0-9_-]{1,64}$/.test(w.id) || w.active !== false || w.activeVersionId || w.activeVersion || w.isArchived))
    throw new Error('Installation is not the expected inactive package.');
  if (!Array.isArray(credentials) || new Set(credentials.map(c=>c.id)).size !== credentials.length) throw new Error('Credential identities are ambiguous.');
  const matching = credentials.filter(c=>c.name===credentialName);
  if (matching.length > 1 || matching.some(c=>c.type !== 'telegramApi' || typeof c.id !== 'string' || !/^[A-Za-z0-9_-]{1,64}$/.test(c.id)))
    throw new Error('Reserved Telegram credential conflict.');
  const id = matching[0]?.id ?? randomBytes(12).toString('hex');
  if (credentials.some(c=>c.id===id && c.name!==credentialName)) throw new Error('Credential identity conflict.');
  const handler = workflows.find(w=>w.name === 'n8n Workflow Error Alerts');
  if (!handler) throw new Error('Missing reviewed error handler.');
  const planned = [];
  let count = 0;
  for (const name of targets) {
    const expected = templates.find(w=>w.name===name);
    const current = workflows.find(w=>w.name===name);
    if (!expected || !current || !Array.isArray(current.nodes) || current.nodes.length !== expected.nodes.length ||
        new Set(current.nodes.map(n=>n.id)).size !== current.nodes.length || !equal(current.connections, expected.connections))
      throw new Error('Modified Telegram graph refused.');
    if ((name === handler.name && current.settings?.errorWorkflow) ||
        (name !== handler.name && current.settings?.errorWorkflow !== handler.id))
      throw new Error('Telegram error route differs from managed bootstrap.');
    for (const [setting,value] of Object.entries(expected.settings ?? {})) {
      if (setting !== 'errorWorkflow' && !equal(current.settings?.[setting],value)) throw new Error('Changed reviewed Telegram setting refused.');
    }
    for (const node of current.nodes) {
      const reviewed = expected.nodes.find(n=>n.id===node.id);
      if (!reviewed || !equal(normalizeTelegramNode(node,name), normalizeTelegramNode(reviewed,name))) throw new Error('Modified Telegram node refused.');
      if (types.has(node.type) && node.credentials &&
          (!matching.length || !equal(node.credentials,{telegramApi:{id,name:credentialName}}))) throw new Error('Unrecognized Telegram credential binding.');
    }
    const update = structuredClone(current);
    delete update.versionId; delete update.activeVersionId;
    for (const node of update.nodes) {
      if (types.has(node.type)) { node.credentials={telegramApi:{id,name:credentialName}}; count++; }
      if (name === 'CTI Telegram Query' && node.name === guardName)
        node.parameters.jsCode = node.parameters.jsCode.replace(/\r\n/g,'\n').replace(assignment, `const allowedId = '${chatId}';`);
      if ((name === 'CTI Weekly Telegram Delivery' && node.name === 'Send Weekly Report to Telegram') ||
          (name === 'n8n Workflow Error Alerts' && node.name === 'Send a text message')) node.parameters.chatId = chatId;
    }
    planned.push(update);
  }
  if (count !== 5) throw new Error('Unexpected Telegram node count.');
  return { workflows:planned, credential:{id,name:credentialName,type:'telegramApi',data:{accessToken:botToken,baseUrl:'https://api.telegram.org'}} };
}
