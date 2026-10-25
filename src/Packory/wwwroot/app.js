'use strict';
const $ = id => document.getElementById(id);
const state = { token: '', access: null, sso: { enabled: false, authenticated: false, role: 'none', csrfToken: null } };
const canAdmin = () => state.sso.authenticated ? state.sso.role === 'admin' : !!state.access?.canAdmin;
const canWrite = () => state.sso.authenticated ? ['admin', 'write'].includes(state.sso.role) : !!state.access?.canWrite;
function updateAccessUi() {
  $('nav-publish').hidden = !canWrite();
  $('nav-feeds').hidden = !(canAdmin() || state.sso.authenticated || (state.access && state.access.role !== 'anonymous'));
  for (const id of ['nav-reviews', 'nav-activity']) $(id).hidden = !canAdmin();
  $('upload-panel').hidden = !canWrite();
  $('jump-to-import').hidden = !canWrite();
}
let catalogRequest = 0, securityRequest = 0, securityDetailRequest = 0;
const catalogView = { nextOffset: null, count: 0, query: '', kind: '' };
let accessRequest = 0;
let qualityRequest = 0;
let feedDetailRequest = 0, activityRequest = 0, auditRequest = 0, auditCursor = null;
let pendingRequest = 0, integrityRequest = 0, integrityNextOffset = null, maintenanceStatus = null;
let mcpAdminDetailRequest = 0;
let modelDetailRequest = 0;
const mcpView = { cursor: null, query: '', count: 0, requestId: 0 };
const mcpAdminView = { nextOffset: null, count: 0, requestId: 0 };
const modelView = { nextOffset: null, count: 0, requestId: 0 };
const modelAdminView = { nextOffset: null, count: 0, requestId: 0 };
let securityPolicy = { enforcement: null };
let activeInlineDetail = null, inlineDetailSequence = 0;
const securityView = { items: [], nextOffset: null };
const managedTokenView = { nextOffset: null, count: 0, requestId: 0 };
function headers() {
  if (state.sso.authenticated) return { 'X-Packory-Browser': '1' };
  return state.token ? { Authorization: `Bearer ${state.token}` } : {};
}
function element(tag, text, css) { const node = document.createElement(tag); node.textContent = text; if (css) node.className = css; return node; }
function closeInlineDetails(kind) {
  if (!activeInlineDetail || (kind && activeInlineDetail.kind !== kind)) return;
  const current = activeInlineDetail;
  if (current.kind === 'security') securityDetailRequest++;
  if (current.kind === 'quality') qualityRequest++;
  if (current.kind === 'feed') feedDetailRequest++;
  if (current.kind === 'mcp-admin') mcpAdminDetailRequest++;
  if (current.kind === 'model' || current.kind === 'model-admin') modelDetailRequest++;
  current.trigger.setAttribute('aria-expanded', 'false');
  current.trigger.textContent = current.openLabel;
  current.slot.remove();
  activeInlineDetail = null;
}
function openInlineDetails(anchor, trigger, label, columns, kind) {
  const wasOpen = activeInlineDetail?.trigger === trigger;
  closeInlineDetails();
  if (wasOpen) return null;
  const slot = document.createElement(anchor.tagName === 'TR' ? 'tr' : 'div');
  slot.className = 'inline-detail-row';
  let host = slot;
  if (anchor.tagName === 'TR') {
    const cell = document.createElement('td');
    cell.colSpan = columns || anchor.children.length;
    slot.append(cell); host = cell;
  }
  const panel = element('section', '', 'inline-detail-panel');
  panel.id = `inline-detail-${++inlineDetailSequence}`;
  panel.setAttribute('aria-label', label);
  host.append(panel);
  anchor.after(slot);
  const openLabel = trigger.dataset.openLabel || trigger.textContent;
  trigger.dataset.openLabel = openLabel;
  trigger.setAttribute('aria-controls', panel.id);
  trigger.setAttribute('aria-expanded', 'true');
  trigger.textContent = 'Close details';
  activeInlineDetail = { anchor, trigger, slot, panel, kind, openLabel };
  return panel;
}
async function request(path, options = {}) {
  const browser = state.sso.authenticated;
  const unsafe = !['GET', 'HEAD'].includes((options.method || 'GET').toUpperCase());
  const requestHeaders = { ...headers(), ...(options.headers || {}) };
  if (browser && unsafe) requestHeaders['X-Packory-Csrf'] = state.sso.csrfToken;
  const response = await fetch(path, { ...options, credentials: 'same-origin', headers: requestHeaders });
  if (!response.ok) throw new Error(`HTTP ${response.status}${response.status === 401 ? ': token required' : ''}`);
  if (response.status === 204) return null;
  return response.json();
}
async function load(append = false) {
  const query = $('search').value;
  const kindFilter = $('kind').value;
  if (append && (catalogView.nextOffset === null || catalogView.query !== query || catalogView.kind !== kindFilter)) append = false;
  const requestId = ++catalogRequest;
  const token = state.token;
  invalidateQuality();
  closeInlineDetails('package');
  const rows = $('packages');
  if (!append) { rows.replaceChildren(); catalogView.count = 0; catalogView.nextOffset = null; catalogView.query = query; catalogView.kind = kindFilter; }
  $('feedback').textContent = 'Loading…'; $('catalog-more').disabled = true;
  const params = new URLSearchParams({ q: query, limit: '100', offset: String(append ? catalogView.nextOffset : 0) });
  if (kindFilter) params.set('kind', kindFilter);
  try {
    const [packages, skills] = await Promise.all([request(`/api/packages?${params}`), request('/api/skills')]);
    if (requestId !== catalogRequest || state.token !== token) return;
    const packageItems = packages.items || [];
    catalogView.count += packageItems.length;
    catalogView.nextOffset = Number.isInteger(packages.nextOffset) ? packages.nextOffset : null;
    $('catalog-more').hidden = catalogView.nextOffset === null;
    for (const item of packageItems) {
      const row = document.createElement('tr');
      row.append(element('td', item.name), element('td', item.version));
      const kind = document.createElement('td'); kind.append(element('span', item.kind, 'pill'));
      row.append(kind, element('td', item.origin));
      const detailCell = document.createElement('td'); const inspect = element('button', 'Inspect', 'button-secondary'); inspect.type = 'button';
      inspect.setAttribute('aria-expanded', 'false');
      inspect.addEventListener('click', () => showPackageDetail(item, row, inspect)); detailCell.append(inspect); row.append(detailCell); rows.append(row);
    }
    $('feedback').textContent = `${catalogView.count} visible package versions loaded.${catalogView.nextOffset !== null ? ' More records are available.' : ''}`;
    $('clear-filters').hidden = !(catalogView.count === 0 && (query || kindFilter));
    if (!catalogView.count && catalogView.nextOffset === null) {
      const row = document.createElement('tr'); const cell = element('td', query || kindFilter ? 'No artifacts match these filters.' : 'No artifacts were returned by this catalog request.', 'muted');
      cell.colSpan = 5; row.append(cell); rows.append(row);
    }
    const list = $('skills'); list.replaceChildren();
    if (!skills.items.length) {
      const note = element('p', 'No approved skills are available in the catalog. Staged revisions are reviewed separately.', 'muted');
      if (canAdmin()) {
        const reviews = element('a', 'Open skill reviews'); reviews.href = '#reviews';
        note.append(' ', reviews);
      }
      list.append(note);
    }
    for (const skill of skills.items) {
      const row = element('div', '', 'skill-row');
      row.append(element('span', `${skill.name} · ${skill.version}`));
      const link = element('a', 'Download');
      link.href = `/api/skills/${encodeURIComponent(skill.name)}/${encodeURIComponent(skill.version)}/download`;
      link.addEventListener('click', async event => {
        if (!state.token && !state.sso.authenticated) return;
        event.preventDefault();
        try {
          const response = await fetch(link.href, { credentials: 'same-origin', headers: headers() });
          if (!response.ok) throw new Error(`HTTP ${response.status}`);
          const url = URL.createObjectURL(await response.blob());
          const download = document.createElement('a'); download.href = url;
          download.download = `${skill.name}-${skill.version}.zip`; download.click();
          setTimeout(() => URL.revokeObjectURL(url), 1000);
        } catch (error) { $('feedback').textContent = String(error.message); }
      });
      const quality = element('button', 'Assess quality'); quality.type = 'button';
      quality.setAttribute('aria-expanded', 'false');
      quality.addEventListener('click', () => showSkillQuality(skill, row, quality));
      row.append(link, quality); list.append(row);
    }
  } catch (error) {
    if (requestId === catalogRequest && state.token === token)
      $('feedback').textContent = error.message.startsWith('HTTP 401')
        ? state.sso.enabled
          ? 'Sign-in required to view this catalog. Sign in above or enter a read token under Settings.'
          : 'This instance requires a read token. Enter one under Settings; browser sign-in is currently unavailable.'
        : `Unable to load catalog: ${error.message}`;
  } finally {
    if (requestId === catalogRequest) $('catalog-more').disabled = false;
  }
}
$('catalog-more').addEventListener('click', () => load(true));
$('token').addEventListener('input', event => {
  accessRequest++;
  state.token = event.target.value;
  state.access = null;
  updateAccessUi();
  clearPrivilegedViews('Credentials changed. Refresh the current view to load data for the current access.');
  $('token-status').textContent = state.token ? 'Token is held in page memory. Finish entry to verify its rights.' : 'No API token is stored by this page.';
});
$('refresh').addEventListener('click', () => load());
$('clear-filters').addEventListener('click', () => { $('search').value = ''; $('kind').value = ''; $('clear-filters').hidden = true; load(); });
$('kind').addEventListener('change', () => load());
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
    formElement.reset();
    $('upload-result').replaceChildren(element('span', response.status === 'QUEUED'
      ? 'Queued ' + response.name + '@' + response.version + ' for scanning and approval.'
      : 'Imported ' + response.name + '@' + response.version));
    if (response.reviewReceipt) {
      const secret = document.createElement('input'); secret.type = 'password'; secret.readOnly = true;
      secret.value = response.reviewReceipt; secret.setAttribute('aria-label', 'One-time Skill review receipt');
      const copy = element('button', 'Copy review receipt', 'button-secondary'); copy.type = 'button';
      copy.addEventListener('click', async () => {
        try { await navigator.clipboard.writeText(response.reviewReceipt); copy.textContent = 'Copied'; }
        catch (_) { copy.textContent = 'Clipboard unavailable'; }
      });
      $('upload-result').append(' Copy the private 90-day receipt now; it is not shown again. ', secret, copy);
      $('publisher-review-receipt').value = response.reviewReceipt;
    }
    await load(); await loadSecurity();
  } catch (error) { $('upload-result').textContent = `Import failed: ${error.message}`; }
  finally { button.disabled = false; }
});
$('publisher-review-form').addEventListener('submit', async event => {
  event.preventDefault();
  const receipt = $('publisher-review-receipt').value.trim();
  const target = $('publisher-review-result'); target.replaceChildren();
  if (!/^[a-f0-9]{64}$/.test(receipt)) { target.append(element('p', 'Enter the exact 64-character review receipt.')); return; }
  target.append(element('p', 'Loading the matching revision…', 'muted'));
  try {
    const result = await request('/api/skills/review-status', { headers: { 'X-Packory-Review-Receipt': receipt } });
    if ($('publisher-review-receipt').value.trim() !== receipt) return;
    target.replaceChildren(element('h3', result.name + '@' + result.version),
      element('p', 'State: ' + securityStateLabel(result.state) + ' · SHA-256: ' + result.sha256),
      element('p', 'Scanner score: ' + (result.riskScore ?? 'unavailable') +
        ' · Recommendation: ' + (result.recommendation || 'unavailable'), 'muted'));
    renderSecurityReport(target, result.report);
    for (const event of result.history || [])
      if (event.to === 'DECLINED' || event.to === 'REVOKED')
        target.append(element('p', event.to + ' · ' + event.reason, 'policy-note'));
  } catch (error) {
    if ($('publisher-review-receipt').value.trim() === receipt)
      target.replaceChildren(element('p', 'Review status unavailable: ' + error.message));
  }
});
load();

function invalidateQuality() {
  qualityRequest++;
  closeInlineDetails('quality');
}

async function showSkillQuality(skill, row, trigger) {
  const detail = openInlineDetails(row, trigger, 'Documentation quality', null, 'quality');
  if (!detail) return;
  const requestId = ++qualityRequest;
  const token = state.token;
  detail.append(element('h3', 'Documentation quality · ' + skill.name + '@' + skill.version));
  const status = element('p', 'Inspecting published skill documentation…', 'feedback');
  status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
  detail.append(status, element('p', 'Checks cover published documentation and local file references. They do not execute the Skill or establish behavioral correctness, publisher identity or malware safety.', 'muted'));
  try {
    const report = await request('/api/v1/skills/' + encodeURIComponent(skill.name) + '/' +
      encodeURIComponent(skill.version) + '/quality');
    if (requestId !== qualityRequest || state.token !== token) return;
    status.textContent = report.assessmentStatus === 'completed'
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
    detail.scrollIntoView({ block: 'start' });
  } catch (error) {
    if (requestId !== qualityRequest || state.token !== token) return;
    detail.replaceChildren();
    status.textContent = 'Quality report unavailable: ' + error.message;
    detail.scrollIntoView({ block: 'start' });
  }
}


// All attacker-controlled metadata and scanner results are rendered only as text.
function shortDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? 'Unknown' : date.toLocaleString();
}
async function loadSecurity(append = false) {
  if (append && securityView.nextOffset === null) return;
  const requestId = ++securityRequest;
  const token = state.token;
  invalidateQuality();
  securityDetailRequest++;
  closeInlineDetails('security');
  const rows = $('security-table');
  if (!append) { rows.replaceChildren(); securityView.items = []; securityView.nextOffset = null; }
  $('security-more').disabled = true;
  if (!canAdmin()) {
    securityPolicy.enforcement = null; updateSecurityPolicy();
    $('security-status').textContent = 'Administrator SSO role or API token required for security review and decisions.';
    for (const id of ['security-pending', 'security-review', 'security-scan-failed', 'security-scan-incomplete', 'security-quarantine', 'security-declined', 'security-approved', 'security-revoked', 'security-publishing']) $(id).textContent = '—';
    securityView.items = []; securityView.nextOffset = null; $('security-more').hidden = true;
    return;
  }
  try {
    const [response, policy] = await Promise.all([request('/api/security/skills?limit=100&offset=' + (append ? securityView.nextOffset : 0)), request('/api/security/status')]);
    securityPolicy.enforcement = policy.enforcement; updateSecurityPolicy();
    if (requestId !== securityRequest || state.token !== token) return;
    if (append) securityView.items.push(...(response.items || []));
    else securityView.items = response.items || [];
    securityView.nextOffset = Number.isInteger(response.nextOffset) ? response.nextOffset : null;
    $('security-more').hidden = securityView.nextOffset === null;
    $('security-status').textContent = `${securityView.items.length} security records loaded.${securityView.nextOffset !== null ? ' More records are available.' : ''} Filter by skill or state to find a loaded revision.`;
    const counts = { QUEUED: 0, NEEDS_REVIEW: 0, SCAN_FAILED: 0, SCAN_INCOMPLETE: 0, QUARANTINED: 0, DECLINED: 0, APPROVED: 0, REVOKED: 0, PUBLISHING: 0 };
    for (const item of securityView.items) if (Object.hasOwn(counts, item.state)) counts[item.state]++;
    $('security-pending').textContent = String(counts.QUEUED);
    $('security-review').textContent = String(counts.NEEDS_REVIEW);
    $('security-scan-failed').textContent = String(counts.SCAN_FAILED);
    $("security-scan-incomplete").textContent = String(counts.SCAN_INCOMPLETE);
    $('security-quarantine').textContent = String(counts.QUARANTINED);
    $('security-declined').textContent = String(counts.DECLINED);
    $('security-approved').textContent = String(counts.APPROVED);
    $('security-revoked').textContent = String(counts.REVOKED);
    $('security-publishing').textContent = String(counts.PUBLISHING);
    renderSecurityRows();
  } catch (error) {
    if (requestId !== securityRequest || state.token !== token) return;
    $('security-status').textContent = 'Cannot access security administration: ' + error.message;
  } finally {
    if (requestId === securityRequest) $('security-more').disabled = false;
  }
}
function securityStateLabel(state) {
  return ({ QUEUED: 'Queued', PUBLISHING: 'Publishing', NEEDS_REVIEW: 'Needs review',
    SCAN_FAILED: 'Scan failed', SCAN_INCOMPLETE: 'Scan incomplete', QUARANTINED: 'Quarantined', DECLINED: 'Declined',
    APPROVED: 'Approved', REVOKED: 'Revoked' })[state] || String(state || 'Unknown').replace(/_/g, ' ');
}
function referenceCaveatOnly(report) {
  const coverage = report?.analysis_completeness;
  const exceptions = coverage?.ledger_exceptions;
  return coverage?.is_complete === false && coverage.status === 'partial' &&
    coverage.coverage_percent === 100 &&
    coverage.partially_inspected_files === 0 && coverage.entirely_uninspected_files === 0 &&
    Array.isArray(exceptions) && exceptions.length > 0 &&
    exceptions.every(item => item?.reason_code === 'reference_missing') &&
    !['failed_files', 'skipped_files']
      .some(key => Number(coverage[key] || 0) !== 0) &&
    !['scope_exclusions', 'limitations'].some(key => coverage[key]?.length > 0);
}
function reviewReasonText(skill, report) {
  if (skill.state === 'NEEDS_REVIEW') {
    const recommendation = skill.recommendation ? ` The scanner recommendation is ${skill.recommendation}.` : '';
    const coverage = referenceCaveatOnly(report)
      ? 'All bundled files were inspected, but referenced paths outside this package were unavailable to the scanner. Review these missing references before deciding. '
      : 'The static scan completed. ';
    return coverage + 'The scanner result is advisory. An administrator must review the evidence and choose Approve or Decline; Packory never publishes a skill automatically.' + recommendation;
  }
  if (skill.state === 'QUARANTINED')
    return 'The scanner placed this revision in quarantine because its findings need attention. Inspect the report; you can request another scan or decline distribution.';
  if (skill.state === 'SCAN_INCOMPLETE')
    return 'The scanner produced a report, but some files or references could not be fully checked. Review the coverage exceptions below; this revision cannot be approved until a complete scan is available. You can request another scan or decline distribution.';
  if (skill.state === 'SCAN_FAILED')
    return 'The scan failed or produced no usable report, so this revision cannot be approved. Request another scan or decline it; it remains unavailable while security enforcement is on.';
  if (skill.state === 'QUEUED')
    return 'The static scan has not finished. Wait for its result before making a distribution decision.';
  if (skill.state === 'DECLINED')
    return 'An administrator declined this revision. Its ZIP is retained but it is not available for distribution. Request another scan to reopen review.';
  if (skill.state === 'REVOKED')
    return 'Distribution was revoked. Copies downloaded before revocation are not recalled.';
  if (skill.state === 'APPROVED')
    return 'An administrator approved this revision under the current policy. Approval is not a safety guarantee.';
  return 'Review the exact scanner report and digest before deciding.';
}
function renderSecurityRows() {
  const rows = $('security-table'); rows.replaceChildren(); closeInlineDetails('security');
  const query = $('security-search').value.trim().toLocaleLowerCase();
  const selectedState = $('security-state-filter').value;
  const items = securityView.items.filter(item => {
    const matchesQuery = !query || `${item.name} ${item.version}`.toLocaleLowerCase().includes(query);
    return matchesQuery && (!selectedState || item.state === selectedState);
  });
  $('security-results').textContent = `Showing ${items.length} of ${securityView.items.length} loaded records.`;
  if (!items.length) {
    const tr = document.createElement('tr');
    const td = element('td', securityView.items.length ? 'No records match these filters.' : 'No security records were returned.', 'muted');
    td.colSpan = 5; tr.append(td); rows.append(tr); return;
  }
  for (const item of items) {
    const row = document.createElement('tr');
    row.append(element('td', item.name + ' · ' + item.version),
      element('td', securityStateLabel(item.state), 'scan-state scan-' + String(item.state).toLowerCase().replace(/_/g, '-')),
      element('td', item.riskScore == null ? '—' : String(item.riskScore)),
      element('td', shortDate(item.updatedAt)));
    const actions = document.createElement('td');
    const view = element('button', 'Inspect', 'button-secondary'); view.type = 'button';
    view.setAttribute('aria-expanded', 'false');
    view.setAttribute('aria-label', `Inspect ${item.name} ${item.version}`);
    view.addEventListener('click', () => showSkillSecurity(item, row, view));
    actions.append(view); row.append(actions); rows.append(row);
  }
}
$('security-search').addEventListener('input', renderSecurityRows);
$('security-state-filter').addEventListener('change', renderSecurityRows);
function reportFindings(report) {
  if (Array.isArray(report)) return report;
  if (!report || typeof report !== 'object') return [];
  for (const value of [report.issues, report.findings, report.results?.findings, report.analysis?.findings])
    if (Array.isArray(value)) return value;
  return [];
}
function renderSecurityReport(target, report) {
  if (!report) { target.append(element('p', 'No completed static report is available.', 'muted')); return; }
  const coverage = report.analysis_completeness;
  if (coverage && coverage.is_complete === false) {
    target.append(element('p', referenceCaveatOnly(report)
      ? 'All bundled files were inspected; referenced paths outside the package were unavailable. Review the missing references before approval.'
      : 'Coverage is incomplete. Findings below are still actionable, but this report cannot establish that every file was checked.', 'policy-note'));
    const exceptions = [...(coverage.ledger_exceptions || []), ...(coverage.scope_exclusions || []), ...(coverage.limitations || [])];
    if (exceptions.length) {
      const list = document.createElement('ul');
      for (const item of exceptions) {
        const reason = item.reason_code || item.code || item.type || 'Coverage exception';
        const file = item.path || item.file || '';
        const line = item.start_line || item.line;
        list.append(element('li', [reason, file, line ? 'line ' + line : ''].filter(Boolean).join(' · ')));
      }
      target.append(list);
    }
  }
  const findings = reportFindings(report);
  if (findings.length) {
    const list = element('div', '', 'report-findings');
    for (const finding of findings) {
      const card = element('article', '', 'report-finding');
      const title = finding.title || finding.finding || finding.rule || finding.id || finding.code || finding.type || 'Finding';
      const severity = finding.severity || finding.risk || finding.level || 'Unrated';
      card.append(element('h4', String(title) + ' · ' + String(severity)));
      const message = finding.message || finding.explanation || finding.description || finding.summary;
      if (message) card.append(element('p', String(message)));
      const location = finding.location || {};
      const file = finding.file || finding.path || location.file || location.path;
      const line = finding.line || finding.lineNumber || location.start_line || location.line;
      if (file || line) card.append(element('p', String(file || 'File not specified') +
        (line ? ' · line ' + String(line) : ''), 'muted'));
      const evidence = finding.code_snippet || finding.snippet ||
        (typeof finding.evidence === 'string' ? finding.evidence : null);
      if (evidence) card.append(element('pre', String(evidence), 'report-preview'));
      if (finding.remediation) card.append(element('p', 'Suggested action: ' + String(finding.remediation)));
      list.append(card);
    }
    target.append(list);
  } else {
    target.append(element('p', 'The report does not contain a recognized structured findings list. Review the original report below.', 'muted'));
  }
  const disclosure = document.createElement('details');
  const summary = element('summary', 'Raw static report (untrusted content)');
  const raw = element('pre', JSON.stringify(report, null, 2), 'report-preview');
  raw.tabIndex = 0; disclosure.append(summary, raw); target.append(disclosure);
}
async function appendSkillSource(skill, target, detailRequestId, token) {
  target.append(element('p', 'Loading exact uploaded ZIP contents…', 'muted'));
  const base = '/api/security/skills/' + encodeURIComponent(skill.name) + '/' + encodeURIComponent(skill.version) + '/source';
  try {
    const source = await request(base);
    if (detailRequestId !== securityDetailRequest || state.token !== token) return false;
    if (source.sha256 !== skill.sha256 || !Array.isArray(source.files)) throw new Error('Archive digest changed.');
    const manifest = source.files.find(file => file.path === 'SKILL.md' || file.path === skill.name + '/SKILL.md');
    if (!manifest?.previewable) throw new Error('SKILL.md is unavailable for text review.');
    target.replaceChildren();
    target.append(element('p', `${source.files.length} ${source.files.length === 1 ? 'file' : 'files'} in the exact staged archive. Text is shown as data; no file is executed.`, 'scope-note'));
    const label = element('label', 'File in uploaded ZIP');
    const picker = document.createElement('select');
    picker.setAttribute('aria-label', 'File in uploaded ZIP');
    for (const file of source.files) {
      const option = new Option(`${file.path} · ${file.bytes} bytes${file.previewable ? '' : ' · preview unavailable'}`, file.path);
      option.disabled = !file.previewable;
      picker.add(option);
    }
    label.append(picker); target.append(label);
    const status = element('p', '', 'muted'); target.append(status);
    const preview = element('pre', '', 'skill-source-preview'); preview.tabIndex = 0; target.append(preview);
    let fileRequest = 0;
    async function openFile(path) {
      const serial = ++fileRequest;
      status.textContent = 'Loading ' + path + '…'; preview.textContent = '';
      try {
        const file = await request(base + '/file?path=' + encodeURIComponent(path));
        if (serial !== fileRequest || detailRequestId !== securityDetailRequest || state.token !== token) return false;
        if (file.sha256 !== skill.sha256 || file.path !== path || typeof file.content !== 'string')
          throw new Error('Source response did not match the selected archive.');
        const lines = file.content.replace(/\r\n?/g, '\n').split('\n');
        preview.textContent = lines.map((line, index) => `${String(index + 1).padStart(4)} | ${line}`).join('\n');
        status.textContent = `${path} · ${file.bytes} bytes · ${lines.length} lines`;
        return true;
      } catch (error) {
        if (serial === fileRequest && detailRequestId === securityDetailRequest) status.textContent = 'Unable to preview file: ' + error.message;
        return false;
      }
    }
    picker.value = manifest.path;
    picker.addEventListener('change', () => { openFile(picker.value); });
    return await openFile(manifest.path);
  } catch (error) {
    if (detailRequestId === securityDetailRequest && state.token === token)
      target.replaceChildren(element('p', 'Uploaded ZIP cannot be inspected: ' + error.message, 'policy-note'));
    return false;
  }
}
async function showSkillSecurity(item, row, trigger) {
  const detail = openInlineDetails(row, trigger, 'Skill review details', 5, 'security');
  if (!detail) return;
  const requestId = ++securityDetailRequest;
  const token = state.token;
  detail.append(element('h3', item.name + '@' + item.version));
  try {
    const response = await request('/api/security/skills/' + encodeURIComponent(item.name) + '/' + encodeURIComponent(item.version));
    if (requestId !== securityDetailRequest || state.token !== token) return;
    const current = response.skill;
    detail.append(element('p', 'Status: ' + securityStateLabel(current.state) + ' · Scanner score: ' +
      (current.riskScore == null ? 'not available' : current.riskScore + ' / 100')));
    if (current.description) detail.append(element('p', 'Uploaded description (unverified): ' + current.description, 'scope-note'));
    const explanation = element('section', '', 'review-reason');
    explanation.append(element('h4', 'Why is a decision required?'), element('p', reviewReasonText(current, response.report)));
    detail.append(explanation);
    detail.append(element('p', 'Archive SHA-256: ' + current.sha256, 'quality-digest'));
    if (item.sha256 !== current.sha256) detail.append(element('p',
      'The selected list entry has a different digest from the current revision. Refresh the queue before deciding.', 'policy-note'));
    detail.append(element('p', 'Before deciding, confirm that this report matches the displayed archive digest. Refresh the review if another scan has run.', 'scope-note'));
    detail.append(element('h4', 'Uploaded skill files'));
    const sourceSection = element('section', '', 'skill-source'); detail.append(sourceSection);
    const sourceReady = await appendSkillSource(current, sourceSection, requestId, token);
    if (requestId !== securityDetailRequest || state.token !== token) return;
    detail.append(element('h4', 'Static findings'));
    const reportSection = element('div', '', 'report-section');
    renderSecurityReport(reportSection, response.report);
    detail.append(reportSection);
    const actions = element('div', '', 'security-actions');
    const digestMatches = item.sha256 === current.sha256;
    if (digestMatches && sourceReady && item.state === 'NEEDS_REVIEW') {
      const approve = element('button', 'Approve after review'); approve.type = 'button';
      approve.addEventListener('click', () => decideSkill(current, 'approve'));
      actions.append(approve);
    }
    if (digestMatches && ['NEEDS_REVIEW', 'QUARANTINED', 'SCAN_FAILED', 'SCAN_INCOMPLETE'].includes(item.state)) {
      const decline = element('button', 'Decline distribution', 'danger-action'); decline.type = 'button';
      decline.addEventListener('click', () => decideSkill(current, 'decline'));
      actions.append(decline);
    }
    if (digestMatches && item.state === 'APPROVED') {
      const revoke = element('button', 'Revoke distribution', 'danger-action'); revoke.type = 'button';
      revoke.addEventListener('click', () => decideSkill(current, 'revoke'));
      actions.append(revoke);
    }
    if (digestMatches && item.state !== 'QUEUED' && item.state !== 'PUBLISHING') {
      const rescan = element('button', 'Request a new scan', 'button-secondary'); rescan.type = 'button';
      rescan.addEventListener('click', () => decideSkill(current, 'rescan'));
      actions.append(rescan);
    }
    if (actions.childElementCount) detail.append(actions);
    if (!digestMatches) detail.append(element('p', 'Actions are unavailable until the review list is refreshed against the current digest.', 'muted'));
    if (!sourceReady && item.state === 'NEEDS_REVIEW') detail.append(element('p', 'Approval is unavailable until the uploaded ZIP can be inspected.', 'policy-note'));
    if (aiView.configuration) await appendSkillAiReview(current, detail, requestId, token);
    else detail.append(element('p', 'Optional AI assessment status is unavailable. Open Settings to load its configuration.', 'muted'));
    const history = response.history || [];
    detail.append(element('h4', 'Decision history'));
    if (!history.length) detail.append(element('p', 'No decision history was returned.', 'muted'));
    for (const action of history) detail.append(element('p',
      (action.at || action.createdAt || 'Time unavailable') + ': ' +
      securityStateLabel(action.from) + ' → ' + securityStateLabel(action.to) + ' · ' + (action.reason || 'No reason recorded')));
    detail.scrollIntoView({ block: 'start' });
  } catch (error) {
    if (requestId !== securityDetailRequest || state.token !== token) return;
    detail.append(element('p', 'Unable to load security details: ' + error.message));
    detail.scrollIntoView({ block: 'start' });
  }
}
let pendingDecision = null;
const decisionDialog = $('decision-dialog');
$('decision-form').addEventListener('submit', event => {
  event.preventDefault();
  const reason = $('decision-reason').value.trim();
  if (reason.length < 3 || reason.length > 512) { $('decision-reason').focus(); return; }
  decisionDialog.close('confirm');
});
$('decision-cancel').addEventListener('click', () => decisionDialog.close('cancel'));
decisionDialog.addEventListener('close', () => {
  if (!pendingDecision) return;
  const resolve = pendingDecision.resolve;
  const reason = $('decision-reason').value.trim();
  resolve(decisionDialog.returnValue === 'confirm' ? reason : null);
  pendingDecision = null;
});
function requestDecision(item, action) {
  const titles = { approve: 'Approve this skill revision?', decline: 'Decline distribution for this revision?', revoke: 'Revoke this skill revision?', rescan: 'Request another scan?' };
  const consequences = {
    approve: 'If the current policy gates pass, this allows distribution under the configured policy. Approval is not a safety guarantee.',
    decline: 'This records that the revision will not be distributed and keeps it out of the approved catalog. The uploaded ZIP is retained; an administrator can request a new scan later.',
    revoke: 'This prevents future distribution through Packory. Copies already downloaded elsewhere are not deleted.',
    rescan: 'The new scan changes the review state. Distribution may be temporarily blocked while it runs when enforcement is active.'
  };
  $('decision-title').textContent = titles[action];
  $('decision-summary').textContent = item.name + '@' + item.version + ' · SHA-256 ' + item.sha256;
  $('decision-consequence').textContent = consequences[action];
  $('decision-submit').textContent = action === 'approve' ? 'Approve revision' : action === 'decline' ? 'Decline distribution' : action === 'revoke' ? 'Revoke distribution' : 'Request scan';
  $('decision-reason').value = '';
  return new Promise(resolve => {
    pendingDecision = { resolve };
    decisionDialog.showModal();
    $('decision-reason').focus();
  });
}
async function decideSkill(item, action) {
  const token = state.token;
  const requestId = securityDetailRequest;
  const reason = await requestDecision(item, action);
  if (reason === null) return;
  const buttons = [...(activeInlineDetail?.panel.querySelectorAll('button') || [])];
  buttons.forEach(button => { button.disabled = true; });
  $('security-status').textContent = 'Submitting the decision for the displayed digest…';
  try {
    const result = await request('/api/security/skills/' + encodeURIComponent(item.name) + '/' +
        encodeURIComponent(item.version) + '/' + action, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sha256: item.sha256, reason })
    });
    invalidateQuality();
    if (state.token !== token || requestId !== securityDetailRequest) return;
    $('security-status').textContent = item.name + '@' + item.version + ': ' + result.status;
    await loadSecurity(); await load();
  } catch (error) {
    if (state.token !== token || requestId !== securityDetailRequest) return;
    if (error.message.startsWith('HTTP 409'))
      $('security-status').textContent = 'The decision could not be completed. Refresh the review and policy status before deciding again.';
    else if (error.message.startsWith('HTTP 401') || error.message.startsWith('HTTP 403'))
      $('security-status').textContent = 'The current credentials do not allow this decision. Review access before continuing.';
    else
      $('security-status').textContent = 'Outcome not confirmed. Check the current review state before trying again.';
    buttons.forEach(button => { button.disabled = false; });
  }
}$('security-refresh').addEventListener('click', () => loadSecurity());
$('security-more').addEventListener('click', () => loadSecurity(true));

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
loadMcpServers();


function clearMcpAdmin() {
  mcpAdminView.requestId++; mcpAdminDetailRequest++;
  mcpAdminView.nextOffset = null; mcpAdminView.count = 0;
  closeInlineDetails('mcp-admin');
  $('mcp-admin-panel').hidden = true;
  $('mcp-admin-table').replaceChildren();
  $('mcp-admin-more').hidden = true;
}
async function loadMcpAdmin(append = false) {
  if (!canAdmin()) { clearMcpAdmin(); return; }
  if (append && mcpAdminView.nextOffset === null) return;
  $('mcp-admin-panel').hidden = false;
  const requestId = ++mcpAdminView.requestId;
  const token = state.token;
  const offset = append ? mcpAdminView.nextOffset : 0;
  $('mcp-admin-status').textContent = 'Loading MCP Registry review entries…';
  $('mcp-admin-more').disabled = true;
  try {
    const response = await request('/api/v1/admin/mcp/servers?limit=50&offset=' + offset);
    if (requestId !== mcpAdminView.requestId || state.token !== token || !canAdmin()) return;
    if (!append) { closeInlineDetails('mcp-admin'); $('mcp-admin-table').replaceChildren(); mcpAdminView.count = 0; }
    const items = response.items || [];
    mcpAdminView.count += items.length;
    mcpAdminView.nextOffset = Number.isInteger(response.nextOffset) ? response.nextOffset : null;
    $('mcp-admin-more').hidden = mcpAdminView.nextOffset === null;
    $('mcp-admin-status').textContent = `${mcpAdminView.count} MCP revisions loaded. Publisher ownership and runtime safety remain unverified.`;
    if (!mcpAdminView.count) {
      const row = document.createElement('tr'); const cell = element('td', 'No MCP Registry review entries were returned.', 'muted');
      cell.colSpan = 5; row.append(cell); $('mcp-admin-table').append(row);
    }
    for (const item of items) {
      const row = document.createElement('tr');
      row.append(element('td', item.name), element('td', item.version),
        element('td', item.reviewState), element('td', item.sha256?.slice(0, 16) + '…'));
      const action = document.createElement('td');
      const view = element('button', 'Inspect', 'button-secondary'); view.type = 'button';
      view.setAttribute('aria-expanded', 'false');
      view.addEventListener('click', () => showMcpAdmin(item, row, view));
      action.append(view); row.append(action); $('mcp-admin-table').append(row);
    }
  } catch (error) {
    if (requestId === mcpAdminView.requestId) $('mcp-admin-status').textContent = 'MCP Registry review unavailable: ' + error.message;
  } finally {
    if (requestId === mcpAdminView.requestId) $('mcp-admin-more').disabled = false;
  }
}

async function showMcpAdmin(item, row, trigger) {
  const detail = openInlineDetails(row, trigger, 'MCP Registry review detail', 5, 'mcp-admin');
  if (!detail) return;
  const requestId = ++mcpAdminDetailRequest;
  const token = state.token;
  detail.append(element('p', 'Loading exact server.json…', 'muted'));
  try {
    const response = await request('/api/v1/admin/mcp/server?name=' + encodeURIComponent(item.name) + '&version=' + encodeURIComponent(item.version));
    if (requestId !== mcpAdminDetailRequest || token !== state.token || !canAdmin()) return;
    const entry = response.entry;
    if (entry.sha256 !== item.sha256) throw new Error('Review digest changed. Refresh the queue.');
    detail.replaceChildren(element('h3', item.name + '@' + item.version),
      element('p', 'State: ' + entry.reviewState + ' · SHA-256: ' + entry.sha256, 'quality-digest'),
      element('p', 'Metadata only: publisher ownership and remote behavior are not verified.', 'scope-note'));
    const source = element('pre', JSON.stringify(entry.server, null, 2), 'report-preview'); source.tabIndex = 0;
    detail.append(element('h4', 'Exact staged server.json'), source, element('h4', 'Decision history'));
    for (const event of response.history || [])
      detail.append(element('p', `${event.at}: ${event.oldState} → ${event.newState} · ${event.actor} · ${event.reason}`, 'muted'));
    if (!(response.history || []).length) detail.append(element('p', 'No decision recorded.', 'muted'));
    const action = entry.reviewState === 'PENDING' ? 'publish' : entry.reviewState === 'PUBLISHED' ? 'revoke' : null;
    if (action) {
      const button = element('button', action === 'publish' ? 'Publish reviewed metadata' : 'Revoke metadata', action === 'revoke' ? 'danger-action' : '');
      button.type = 'button'; button.addEventListener('click', () => decideMcpAdmin(item, action)); detail.append(button);
    }
    detail.scrollIntoView({ block: 'start' });
  } catch (error) {
    if (requestId === mcpAdminDetailRequest) detail.replaceChildren(element('p', 'MCP review detail unavailable: ' + error.message, 'policy-note'));
  }
}
async function decideMcpAdmin(item, action) {
  const reason = window.prompt('Reason for ' + action + ' decision (3–256 characters):');
  if (reason === null) return;
  if (reason.trim().length < 3 || reason.length > 256) { $('mcp-admin-status').textContent = 'A reason of 3–256 characters is required.'; return; }
  if (!window.confirm(`${action === 'publish' ? 'Publish' : 'Revoke'} ${item.name}@${item.version} at SHA-256 ${item.sha256}? Metadata review does not verify publisher ownership or runtime safety.`)) return;
  try {
    await request('/api/v1/admin/mcp/servers/decide', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: item.name, version: item.version, sha256: item.sha256, action, reason: reason.trim() }) });
    await Promise.all([loadMcpAdmin(), loadMcpServers()]);
    $('mcp-admin-status').textContent = 'Decision recorded for ' + item.name + '@' + item.version + '.';
  } catch (error) { $('mcp-admin-status').textContent = 'Decision not confirmed: ' + error.message; }
}
$('mcp-stage-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (!canAdmin()) return;
  const file = $('mcp-stage-file').files?.[0];
  if (!file || file.size < 2 || file.size > 16384) { $('mcp-admin-status').textContent = 'Choose a server.json no larger than 16 KiB.'; return; }
  try {
    const result = await request('/api/v1/admin/mcp/servers/stage', { method: 'POST',
      headers: { 'Content-Type': 'application/json' }, body: file });
    $('mcp-stage-file').value = '';
    await loadMcpAdmin();
    $('mcp-admin-status').textContent = 'Staged ' + result.name + '@' + result.version + '. Inspect the digest before deciding.';
  } catch (error) { $('mcp-admin-status').textContent = 'Metadata was not staged: ' + error.message; }
});
$('mcp-admin-refresh').addEventListener('click', () => loadMcpAdmin());
$('mcp-admin-more').addEventListener('click', () => loadMcpAdmin(true));

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
  if (!canAdmin()) { invalidateSkillAi(); return; }
  $('skill-ai-panel').hidden = false;
  $('ai-status').textContent = 'Loading AI configuration…';
  try {
    const result = await request('/api/admin/skill-ai/settings');
    if (requestId !== aiView.requestId || state.token !== token) return;
    populateAiForm(result);
    updateSecurityPolicy();
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
  if (!aiView.configuration || !canAdmin()) return;
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
  if (!aiView.configuration || !canAdmin()) return;
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
loadSkillAi();

 
// Keep browser credentials only in HttpOnly server-managed cookies; do not
// place ID/access/refresh tokens in localStorage, URLs, or client-side state.
async function loadSso() {
  try {
    const response = await fetch('/auth/session', { credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok) throw new Error('Session endpoint unavailable');
    const session = await response.json();
    const nextSso = { enabled: !!session.enabled, authenticated: !!session.authenticated,
      role: session.role || 'none', csrfToken: session.csrfToken || null };
    const identityChanged = state.sso.enabled !== nextSso.enabled ||
      state.sso.authenticated !== nextSso.authenticated || state.sso.role !== nextSso.role;
    state.sso = nextSso;
    if (nextSso.authenticated && state.token) {
      state.token = '';
      $('token').value = '';
    }
    if (nextSso.authenticated) state.access = null;
    updateAccessUi();
    if (identityChanged) clearPrivilegedViews('Sign-in state changed. Refresh the current view to load data for this session.');
    $('access-description').textContent = nextSso.authenticated
      ? 'Signed in with SSO. Dashboard actions use this session; no API token is required.'
      : nextSso.enabled
      ? 'Sign in above to use the dashboard. API tokens remain available as a separate fallback.'
      : 'Browser sign-in is not enabled for this instance. An API token remains in page memory only.';
    $('api-token-fallback').hidden = nextSso.authenticated;
    $('sso-login').hidden = !state.sso.enabled || state.sso.authenticated;
    $('sso-logout').hidden = !state.sso.authenticated;
    $('sso-status').textContent = !state.sso.enabled ? 'API token access' :
      state.sso.authenticated ? 'Signed in · ' + state.sso.role : 'Not signed in';
    await Promise.all([load(), loadSecurity(), loadMcpServers(), loadSkillAi(), loadManagedTokens()]);
    if (activeCatalogTab === 'mcp') await loadMcpAdmin();
    if (activeRoute === 'models') await Promise.all([loadModels(), loadModelAdmin()]);
  } catch (_) {
    state.sso = { enabled: false, authenticated: false, role: 'none', csrfToken: null };
    $('api-token-fallback').hidden = false;
    updateAccessUi();
    clearPrivilegedViews('Browser sign-in is unavailable. Enter a read token under Settings, then refresh this view.');
    $('access-description').textContent = 'Browser sign-in status is unavailable. API tokens remain supported and are held in page memory only.';
    $('sso-status').textContent = 'SSO status unavailable; API tokens remain supported';
    await loadManagedTokens();
  }
}
async function refreshTokenAccess() {
  const token = state.token;
  const requestId = ++accessRequest;
  if (state.sso.authenticated || !token) {
    state.access = null;
    updateAccessUi();
  } else {
    $('token-status').textContent = 'Checking API token rights…';
    try {
      const capabilities = await request('/api/auth/capabilities');
      if (requestId !== accessRequest || state.token !== token || state.sso.authenticated) return;
      state.access = capabilities;
      $('token-status').textContent = `API token verified · ${capabilities.role}${capabilities.canReadGlobal ? '' : ' · scoped'}. Held in page memory only.`;
    } catch (_) {
      if (requestId !== accessRequest || state.token !== token) return;
      state.access = null;
      $('token-status').textContent = 'API token was not accepted for this dashboard.';
    }
    updateAccessUi();
  }
  await Promise.all([load(), loadSecurity(), loadMcpServers(), loadSkillAi(), loadManagedTokens()]);
  if (activeCatalogTab === 'mcp') await loadMcpAdmin();
  if (activeRoute === 'models') await Promise.all([loadModels(), loadModelAdmin()]);
  if (activeRoute === 'feeds') await loadFeeds();
  if (activeRoute === 'activity') await loadActivity();
}
$('sso-login').addEventListener('click', () => window.location.assign('/auth/login'));
$('sso-logout').addEventListener('click', () => {
  if (!state.sso.authenticated || !state.sso.csrfToken) return;
  // A regular navigation (not fetch) permits a provider end-session redirect.
  const form = document.createElement('form');
  form.method = 'POST'; form.action = '/auth/logout'; form.hidden = true;
  const csrf = document.createElement('input');
  csrf.type = 'hidden'; csrf.name = '__RequestVerificationToken';
  csrf.value = state.sso.csrfToken;
  form.append(csrf); document.body.append(form);
  state.sso = { enabled: state.sso.enabled, authenticated: false, role: 'none', csrfToken: null };
  clearPrivilegedViews('Signed out. Refresh the current view to load data for another session.');
  form.submit();
});
loadSso();


// Task navigation and product views. Existing API authority remains server-side.
const appRoutes = ['catalog', 'models', 'feeds', 'publish', 'reviews', 'activity', 'settings'];
let activeRoute = 'catalog';
function showRoute(route, refresh = true) {
  if (!appRoutes.includes(route)) route = 'catalog';
  const previousRoute = activeRoute;
  const changed = route !== activeRoute;
  activeRoute = route;
  if (changed && previousRoute === 'feeds' && route !== 'feeds') clearFeedView();
  for (const view of document.querySelectorAll('.page-view')) view.hidden = view.id !== route + '-view';
  for (const link of document.querySelectorAll('[data-route]')) {
    if (link.dataset.route === route) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
  const label = route[0].toUpperCase() + route.slice(1);
  $('page-context').textContent = label;
  document.title = 'Packory · ' + label;
  if (!refresh || (!changed && route === 'catalog')) return;
  if (route === 'catalog') { load(); loadMcpServers(); if (activeCatalogTab === 'mcp') loadMcpAdmin(); }
  if (route === 'models') { loadModels(); loadModelAdmin(); }
  if (route === 'feeds') loadFeeds();
  if (route === 'reviews') loadSecurity();
  if (route === 'activity') { loadActivity(); loadSkillAiJobs(); }
  if (route === 'settings') { loadSkillAi(); loadManagedTokens(); }
}
function routeFromHash() { showRoute(location.hash.slice(1).split('/')[0] || 'catalog'); }
window.addEventListener('hashchange', routeFromHash);

const themeToggle = $('theme-toggle');
function setTheme(theme) {
  const dark = theme === 'dark';
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
  themeToggle.setAttribute('aria-pressed', String(dark));
  themeToggle.setAttribute('aria-label', dark ? 'Switch to light theme' : 'Switch to dark theme');
  themeToggle.textContent = dark ? '☼' : '◐';
  try { localStorage.setItem('packory-theme', dark ? 'dark' : 'light'); } catch (_) { }
}
try { setTheme(localStorage.getItem('packory-theme') === 'dark' ? 'dark' : 'light'); }
catch (_) { setTheme('light'); }
themeToggle.addEventListener('click', () => setTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark'));
$('jump-to-import').addEventListener('click', () => { $('version').focus(); $('upload-panel').scrollIntoView({ behavior: 'smooth', block: 'center' }); });
let activeCatalogTab = 'packages';
function selectCatalogTab(tab, focus = false) {
  activeCatalogTab = tab === 'mcp' ? 'mcp' : 'packages';
  const selected = activeCatalogTab === 'mcp' ? $('mcp-tab') : $('packages-tab');
  for (const item of [$('packages-tab'), $('mcp-tab')]) {
    const current = item === selected;
    item.setAttribute('aria-selected', String(current)); item.tabIndex = current ? 0 : -1;
  }
  $('packages-tab-panel').hidden = activeCatalogTab !== 'packages';
  $('mcp-tab-panel').hidden = activeCatalogTab !== 'mcp';
  if (focus) selected.focus();
  if (activeCatalogTab === 'mcp') { loadMcpServers(); loadMcpAdmin(); }
}
for (const [tab, id] of [['packages', 'packages-tab'], ['mcp', 'mcp-tab']]) {
  $(id).addEventListener('click', () => selectCatalogTab(tab));
  $(id).addEventListener('keydown', event => {
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight' || event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      const next = event.key === 'Home' ? 'packages' : event.key === 'End' ? 'mcp' : tab === 'packages' ? 'mcp' : 'packages';
      selectCatalogTab(next, true);
    }
  });
}
$('token').addEventListener('input', () => {
  securityPolicy.enforcement = null; updateSecurityPolicy();
});
$('token').addEventListener('change', refreshTokenAccess);

const feedView = { items: [], cursor: null, supportedKinds: [], requestId: 0, selected: null };
function clearFeedView() {
  feedView.requestId++; feedDetailRequest++;
  closeInlineDetails('feed');
  feedView.items = []; feedView.cursor = null; feedView.selected = null; feedView.supportedKinds = [];
  $('feed-table').replaceChildren(); $('feed-more').hidden = true; $('feed-more').disabled = false;
  $('feed-create-result').textContent = '';
  $('feed-kind').replaceChildren(new Option('Load supported kinds', ''));
  document.querySelector('.feed-create-panel').hidden = true;
  $('feed-self-service-result').replaceChildren();
  $('feed-self-service-id').value = '';
}
function clearActivityView() {
  activityRequest++; auditRequest++; pendingRequest++; integrityRequest++;
  auditCursor = null; integrityNextOffset = null; maintenanceStatus = null;
  $('operations-content').replaceChildren(); $('metrics-content').replaceChildren(); $('audit-table').replaceChildren();
  $('pending-table').replaceChildren(); $('pending-status').textContent = '';
  $('maintenance-status').textContent = ''; $('integrity-result').textContent = '';
  $('integrity-result').hidden = true; $('integrity-start').hidden = true;
  $('integrity-more').hidden = true; $('cache-evict').hidden = true;
  $('audit-more').hidden = true; $('audit-more').disabled = false;
}
function clearPrivilegedViews(message = 'Credentials changed. Refresh the current view to load data for the current access.') {
  catalogRequest++; securityRequest++; securityDetailRequest++;
  managedTokenView.requestId++; managedTokenView.nextOffset = null; managedTokenView.count = 0;
  $('managed-tokens-panel').hidden = true; $('managed-tokens-table').replaceChildren();
  modelView.requestId++; modelAdminView.requestId++; modelDetailRequest++;
  modelView.nextOffset = null; modelAdminView.nextOffset = null;
  $('models-table').replaceChildren(); $('model-admin-table').replaceChildren();
  $('model-stage-panel').hidden = true; $('model-admin-panel').hidden = true;
  $('upload-result').replaceChildren(); $('publisher-review-receipt').value = '';
  $('publisher-review-result').replaceChildren();
  closeInlineDetails(); securityView.items = []; securityView.nextOffset = null;
  catalogView.nextOffset = null; catalogView.count = 0;
  $('security-more').hidden = true; $('catalog-more').hidden = true;
  invalidateQuality();
  for (const id of ['packages', 'skills', 'security-table']) $(id).replaceChildren();
  $('feedback').textContent = message; $('security-status').textContent = message;
  for (const id of ['security-pending', 'security-review', 'security-scan-failed', 'security-scan-incomplete', 'security-quarantine', 'security-declined', 'security-approved', 'security-revoked', 'security-publishing']) $(id).textContent = '—';
  $('security-results').textContent = message;
  invalidateSkillAi();
  mcpView.requestId++; mcpView.cursor = null; $('mcp-table').replaceChildren(); $('mcp-more').hidden = true;
  $('mcp-status').textContent = message;
  clearMcpAdmin();
  securityPolicy.enforcement = null; updateSecurityPolicy();
  clearFeedView(); clearActivityView();
  $('feeds-status').textContent = message; $('activity-status').textContent = message;
}
const ecosystemLabels = { nuget: 'NuGet', npm: 'npm', cargo: 'Cargo', pypi: 'PyPI', oci: 'OCI', raw: 'Raw', maven: 'Maven', go: 'Go' };
function feedStateLabel(feed) {
  if (feed.enabled) return 'Active';
  return 'Inactive';
}
function populateFeedKinds(kinds) {
  feedView.supportedKinds = Array.isArray(kinds) ? kinds.filter(kind => Object.hasOwn(ecosystemLabels, kind)) : [];
  const select = $('feed-kind');
  const prior = select.value;
  select.replaceChildren(new Option('Select an ecosystem', ''));
  for (const kind of feedView.supportedKinds) select.add(new Option(ecosystemLabels[kind], kind));
  if (feedView.supportedKinds.includes(prior)) select.value = prior;
}
function appendFeedRow(feed) {
  const row = document.createElement('tr');
  row.dataset.feedId = feed.id;
  row.append(element('td', (feed.displayName || feed.id) + ' · ' + feed.id, 'feed-id'),
    element('td', ecosystemLabels[feed.kind] || feed.kind), element('td', feed.mode),
    element('td', feed.visibility), element('td', feedStateLabel(feed)));
  const actions = element('td', '');
  const view = element('button', 'Inspect', 'button-secondary'); view.type = 'button';
  view.setAttribute('aria-expanded', 'false'); view.setAttribute('aria-label', 'Inspect feed ' + feed.id);
  view.addEventListener('click', () => showFeed(feed.id, row, view)); actions.append(view); row.append(actions);
  $('feed-table').append(row);
}
async function loadFeeds(append = false) {
  if (!canAdmin()) {
    clearFeedView();
    $('feed-self-service').hidden = !(state.sso.authenticated || state.access);
    $('feeds-status').textContent = 'Feed definitions require an administrator. Use an exact feed ID to inspect a feed granted to your credential.';
    return;
  }
  $('feed-self-service').hidden = true;
  if (append && !feedView.cursor) return;
  const requestId = ++feedView.requestId;
  const token = state.token;
  document.querySelector('.feed-create-panel').hidden = false;
  $('feeds-status').textContent = 'Loading feed definitions…';
  $('feed-more').disabled = true;
  try {
    const params = new URLSearchParams({ limit: '50' });
    if (append) params.set('after', feedView.cursor);
    const response = await request('/api/v1/admin/feeds?' + params);
    if (requestId !== feedView.requestId || token !== state.token || !canAdmin()) return;
    if (!append) { closeInlineDetails('feed'); feedView.items = []; $('feed-table').replaceChildren(); }
    feedView.items.push(...(response.items || []));
    for (const feed of response.items || []) appendFeedRow(feed);
    feedView.cursor = typeof response.nextCursor === 'string' ? response.nextCursor : null;
    populateFeedKinds(response.isolatedHostedKinds);
    $('feed-more').hidden = !feedView.cursor;
    $('feeds-status').textContent = feedView.items.length + ' feed definitions loaded. Inactive feeds may be drafts or disabled; lifecycle history is unavailable.';
  } catch (error) {
    if (requestId !== feedView.requestId || token !== state.token) return;
    if (error.message.startsWith('HTTP 401') || error.message.startsWith('HTTP 403')) {
      clearFeedView(); $('feeds-status').textContent = 'Administrator access is required to view feed definitions.';
    } else $('feeds-status').textContent = 'Unable to load feed definitions: ' + error.message;
  } finally { if (requestId === feedView.requestId) $('feed-more').disabled = false; }
}
function propertyBlock(label, value, css = '') {
  const block = element('div', '', 'feed-property');
  const term = element('dt', label); const description = element('dd', value === null || value === undefined || value === '' ? 'Not provided' : String(value), css);
  block.append(term, description); return block;
}
async function showFeed(id, row, trigger) {
  if (!canAdmin()) { clearFeedView(); $('feeds-status').textContent = 'Administrator access is required to inspect feed details.'; return; }
  const detail = openInlineDetails(row, trigger, 'Feed details', 6, 'feed');
  if (!detail) return;
  const requestId = ++feedDetailRequest;
  const token = state.token;
  detail.append(element('h3', 'Loading feed details…'));
  try {
    const feed = await request('/api/v1/admin/feeds/' + encodeURIComponent(id));
    if (requestId !== feedDetailRequest || state.token !== token || !canAdmin() || activeInlineDetail?.panel !== detail) return;
    feedView.selected = feed;
    detail.replaceChildren(element('h3', feed.displayName || feed.id));
    const facts = document.createElement('dl'); facts.className = 'feed-detail-content';
    facts.append(propertyBlock('Feed ID', feed.id, 'feed-id'), propertyBlock('Ecosystem', ecosystemLabels[feed.kind] || feed.kind),
      propertyBlock('Mode', feed.mode), propertyBlock('Visibility', feed.visibility),
      propertyBlock('Lifecycle state', feedStateLabel(feed)), propertyBlock('Created', feed.createdAt),
      propertyBlock('Updated', feed.updatedAt));
    detail.append(facts);
    const artifacts = element('section', '', 'feed-artifact-section'); detail.append(artifacts);
    loadFeedArtifacts(feed.id, artifacts);
    if (feed.enabled) {
      const deactivate = element('button', 'Deactivate feed', 'danger-action'); deactivate.type = 'button'; deactivate.id = 'feed-deactivate';
      deactivate.addEventListener('click', () => changeFeedState(feed, 'deactivate')); detail.append(deactivate);
      detail.append(element('p', 'Deactivation stops feed routes while retaining stored content and grants.', 'scope-note'));
    } else if (feed.mode === 'hosted' && feed.visibility === 'private' && feedView.supportedKinds.includes(feed.kind)) {
      const activate = element('button', 'Activate private hosted feed'); activate.type = 'button'; activate.id = 'feed-activate';
      activate.addEventListener('click', () => changeFeedState(feed, 'activate')); detail.append(activate);
      detail.append(element('p', 'Activation enables this feed route. It does not issue access grants. Confirmed lifecycle history is not available from this API.', 'scope-note'));
    } else {
      detail.append(element('p', 'This feed profile is not eligible for activation through the supported private hosted endpoint.', 'scope-note'));
    }
    if (feed.enabled && feed.mode === 'hosted' && feed.visibility === 'private' &&
        feedView.supportedKinds.includes(feed.kind)) {
      const grants = element('section', '', 'feed-grants');
      grants.append(element('h3', 'Access tokens'));
      grants.append(element('p', 'Create a short-lived token for this active feed. The secret is shown once and is not stored in the browser after you leave this feed.'));
      const form = document.createElement('form'); form.className = 'feed-grant-form';
      const grantLabel = document.createElement('label'); grantLabel.append(document.createTextNode('Permission'));
      const grant = document.createElement('select'); grant.name = 'permission';
      grant.add(new Option('Read packages', 'read')); grant.add(new Option('Publish and read packages', 'publish'));
      grantLabel.append(grant);
      const expiryLabel = document.createElement('label'); expiryLabel.append(document.createTextNode('Expires in hours'));
      const expiry = document.createElement('input'); expiry.type = 'number'; expiry.name = 'expiresInHours'; expiry.min = '1'; expiry.max = '2160'; expiry.value = '168'; expiry.required = true;
      expiryLabel.append(expiry);
      const create = element('button', 'Create token'); create.type = 'submit';
      const result = element('div', '', 'feed-grant-result'); result.setAttribute('role', 'status'); result.setAttribute('aria-live', 'polite');
      form.append(grantLabel, expiryLabel, create, result); grants.append(form);
      form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!canAdmin()) { result.textContent = 'Administrator access is required.'; return; }
        const hours = Number(expiry.value);
        if (!Number.isInteger(hours) || hours < 1 || hours > 2160) { result.textContent = 'Choose an expiry between 1 and 2160 hours.'; return; }
        create.disabled = true; result.textContent = 'Creating feed token…';
        try {
          const permission = grant.value;
          const issued = await request('/api/admin/tokens', { method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ role: permission === 'publish' ? 'write' : 'read', namespace: `feed:${feed.id}:${permission}`, expiresInHours: hours }) });
          const notice = element('p', `Token ${issued.id} expires ${issued.expiresAt}. Copy it now; it cannot be retrieved later.`);
          const secret = document.createElement('input'); secret.type = 'password'; secret.readOnly = true; secret.autocomplete = 'off'; secret.value = issued.token; secret.setAttribute('aria-label', 'New feed access token');
          const toggle = element('button', 'Show token', 'button-secondary'); toggle.type = 'button';
          toggle.addEventListener('click', () => { const reveal = secret.type === 'password'; secret.type = reveal ? 'text' : 'password'; toggle.textContent = reveal ? 'Hide token' : 'Show token'; });
          const copy = element('button', 'Copy token', 'button-secondary'); copy.type = 'button';
          copy.addEventListener('click', async () => { try { await navigator.clipboard.writeText(issued.token); copy.textContent = 'Copied'; } catch (_) { copy.textContent = 'Clipboard unavailable'; } });
          const revoke = element('button', 'Revoke token', 'danger-action'); revoke.type = 'button';
          revoke.addEventListener('click', async () => {
            if (!window.confirm('Revoke this feed token now? It will stop working immediately.')) return;
            revoke.disabled = true;
            try { await request('/api/admin/tokens/' + encodeURIComponent(issued.id), { method: 'DELETE' }); secret.value = ''; result.replaceChildren(element('p', 'Token revoked.')); }
            catch (error) { revoke.disabled = false; notice.textContent = 'Revocation was not confirmed: ' + error.message; }
          });
          result.replaceChildren(notice, secret, toggle, copy, revoke);
        } catch (error) { result.textContent = 'Token was not confirmed: ' + error.message; }
        finally { create.disabled = false; }
      });
      detail.append(grants);
    }
    if (!feed.enabled && !feed.id.startsWith('default-')) {
      const remove = element('button', 'Delete if unused draft', 'danger-action'); remove.type = 'button';
      remove.addEventListener('click', async () => {
        if (!window.confirm('Delete draft ' + feed.id + '? Packory will refuse if it was activated or has references.')) return;
        remove.disabled = true;
        try {
          await request('/api/v1/admin/feeds/drafts/' + encodeURIComponent(feed.id), { method: 'DELETE' });
          closeInlineDetails('feed'); await loadFeeds();
          $('feeds-status').textContent = 'Unused draft ' + feed.id + ' was deleted.';
        } catch (error) {
          remove.disabled = false;
          detail.append(element('p', 'Draft deletion not confirmed: ' + error.message + '. Refresh to inspect its current state.', 'policy-note'));
        }
      });
      detail.append(remove, element('p', 'The server permits deletion only for a never-activated, unreferenced draft. All other feed identities are retained.', 'scope-note'));
    }
    detail.scrollIntoView({ block: 'start' });
  } catch (error) {
    if (requestId !== feedDetailRequest || token !== state.token) return;
    if (error.message.startsWith('HTTP 401') || error.message.startsWith('HTTP 403')) {
      clearFeedView(); $('feeds-status').textContent = 'Administrator access is required to inspect feed details.'; return;
    }
    detail.replaceChildren(element('h3', 'Feed details unavailable'),
      element('p', 'Unable to read this feed definition: ' + error.message));
    detail.scrollIntoView({ block: 'start' });
  }
}
function loadFeedArtifacts(id, target) {
  let next = null, count = 0, pageRequest = 0;
  const token = state.token;
  async function loadPage(append = false) {
    if (append && !next) return;
    const requestId = ++pageRequest;
    const params = new URLSearchParams({ limit: '50' });
    if (append) for (const [key, value] of Object.entries(next)) if (value != null) params.set(key, value);
    if (!append) target.replaceChildren(element('p', 'Loading feed artifacts…', 'muted'));
    try {
      const response = await request('/api/v1/feeds/' + encodeURIComponent(id) + '/artifacts?' + params);
      if (requestId !== pageRequest || token !== state.token || !target.isConnected) return;
      if (!append) {
        target.replaceChildren(element('h4', 'Feed artifacts'));
        const scope = element('p', response.feed.enabled ? 'Active private feed' : 'Inactive feed retained for administrator inspection', 'scope-note');
        target.append(scope);
        for (const [label, value] of [['Native read endpoint', response.readUrl], ['Native publish endpoint', response.publishUrl]])
          if (value) target.append(element('p', label + ': ' + value, 'muted'));
        if (response.usage) target.append(element('p',
          `Raw usage: ${response.usage.artifacts} artifacts · ${response.usage.bytes} bytes · limits ${response.usage.maxArtifacts || 'unbounded'} artifacts / ${response.usage.maxBytes || 'unbounded'} bytes`, 'muted'));
        const table = document.createElement('table');
        const head = document.createElement('thead'); const header = document.createElement('tr');
        for (const label of ['Name', 'Version', 'Path', 'Size', 'Digest']) header.append(element('th', label));
        head.append(header); table.append(head);
        const body = document.createElement('tbody'); table.append(body);
        const wrap = element('div', '', 'table-wrap'); wrap.append(table); target.append(wrap);
        const status = element('p', '', 'muted'); status.dataset.role = 'feed-artifact-status'; target.append(status);
        const more = element('button', 'Load more artifacts', 'button-secondary'); more.type = 'button'; more.dataset.role = 'feed-artifact-more';
        more.addEventListener('click', () => loadPage(true)); target.append(more);
        count = 0;
      }
      const body = target.querySelector('tbody');
      const items = response.items || [];
      for (const item of items) {
        const row = document.createElement('tr');
        row.append(element('td', item.name), element('td', item.version), element('td', item.path || '—'),
          element('td', String(item.size)), element('td', item.digest)); body.append(row);
      }
      count += items.length;
      next = response.nextCursor || null;
      target.querySelector('[data-role="feed-artifact-more"]').hidden = !next;
      target.querySelector('[data-role="feed-artifact-status"]').textContent = `${count} artifact records loaded. A native client needs an exact read/publish grant; URLs contain no credentials.`;
    } catch (error) {
      if (requestId === pageRequest && target.isConnected)
        target.replaceChildren(element('p', 'Feed artifacts unavailable: ' + error.message, 'policy-note'));
    }
  }
  loadPage();
}
$('feed-self-service-form').addEventListener('submit', event => {
  event.preventDefault();
  const id = $('feed-self-service-id').value.trim();
  if (!/^[a-z0-9][a-z0-9-]{0,62}$/.test(id)) {
    $('feed-self-service-result').replaceChildren(element('p', 'Enter a valid exact feed ID.')); return;
  }
  loadFeedArtifacts(id, $('feed-self-service-result'));
});
async function inspectFeed(id) {
  const row = [...$('feed-table').rows].find(candidate => candidate.dataset.feedId === id);
  if (row) await showFeed(id, row, row.querySelector('button'));
}
async function changeFeedState(feed, action) {
  const activate = action === 'activate';
  const title = activate ? 'Activate ' + (feed.displayName || feed.id) + '?' : 'Deactivate ' + (feed.displayName || feed.id) + '?';
  const consequence = activate
    ? 'This enables the private hosted feed routes. It does not create tokens or access grants.'
    : 'This disables the feed routes. Stored artifacts and existing grants are retained.';
  if (!window.confirm(title + '\n\n' + consequence)) return;
  const button = $('feed-' + action); if (button) button.disabled = true;
  $('feeds-status').textContent = activate ? 'Activation requested…' : 'Deactivation requested…';
  try {
    await request('/api/v1/admin/feeds/' + encodeURIComponent(feed.id) + '/' + action, { method: 'POST' });
    $('feeds-status').textContent = activate ? 'Feed activation confirmed by the API.' : 'Feed deactivation confirmed. Stored content and grants remain.';
    await loadFeeds(); await inspectFeed(feed.id);
  } catch (error) {
    $('feeds-status').textContent = error.message.startsWith('HTTP 409')
      ? 'The requested state change was rejected. Refresh the feed and check its current state.'
      : 'Outcome not confirmed. Refresh the feed state before trying again.';
    if (button) button.disabled = false;
  }
}
$('feed-create').addEventListener('submit', async event => {
  event.preventDefault();
  if (!canAdmin()) { $('feed-create-result').textContent = 'Administrator access is required.'; return; }
  const button = event.currentTarget.querySelector('button[type=submit]'); button.disabled = true;
  $('feed-create-result').textContent = 'Creating a private hosted draft…';
  const form = new FormData(event.currentTarget);
  const body = { id: String(form.get('id') || '').trim(), kind: String(form.get('kind') || ''),
    mode: 'hosted', displayName: String(form.get('displayName') || '').trim() };
  if (!feedView.supportedKinds.includes(body.kind)) { $('feed-create-result').textContent = 'Select a currently supported hosted ecosystem.'; button.disabled = false; return; }
  try {
    const result = await request('/api/v1/admin/feeds/drafts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    $('feed-create-result').textContent = 'Draft created for ' + result.feed.id + '. It is not active and does not grant access.';
    event.currentTarget.reset(); await loadFeeds(); await inspectFeed(result.feed.id);
  } catch (error) {
    $('feed-create-result').textContent = error.message.startsWith('HTTP 409')
      ? 'That feed ID already exists. Choose a different ID.' : 'Draft was not confirmed: ' + error.message;
  } finally { button.disabled = false; }
});
$('feeds-refresh').addEventListener('click', () => loadFeeds());
$('feed-more').addEventListener('click', () => loadFeeds(true));

function formatDataValue(value) {
  if (value === null || value === undefined) return 'Not reported';
  if (typeof value === 'boolean') return value ? 'Enabled' : 'Disabled';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}
function renderDataPairs(target, data, emptyText) {
  target.replaceChildren();
  if (!data || typeof data !== 'object' || !Object.keys(data).length) { target.append(element('p', emptyText, 'muted')); return; }
  const list = element('dl', '', 'data-list');
  for (const [key, value] of Object.entries(data)) {
    const item = element('div', '', 'data-item'); item.append(element('dt', key.replace(/[A-Z]/g, letter => ' ' + letter.toLowerCase())), element('dd', formatDataValue(value)));
    list.append(item);
  }
  target.append(list);
}
async function loadAudit(append = false) {
  if (!canAdmin()) { clearActivityView(); $('activity-status').textContent = 'Administrator access is required to view audit events.'; return; }
  if (append && !auditCursor) return;
  const requestId = ++auditRequest;
  const token = state.token;
  const params = new URLSearchParams({ limit: '50' }); if (append) params.set('before', String(auditCursor));
  try {
    const response = await request('/api/admin/audit?' + params);
    if (requestId !== auditRequest || token !== state.token || !canAdmin()) return;
    const items = response.items || [];
    if (!append) $('audit-table').replaceChildren();
    if (!append && !items.length) {
      const row = document.createElement('tr'); const cell = element('td', 'No audit events were returned.', 'muted'); cell.colSpan = 4; row.append(cell); $('audit-table').append(row);
    }
    for (const item of items) {
      const row = document.createElement('tr');
      row.append(element('td', shortDate(item.createdAt)), element('td', item.action),
        element('td', [item.kind, item.name, item.version].filter(Boolean).join(' · ')), element('td', item.actor));
      $('audit-table').append(row);
    }
    const ids = items.map(item => Number(item.id)).filter(Number.isFinite);
    auditCursor = items.length >= 50 && ids.length ? Math.min(...ids) : null;
    $('audit-more').hidden = !auditCursor;
  } catch (error) {
    if (requestId !== auditRequest || token !== state.token) return;
    $('activity-status').textContent = 'Audit events could not be loaded: ' + error.message;
    $('audit-table').replaceChildren(); auditCursor = null; $('audit-more').hidden = true;
  }
}
async function loadActivity() {
  if (!canAdmin()) { clearActivityView(); $('activity-status').textContent = 'Administrator SSO role or API token required to view activity.'; return; }
  clearActivityView();
  const requestId = ++activityRequest;
  const token = state.token;
  $('activity-status').textContent = 'Loading activity from the available administrator endpoints…';
  const results = await Promise.allSettled([
    request('/api/admin/operations/status'), request('/api/admin/metrics')
  ]);
  if (requestId !== activityRequest || token !== state.token || !canAdmin()) return;
  if (results[0].status === 'fulfilled') {
    maintenanceStatus = results[0].value;
    renderDataPairs($('operations-content'), maintenanceStatus, 'No operations status was returned.');
    $('integrity-start').hidden = !(maintenanceStatus.recoveryMaintenanceEnabled && maintenanceStatus.writerLeaseHeld);
    $('cache-evict').hidden = maintenanceStatus.blobBackend !== 'filesystem';
    $('maintenance-status').textContent = maintenanceStatus.recoveryMaintenanceEnabled
      ? 'Exclusive maintenance is enabled. Inspect exact keys before any recovery action.'
      : 'Recovery actions and integrity scanning require exclusive maintenance mode.';
  }
  else renderDataPairs($('operations-content'), null, 'Operations status unavailable: ' + results[0].reason.message);
  if (results[1].status === 'fulfilled') renderDataPairs($('metrics-content'), results[1].value, 'No metrics were returned.');
  else renderDataPairs($('metrics-content'), null, 'Metrics unavailable: ' + results[1].reason.message);
  $('activity-status').textContent = results.some(result => result.status === 'fulfilled')
    ? 'Values shown are limited to the responses returned by this instance.'
    : 'The administrator endpoints did not return activity data.';
  await Promise.all([loadAudit(), loadPendingPublications()]);
}
async function loadPendingPublications() {
  if (!canAdmin()) { $('pending-table').replaceChildren(); return; }
  const requestId = ++pendingRequest;
  const token = state.token;
  $('pending-status').textContent = 'Loading pending publication intents…';
  try {
    const response = await request('/api/admin/publications/pending');
    if (requestId !== pendingRequest || token !== state.token || !canAdmin()) return;
    const items = response.items || [];
    $('pending-table').replaceChildren();
    $('pending-status').textContent = `${items.length} pending publication intents returned. A count alone is not a recovery decision.`;
    if (!items.length) {
      const row = document.createElement('tr'); const cell = element('td', 'No pending publications.', 'muted');
      cell.colSpan = 4; row.append(cell); $('pending-table').append(row);
    }
    for (const item of items) {
      const row = document.createElement('tr');
      row.append(element('td', item.key), element('td', [item.kind, item.name, item.version].join(' · ')),
        element('td', shortDate(item.startedAt)));
      const action = document.createElement('td');
      if (maintenanceStatus?.recoveryMaintenanceEnabled && maintenanceStatus?.writerLeaseHeld) {
        const reconcile = element('button', 'Reconcile', 'danger-action'); reconcile.type = 'button';
        reconcile.addEventListener('click', async () => {
          if (!window.confirm('Reconcile exact publication ' + item.key + ' started ' + item.startedAt + '? This may change its recovery state.')) return;
          reconcile.disabled = true;
          try {
            await request('/api/admin/publications/reconcile', { method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ key: item.key, expectedStartedAt: item.startedAt }) });
            await loadActivity();
          } catch (error) { reconcile.disabled = false; $('maintenance-status').textContent = 'Reconciliation not confirmed: ' + error.message; }
        });
        action.append(reconcile);
      }
      row.append(action); $('pending-table').append(row);
    }
  } catch (error) {
    if (requestId === pendingRequest) $('pending-status').textContent = 'Pending publications unavailable: ' + error.message;
  }
}
async function loadIntegrityPage(after = 0) {
  if (!canAdmin() || !maintenanceStatus?.recoveryMaintenanceEnabled || !maintenanceStatus?.writerLeaseHeld) return;
  const requestId = ++integrityRequest;
  const token = state.token;
  $('maintenance-status').textContent = 'Inspecting up to five referenced artifacts…';
  try {
    const page = await request('/api/admin/integrity/scan?after=' + after + '&limit=5');
    if (requestId !== integrityRequest || token !== state.token || !canAdmin()) return;
    $('integrity-result').hidden = false;
    $('integrity-result').textContent = JSON.stringify(page, null, 2);
    integrityNextOffset = Number.isInteger(page.nextOffset) ? page.nextOffset : null;
    $('integrity-more').hidden = integrityNextOffset === null;
    $('maintenance-status').textContent = `Checked ${page.checked} references from offset ${page.offset}. This is bounded evidence, not restore certification.`;
  } catch (error) {
    if (requestId === integrityRequest) $('maintenance-status').textContent = 'Integrity page unavailable: ' + error.message;
  }
}
$('pending-refresh').addEventListener('click', loadPendingPublications);
$('integrity-start').addEventListener('click', () => loadIntegrityPage(0));
$('integrity-more').addEventListener('click', () => { if (integrityNextOffset !== null) loadIntegrityPage(integrityNextOffset); });
$('cache-evict').addEventListener('click', async () => {
  if (!canAdmin() || maintenanceStatus?.blobBackend !== 'filesystem') return;
  if (!window.confirm('Evict recomputable mutable cache metadata? Hosted immutable artifacts are retained.')) return;
  try {
    const result = await request('/api/admin/cache/mutable/evict', { method: 'POST' });
    $('maintenance-status').textContent = `${result.evictedFiles} mutable cache files evicted. Refresh catalog reads as needed.`;
  } catch (error) { $('maintenance-status').textContent = 'Cache eviction not confirmed: ' + error.message; }
});
async function loadManagedTokens(append = false) {
  const panel = $('managed-tokens-panel');
  if (!canAdmin()) {
    managedTokenView.requestId++; managedTokenView.nextOffset = null; managedTokenView.count = 0;
    panel.hidden = true; $('managed-tokens-table').replaceChildren(); return;
  }
  if (append && managedTokenView.nextOffset === null) return;
  panel.hidden = false;
  const requestId = ++managedTokenView.requestId;
  const token = state.token;
  const offset = append ? managedTokenView.nextOffset : 0;
  $('managed-tokens-status').textContent = 'Loading issued token metadata…';
  $('managed-tokens-more').disabled = true;
  try {
    const response = await request('/api/admin/tokens?limit=50&offset=' + offset);
    if (requestId !== managedTokenView.requestId || token !== state.token || !canAdmin()) return;
    if (!append) { $('managed-tokens-table').replaceChildren(); managedTokenView.count = 0; }
    const items = response.items || [];
    managedTokenView.count += items.length;
    managedTokenView.nextOffset = Number.isInteger(response.nextOffset) ? response.nextOffset : null;
    $('managed-tokens-more').hidden = managedTokenView.nextOffset === null;
    $('managed-tokens-status').textContent = `${managedTokenView.count} issued token records loaded. No secret or token hash is returned.`;
    if (!managedTokenView.count) {
      const row = document.createElement('tr'); const cell = element('td', 'No issued tokens were returned.', 'muted');
      cell.colSpan = 6; row.append(cell); $('managed-tokens-table').append(row);
    }
    for (const item of items) {
      const row = document.createElement('tr');
      const status = item.revoked ? 'Revoked' : item.expired ? 'Expired' : 'Active';
      row.append(element('td', item.id), element('td', item.role),
        element('td', item.namespace || 'Global'), element('td', shortDate(item.expiresAt)), element('td', status));
      const action = document.createElement('td');
      if (!item.revoked && !item.expired) {
        const revoke = element('button', 'Revoke', 'danger-action'); revoke.type = 'button';
        revoke.setAttribute('aria-label', 'Revoke token ' + item.id);
        revoke.addEventListener('click', async () => {
          if (!window.confirm('Revoke token ' + item.id + '? This immediately removes its access.')) return;
          revoke.disabled = true;
          try { await request('/api/admin/tokens/' + encodeURIComponent(item.id), { method: 'DELETE' }); await loadManagedTokens(); }
          catch (error) { revoke.disabled = false; $('managed-tokens-status').textContent = 'Revocation not confirmed: ' + error.message; }
        });
        action.append(revoke);
      }
      row.append(action); $('managed-tokens-table').append(row);
    }
  } catch (error) {
    if (requestId === managedTokenView.requestId)
      $('managed-tokens-status').textContent = 'Issued token inventory unavailable: ' + error.message;
  } finally {
    if (requestId === managedTokenView.requestId) $('managed-tokens-more').disabled = false;
  }
}
$('managed-tokens-refresh').addEventListener('click', () => loadManagedTokens());
$('managed-tokens-more').addEventListener('click', () => loadManagedTokens(true));
$('activity-refresh').addEventListener('click', () => { loadActivity(); loadSkillAiJobs(); });
$('audit-more').addEventListener('click', () => loadAudit(true));


function updateSecurityPolicy() {
  const enforcement = securityPolicy.enforcement;
  const config = aiView.configuration;
  const enforcementLabel = enforcement === true ? 'On' : enforcement === false ? 'Off' : 'Unknown';
  const aiLabel = !config ? 'Unknown' : config.enabled && config.requireForApproval ? 'On' : 'Off';
  const policyDescription = enforcement === false
    ? 'When enforcement is off, the review state alone does not prove that distribution is blocked.'
    : enforcement === null ? 'Policy status has not been confirmed for the current credentials.'
      : 'Distribution status follows this instance policy; approval is not a safety guarantee.';
  $('security-policy').textContent = 'Distribution enforcement: ' + enforcementLabel +
    ' · Optional AI approval gate: ' + aiLabel + '. ' + policyDescription;
}

async function loadModels(append = false) {
  if (append && modelView.nextOffset === null) return;
  const requestId = ++modelView.requestId;
  const token = state.token;
  const offset = append ? modelView.nextOffset : 0;
  if (!append) { $('models-table').replaceChildren(); modelView.count = 0; modelView.nextOffset = null; }
  $('model-stage-panel').hidden = !canWrite();
  $('models-status').textContent = 'Loading published model metadata…';
  try {
    const response = await request('/api/v1/models?limit=50&offset=' + offset);
    if (requestId !== modelView.requestId || token !== state.token) return;
    const items = response.items || [];
    modelView.count += items.length;
    modelView.nextOffset = Number.isInteger(response.nextOffset) ? response.nextOffset : null;
    $('models-more').hidden = modelView.nextOffset === null;
    $('models-status').textContent = `${modelView.count} published descriptors loaded. Packory has not verified external model bytes or publisher identity.`;
    if (!modelView.count) {
      const row = document.createElement('tr'); const cell = element('td', 'No reviewed model descriptors were returned.', 'muted');
      cell.colSpan = 6; row.append(cell); $('models-table').append(row);
    }
    for (const item of items) {
      const row = document.createElement('tr');
      row.append(element('td', item.name), element('td', item.version),
        element('td', item.format), element('td', item.license), element('td', String(item.sizeBytes)));
      const action = document.createElement('td');
      const inspect = element('button', 'Inspect', 'button-secondary'); inspect.type = 'button';
      inspect.setAttribute('aria-expanded', 'false');
      inspect.addEventListener('click', () => showModelDetail(item, row, inspect));
      action.append(inspect); row.append(action); $('models-table').append(row);
    }
  } catch (error) {
    if (requestId === modelView.requestId) $('models-status').textContent = 'Model catalog unavailable: ' + error.message;
  }
}
async function showModelDetail(item, row, trigger) {
  const detail = openInlineDetails(row, trigger, 'Model descriptor detail', 6, 'model');
  if (!detail) return;
  const requestId = ++modelDetailRequest; const token = state.token;
  try {
    const entry = await request('/api/v1/model?name=' + encodeURIComponent(item.name) + '&version=' + encodeURIComponent(item.version));
    if (requestId !== modelDetailRequest || token !== state.token) return;
    detail.append(element('h3', entry.name + '@' + entry.version),
      element('p', 'Format ' + entry.format + ' · declared SHA-256 ' + entry.sha256 + ' · declared size ' + entry.sizeBytes + ' bytes'),
      element('p', 'License claim: ' + entry.license + ' · claimed publisher: ' + entry.publisher, 'muted'),
      element('p', entry.description),
      element('p', 'Metadata reviewed; external bytes and publisher identity were not verified by Packory.', 'scope-note'));
    const link = element('a', 'Open claimed HTTPS source'); link.href = entry.sourceUrl;
    link.target = '_blank'; link.rel = 'noopener noreferrer'; detail.append(link);
    if (entry.provenanceUrl) {
      const provenance = element('a', 'Open claimed provenance'); provenance.href = entry.provenanceUrl;
      provenance.target = '_blank'; provenance.rel = 'noopener noreferrer'; detail.append(' · ', provenance);
    }
    detail.scrollIntoView({ block: 'start' });
  } catch (error) { if (requestId === modelDetailRequest) detail.append(element('p', 'Model detail unavailable: ' + error.message)); }
}
async function loadModelAdmin(append = false) {
  if (!canAdmin()) { modelAdminView.requestId++; $('model-admin-panel').hidden = true; $('model-admin-table').replaceChildren(); return; }
  if (append && modelAdminView.nextOffset === null) return;
  $('model-admin-panel').hidden = false;
  const requestId = ++modelAdminView.requestId; const token = state.token;
  const offset = append ? modelAdminView.nextOffset : 0;
  if (!append) { $('model-admin-table').replaceChildren(); modelAdminView.count = 0; modelAdminView.nextOffset = null; }
  $('model-admin-status').textContent = 'Loading model review entries…';
  try {
    const response = await request('/api/v1/admin/models?limit=50&offset=' + offset);
    if (requestId !== modelAdminView.requestId || token !== state.token || !canAdmin()) return;
    const items = response.items || [];
    modelAdminView.count += items.length;
    modelAdminView.nextOffset = Number.isInteger(response.nextOffset) ? response.nextOffset : null;
    $('model-admin-more').hidden = modelAdminView.nextOffset === null;
    $('model-admin-status').textContent = `${modelAdminView.count} model metadata revisions loaded. No external bytes were verified.`;
    if (!modelAdminView.count) {
      const row = document.createElement('tr'); const cell = element('td', 'No model metadata is staged.', 'muted');
      cell.colSpan = 5; row.append(cell); $('model-admin-table').append(row);
    }
    for (const item of items) {
      const row = document.createElement('tr');
      row.append(element('td', item.name), element('td', item.version),
        element('td', item.reviewState), element('td', item.descriptorSha256?.slice(0, 16) + '…'));
      const action = document.createElement('td');
      const inspect = element('button', 'Inspect', 'button-secondary'); inspect.type = 'button';
      inspect.setAttribute('aria-expanded', 'false');
      inspect.addEventListener('click', () => showModelAdmin(item, row, inspect));
      action.append(inspect); row.append(action); $('model-admin-table').append(row);
    }
  } catch (error) { if (requestId === modelAdminView.requestId) $('model-admin-status').textContent = 'Model review unavailable: ' + error.message; }
}
async function showModelAdmin(item, row, trigger) {
  const detail = openInlineDetails(row, trigger, 'Model metadata review', 5, 'model-admin');
  if (!detail) return;
  const requestId = ++modelDetailRequest; const token = state.token;
  try {
    const response = await request('/api/v1/admin/model?name=' + encodeURIComponent(item.name) + '&version=' + encodeURIComponent(item.version));
    if (requestId !== modelDetailRequest || token !== state.token || !canAdmin()) return;
    const entry = response.entry;
    if (entry.descriptorSha256 !== item.descriptorSha256) throw new Error('Descriptor digest changed.');
    detail.append(element('h3', entry.name + '@' + entry.version),
      element('p', 'State: ' + entry.reviewState + ' · descriptor SHA-256: ' + entry.descriptorSha256),
      element('p', 'External bytes and publisher identity are unverified, including after metadata publication.', 'scope-note'));
    const source = element('pre', JSON.stringify(entry, null, 2), 'report-preview'); source.tabIndex = 0;
    detail.append(source, element('h4', 'Decision history'));
    for (const event of response.history || [])
      detail.append(element('p', `${event.at}: ${event.oldState} → ${event.newState} · ${event.actor} · ${event.reason}`, 'muted'));
    const action = entry.reviewState === 'PENDING' ? 'publish' : entry.reviewState === 'PUBLISHED' ? 'revoke' : null;
    if (action) {
      const button = element('button', action === 'publish' ? 'Publish reviewed metadata' : 'Revoke metadata', action === 'revoke' ? 'danger-action' : '');
      button.type = 'button'; button.addEventListener('click', () => decideModel(item, action)); detail.append(button);
    }
    detail.scrollIntoView({ block: 'start' });
  } catch (error) { if (requestId === modelDetailRequest) detail.append(element('p', 'Model review detail unavailable: ' + error.message)); }
}
async function decideModel(item, action) {
  const reason = window.prompt('Reason for ' + action + ' decision (3–256 characters):');
  if (reason === null) return;
  if (reason.trim().length < 3 || reason.length > 256) { $('model-admin-status').textContent = 'A reason of 3–256 characters is required.'; return; }
  if (!window.confirm(`${action === 'publish' ? 'Publish' : 'Revoke'} metadata ${item.name}@${item.version} at descriptor digest ${item.descriptorSha256}? Packory has not checked the model bytes.`)) return;
  try {
    await request('/api/v1/admin/models/decide', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: item.name, version: item.version, descriptorSha256: item.descriptorSha256, action, reason: reason.trim() }) });
    await Promise.all([loadModelAdmin(), loadModels()]);
  } catch (error) { $('model-admin-status').textContent = 'Model decision not confirmed: ' + error.message; }
}
$('model-stage-form').addEventListener('submit', async event => {
  event.preventDefault(); if (!canWrite()) return;
  const body = {
    name: $('model-name').value.trim(), version: $('model-version').value.trim(), format: $('model-format').value,
    sha256: $('model-sha256').value.trim(), sizeBytes: Number($('model-size').value),
    license: $('model-license').value.trim(), publisher: $('model-publisher').value.trim(),
    sourceUrl: $('model-source').value.trim(), description: $('model-description').value.trim()
  };
  if ($('model-provenance').value.trim()) body.provenanceUrl = $('model-provenance').value.trim();
  $('model-stage-status').textContent = 'Staging model metadata…';
  try {
    const result = await request('/api/v1/models/stage', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    $('model-stage-form').reset();
    $('model-stage-status').textContent = `Staged ${result.name}@${result.version} at descriptor SHA-256 ${result.descriptorSha256}. Admin review is required; bytes remain unverified.`;
    if (canAdmin()) await loadModelAdmin();
  } catch (error) { $('model-stage-status').textContent = 'Model metadata was not staged: ' + error.message; }
});
$('models-refresh').addEventListener('click', () => loadModels());
$('models-more').addEventListener('click', () => loadModels(true));
$('model-admin-refresh').addEventListener('click', () => loadModelAdmin());
$('model-admin-more').addEventListener('click', () => loadModelAdmin(true));

const publishHelp = {
  nuget: 'Select a .nupkg. Its package ID and version come from the nuspec inside the archive.',
  npm: 'Select a .tgz built by npm pack. Enter its exact package name and version; Packory validates the archive.',
  cargo: 'Select a .crate. Enter its exact name and version, and the Cargo publish metadata JSON.',
  pypi: 'Select a wheel or source distribution. Its filename and internal metadata must match the name and version.',
  oci: 'Upload each blob first, then publish a manifest that references those digests in the same repository.',
  maven: 'Enter the full repository path under /maven and select the matching file.',
  go: 'Select the module .zip and its go.mod. Packory verifies the module coordinates and archive.',
  raw: 'Enter an artifact name and version, select a file, and supply its media type.'
};
function updatePublisherForm() {
  const kind = $('publish-kind').value;
  const manifest = kind === 'oci' && $('publish-oci-mode').value === 'manifest';
  $('publish-format-help').textContent = publishHelp[kind];
  $('publish-feed-id-row').hidden = $('publish-feed').value !== 'private';
  $('publish-feed-id').required = $('publish-feed').value === 'private';
  $('publish-name-row').hidden = ['nuget', 'maven'].includes(kind);
  $('publish-version-row').hidden = ['nuget', 'maven'].includes(kind) || kind === 'oci' && !manifest;
  $('publish-path-row').hidden = kind !== 'maven';
  $('publish-media-row').hidden = kind !== 'raw' && !manifest;
  $('publish-metadata-row').hidden = kind !== 'cargo' && !manifest;
  $('publish-file-row').hidden = manifest;
  $('publish-second-file-row').hidden = kind !== 'go';
  $('publish-oci-mode-row').hidden = kind !== 'oci';
  $('publish-name').required = !$('publish-name-row').hidden;
  $('publish-version').required = !$('publish-version-row').hidden;
  $('publish-path').required = kind === 'maven';
  $('publish-file').required = !manifest;
  $('publish-second-file').required = kind === 'go';
  $('publish-metadata').required = kind === 'cargo' || manifest;
}
for (const id of ['publish-kind', 'publish-feed', 'publish-oci-mode']) $(id).addEventListener('change', updatePublisherForm);
updatePublisherForm();
const publishHex = bytes => [...new Uint8Array(bytes)].map(value => value.toString(16).padStart(2, '0')).join('');
const publishBase64 = bytes => {
  let text = '';
  for (let offset = 0; offset < bytes.length; offset += 8192)
    text += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
  return btoa(text);
};
function publishPathPart(value) {
  if (!value || value === '.' || value === '..' || /[\\%?#]/.test(value)) throw new Error('Invalid package path');
  return value.split('/').map(part => {
    if (!part || part === '.' || part === '..') throw new Error('Invalid package path');
    return encodeURIComponent(part);
  }).join('/');
}
async function nativePublish(path, options) {
  const browser = state.sso.authenticated;
  const requestHeaders = { ...headers(), ...(options.headers || {}) };
  if (browser) requestHeaders['X-Packory-Csrf'] = state.sso.csrfToken;
  const response = await fetch(path, { ...options, headers: requestHeaders, credentials: 'same-origin' });
  if (!response.ok) {
    let detail = '';
    try { const body = await response.json(); detail = body.error || body.detail || body.message || ''; } catch (_) { /* native protocol may return an empty body */ }
    throw new Error(`HTTP ${response.status}${detail ? ': ' + detail : ''}`);
  }
  return response;
}
async function publishSelectedPackage() {
  const kind = $('publish-kind').value;
  const feedId = $('publish-feed').value === 'private' ? $('publish-feed-id').value.trim() : '';
  if (feedId && !/^[a-z0-9][a-z0-9-]{1,62}$/.test(feedId)) throw new Error('Invalid feed ID');
  const privateRoot = feedId ? '/feeds/' + feedId : '';
  const name = $('publish-name').value.trim();
  const version = $('publish-version').value.trim();
  const file = $('publish-file').files[0];
  if (file && (!file.size || file.size > 64 * 1024 * 1024)) throw new Error('Choose a nonempty file of at most 64 MiB');
  if (kind === 'nuget') {
    const form = new FormData(); form.append('package', file);
    await nativePublish(privateRoot + '/nuget/v3/push', { method: 'PUT', body: form });
  } else if (kind === 'pypi') {
    const form = new FormData();
    form.append('name', name); form.append('version', version); form.append(':action', 'file_upload'); form.append('protocol_version', '1');
    form.append('sha256_digest', publishHex(await crypto.subtle.digest('SHA-256', await file.arrayBuffer())));
    form.append('content', file);
    await nativePublish(privateRoot + '/pypi/legacy/', { method: 'POST', body: form });
  } else if (kind === 'go') {
    const mod = $('publish-second-file').files[0];
    if (!mod || !mod.size || mod.size > 1024 * 1024) throw new Error('Choose a nonempty go.mod of at most 1 MiB');
    const form = new FormData(); form.append('module', name); form.append('version', version);
    form.append('zip', file); form.append('mod', mod);
    await nativePublish(privateRoot + '/api/go/modules', { method: 'POST', body: form });
  } else if (kind === 'raw') {
    await nativePublish(privateRoot + '/raw/' + publishPathPart(name) + '/' + publishPathPart(version),
      { method: 'PUT', body: file, headers: { 'Content-Type': $('publish-media').value.trim() || 'application/octet-stream' } });
  } else if (kind === 'maven') {
    await nativePublish(privateRoot + '/maven/' + publishPathPart($('publish-path').value.trim()),
      { method: 'PUT', body: file, headers: { 'Content-Type': file.type || 'application/octet-stream' } });
  } else if (kind === 'cargo') {
    const metadata = JSON.parse($('publish-metadata').value);
    if (metadata.name !== name || metadata.vers !== version) throw new Error('Cargo metadata name/vers must match the form');
    const json = new TextEncoder().encode(JSON.stringify(metadata)); const archive = new Uint8Array(await file.arrayBuffer());
    const body = new Uint8Array(json.length + archive.length + 8); const view = new DataView(body.buffer);
    view.setUint32(0, json.length, true); body.set(json, 4);
    view.setUint32(json.length + 4, archive.length, true); body.set(archive, json.length + 8);
    await nativePublish(privateRoot + '/cargo/api/v1/crates/new', { method: 'PUT', body, headers: { 'Content-Type': 'application/octet-stream' } });
  } else if (kind === 'npm') {
    const bytes = new Uint8Array(await file.arrayBuffer());
    const sha512 = btoa(String.fromCharCode(...new Uint8Array(await crypto.subtle.digest('SHA-512', bytes))));
    const sha1 = publishHex(await crypto.subtle.digest('SHA-1', bytes));
    const document = { name, versions: { [version]: { name, version, dist: { integrity: 'sha512-' + sha512, shasum: sha1 } } },
      'dist-tags': { latest: version }, _attachments: { [file.name]: { content_type: 'application/octet-stream', length: bytes.length, data: publishBase64(bytes) } } };
    await nativePublish(privateRoot + '/npm/' + publishPathPart(name),
      { method: 'PUT', body: JSON.stringify(document), headers: { 'Content-Type': 'application/json' } });
  } else if (kind === 'oci') {
    const repository = publishPathPart(name);
    const root = '/v2/' + (feedId ? 'feeds/' + feedId + '/' : '') + repository;
    if ($('publish-oci-mode').value === 'blob') {
      const digest = 'sha256:' + publishHex(await crypto.subtle.digest('SHA-256', await file.arrayBuffer()));
      const started = await nativePublish(root + '/blobs/uploads/', { method: 'POST' });
      const uploadLocation = started.headers.get('Location');
      if (!uploadLocation) throw new Error('OCI upload session did not return a location');
      const target = new URL(uploadLocation, window.location.origin);
      if (target.origin !== window.location.origin || !target.pathname.startsWith(root + '/blobs/uploads/')) throw new Error('Invalid OCI upload location');
      target.searchParams.set('digest', digest);
      await nativePublish(target.pathname + target.search, { method: 'PUT', body: file, headers: { 'Content-Type': 'application/octet-stream' } });
      return 'Published OCI blob ' + digest;
    }
    const manifest = JSON.parse($('publish-metadata').value);
    if (manifest.schemaVersion !== 2) throw new Error('OCI manifest needs schemaVersion 2');
    const media = $('publish-media').value.trim() || 'application/vnd.oci.image.manifest.v1+json';
    await nativePublish(root + '/manifests/' + publishPathPart(version),
      { method: 'PUT', body: JSON.stringify(manifest), headers: { 'Content-Type': media } });
  }
  return 'Published through the native ' + kind + ' endpoint. Verify the exact revision in Catalog or its private feed.';
}
$('native-publish-form').addEventListener('submit', async event => {
  event.preventDefault(); if (!canWrite()) return;
  const submit = $('publish-submit'); submit.disabled = true; $('publish-result').textContent = 'Uploading and validating…';
  try { $('publish-result').textContent = await publishSelectedPackage(); }
  catch (error) { $('publish-result').textContent = 'Publication failed: ' + error.message; }
  finally { submit.disabled = false; }
});

routeFromHash();


function showPackageDetail(item, row, trigger) {
  const content = openInlineDetails(row, trigger, 'Artifact details', 5, 'package');
  if (!content) return;
  content.append(element('h3', item.name + '@' + item.version));
  const facts = document.createElement('dl'); facts.className = 'package-facts';
  for (const [label, value] of [['Ecosystem', item.kind], ['Version', item.version],
    ['Origin', item.origin], ['Catalog record updated', item.updatedAt ? shortDate(item.updatedAt) : 'Not reported']]) {
    const row = element('div', '', 'feed-property'); row.append(element('dt', label), element('dd', value || 'Not reported')); facts.append(row);
  }
  content.append(facts);
  if (item.description) content.append(element('p', item.description));
  const note = element('p', 'The catalog API does not return an exact feed endpoint or native-client command. Use the supported client configuration for the correct feed; Packory will not guess a source or embed credentials.', 'scope-note');
  const link = document.createElement('a'); link.href = 'https://github.com/Cenn0/Packory/blob/main/docs/support-matrix.md';
  link.target = '_blank'; link.rel = 'noopener noreferrer'; link.textContent = 'Read the support matrix';
  note.append(' ', link); content.append(note);
  content.scrollIntoView({ block: 'start' });
}
