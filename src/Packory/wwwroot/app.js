'use strict';
const $ = id => document.getElementById(id);
const state = { token: '' };
let catalogRequest = 0, securityRequest = 0, securityDetailRequest = 0;
let qualityRequest = 0;
const mcpView = { cursor: null, query: '', count: 0, requestId: 0 };
function headers() { return state.token ? { Authorization: `Bearer ${state.token}` } : {}; }
function element(tag, text, css) { const node = document.createElement(tag); node.textContent = text; if (css) node.className = css; return node; }
async function request(path, options = {}) {
  const response = await fetch(path, { ...options, headers: { ...headers(), ...(options.headers || {}) } });
  if (!response.ok) throw new Error(`HTTP ${response.status}${response.status === 401 ? ': token required' : ''}`);
  return response.json();
}
async function load() {
  const requestId = ++catalogRequest;
  const token = state.token;
  invalidateQuality();
  const rows = $('packages'); rows.replaceChildren(); $('feedback').textContent = 'Loading…';
  const params = new URLSearchParams({ q: $('search').value, limit: '100' });
  if ($('kind').value) params.set('kind', $('kind').value);
  try {
    const [packages, skills] = await Promise.all([request(`/api/packages?${params}`), request('/api/skills')]);
    if (requestId !== catalogRequest || state.token !== token) return;
    for (const item of packages.items) {
      const row = document.createElement('tr');
      row.append(element('td', item.name), element('td', item.version));
      const kind = document.createElement('td'); kind.append(element('span', item.kind, 'pill'));
      row.append(kind, element('td', item.origin)); rows.append(row);
    }
    $('feedback').textContent = `${packages.items.length} indexed package versions. Mirrored packages appear after first download.`;
    const list = $('skills'); list.replaceChildren();
    if (!skills.items.length) list.append(element('p', 'No imported skills yet.', 'muted'));
    for (const skill of skills.items) {
      const row = element('div', '', 'skill-row');
      row.append(element('span', `${skill.name} · ${skill.version}`));
      const link = element('a', 'Download');
      link.href = `/api/skills/${encodeURIComponent(skill.name)}/${encodeURIComponent(skill.version)}/download`;
      link.addEventListener('click', async event => {
        if (!state.token) return;
        event.preventDefault();
        try {
          const response = await fetch(link.href, { headers: headers() });
          if (!response.ok) throw new Error(`HTTP ${response.status}`);
          const url = URL.createObjectURL(await response.blob());
          const download = document.createElement('a'); download.href = url;
          download.download = `${skill.name}-${skill.version}.zip`; download.click();
          setTimeout(() => URL.revokeObjectURL(url), 1000);
        } catch (error) { $('feedback').textContent = String(error.message); }
      });
      const quality = element('button', 'Assess quality'); quality.type = 'button';
      quality.addEventListener('click', () => showSkillQuality(skill));
      row.append(link, quality); list.append(row);
    }
  } catch (error) {
    if (requestId === catalogRequest && state.token === token)
      $('feedback').textContent = `Unable to load catalog: ${error.message}`;
  }
}
$('token').addEventListener('input', event => {
  state.token = event.target.value;
  catalogRequest++; securityRequest++; securityDetailRequest++;
  invalidateQuality();
  for (const id of ['packages', 'skills', 'security-table', 'security-detail']) $(id).replaceChildren();
  $('security-detail').hidden = true;
  $('feedback').textContent = 'Credentials changed. Refreshing requires the current token.';
  $('security-status').textContent = 'Credentials changed. Refresh security records with the current token.';
  for (const id of ['security-pending', 'security-review', 'security-quarantine', 'security-approved']) $(id).textContent = '—';
  invalidateSkillAi();
  mcpView.requestId++; mcpView.cursor = null;
  $('mcp-table').replaceChildren(); $('mcp-more').hidden = true;
  $('mcp-status').textContent = 'Credentials changed. Search again to refresh server metadata.';
});
$('token').addEventListener('change', () => load());
$('refresh').addEventListener('click', load);
$('kind').addEventListener('change', load);
let debounce;
$('search').addEventListener('input', () => { clearTimeout(debounce); debounce = setTimeout(load, 300); });
$('upload').addEventListener('submit', async event => {
  event.preventDefault();
  const formElement = event.currentTarget;
  const button = formElement.querySelector('button'); button.disabled = true;
  $('upload-result').textContent = 'Importing…';
  try {
    const form = new FormData(formElement);
    const response = await request('/api/skills', { method: 'POST', body: form });
    $('upload-result').textContent = response.status === 'QUEUED' ? 'Queued ' + response.name + '@' + response.version + ' for scanning and approval.' : 'Imported ' + response.name + '@' + response.version;
    formElement.reset(); await load(); await loadSecurity();
  } catch (error) { $('upload-result').textContent = `Import failed: ${error.message}`; }
  finally { button.disabled = false; }
});
load();

function invalidateQuality() {
  qualityRequest++;
  $('quality-detail').replaceChildren(); $('quality-panel').hidden = true;
}

async function showSkillQuality(skill) {
  const requestId = ++qualityRequest;
  const token = state.token;
  const panel = $('quality-panel'); const detail = $('quality-detail');
  panel.hidden = false; detail.replaceChildren();
  $('quality-title').textContent = 'Skill quality · ' + skill.name + '@' + skill.version;
  $('quality-status').textContent = 'Inspecting published skill documentation…';
  try {
    const report = await request('/api/v1/skills/' + encodeURIComponent(skill.name) + '/' +
      encodeURIComponent(skill.version) + '/quality');
    if (requestId !== qualityRequest || state.token !== token) return;
    $('quality-status').textContent = report.assessmentStatus === 'completed'
      ? 'Static checks completed. Findings and suggestions are shown below.'
      : 'Assessment incomplete. Review coverage limits before interpreting the findings.';
    detail.append(element('p', 'Profile: ' + report.profileVersion));
    const digest = element('p', 'Archive SHA-256: ' + report.archiveSha256, 'quality-digest');
    detail.append(digest);
    const coverage = report.coverage || {};
    detail.append(element('p', (coverage.inspectedMarkdownFiles ?? 0) + ' of ' +
      (coverage.markdownFiles ?? 0) + ' Markdown files inspected; ' +
      (coverage.localLinksChecked ?? 0) + ' local references checked. ' +
      (coverage.externalLinksNotChecked ?? 0) + ' external references were not fetched.'));
    const dimensions = element('ul', '', 'quality-dimensions');
    for (const dimension of report.dimensions || []) {
      dimensions.append(element('li', dimension.id.replace(/_/g, ' ') + ': ' +
        dimension.status.replace(/_/g, ' ') + ' · ' + dimension.errorCount + ' errors, ' +
        dimension.warningCount + ' warnings, ' + dimension.infoCount + ' suggestions'));
    }
    detail.append(dimensions);
    const findings = report.findings || [];
    if (!findings.length) detail.append(element('p', 'No findings within the inspected scope.', 'muted'));
    for (const finding of findings) {
      const item = element('article', '', 'quality-finding');
      item.append(element('h3', finding.code + ' · ' + finding.severity),
        element('p', finding.message), element('p', finding.suggestion),
        element('p', 'Basis: ' + finding.basis + ' · File SHA-256: ' + finding.fileSha256 +
          (finding.line ? ' · Line ' + finding.line : ''), 'muted quality-digest'));
      detail.append(item);
    }
    if (report.findingsTruncated) detail.append(element('p',
      'The findings display limit was reached; dimension counts include all detected findings.', 'muted'));
  } catch (error) {
    if (requestId !== qualityRequest || state.token !== token) return;
    detail.replaceChildren();
    $('quality-status').textContent = 'Quality report unavailable: ' + error.message;
  }
}


// All attacker-controlled metadata and scanner results are rendered only as text.
function shortDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? 'Unknown' : date.toLocaleString();
}
async function loadSecurity() {
  const requestId = ++securityRequest;
  const token = state.token;
  invalidateQuality();
  securityDetailRequest++;
  const rows = $('security-table'); rows.replaceChildren();
  $('security-detail').hidden = true;
  if (!state.token) {
    $('security-status').textContent = 'Administrator API token required for security review and decisions.';
    for (const id of ['security-pending', 'security-review', 'security-quarantine', 'security-approved']) $(id).textContent = '—';
    return;
  }
  try {
    const response = await request('/api/security/skills?limit=100');
    if (requestId !== securityRequest || state.token !== token) return;
    const items = response.items || [];
    $('security-status').textContent = items.length + ' security records displayed. Select a version to inspect findings.';
    const counts = { QUEUED: 0, NEEDS_REVIEW: 0, QUARANTINED: 0, APPROVED: 0 };
    for (const item of items) if (Object.hasOwn(counts, item.state)) counts[item.state]++;
    $('security-pending').textContent = String(counts.QUEUED);
    $('security-review').textContent = String(counts.NEEDS_REVIEW);
    $('security-quarantine').textContent = String(counts.QUARANTINED);
    $('security-approved').textContent = String(counts.APPROVED);
    if (!items.length) {
      const tr = document.createElement('tr');
      const td = element('td', 'No scanned skills yet.', 'muted'); td.colSpan = 5; tr.append(td); rows.append(tr);
    }
    for (const item of items) {
      const row = document.createElement('tr');
      row.append(element('td', item.name + ' · ' + item.version),
        element('td', item.state, 'scan-state scan-' + item.state.toLowerCase().replace(/_/g, '-')),
        element('td', item.riskScore === null ? '—' : String(item.riskScore)),
        element('td', shortDate(item.updatedAt)));
      const actions = document.createElement('td');
      const view = element('button', 'Inspect'); view.type = 'button';
      view.addEventListener('click', () => showSkillSecurity(item));
      actions.append(view); row.append(actions); rows.append(row);
    }
  } catch (error) {
    if (requestId !== securityRequest || state.token !== token) return;
    $('security-status').textContent = 'Cannot access security administration: ' + error.message;
  }
}
async function showSkillSecurity(item) {
  const requestId = ++securityDetailRequest;
  const token = state.token;
  const detail = $('security-detail'); detail.hidden = false; detail.replaceChildren();
  detail.append(element('h3', item.name + '@' + item.version));
  try {
    const response = await request('/api/security/skills/' + encodeURIComponent(item.name) + '/' + encodeURIComponent(item.version));
    if (requestId !== securityDetailRequest || state.token !== token) return;
    detail.append(element('p', 'State: ' + response.skill.state + ' · Risk: ' +
      (response.skill.riskScore ?? 'not assessed') + ' · SHA-256: ' + response.skill.sha256));
    const actions = element('div', '', 'security-actions');
    if (item.state === 'NEEDS_REVIEW') {
      const approve = element('button', 'Approve after review'); approve.type = 'button';
      approve.addEventListener('click', () => decideSkill(item, 'approve'));
      actions.append(approve);
    }
    if (item.state === 'APPROVED') {
      const revoke = element('button', 'Revoke distribution', 'danger-action'); revoke.type = 'button';
      revoke.addEventListener('click', () => decideSkill(item, 'revoke'));
      actions.append(revoke);
    }
    if (item.state !== 'QUEUED' && item.state !== 'PUBLISHING') {
      const rescan = element('button', 'Re-scan (temporarily blocks distribution)'); rescan.type = 'button';
      rescan.addEventListener('click', () => decideSkill(item, 'rescan'));
      actions.append(rescan);
    }
    detail.append(actions);
    await appendSkillAiReview(item, detail, requestId, token);
    detail.append(element('h4', 'Scan report (untrusted text)'));;
    const output = element('pre', response.report ? JSON.stringify(response.report, null, 2) : 'No completed report available.', 'report-preview');
    output.tabIndex = 0;
    detail.append(output);
    const history = response.history || [];
    detail.append(element('h4', 'Decision history'));
    for (const action of history) detail.append(element('p', action.at + ': ' + action.from + ' → ' + action.to + ' · ' + action.reason));
  } catch (error) {
    if (requestId !== securityDetailRequest || state.token !== token) return;
    detail.append(element('p', 'Unable to load security details: ' + error.message));
  }
}
async function decideSkill(item, action) {
  const token = state.token;
  const requestId = securityDetailRequest;
  const prompt = action === 'approve' ? 'Document why this skill should be approved after reviewing its report:'
    : action === 'revoke' ? 'Document why this skill must be revoked:' : 'Reason for re-scanning:';
  const reason = window.prompt(prompt);
  if (reason === null) return;
  if (reason.trim().length < 3 || reason.length > 512) {
    $('security-status').textContent = 'A reason of 3–512 characters is required.';
    return;
  }
  if (action === 'revoke' && !window.confirm('Revoke this version from all REST and MCP distribution endpoints?')) return;
  try {
    const result = await request('/api/security/skills/' + encodeURIComponent(item.name) + '/' +
        encodeURIComponent(item.version) + '/' + action, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sha256: item.sha256, reason })
    });
    invalidateQuality();
    if (state.token !== token || requestId !== securityDetailRequest) return;
    $('security-status').textContent = item.name + '@' + item.version + ': ' + result.status;
    await loadSecurity(); await load();
  } catch (error) {
    if (state.token !== token || requestId !== securityDetailRequest) return;
    $('security-status').textContent = 'Decision was not applied: ' + error.message;
  }
}
$('security-refresh').addEventListener('click', loadSecurity);
$('token').addEventListener('change', loadSecurity);
loadSecurity();

// MCP server descriptors are untrusted metadata. Only render as text; do not
// automatically visit remotes, forward API tokens or execute a tool/Skill.
async function loadMcpServers(append = false) {
  const query = $('mcp-query').value.trim();
  if (query !== mcpView.query) append = false;
  if (append && !mcpView.cursor) return;
  const requestId = ++mcpView.requestId;
  const token = state.token;
  const rows = $('mcp-table');
  const more = $('mcp-more'); more.disabled = true;
  if (!append) {
    rows.replaceChildren(); mcpView.cursor = null; mcpView.count = 0;
    mcpView.query = query; more.hidden = true;
  }
  $('mcp-status').textContent = 'Loading reviewed MCP server metadata…';
  try {
    const params = new URLSearchParams({ q: query, limit: '50' });
    if (append) params.set('cursor', mcpView.cursor);
    const response = await request('/api/v1/mcp/servers?' + params);
    if (requestId !== mcpView.requestId || state.token !== token || $('mcp-query').value.trim() !== query) return;
    const items = response.items || [];
    mcpView.count += items.length;
    mcpView.cursor = typeof response.nextCursor === 'string' ? response.nextCursor : null;
    more.hidden = !mcpView.cursor;
    $('mcp-status').textContent = mcpView.count + ' operator-published server revisions displayed.' +
      (mcpView.cursor ? ' More results are available.' : '') + ' Publisher ownership is not verified.';
    if (!mcpView.count) {
      const row = document.createElement('tr');
      const cell = element('td', 'No published MCP server metadata matches this search.', 'muted');
      cell.colSpan = 4; row.append(cell); rows.append(row);
    }
    for (const item of items) {
      const row = document.createElement('tr');
      row.append(
        element('td', item.name || ''),
        element('td', item.version || ''),
        element('td', item.remoteUrl || ''),
        element('td', item.publisherVerified ? 'Publisher verified by provider' : 'Operator-reviewed metadata only')
      );
      rows.append(row);
    }
  } catch (error) {
    if (requestId !== mcpView.requestId || state.token !== token) return;
    $('mcp-status').textContent = 'Cannot load MCP metadata: ' + error.message;
  } finally {
    if (requestId === mcpView.requestId) more.disabled = false;
  }
}
$('mcp-filter').addEventListener('submit', event => {
  event.preventDefault(); loadMcpServers();
});
$('mcp-more').addEventListener('click', () => loadMcpServers(true));
$('token').addEventListener('change', () => loadMcpServers());
loadMcpServers();


// Optional SkillSpector inference. This UI never stores credentials or renders
// scanner/LLM output as HTML. All mutation authority remains on the server.
const aiView = { configuration: null, endpoints: [], requestId: 0 };
const aiJson = body => ({ method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
function invalidateSkillAi() {
  aiView.requestId++;
  aiView.configuration = null;
  aiView.endpoints = [];
  $('skill-ai-panel').hidden = true;
  $('ai-settings').reset();
  $('ai-key').value = '';
  for (const id of ['ai-jobs', 'ai-job-detail', 'ai-audit']) $(id).replaceChildren();
  $('ai-job-detail').hidden = true;
  $('ai-status').textContent = '';
  $('ai-usage').textContent = '';
}
function aiValidReason(value) { return value && value.trim().length >= 3 && value.length <= 512; }
function aiId() {
  if (!globalThis.crypto || !crypto.randomUUID) throw new Error('Secure browser context required for unique request IDs.');
  return crypto.randomUUID().replaceAll('-', '');
}
function aiNumber(id, minimum, maximum) {
  const value = Number($(id).value);
  if (!Number.isFinite(value) || value < minimum || value > maximum) throw new Error('Invalid limit: ' + id);
  return value;
}
function aiConfigurationFromForm(previous) {
  const enabled = $('ai-enabled').checked;
  const budget = aiNumber('ai-budget', 0, 1000000);
  const config = {
    ...previous, enabled, endpointId: $('ai-endpoint').value,
    model: $('ai-model').value.trim(), trigger: $('ai-trigger').value,
    requireForApproval: enabled && $('ai-required').checked,
    externalTransmissionApproved: $('ai-transmission').checked,
    timeoutSeconds: aiNumber('ai-timeout', 15, 600),
    maxConcurrentScans: aiNumber('ai-concurrent', 1, 4),
    maxRequestsPerScan: aiNumber('ai-requests', 1, 64),
    maxPromptBytes: aiNumber('ai-prompt-bytes', 1024, 131072),
    maxOutputTokens: aiNumber('ai-output-tokens', 64, 16384),
    monthlyBudgetMicroUsd: Math.round(budget * 1000000),
    inputUsdPerMillionTokens: aiNumber('ai-input-rate', 0, 1000),
    outputUsdPerMillionTokens: aiNumber('ai-output-rate', 0, 1000),
    outputLanguage: $('ai-language').value
  };
  if (!Number.isInteger(config.timeoutSeconds) || !Number.isInteger(config.maxConcurrentScans) ||
      !Number.isInteger(config.maxRequestsPerScan) || !Number.isInteger(config.maxPromptBytes) ||
      !Number.isInteger(config.maxOutputTokens)) throw new Error('Scan limits must be integers.');
  return config;
}
function populateAiForm(result) {
  const config = result.configuration;
  aiView.configuration = config;
  aiView.endpoints = result.endpoints || [];
  const endpointSelect = $('ai-endpoint');
  endpointSelect.replaceChildren();
  for (const e of aiView.endpoints) {
    const option = element('option', e.id + ' · ' + e.provider + ' · ' + e.baseUrl);
    option.value = e.id; endpointSelect.append(option);
  }
  $('ai-enabled').checked = config.enabled;
  endpointSelect.value = config.endpointId;
  $('ai-model').value = config.model;
  $('ai-trigger').value = config.trigger;
  $('ai-required').checked = config.requireForApproval;
  $('ai-transmission').checked = config.externalTransmissionApproved;
  $('ai-timeout').value = String(config.timeoutSeconds);
  $('ai-concurrent').value = String(config.maxConcurrentScans);
  $('ai-requests').value = String(config.maxRequestsPerScan);
  $('ai-prompt-bytes').value = String(config.maxPromptBytes);
  $('ai-output-tokens').value = String(config.maxOutputTokens);
  $('ai-budget').value = String(config.monthlyBudgetMicroUsd / 1000000);
  $('ai-input-rate').value = String(config.inputUsdPerMillionTokens);
  $('ai-output-rate').value = String(config.outputUsdPerMillionTokens);
  $('ai-language').value = config.outputLanguage;
  $('ai-key').value = '';
  $('ai-clear-key').checked = false;
  $('ai-reason').value = '';
  $('ai-provider-info').textContent = 'Allowed endpoints are deployment-managed. Saved key: ' +
    (result.hasApiKey ? 'configured (not retrievable).' : 'none.') +
    ' Rate inputs bound admission estimates, not the provider invoice.';
  const usage = result.usage || {};
  $('ai-usage').textContent = 'Period ' + (usage.month || '—') + ' · Reserved $' +
    (Number(usage.reservedMicroUsd || 0) / 1000000).toFixed(6) + ' · Requests ' +
    (usage.requests ?? 0) + ' · Missing provider usage ' + (usage.requestsWithMissingUsage ?? 0);
}
async function loadSkillAi() {
  const requestId = ++aiView.requestId;
  const token = state.token;
  if (!token) { invalidateSkillAi(); return; }
  $('skill-ai-panel').hidden = false;
  $('ai-status').textContent = 'Loading AI configuration…';
  try {
    const result = await request('/api/admin/skill-ai/settings');
    if (requestId !== aiView.requestId || state.token !== token) return;
    populateAiForm(result);
    $('ai-status').textContent = result.configuration.enabled
      ? 'AI enabled; human approval and the offline scan still apply.'
      : 'AI is disabled by default. Static scanning continues without model inference.';
    await loadSkillAiJobs();
  } catch (error) {
    if (requestId === aiView.requestId && state.token === token)
      $('ai-status').textContent = 'AI administration unavailable: ' + error.message;
  }
}
async function loadSkillAiJobs() {
  const requestId = aiView.requestId;
  const token = state.token;
  if (!aiView.configuration || !token) return;
  const list = $('ai-jobs'), audit = $('ai-audit');
  try {
    const [jobs, history] = await Promise.all([
      request('/api/admin/skill-ai/assessments'), request('/api/admin/skill-ai/audit')
    ]);
    if (requestId !== aiView.requestId || state.token !== token) return;
    list.replaceChildren(); audit.replaceChildren();
    const items = jobs.items || [];
    if (!items.length) list.append(element('p', 'No AI assessments yet.', 'muted'));
    for (const job of items) {
      const row = element('div', '', 'ai-job-row');
      row.append(element('span', (job.kind === 'probe' ? 'Connection test' : job.name + '@' + job.version) +
        ' · ' + job.state + ' · ' + job.createdAt));
      const view = element('button', 'View result'); view.type = 'button';
      view.addEventListener('click', () => showSkillAiJob(job.id)); row.append(view);
      if (job.state === 'QUEUED' || job.state === 'RUNNING') {
        const cancel = element('button', 'Cancel'); cancel.type = 'button';
        cancel.addEventListener('click', () => cancelSkillAiJob(job.id)); row.append(cancel);
      }
      list.append(row);
    }
    for (const action of (history.items || []).slice(0, 25))
      audit.append(element('p', action.action + ' · revision ' + action.revision + ' · ' + action.reason, 'muted'));
  } catch (error) {
    if (requestId === aiView.requestId && state.token === token)
      $('ai-status').textContent = 'Could not read AI activity: ' + error.message;
  }
}
async function showSkillAiJob(id, target = $('ai-job-detail')) {
  const token = state.token;
  const requestId = aiView.requestId;
  target.hidden = false; target.replaceChildren(element('p', 'Loading AI report…'));
  try {
    const result = await request('/api/admin/skill-ai/jobs/' + encodeURIComponent(id));
    if (state.token !== token || requestId !== aiView.requestId) return;
    target.replaceChildren();
    const job = result.job;
    target.append(element('p', job.state + ' · ' + job.kind + ' · ' + (job.errorCode || 'No error') +
      ' · Risk ' + (job.riskScore ?? 'unavailable') + ' · Model ' +
      (job.configuration?.model || 'unavailable')));
    target.append(element('p', 'SHA-256: ' + (job.sha256 || 'connection test') +
      ' · Config revision ' + job.revision + ' · Scanner ' + job.scannerVersion, 'muted'));
    const output = element('pre', result.report ? JSON.stringify(result.report, null, 2) :
      'No completed model report. A queued/failed job is not a clean result.', 'report-preview');
    output.tabIndex = 0; target.append(output);
  } catch (error) {
    if (state.token === token && requestId === aiView.requestId)
      target.replaceChildren(element('p', 'AI report unavailable: ' + error.message));
  }
}
async function cancelSkillAiJob(id) {
  const reason = window.prompt('Reason for cancelling this AI job:');
  if (reason === null) return;
  if (!aiValidReason(reason)) { $('ai-status').textContent = '3–512 characters required.'; return; }
  const token = state.token;
  try {
    await request('/api/admin/skill-ai/jobs/' + encodeURIComponent(id) + '/cancel', aiJson({ reason }));
    if (state.token === token) { $('ai-status').textContent = 'AI job cancelled.'; await loadSkillAiJobs(); }
  } catch (error) { if (state.token === token) $('ai-status').textContent = 'Cancel failed: ' + error.message; }
}
async function appendSkillAiReview(item, target, requestId, token) {
  if (!aiView.configuration || !state.token) return;
  target.append(element('h4', 'Optional AI analysis (separate from static scan)'));
  const content = element('div', '', 'ai-review-results'); target.append(content);
  if (aiView.configuration.enabled && ['NEEDS_REVIEW', 'QUARANTINED', 'APPROVED', 'REVOKED'].includes(item.state)) {
    const run = element('button', 'Queue AI assessment'); run.type = 'button';
    run.addEventListener('click', async () => {
      const reason = window.prompt('Document the reason for this AI assessment:');
      if (reason === null) return;
      if (!aiValidReason(reason)) { content.textContent = '3–512 characters required.'; return; }
      const selectedToken = state.token;
      try {
        const result = await request('/api/admin/skill-ai/assessments', aiJson({
          name: item.name, version: item.version, sha256: item.sha256, requestId: aiId(), reason
        }));
        if (state.token !== selectedToken || securityDetailRequest !== requestId) return;
        content.textContent = 'AI scan queued: ' + result.id + '. Refresh job results to inspect findings.';
        await loadSkillAiJobs();
      } catch (error) {
        if (state.token === selectedToken && securityDetailRequest === requestId)
          content.textContent = 'Cannot start AI analysis: ' + error.message;
      }
    });
    content.append(run);
  }
  try {
    const result = await request('/api/admin/skill-ai/assessments?name=' +
      encodeURIComponent(item.name) + '&version=' + encodeURIComponent(item.version));
    if (state.token !== token || securityDetailRequest !== requestId) return;
    const results = result.items || [];
    if (!results.length) content.append(element('p', 'No AI analysis for this revision.', 'muted'));
    for (const job of results.slice(0, 10)) {
      const row = element('div', '', 'ai-job-row');
      row.append(element('span', job.state + ' · Model ' + (job.configuration?.model || 'unknown') +
        ' · revision ' + job.revision + (job.riskScore === null ? '' : ' · Risk ' + job.riskScore)));
      const view = element('button', 'Read AI report'); view.type = 'button';
      view.addEventListener('click', () => showSkillAiJob(job.id, content));
      row.append(view); content.append(row);
    }
  } catch (error) {
    if (state.token === token && securityDetailRequest === requestId)
      content.append(element('p', 'AI history unavailable: ' + error.message, 'muted'));
  }
}
$('ai-settings').addEventListener('submit', async event => {
  event.preventDefault();
  const previous = aiView.configuration;
  if (!previous) return;
  const reason = $('ai-reason').value;
  if (!aiValidReason(reason)) { $('ai-status').textContent = 'An audit reason of 3–512 characters is required.'; return; }
  const token = state.token;
  $('ai-save').disabled = true;
  try {
    const config = aiConfigurationFromForm(previous);
    if ($('ai-clear-key').checked && $('ai-key').value)
      throw new Error('Clear key and new key are mutually exclusive.');
    const result = await request('/api/admin/skill-ai/settings', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ configuration: config, apiKey: $('ai-key').value || null,
        clearApiKey: $('ai-clear-key').checked, reason })
    });
    if (state.token !== token) return;
    // Populate from a fresh read to restore effective credentials/usage and revision.
    $('ai-key').value = '';
    $('ai-status').textContent = 'Configuration saved at revision ' + result.configuration.revision +
      '. Running jobs from previous revisions were cancelled.';
    await loadSkillAi();
  } catch (error) { if (state.token === token) $('ai-status').textContent = 'Save failed: ' + error.message; }
  finally { $('ai-save').disabled = false; }
});
$('ai-probe').addEventListener('click', async () => {
  const reason = window.prompt('Audit reason for testing the configured AI provider:');
  if (reason === null) return;
  if (!aiValidReason(reason)) { $('ai-status').textContent = '3–512 characters required.'; return; }
  const token = state.token;
  try {
    const job = await request('/api/admin/skill-ai/probe', aiJson({ requestId: aiId(), reason }));
    if (state.token === token) {
      $('ai-status').textContent = 'Provider connection test queued: ' + job.id +
        '. Refresh assessment jobs to see the result.';
      await loadSkillAiJobs();
    }
  } catch (error) { if (state.token === token) $('ai-status').textContent = 'Probe failed: ' + error.message; }
});
$('ai-reload').addEventListener('click', loadSkillAi);
$('ai-jobs-reload').addEventListener('click', loadSkillAiJobs);
$('token').addEventListener('change', loadSkillAi);
loadSkillAi();
