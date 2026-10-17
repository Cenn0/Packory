'use strict';
const $ = id => document.getElementById(id);
const state = { token: '' };
function headers() { return state.token ? { Authorization: `Bearer ${state.token}` } : {}; }
function element(tag, text, css) { const node = document.createElement(tag); node.textContent = text; if (css) node.className = css; return node; }
async function request(path, options = {}) {
  const response = await fetch(path, { ...options, headers: { ...headers(), ...(options.headers || {}) } });
  if (!response.ok) throw new Error(`HTTP ${response.status}${response.status === 401 ? ': token required' : ''}`);
  return response.json();
}
async function load() {
  const rows = $('packages'); rows.replaceChildren(); $('feedback').textContent = 'Loading…';
  const params = new URLSearchParams({ q: $('search').value, limit: '100' });
  if ($('kind').value) params.set('kind', $('kind').value);
  try {
    const [packages, skills] = await Promise.all([request(`/api/packages?${params}`), request('/api/skills')]);
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
      row.append(link); list.append(row);
    }
  } catch (error) { $('feedback').textContent = `Unable to load catalog: ${error.message}`; }
}
$('token').addEventListener('input', event => { state.token = event.target.value; });
$('refresh').addEventListener('click', load);
$('kind').addEventListener('change', load);
let debounce;
$('search').addEventListener('input', () => { clearTimeout(debounce); debounce = setTimeout(load, 300); });
$('upload').addEventListener('submit', async event => {
  event.preventDefault();
  const button = event.currentTarget.querySelector('button'); button.disabled = true;
  $('upload-result').textContent = 'Importing…';
  try {
    const form = new FormData(event.currentTarget);
    const response = await request('/api/skills', { method: 'POST', body: form });
    $('upload-result').textContent = response.status === 'QUEUED' ? 'Queued ' + response.name + '@' + response.version + ' for scanning and approval.' : 'Imported ' + response.name + '@' + response.version;
    event.currentTarget.reset(); await load(); await loadSecurity();
  } catch (error) { $('upload-result').textContent = `Import failed: ${error.message}`; }
  finally { button.disabled = false; }
});
load();


// All attacker-controlled metadata and scanner results are rendered only as text.
function shortDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? 'Unknown' : date.toLocaleString();
}
async function loadSecurity() {
  const rows = $('security-table'); rows.replaceChildren();
  $('security-detail').hidden = true;
  if (!state.token) {
    $('security-status').textContent = 'Administrator API token required for security review and decisions.';
    for (const id of ['security-pending', 'security-review', 'security-quarantine', 'security-approved']) $(id).textContent = '—';
    return;
  }
  try {
    const response = await request('/api/security/skills?limit=100');
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
    $('security-status').textContent = 'Cannot access security administration: ' + error.message;
  }
}
async function showSkillSecurity(item) {
  const detail = $('security-detail'); detail.hidden = false; detail.replaceChildren();
  detail.append(element('h3', item.name + '@' + item.version));
  try {
    const response = await request('/api/security/skills/' + encodeURIComponent(item.name) + '/' + encodeURIComponent(item.version));
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
    detail.append(element('h4', 'Scan report (untrusted text)'));
    const output = element('pre', response.report ? JSON.stringify(response.report, null, 2) : 'No completed report available.', 'report-preview');
    output.tabIndex = 0;
    detail.append(output);
    const history = response.history || [];
    detail.append(element('h4', 'Decision history'));
    for (const action of history) detail.append(element('p', action.at + ': ' + action.from + ' → ' + action.to + ' · ' + action.reason));
  } catch (error) {
    detail.append(element('p', 'Unable to load security details: ' + error.message));
  }
}
async function decideSkill(item, action) {
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
    $('security-status').textContent = item.name + '@' + item.version + ': ' + result.status;
    await loadSecurity(); await load();
  } catch (error) {
    $('security-status').textContent = 'Decision was not applied: ' + error.message;
  }
}
$('security-refresh').addEventListener('click', loadSecurity);
$('token').addEventListener('change', loadSecurity);
loadSecurity();
