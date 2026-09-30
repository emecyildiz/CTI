import { randomBytes } from 'node:crypto';

const names = ['CTI Article Analysis', 'CTI Retention Maintenance', 'CTI Source Collection',
  'CTI Telegram Query', 'CTI Vulnerability Enrichment', 'CTI Weekly Report',
  'CTI Weekly Telegram Delivery', 'n8n Workflow Error Alerts'];
export function makePlan(templates, { databasePassword, databaseName = 'cti', aiKey } = {}) {
  if (!/^[A-Za-z0-9_-]{43}$/.test(databasePassword ?? '') ||
      !/^[A-Za-z_][A-Za-z0-9_]{0,62}$/.test(databaseName)) throw new Error('Invalid managed configuration.');
  if (aiKey !== undefined && (typeof aiKey !== 'string' || aiKey.length < 20 || aiKey.length > 512 || /[\s\x00-\x1f]/.test(aiKey)))
    throw new Error('Invalid AI key format.');
  if (!Array.isArray(templates) || templates.length !== names.length ||
      new Set(templates.map(w => w.name)).size !== names.length || templates.some(w => !names.includes(w.name) ||
        w.active !== false || w.activeVersionId || !Array.isArray(w.nodes))) throw new Error('Unexpected workflow package.');
  const workflows = structuredClone(templates);
  const dbId = randomBytes(12).toString('hex');
  const aiId = randomBytes(12).toString('hex');
  const credentials = [{ id: dbId, name: 'CTI Self-Hosted - PostgreSQL', type: 'postgres', data: {
    host: 'cti-db', database: databaseName, user: 'cti_n8n', password: databasePassword,
    port: 5432, ssl: 'disable', allowUnauthorizedCerts: false, maxConnections: 10,
  }}];
  if (aiKey) credentials.push({ id: aiId, name: 'CTI Self-Hosted - Google Gemini', type: 'googlePalmApi', data: { apiKey: aiKey, host: 'https://generativelanguage.googleapis.com' } });
  let dbNodes = 0, aiNodes = 0;
  for (const workflow of workflows) {
    workflow.id = randomBytes(12).toString('hex');
    delete workflow.activeVersionId;
    delete workflow.versionId;
    workflow.active = false;
    for (const node of workflow.nodes) {
      // Never keep credential references from the author's installation.
      delete node.credentials;
      if (node.type === 'n8n-nodes-base.postgres') {
        node.credentials = { postgres: { id: dbId, name: credentials[0].name } }; dbNodes++;
      } else if (node.type === '@n8n/n8n-nodes-langchain.googleGemini') {
        aiNodes++;
        if (aiKey) node.credentials = { googlePalmApi: { id: aiId, name: credentials[1].name } };
      }
    }
  }
  if (dbNodes !== 31 || aiNodes !== 2) throw new Error('Workflow bindings differ from reviewed package.');
  const errorId = workflows.find(w => w.name === 'n8n Workflow Error Alerts').id;
  for (const workflow of workflows) {
    workflow.settings ??= {};
    if (workflow.id !== errorId) workflow.settings.errorWorkflow = errorId;
    else delete workflow.settings.errorWorkflow;
  }
  return { workflows, credentials };
}
