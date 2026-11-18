#!/usr/bin/env node
'use strict';

// Browser regression coverage of the real wwwroot assets using synthetic API
// responses. This is UI evidence, not an end-to-end backend or authorization test.
// Prerequisite: Playwright and its Chromium browser. A separately installed
// browser can be selected with PACKORY_CHROMIUM_EXECUTABLE.
// Optional: PACKORY_DASHBOARD_SCREENSHOT=/absolute/path/mobile.png
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '..');
const webroot = path.join(root, 'src/Packory/wwwroot');
const oldToken = 'synthetic-dashboard-old';
const newToken = 'synthetic-dashboard-new';
const archiveDigest = 'a'.repeat(64);
const fileDigest = 'b'.repeat(64);
const hostile = '<img src="/__dashboard_injection" onerror="window.__packoryInjected=1">';
const scriptText = '<script>window.__packoryInjected=2</script>';
const reason = 'Synthetic browser regression decision';

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

function qualityReport(overrides = {}) {
  return {
    assessmentStatus: 'completed', profileVersion: 'packory.skill-quality/1',
    archiveSha256: archiveDigest,
    coverage: { inspectedMarkdownFiles: 2, markdownFiles: 3, localLinksChecked: 4, externalLinksNotChecked: 1 },
    dimensions: [{ id: 'local_references', status: 'issues_found', errorCount: 1, warningCount: 2, infoCount: 3 }],
    findings: [{ code: 'REFERENCE_MISSING', severity: 'error', message: hostile,
      suggestion: scriptText, basis: 'Static file lookup', fileSha256: fileDigest, line: 7 }],
    findingsTruncated: false, ...overrides
  };
}

function record(prefix = 'old') {
  return { name: `${prefix}-skill`, version: '1.0.0', state: 'APPROVED',
    riskScore: 0, sha256: archiveDigest, updatedAt: '2026-01-01T12:00:00Z' };
}

function serverRecord(name) {
  return { name, version: '1.0.0', remoteUrl: 'https://mcp.example.test/mcp', publisherVerified: false };
}

function databaseRecord() {
  return { definition: { id: 'reporting-db', name: 'Synthetic reporting database', provider: 'postgresql',
    host: 'db.example.invalid', port: 5432, database: 'reporting', username: 'packory_reader',
    tlsServerName: 'db.example.invalid', oracleServerCertificateDn: null, enabled: true, revision: 1,
    queries: [{ name: 'lookup', description: 'Look up a synthetic row', sql: 'SELECT id,name FROM example_records WHERE id=@id',
      parameters: [{ name: 'id', type: 'int64', required: true, maxLength: 100 }], maxRows: 20, timeoutSeconds: 5 }]
  }, hasPassword: true, updatedAt: 123456789 };
}

function defaultReply(call) {
  const prefix = call.authorization === `Bearer ${newToken}` ? 'new' : 'old';
  const skill = record(prefix);
  switch (call.kind) {
    case 'session': return { enabled: false, authenticated: false, role: 'none' };
    case 'capabilities': return { role: call.authorization === `Bearer ${newToken}` ? 'read' : 'admin',
      source: 'static-token', canReadGlobal: true, canWrite: call.authorization !== `Bearer ${newToken}`,
      canAdmin: call.authorization !== `Bearer ${newToken}` };
    case 'catalog': return { items: [{ name: `${prefix}-package`, version: '1.0.0', kind: 'nuget', origin: 'hosted' }] };
    case 'skills': return { items: [{ name: skill.name, version: skill.version }] };
    case 'security': return { items: [skill] };
    case 'security-status': return { enforcement: true };
    case 'managed-tokens': return { items: [], nextOffset: null };
    case 'detail': return { skill, report: { fixture: `${prefix}-report`, untrustedText: hostile }, history: [] };
    case 'source': return { sha256: archiveDigest, files: [{ path: 'SKILL.md', bytes: 24, previewable: true }] };
    case 'source-file': return { sha256: archiveDigest, path: 'SKILL.md', bytes: 24, content: '# Synthetic Skill\n' };
    case 'quality': return qualityReport();
    case 'connector-list': return { items: [{
      id: 'team-database', name: 'Synthetic remote MCP', endpoint: 'https://mcp.example.org/mcp',
      enabled: false, allowedTools: [], hasBearerToken: true, revision: 1, updatedAt: 123456789
    }], allowedOrigins: ['https://mcp.example.org'], egressConfigured: true };
    case 'connector-probe': return { id: 'team-database',
      tools: [{ name: 'query_readonly', description: hostile, allowed: false }], count: 1 };
    case 'connector-update': return { id: call.body.id, revision: call.body.revision + 1, hasBearerToken: true };
    case 'connector-save': return { id: 'team-database', revision: 1, hasBearerToken: true };
    case 'connector-delete': return { deleted: true };
    case 'database-list': return { items: [databaseRecord()], enabled: true,
      allowedTargets: ['postgresql://db.example.invalid:5432'] };
    case 'database-probe': return { connected: true, qualification: 'Synthetic TLS probe completed.' };
    case 'database-query': return { result: { columns: ['id', 'name'], rows: [{ id: '1', name: hostile }], truncated: false, sourceDataUntrusted: true } };
    case 'database-audit': return { items: [{ connectorId: 'reporting-db', query: 'lookup', action: 'query_succeeded', actor: 'admin-token', createdAt: 123456789 }] };
    case 'database-update': case 'database-save': return { definition: { ...call.body.definition, revision: call.body.definition.revision + 1 }, hasPassword: true, updatedAt: 123456789 };
    case 'database-delete': return { deleted: true };
    case 'mcp': return { items: [serverRecord(`${prefix}-server`)], nextCursor: call.cursor ? null : 'fixture-next-page' };
    case 'ai-settings': return { configuration: { revision: 0, enabled: false,
      endpointId: 'openai', model: '', trigger: 'manual', requireForApproval: false,
      externalTransmissionApproved: false, timeoutSeconds: 180, maxConcurrentScans: 1,
      maxRequestsPerScan: 16, maxPromptBytes: 32768, maxOutputTokens: 4096,
      monthlyBudgetMicroUsd: 10000000, inputUsdPerMillionTokens: 0,
      outputUsdPerMillionTokens: 0, outputLanguage: 'English' },
      hasApiKey: false, endpoints: [{ id: 'openai', provider: 'openai',
        baseUrl: 'https://api.openai.com/v1', external: true }],
      usage: { month: '2026-10', reservedMicroUsd: 0, requests: 0, requestsWithMissingUsage: 0 } };
    case 'ai-settings-save': return { configuration: { revision: 1 }, hasApiKey: false };
    case 'ai-assessments': return { items: [{ id: 'a'.repeat(32), kind: 'assessment',
      name: skill.name, version: skill.version, state: 'COMPLETED', riskScore: 18,
      revision: 0, createdAt: '2026-10-23', configuration: { model: hostile } }] };
    case 'ai-jobs': return { items: [{ id: 'a'.repeat(32), kind: 'assessment',
      name: skill.name, version: skill.version, state: 'COMPLETED', riskScore: 18,
      revision: 0, createdAt: '2026-10-23', configuration: { model: hostile } }] };
    case 'ai-audit': return { items: [{ action: 'configuration_changed', revision: 0, reason: hostile }] };
    case 'ai-job-detail': return { job: { state: 'COMPLETED', kind: 'assessment',
      riskScore: 18, sha256: archiveDigest, revision: 0, scannerVersion: '2.13.0',
      configuration: { model: hostile } }, report: { untrustedText: hostile } };
    case 'ai-probe': return { id: 'b'.repeat(32), kind: 'probe' };
    case 'ai-cancel': return { status: 'CANCELLED' };
    case 'ai-queue': return { id: 'c'.repeat(32), state: 'QUEUED' };
    case 'decision': return { status: call.action === 'revoke' ? 'REVOKED' : 'QUEUED' };
    default: throw new Error(`Unexpected mocked route: ${call.method} ${call.pathname}`);
  }
}

function classify(url, request) {
  const pathname = url.pathname;
  let kind, action;
  if (pathname === '/auth/session') kind = 'session';
  else if (pathname === '/api/auth/capabilities') kind = 'capabilities';
  else if (pathname === '/api/packages') kind = 'catalog';
  else if (pathname === '/api/skills') kind = 'skills';
  else if (pathname === '/api/security/skills') kind = 'security';
  else if (pathname === '/api/security/status') kind = 'security-status';
  else if (pathname === '/api/admin/tokens') kind = 'managed-tokens';
  else if (/^\/api\/security\/skills\/[^/]+\/[^/]+$/.test(pathname)) kind = 'detail';
  else if (/^\/api\/security\/skills\/[^/]+\/[^/]+\/source$/.test(pathname)) kind = 'source';
  else if (/^\/api\/security\/skills\/[^/]+\/[^/]+\/source\/file$/.test(pathname)) kind = 'source-file';
  else if (/^\/api\/v1\/skills\/[^/]+\/[^/]+\/quality$/.test(pathname)) kind = 'quality';
  else if (pathname === '/api/v1/mcp/servers') kind = 'mcp';
  else if (pathname === '/api/v1/admin/mcp/connectors')
    kind = request.method() === 'POST' ? 'connector-save' : 'connector-list';
  else if (/^\/api\/v1\/admin\/mcp\/connectors\/[^/]+\/probe$/.test(pathname)) kind = 'connector-probe';
  else if (/^\/api\/v1\/admin\/mcp\/connectors\/[^/]+$/.test(pathname))
    kind = request.method() === 'DELETE' ? 'connector-delete' : 'connector-update';
  else if (pathname === '/api/v1/admin/database-connectors')
    kind = request.method() === 'POST' ? 'database-save' : 'database-list';
  else if (/^\/api\/v1\/admin\/database-connectors\/[^/]+\/(probe|query|audit)$/.test(pathname))
    kind = 'database-' + pathname.split('/').at(-1);
  else if (/^\/api\/v1\/admin\/database-connectors\/[^/]+$/.test(pathname))
    kind = request.method() === 'DELETE' ? 'database-delete' : 'database-update';
  else if (pathname === '/api/admin/skill-ai/settings') kind = request.method() === 'PUT' ? 'ai-settings-save' : 'ai-settings';
  else if (pathname === '/api/admin/skill-ai/audit') kind = 'ai-audit';
  else if (pathname === '/api/admin/skill-ai/assessments') kind = request.method() === 'POST' ? 'ai-queue' : (url.searchParams.has('name') ? 'ai-assessments' : 'ai-jobs');
  else if (pathname === '/api/admin/skill-ai/probe') kind = 'ai-probe';
  else if (/^\/api\/admin\/skill-ai\/jobs\/[a-f0-9]{32}\/cancel$/.test(pathname)) kind = 'ai-cancel';
  else if (/^\/api\/admin\/skill-ai\/jobs\/[a-f0-9]{32}$/.test(pathname)) kind = 'ai-job-detail';
  else if (/^\/api\/security\/skills\/[^/]+\/[^/]+\/(revoke|rescan)$/.test(pathname)) {
    kind = 'decision'; action = pathname.split('/').at(-1);
  }
  return { kind, action, pathname, method: request.method(), q: url.searchParams.get('q'),
    cursor: url.searchParams.get('cursor'), limit: url.searchParams.get('limit'),
    authorization: request.headers().authorization || '',
    browserHeader: request.headers()['x-packory-browser'] || '',
    csrfHeader: request.headers()['x-packory-csrf'] || '',
    rawBody: request.postData(),
    body: ['POST', 'PUT'].includes(request.method()) ? request.postDataJSON() : null };
}

async function serveAssets() {
  const program = await fs.readFile(path.join(root, 'src/Packory/Program.cs'), 'utf8');
  const match = program.match(/Response\.Headers\["Content-Security-Policy"\]\s*=\s*"([^"\r\n]+)";/);
  assert.ok(match, 'Cannot find the application CSP; update the static test server when its definition changes.');
  const csp = match[1];
  const mime = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml' };
  const server = http.createServer(async (request, response) => {
    try {
      const pathname = decodeURIComponent(new URL(request.url, 'http://127.0.0.1').pathname);
      const file = path.resolve(webroot, '.' + (pathname === '/' ? '/index.html' : pathname));
      if (!file.startsWith(webroot + path.sep) || !['GET', 'HEAD'].includes(request.method)) {
        response.writeHead(400); response.end(); return;
      }
      const bytes = await fs.readFile(file);
      response.writeHead(200, { 'Content-Type': mime[path.extname(file)] || 'application/octet-stream',
        'Content-Security-Policy': csp, 'X-Content-Type-Options': 'nosniff', 'Cache-Control': 'no-store' });
      response.end(request.method === 'HEAD' ? undefined : bytes);
    } catch {
      response.writeHead(404); response.end();
    }
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject); server.listen(0, '127.0.0.1', resolve);
  });
  return { server, csp, origin: `http://127.0.0.1:${server.address().port}` };
}

async function harness(browser, origin, reply = defaultReply) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1100 },
    locale: 'en-US', timezoneId: 'UTC', reducedMotion: 'reduce', serviceWorkers: 'block' });
  const page = await context.newPage();
  page.setDefaultTimeout(8000);
  const calls = [], failures = [], holds = new Map(), allHolds = [];
  let closing = false;
  page.on('pageerror', error => failures.push(`Browser JavaScript error: ${error.message}`));
  await page.addInitScript(() => {
    window.__dashboardPolicyViolations = [];
    document.addEventListener('securitypolicyviolation', event => {
      window.__dashboardPolicyViolations.push(event.violatedDirective);
    });
  });
  await context.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin !== origin || url.pathname === '/__dashboard_injection') {
      failures.push('Unexpected external or injected resource request');
      await route.abort(); return;
    }
    if (!url.pathname.startsWith('/api/') && url.pathname !== '/auth/session') {
      await route.continue(); return;
    }
    let held;
    try {
      const call = classify(url, request); calls.push(call);
      const result = reply(call);
      held = holds.get(call.kind)?.shift();
      if (held) { held.request = request; held.seen.resolve(call); await held.release.promise; }
      const status = result.httpStatus ?? 200;
      const body = Object.hasOwn(result, 'httpBody') ? result.httpBody : result;
      await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    } catch (error) {
      if (!closing) {
        failures.push(error.message);
        await route.fulfill({ status: 500, contentType: 'application/json', body: '{}' }).catch(() => {});
      }
    } finally { held?.finished.resolve(); }
  });
  const documentResponse = await page.goto(origin, { waitUntil: 'load' });
  async function text(id, value) {
    await page.waitForFunction(({ id, value }) => document.getElementById(id)?.textContent.includes(value), { id, value });
  }
  async function ready(prefix = 'old', security = false) {
    await text('packages', `${prefix}-package`);
    await text('skills', `${prefix}-skill`);
    await text('mcp-table', `${prefix}-server`);
    if (security) await text('security-table', `${prefix}-skill`);
  }
  async function assess() {
    await page.locator('#skills button').click();
    await text('quality-detail', 'Archive SHA-256: ');
  }
  return { page, calls, documentResponse, text, ready, assess,
    async authenticate() {
      await ready();
      await page.locator('#token').fill(oldToken);
      await page.locator('#token').press('Tab');
      await ready('old', true);
    },
    holdNext(kind) {
      const held = { seen: deferred(), release: deferred(), finished: deferred() };
      if (!holds.has(kind)) holds.set(kind, []);
      holds.get(kind).push(held); allHolds.push(held);
      return { seen: held.seen.promise, release: async () => {
        held.release.resolve(); await held.finished.promise;
        const response = await held.request.response();
        if (response) await response.finished();
        // Flush the delivered response and its UI work even if a different
        // request remains deliberately held; networkidle cannot do that.
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(resolve)));
      } };
    },
    async drained() {
      // Only used after releasing held responses, so negative DOM assertions
      // cannot pass before those old fetches have finished and been handled.
      await page.waitForLoadState('networkidle');
    },
    async healthy() {
      assert.deepEqual(failures, []);
      assert.equal(await page.evaluate(() => window.__packoryInjected), undefined, 'Untrusted markup executed');
      assert.deepEqual(await page.evaluate(() => window.__dashboardPolicyViolations), [], 'Unexpected CSP violation');
    },
    async close() {
      closing = true;
      for (const held of allHolds) held.release.resolve();
      await context.close();
    }
  };
}

async function openDatabaseDashboard(h, id = 'reporting-db') {
  await h.page.locator('a[href="#settings"][data-route]').click();
  await h.page.locator('#token').fill(oldToken); await h.page.locator('#token').press('Tab');
  await h.page.locator('#nav-databases').waitFor({ state: 'visible' });
  await h.page.locator('#nav-databases').click();
  await h.text('database-table', id);
}

async function assertCleared(page) {
  for (const id of ['packages', 'skills', 'security-table', 'security-detail', 'mcp-table', 'quality-detail', 'ai-jobs', 'ai-audit', 'ai-job-detail'])
    assert.equal(await page.locator(`#${id}`).textContent(), '', `${id} retained protected data`);
  for (const id of ['quality-panel', 'security-detail', 'mcp-more', 'skill-ai-panel'])
    assert.equal(await page.locator(`#${id}`).isVisible(), false, `${id} should be hidden`);
  for (const id of ['security-pending', 'security-review', 'security-quarantine', 'security-approved'])
    assert.equal(await page.locator(`#${id}`).textContent(), '—');
  for (const id of ['feedback', 'security-status', 'mcp-status'])
    assert.match(await page.locator(`#${id}`).textContent(), /^Credentials changed\./);
}

async function qualityScenario(h) {
  await h.ready();
  await h.assess();
  assert.match(await h.page.locator('#quality-status').textContent(), /^Static checks completed\./);
  await h.text('quality-detail', archiveDigest);
  await h.text('quality-detail', fileDigest);
  await h.text('quality-detail', '1 errors, 2 warnings, 3 suggestions');
  await h.text('quality-detail', '2 of 3 Markdown files inspected; 4 local references checked. 1 external references were not fetched.');
  const finding = h.page.locator('.quality-finding');
  assert.equal(await finding.locator('p').nth(0).textContent(), hostile);
  assert.equal(await finding.locator('p').nth(1).textContent(), scriptText);
  assert.match(await finding.locator('h3').textContent(), /REFERENCE_MISSING · error/);
  assert.equal(await h.page.locator('#quality-detail img, #quality-detail script').count(), 0);
}

async function paginationScenario(h) {
  await h.ready();
  await h.page.locator('#mcp-query').fill('  reviewed + alpha  ');
  await h.page.locator('#mcp-filter button').click();
  await h.text('mcp-table', 'alpha-2');
  assert.equal(await h.page.locator('#mcp-more').isVisible(), true);
  await h.page.locator('#mcp-more').click();
  await h.text('mcp-table', 'alpha-3');
  assert.equal(await h.page.locator('#mcp-table tr').count(), 3);
  assert.equal(await h.page.locator('#mcp-more').isVisible(), false);
  await h.text('mcp-status', '3 operator-published server revisions displayed.');
  const queries = h.calls.filter(call => call.kind === 'mcp' && call.q === 'reviewed + alpha');
  assert.deepEqual(queries.map(call => [call.q, call.limit, call.cursor]), [
    ['reviewed + alpha', '50', null], ['reviewed + alpha', '50', 'opaque:next+/=?']
  ]);
  await h.page.locator('#mcp-query').fill('beta');
  await h.page.locator('#mcp-filter button').click();
  await h.text('mcp-table', 'beta-1');
  assert.equal(await h.page.locator('#mcp-table tr').count(), 1);
  assert.doesNotMatch(await h.page.locator('#mcp-table').textContent(), /alpha-/);
  assert.equal(h.calls.filter(call => call.kind === 'mcp').at(-1).cursor, null);
  assert.equal(await h.page.locator('#mcp-table a').count(), 0, 'Remote endpoints must remain inert text');
}

async function tokenScenario(h) {
  await h.authenticate();
  await h.assess();
  await h.page.locator('#security-table button').click();
  await h.text('security-detail', 'old-report');
  await h.text('ai-jobs', 'old-skill');
  assert.equal(await h.page.locator('#mcp-more').isVisible(), true);

  const detail = h.holdNext('detail');
  await h.page.locator('#security-table button').click(); await detail.seen;
  const catalog = h.holdNext('catalog'), skills = h.holdNext('skills');
  await h.page.locator('#refresh').click(); await Promise.all([catalog.seen, skills.seen]);
  const security = h.holdNext('security');
  await h.page.locator('#security-refresh').click(); await security.seen;
  const mcp = h.holdNext('mcp');
  await h.page.locator('#mcp-filter button').click(); await mcp.seen;
  const quality = h.holdNext('quality');
  await h.page.locator('#skills button').click(); await quality.seen;
  assert.equal(await h.page.locator('#quality-panel').isVisible(), true);

  // fill dispatches input but leaves focus in the field. The change event and
  // new-token refresh are deliberately deferred until stale replies are drained.
  await h.page.locator('#token').fill(newToken);
  await assertCleared(h.page);
  await Promise.all([detail, catalog, skills, security, mcp, quality].map(held => held.release()));
  await h.drained();
  await assertCleared(h.page);

  const boundary = h.calls.length;
  await h.page.locator('#token').press('Tab');
  await h.ready('new', true);
  await h.text('ai-status', 'AI is disabled');
  await h.text('ai-jobs', 'new-skill');
  const fresh = h.calls.slice(boundary);
  assert.deepEqual(fresh.map(call => call.kind).sort(),
    ['ai-audit', 'ai-jobs', 'ai-settings', 'catalog', 'mcp', 'security', 'skills']);
  assert.ok(fresh.every(call => call.authorization === `Bearer ${newToken}`));
  for (const id of ['packages', 'skills', 'security-table', 'mcp-table'])
    assert.doesNotMatch(await h.page.locator(`#${id}`).textContent(), /old-/);
}

async function invalidationScenario(h) {
  await h.authenticate();
  h.page.on('dialog', dialog => dialog.accept(dialog.type() === 'prompt' ? reason : undefined));
  for (const action of ['revoke', 'rescan']) {
    await h.page.locator('#security-table button').click();
    await h.text('security-detail', 'old-report');
    await h.assess();
    const oldQuality = h.holdNext('quality');
    await h.page.locator('#skills button').click(); await oldQuality.seen;
    const decision = h.holdNext('decision');
    const name = action === 'revoke' ? 'Revoke distribution' : 'Re-scan (temporarily blocks distribution)';
    await h.page.getByRole('button', { name, exact: true }).click();
    const call = await decision.seen;
    assert.equal(call.action, action);
    assert.equal(call.method, 'POST');
    assert.equal(call.authorization, `Bearer ${oldToken}`);
    assert.deepEqual(call.body, { sha256: archiveDigest, reason });
    assert.equal(await h.page.locator('#quality-panel').isVisible(), true);
    await decision.release();
    await h.page.locator('#quality-panel').waitFor({ state: 'hidden' });
    await h.ready('old', true);
    await oldQuality.release(); await h.drained();
    assert.equal(await h.page.locator('#quality-panel').isVisible(), false);
    assert.equal(await h.page.locator('#quality-detail').textContent(), '');
  }
  for (const refresh of ['refresh', 'security-refresh']) {
    await h.assess();
    const oldQuality = h.holdNext('quality');
    await h.page.locator('#skills button').click(); await oldQuality.seen;
    await h.page.locator(`#${refresh}`).click();
    await h.page.locator('#quality-panel').waitFor({ state: 'hidden' });
    await oldQuality.release(); await h.drained();
    assert.equal(await h.page.locator('#quality-panel').isVisible(), false);
    assert.equal(await h.page.locator('#quality-detail').textContent(), '');
  }
}

async function skillAiScenario(h) {
  await h.authenticate();
  h.page.on('dialog', dialog => dialog.accept(dialog.type() === 'prompt' ? reason : undefined));
  await h.text('ai-status', 'AI is disabled');
  assert.equal(await h.page.locator('#skill-ai-panel').isVisible(), true);
  assert.equal(await h.page.locator('#ai-enabled').isChecked(), false);
  let probeCount = h.calls.filter(call => call.kind === 'ai-probe').length;
  await h.page.locator('#ai-probe').click();
  await h.text('ai-status', 'AI is disabled in the saved configuration');
  assert.equal(h.calls.filter(call => call.kind === 'ai-probe').length, probeCount,
    'A disabled saved configuration must not queue a probe.');
  await h.text('ai-jobs', 'old-skill');
  await h.page.locator('#ai-jobs button').first().click();
  await h.text('ai-job-detail', hostile);
  assert.equal(await h.page.locator('#ai-job-detail img, #ai-job-detail script').count(), 0);
  assert.equal(await h.page.locator('#ai-audit img, #ai-audit script').count(), 0);
  await h.page.locator('#security-table button').click();
  await h.text('security-detail', 'Optional AI analysis');
  await h.text('security-detail', hostile);
  assert.equal(await h.page.locator('#security-detail img, #security-detail script').count(), 0);
  await h.page.locator('#ai-enabled').check();
  await h.page.locator('#ai-model').fill('example-model');
  await h.page.locator('#ai-key').fill('synthetic-do-not-store');
  await h.page.locator('#ai-input-rate').fill('1');
  await h.page.locator('#ai-output-rate').fill('2');
  await h.page.locator('#ai-transmission').check();
  await h.page.locator('#ai-reason').fill('Synthetic opt in');
  await h.page.locator('#ai-probe').click();
  await h.text('ai-status', 'Unsaved AI settings');
  assert.equal(h.calls.filter(call => call.kind === 'ai-probe').length, probeCount,
    'Unsaved form values must not queue a probe.');
  const oldCount = h.calls.length;
  await h.page.locator('#ai-save').click();
  await h.page.waitForFunction(() => document.getElementById('ai-key').value === '');
  await h.text('ai-status', 'AI enabled; human approval');
  const put = h.calls.slice(oldCount).find(call => call.kind === 'ai-settings-save');
  assert.ok(put, 'AI settings must be sent to backend');
  assert.equal(put.method, 'PUT');
  assert.equal(put.body.configuration.enabled, true);
  assert.equal(put.body.configuration.model, 'example-model');
  assert.equal(put.body.reason, 'Synthetic opt in');
  assert.equal(put.body.apiKey, 'synthetic-do-not-store');
  assert.equal(await h.page.locator('#ai-key').inputValue(), '', 'Credential must leave the input after save');

  probeCount = h.calls.filter(call => call.kind === 'ai-probe').length;
  await h.page.locator('#ai-probe').click();
  await h.text('ai-status', 'Provider connection test queued');
  const probe = h.calls.slice(oldCount).find(call => call.kind === 'ai-probe');
  assert.ok(probe, 'A saved, enabled configuration must be used for the provider probe.');
  assert.equal(probe.body.reason, reason);
}

async function layoutScenario(h) {
  await h.page.locator('#token').fill(oldToken);
  await h.page.locator('#token').press('Tab');
  await h.text('skills', 'fixture-');
  await h.text('security-table', 'fixture-');
  await h.assess();
  await h.page.locator('#security-table button').click();
  await h.text('security-detail', 'Scan report');
  for (const width of [1440, 375]) {
    await h.page.setViewportSize({ width, height: 1000 });
    const size = await h.page.evaluate(() => ({ viewport: document.documentElement.clientWidth,
      content: Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) }));
    assert.ok(size.content <= size.viewport + 1,
      `${width}px layout overflows horizontally: ${size.content}px content / ${size.viewport}px viewport`);
  }
  if (process.env.PACKORY_DASHBOARD_SCREENSHOT) {
    const screenshot = path.resolve(process.env.PACKORY_DASHBOARD_SCREENSHOT);
    await fs.mkdir(path.dirname(screenshot), { recursive: true });
    await h.page.screenshot({ path: screenshot, fullPage: true, animations: 'disabled' });
  }
}

async function main() {
  let assets, browser, active = 'startup', passed = 0;
  try {
    assets = await serveAssets();
    browser = await chromium.launch({ headless: true, timeout: 15000,
      ...(process.env.PACKORY_CHROMIUM_EXECUTABLE ? { executablePath: process.env.PACKORY_CHROMIUM_EXECUTABLE } : {}) });
    // Each scenario owns a new browser context and fresh in-memory fixtures.
    async function scenario(name, reply, run) {
      active = name;
      const h = await harness(browser, assets.origin, reply);
      let timer;
      try {
        assert.equal(h.documentResponse.headers()['content-security-policy'], assets.csp);
        await Promise.race([run(h), new Promise((_, reject) => {
          timer = setTimeout(() => reject(new Error('Scenario exceeded 30 seconds')), 30000);
        })]);
        await h.healthy(); passed++;
      } finally { clearTimeout(timer); await h.close(); }
    }
    let qualityMode = 'completed';
    await scenario('quality rendering and errors', call => {
      if (call.kind !== 'quality') return defaultReply(call);
      if (qualityMode === 'error') return { httpStatus: 503, httpBody: { error: 'synthetic-unavailable' } };
      return qualityReport(qualityMode === 'incomplete' ? { assessmentStatus: 'incomplete', findingsTruncated: true } : {});
    }, async h => {
      await h.text('skills', 'old-skill');
      await h.page.locator('#skills button').click();
      await h.page.locator('#skills .inline-detail-panel').getByText('Archive SHA-256:', { exact: false }).waitFor();
      assert.match(await h.page.locator('#skills .inline-detail-panel').textContent(), /Static checks completed/);
      assert.equal(await h.page.locator('.quality-finding p').first().textContent(), hostile);
      assert.equal(await h.page.locator('#skills .inline-detail-panel img, #skills .inline-detail-panel script').count(), 0);
      await h.page.locator('#skills button').click();
      qualityMode = 'incomplete'; await h.page.locator('#skills button').click();
      await h.page.locator('#skills .inline-detail-panel').getByText('Assessment incomplete.', { exact: false }).waitFor();
      assert.match(await h.page.locator('#skills .inline-detail-panel').textContent(), /findings display limit was reached/);
      await h.page.locator('#skills button').click();
      qualityMode = 'error'; await h.page.locator('#skills button').click();
      await h.page.locator('#skills .inline-detail-panel').getByText('Quality report unavailable: HTTP 503').waitFor();
    });
    await scenario('MCP cursor and query paging', call => {
      if (call.kind !== 'mcp' || !call.q) return defaultReply(call);
      if (call.q === 'reviewed + alpha') return call.cursor
        ? { items: [serverRecord('alpha-3')], nextCursor: null }
        : { items: [serverRecord('alpha-1'), serverRecord('alpha-2')], nextCursor: 'opaque:next+/=?' };
      assert.equal(call.q, 'beta');
      return { items: [serverRecord('beta-1')], nextCursor: null };
    }, async h => {
      await h.page.locator('#mcp-tab').click();
      await h.text('mcp-table', 'old-server');
      await h.page.locator('#mcp-query').fill('  reviewed + alpha  ');
      await h.page.locator('#mcp-filter button').click();
      await h.text('mcp-table', 'alpha-2');
      await h.page.locator('#mcp-more').click();
      await h.text('mcp-table', 'alpha-3');
      assert.equal(await h.page.locator('#mcp-table a').count(), 0, 'Untrusted remote URLs must remain text');
      assert.deepEqual(h.calls.filter(x => x.kind === 'mcp' && x.q === 'reviewed + alpha').map(x => x.cursor),
        [null, 'opaque:next+/=?']);
    });
    await scenario('MCP connector UI keeps credentials write-only and escapes remote output', defaultReply, async h => {
      await h.page.locator('a[href="#settings"][data-route]').click();
      await h.page.locator('#token').fill(oldToken);
      await h.page.locator('#token').press('Tab');
      await h.page.locator('#nav-connectors').waitFor({ state: 'visible' });
      await h.page.locator('#nav-connectors').click();
      await h.text('connector-table', 'team-database');
      assert.doesNotMatch(await h.page.locator('#connector-table').textContent(), /synthetic-connector-credential/);
      await h.page.locator('#connector-table button').filter({ hasText: 'Test' }).click();
      await h.text('connector-test-results', 'query_readonly');
      assert.match(await h.page.locator('#connector-test-results').textContent(), /<img/);
      assert.equal(await h.page.locator('#connector-test-results img, #connector-test-results script').count(), 0);
      await h.page.locator('#connector-table button').filter({ hasText: 'Edit' }).click();
      assert.equal(await h.page.locator('#connector-bearer').inputValue(), '');
      await h.page.locator('#connector-bearer').fill('synthetic-connector-credential-browser-only');
      await h.page.locator('#connector-tools').fill('query_readonly');
      await h.page.locator('#connector-enabled').check();
      const saving = h.holdNext('connector-update');
      await h.page.locator('#connector-save').click(); const write = await saving.seen;
      assert.equal(await h.page.locator('#connector-bearer').inputValue(), '', 'Clear the remote credential at dispatch');
      await h.page.locator('#connector-name').fill('Newer remote draft');
      await h.page.locator('#connector-bearer').fill('synthetic-newer-remote-credential');
      await saving.release(); await h.drained();
      await h.text('connector-form-status', 'newer edits are still unsaved');
      assert.equal(await h.page.locator('#connector-name').inputValue(), 'Newer remote draft');
      assert.equal(await h.page.locator('#connector-bearer').inputValue(), 'synthetic-newer-remote-credential');
      assert.ok(write, 'The dashboard must send an administrator update');
      assert.equal(write.method, 'PUT');
      assert.equal(write.authorization, 'Bearer ' + oldToken);
      assert.equal(write.body.bearerToken, 'synthetic-connector-credential-browser-only');
      assert.equal(write.body.enabled, true);
      assert.deepEqual(write.body.allowedTools, ['query_readonly']);
      const newer = h.holdNext('connector-update');
      await h.page.locator('#connector-save').click(); const newerWrite = await newer.seen;
      assert.equal(newerWrite.body.revision, 2);
      assert.equal(newerWrite.body.bearerToken, 'synthetic-newer-remote-credential');
      assert.equal(await h.page.locator('#connector-bearer').inputValue(), '');
      await newer.release(); await h.text('connector-form-status', 'Connector saved');
      assert.equal(await h.page.locator('#connector-bearer').inputValue(), '');
      assert.equal(await h.page.locator('#nav-connectors').isVisible(), true);
      await h.healthy();
    });
    await scenario('token role change clears administrator UI', defaultReply, async h => {
      await h.page.locator('a[href="#settings"][data-route]').click();
      await h.page.locator('#token').fill(oldToken);
      await h.page.locator('#token').press('Tab');
      await h.page.locator('#nav-publish').waitFor({ state: 'visible' });
      await h.page.locator('a[href="#reviews"][data-route]').click();
      await h.text('security-table', 'old-skill');
      await h.page.locator('a[href="#settings"][data-route]').click();
      await h.page.locator('#token').fill(newToken);
      await h.page.locator('#token').press('Tab');
      await h.page.locator('#nav-publish').waitFor({ state: 'hidden' });
      assert.equal(await h.page.locator('#security-table').textContent(), '');
      assert.equal(await h.page.locator('#nav-reviews').isVisible(), false);
      assert.ok(h.calls.some(x => x.kind === 'capabilities' && x.authorization === `Bearer ${newToken}`));
    });
    await scenario('browser SSO session and CSRF headers', call => {
      if (call.kind === 'session')
        return { enabled: true, authenticated: true, role: 'admin', csrfToken: 'synthetic-csrf-token' };
      return defaultReply(call);
    }, async h => {
      await h.text('sso-status', 'Signed in · admin');
      await h.page.locator('#nav-publish').waitFor({ state: 'visible' });
      await h.page.locator('#nav-reviews').waitFor({ state: 'visible' });
      assert.equal(await h.page.locator('#sso-login').isVisible(), false);
      assert.equal(await h.page.locator('#sso-logout').isVisible(), true);
      assert.equal(await h.page.locator('#api-token-fallback').isVisible(), false);
      // Exercise the actual dashboard request transport, not a synthetic header builder.
      await h.page.evaluate(() => request('/api/admin/skill-ai/settings', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: '{}'
      }));
      const put = h.calls.find(x => x.kind === 'ai-settings-save');
      assert.ok(put, 'browser must reach admin endpoint using same-origin request');
      assert.equal(put.browserHeader, '1');
      assert.equal(put.csrfHeader, 'synthetic-csrf-token');
      assert.equal(put.authorization, '', 'browser session must not synthesize bearer tokens');
    });
    await scenario('review decision binds digest and clears stale quality', defaultReply, async h => {
      await h.page.locator('a[href="#settings"][data-route]').click();
      await h.page.locator('#token').fill(oldToken);
      await h.page.locator('#token').press('Tab');
      await h.page.locator('a[href="#reviews"][data-route]').click();
      await h.text('security-table', 'old-skill');
      await h.page.locator('#security-table button').click();
      await h.page.locator('.inline-detail-panel').getByRole('button', { name: 'Revoke distribution' }).waitFor();
      assert.match(await h.page.locator('.inline-detail-panel').textContent(), /old-report/);
      await h.page.locator('a[href="#catalog"][data-route]').click();
      await h.text('skills', 'old-skill');
      await h.page.locator('#skills button').click();
      await h.page.locator('#skills .inline-detail-panel').getByText('Archive SHA-256:', { exact: false }).waitFor();
      await h.page.locator('a[href="#reviews"][data-route]').click();
      await h.page.locator('#security-table button').click();
      await h.page.locator('.inline-detail-panel').getByRole('button', { name: 'Revoke distribution' }).click();
      await h.page.locator('#decision-reason').fill(reason);
      const decisionResponse = h.page.waitForResponse(response =>
        new URL(response.url()).pathname.endsWith('/revoke'));
      await h.page.locator('#decision-submit').click();
      await decisionResponse;
      const decision = h.calls.find(x => x.kind === 'decision' && x.action === 'revoke');
      assert.ok(decision, 'revocation request must reach its exact revision');
      assert.equal(decision.authorization, `Bearer ${oldToken}`);
      assert.deepEqual(decision.body, { sha256: archiveDigest, reason });
      assert.equal(await h.page.locator('#skills .inline-detail-panel').count(), 0);
    });
    await scenario('native database editor handles every provider, secrets, queries and audit', defaultReply, async h => {
      await openDatabaseDashboard(h);
      await h.page.locator('#database-table button').filter({ hasText: 'Test connection' }).click();
      await h.text('database-results', 'Connection verified');
      await h.page.locator('#database-table button').filter({ hasText: 'Run query' }).click();
      await h.page.locator('#database-query-arguments').fill('{"id":1}');
      await h.page.locator('#database-run').click(); await h.text('database-query-results', '<img');
      assert.equal(await h.page.locator('#database-query-results img, #database-query-results script').count(), 0);
      assert.deepEqual(h.calls.find(c => c.kind === 'database-query').body, { query: 'lookup', arguments: { id: 1 } });
      await h.page.locator('#database-table button').filter({ hasText: 'Audit' }).click(); await h.text('database-results', 'query_succeeded');
      await h.page.locator('#database-table button').filter({ hasText: 'Edit' }).click();
      assert.equal(await h.page.locator('#database-password').inputValue(), '');
      assert.equal(await h.page.locator('#database-query-editors [data-field="queryName"]').inputValue(), 'lookup');
      for (const [provider, port] of [['postgresql', '5432'], ['mysql', '3306'], ['mariadb', '3306'], ['sqlserver', '1433'], ['oracle', '2484']]) {
        await h.page.locator('#database-provider').selectOption(provider);
        assert.equal(await h.page.locator('#database-port').inputValue(), port);
        assert.equal(await h.page.locator('#database-oracle-dn-field').isVisible(), provider === 'oracle');
      }
      await h.page.locator('#database-oracle-dn').fill('CN=db.example.invalid');
      await h.page.locator('#database-query-editors [data-field="sql"]').fill('SELECT :id AS value FROM DUAL');
      await h.page.locator('#database-password').fill('synthetic-browser-password');
      const saving = h.holdNext('database-update');
      await h.page.locator('#database-save').click(); const write = await saving.seen;
      assert.equal(await h.page.locator('#database-password').inputValue(), '', 'Clear the password at dispatch');
      await saving.release(); await h.text('database-form-status', 'Database connector saved');
      assert.equal(write.body.definition.provider, 'oracle'); assert.equal(write.body.definition.port, 2484);
      assert.equal(write.body.definition.queries[0].parameters[0].type, 'int64');
      assert.equal(write.body.password, 'synthetic-browser-password');
      assert.equal(await h.page.locator('#database-password').inputValue(), '');
      assert.doesNotMatch(await h.page.locator('#database-table').textContent(), /synthetic-browser-password/);
    });
    let savedDatabase = databaseRecord(), failDatabaseSave = false;
    await scenario('database saves preserve newer field and structural edits', call => {
      if (call.kind === 'database-list') return { ...defaultReply(call), items: [savedDatabase] };
      if (call.kind === 'database-update') {
        if (failDatabaseSave) return { httpStatus: 503, httpBody: {} };
        savedDatabase = defaultReply(call); return savedDatabase;
      }
      return defaultReply(call);
    }, async h => {
      await openDatabaseDashboard(h);
      await h.page.locator('#database-table button').filter({ hasText: 'Edit' }).click();
      await h.page.locator('#database-password').fill('synthetic-first-draft-password');
      const first = h.holdNext('database-update');
      await h.page.locator('#database-save').click(); await first.seen;
      assert.equal(await h.page.locator('#database-password').inputValue(), '');
      await h.page.locator('#database-name').fill('Newer unsaved name');
      await h.page.locator('#database-password').fill('synthetic-newer-draft-password');
      await first.release(); await h.drained();
      assert.equal(await h.page.locator('#database-name').inputValue(), 'Newer unsaved name');
      assert.equal(await h.page.locator('#database-password').inputValue(), 'synthetic-newer-draft-password');
      await h.text('database-form-status', 'newer edits are still unsaved');
      const second = h.holdNext('database-update');
      await h.page.locator('#database-save').click(); const secondWrite = await second.seen;
      assert.equal(secondWrite.body.definition.revision, 2, 'Retained draft must use the acknowledged saved revision');
      await h.page.locator('#database-add-query').click();
      await second.release(); await h.drained();
      assert.equal(await h.page.locator('.database-query-editor').count(), 2, 'Keep a query added after dispatch');
      assert.equal(await h.page.locator('#database-name').inputValue(), 'Newer unsaved name');
      await h.page.locator('.database-query-editor').last().getByRole('button', { name: 'Remove query', exact: true }).click();
      failDatabaseSave = true;
      const obsolete = h.holdNext('database-update');
      await h.page.locator('#database-save').click(); await obsolete.seen;
      await h.page.locator('#database-reset').click();
      await h.page.locator('#database-table button').filter({ hasText: 'Edit' }).click();
      await h.page.locator('#database-name').fill('Replacement editor');
      failDatabaseSave = false;
      const replacement = h.holdNext('database-update');
      await h.page.locator('#database-save').click(); await replacement.seen;
      await obsolete.release();
      assert.equal(await h.page.locator('#database-save').isDisabled(), true, 'Old completion must not enable a newer save');
      assert.equal(await h.page.locator('#database-name').inputValue(), 'Replacement editor');
      assert.doesNotMatch(await h.page.locator('#database-form-status').textContent(), /Save failed/);
      await replacement.release(); await h.text('database-form-status', 'Database connector saved');
    });
    let databaseExecutionEnabled = true, failDatabaseQuery = false;
    const queryDatabase = databaseRecord();
    queryDatabase.definition.queries[0].parameters.push({ name: 'amount', type: 'decimal', required: false, maxLength: 100 });
    queryDatabase.definition.queries[0].sql += ' AND amount>=@amount';
    queryDatabase.definition.queries.push({ name: 'other_lookup', description: 'Another approved query',
      sql: 'SELECT id FROM example_records', parameters: [], maxRows: 20, timeoutSeconds: 5 });
    await scenario('database query generations preserve exact JSON and discard obsolete responses', call => {
      if (call.kind === 'database-list') return { ...defaultReply(call), items: [queryDatabase], enabled: databaseExecutionEnabled };
      if (call.kind === 'database-query') {
        if (failDatabaseQuery) return { httpStatus: 503, httpBody: {} };
        return { result: { columns: ['value'], rows: [{ value: call.body.query === 'other_lookup' ? 'other-current-result' : 'row-' + call.body.arguments.id }], truncated: false } };
      }
      return defaultReply(call);
    }, async h => {
      await openDatabaseDashboard(h);
      await h.page.locator('#database-table button').filter({ hasText: 'Run query' }).click();
      const precise = '{"id":9223372036854775807,"amount":0.1234567890123456789012345678}';
      await h.page.locator('#database-query-arguments').fill(precise);
      await h.page.locator('#database-run').click(); await h.text('database-query-results', 'rows returned');
      assert.equal(h.calls.find(call => call.kind === 'database-query').rawBody,
        '{"query":"lookup","arguments":' + precise + '}', 'Preserve integer and decimal tokens before JSON parsing');
      await h.page.locator('#database-query-arguments').fill('{"id":1}');
      const old = h.holdNext('database-query');
      await h.page.locator('#database-run').click(); await old.seen;
      await h.page.locator('#database-query-arguments').fill('{"id":2}');
      const newer = h.holdNext('database-query');
      await h.page.locator('#database-run').click(); await newer.seen;
      await old.release();
      assert.equal(await h.page.locator('#database-run').isDisabled(), true, 'Old completion must not enable the newer query');
      assert.doesNotMatch(await h.page.locator('#database-query-results').textContent(), /row-1/);
      await newer.release(); await h.text('database-query-results', 'row-2');
      failDatabaseQuery = true;
      const failed = h.holdNext('database-query');
      await h.page.locator('#database-run').click(); await failed.seen;
      await h.page.locator('#database-query-select').selectOption('other_lookup');
      failDatabaseQuery = false;
      await h.page.locator('#database-run').click(); await h.text('database-query-results', 'other-current-result');
      await failed.release(); await h.drained();
      assert.doesNotMatch(await h.page.locator('#database-query-results').textContent(), /Query failed/);
      assert.match(await h.page.locator('#database-query-results').textContent(), /other-current-result/);
      const disabled = h.holdNext('database-query');
      await h.page.locator('#database-run').click(); await disabled.seen;
      databaseExecutionEnabled = false;
      await h.page.locator('#database-refresh').click(); await h.text('database-status', 'disabled by the operator');
      assert.equal(await h.page.locator('#database-query-panel').isVisible(), false);
      assert.equal(await h.page.locator('#database-run').isDisabled(), true);
      await disabled.release(); await h.drained();
      assert.equal(await h.page.locator('#database-query-results').textContent(), '');
      assert.equal(await h.page.locator('#database-table button').filter({ hasText: 'Run query' }).isDisabled(), true);
    });
    await scenario('a late database probe does not overwrite a newer audit', defaultReply, async h => {
      await openDatabaseDashboard(h);
      const probe = h.holdNext('database-probe');
      const probeButton = h.page.locator('#database-table button').filter({ hasText: 'Test connection' });
      await probeButton.click(); await probe.seen;
      await h.page.locator('#database-table button').filter({ hasText: 'Audit' }).click();
      await h.text('database-results', 'query_succeeded');
      await probe.release(); await h.drained();
      assert.match(await h.page.locator('#database-results').textContent(), /Recent activity/);
      assert.doesNotMatch(await h.page.locator('#database-results').textContent(), /Connection verified/);
      assert.equal(await probeButton.isDisabled(), false);
    });
    const longDatabase = databaseRecord();
    longDatabase.definition.id = 'r' + 'x'.repeat(62);
    longDatabase.definition.host = 'db.' + 'h'.repeat(63) + '.' + 'n'.repeat(63) + '.example.invalid';
    longDatabase.definition.tlsServerName = longDatabase.definition.host;
    longDatabase.definition.queries[0].name = 'q' + 'x'.repeat(63);
    longDatabase.definition.queries[0].description = 'd'.repeat(512);
    longDatabase.definition.queries[0].parameters[0].name = 'p' + 'x'.repeat(63);
    longDatabase.definition.queries[0].sql = 'SELECT id FROM example_records WHERE id=@' + longDatabase.definition.queries[0].parameters[0].name;
    await scenario('long valid database identities and forms fit mobile and sidebar layouts', call => {
      if (call.kind === 'database-list') return { ...defaultReply(call), items: [longDatabase],
        allowedTargets: ['postgresql://' + longDatabase.definition.host + ':5432'] };
      return defaultReply(call);
    }, async h => {
      await openDatabaseDashboard(h, longDatabase.definition.id);
      await h.page.locator('#database-table button').filter({ hasText: 'Edit' }).click();
      await h.page.locator('#database-table button').filter({ hasText: 'Run query' }).click();
      for (const width of [375, 960]) {
        await h.page.setViewportSize({ width, height: 1000 });
        const layout = await h.page.evaluate(() => ({ viewport: document.documentElement.clientWidth,
          content: Math.max(document.documentElement.scrollWidth, document.body.scrollWidth),
          unlabelled: Array.from(document.querySelectorAll('#databases-view input, #databases-view select, #databases-view textarea'))
            .filter(control => !control.labels?.length).map(control => control.id) }));
        assert.ok(layout.content <= layout.viewport + 1, 'Database view overflow at ' + width + 'px');
        assert.deepEqual(layout.unlabelled, [], 'Every database form control needs a label');
      }
    });
    await scenario('credentials changing discard late remote and database responses', defaultReply, async h => {
      await h.page.locator('a[href="#settings"][data-route]').click();
      await h.page.locator('#token').fill(oldToken); await h.page.locator('#token').press('Tab');
      await h.page.locator('#nav-connectors').waitFor({ state: 'visible' }); await h.page.locator('#nav-connectors').click();
      await h.text('connector-table', 'team-database');
      const remote = h.holdNext('connector-probe');
      await h.page.locator('#connector-table button').filter({ hasText: 'Test' }).click(); await remote.seen;
      // Trigger the credential input event without leaving the current page:
      // navigation invalidation must not make this identity test pass by itself.
      await h.page.locator('#token').evaluate((input, token) => {
        input.value = token; input.dispatchEvent(new Event('input', { bubbles: true }));
      }, newToken);
      assert.equal(new URL(h.page.url()).hash, '#connectors');
      await remote.release(); await h.drained();
      assert.equal(await h.page.locator('#connector-test-results').textContent(), '');
      await h.page.locator('#token').evaluate((input, token) => {
        input.value = token; input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
      }, oldToken);
      await h.page.locator('#nav-databases').waitFor({ state: 'visible' });
      await h.page.locator('#nav-databases').click(); await h.text('database-table', 'reporting-db');
      await h.page.locator('#database-table button').filter({ hasText: 'Edit' }).click();
      await h.page.locator('#database-password').fill('synthetic-unsaved-credential');
      await h.page.locator('#database-table button').filter({ hasText: 'Run query' }).click();
      const database = h.holdNext('database-query');
      await h.page.locator('#database-run').click(); await database.seen;
      await h.page.locator('#token').evaluate((input, token) => {
        input.value = token; input.dispatchEvent(new Event('input', { bubbles: true }));
      }, newToken);
      assert.equal(new URL(h.page.url()).hash, '#databases');
      await database.release(); await h.drained();
      for (const id of ['connector-test-results', 'database-results', 'database-query-results', 'database-table'])
        assert.equal(await h.page.locator('#' + id).textContent(), '', id + ' retained a privileged response');
      assert.equal(await h.page.locator('#nav-databases').isVisible(), false);
      assert.equal(await h.page.locator('#database-password').inputValue(), '');
    });
    await scenario('token change discards an in-flight quality response', defaultReply, async h => {
      await h.page.locator('a[href="#settings"][data-route]').click();
      await h.page.locator('#token').fill(oldToken);
      await h.page.locator('#token').press('Tab');
      await h.page.locator('a[href="#catalog"][data-route]').click();
      await h.text('skills', 'old-skill');
      const held = h.holdNext('quality');
      await h.page.locator('#skills button').click();
      await held.seen;
      await h.page.locator('a[href="#settings"][data-route]').click();
      await h.page.locator('#token').fill(newToken);
      await h.page.locator('#token').press('Tab');
      await held.release(); await h.drained();
      await h.page.locator('a[href="#catalog"][data-route]').click();
      await h.text('skills', 'new-skill');
      assert.doesNotMatch(await h.page.locator('#skills').textContent(), /old-skill/);
      assert.equal(await h.page.locator('#skills .inline-detail-panel').count(), 0);
    });
    let savedAiConfiguration = null;
    const aiReply = call => {
      if (call.kind === 'ai-settings-save') {
        savedAiConfiguration = { ...call.body.configuration, revision: 1 };
        return { configuration: savedAiConfiguration, hasApiKey: !!call.body.apiKey };
      }
      const result = defaultReply(call);
      if (call.kind === 'ai-settings' && savedAiConfiguration) {
        result.configuration = savedAiConfiguration;
        result.hasApiKey = true;
      }
      return result;
    };
    await scenario('AI settings and assessment content remain inert', aiReply, async h => {
      await h.page.locator('a[href="#settings"][data-route]').click();
      await h.page.locator('#token').fill(oldToken);
      await h.page.locator('#token').press('Tab');
      await h.page.locator('#skill-ai-panel').waitFor({ state: 'visible' });
      await h.text('ai-jobs', 'old-skill');
      assert.equal(await h.page.locator('#ai-jobs img, #ai-jobs script').count(), 0);
      await h.page.locator('#ai-enabled').check();
      await h.page.locator('#ai-model').fill('example-model');
      await h.page.locator('#ai-key').fill('synthetic-do-not-store');
      await h.page.locator('#ai-input-rate').fill('1');
      await h.page.locator('#ai-output-rate').fill('2');
      await h.page.locator('#ai-transmission').check();
      await h.page.locator('#ai-reason').fill('Synthetic opt in');
      await h.page.locator('#ai-save').click();
      await h.page.waitForFunction(() => document.getElementById('ai-key').value === '');
      const put = h.calls.find(x => x.kind === 'ai-settings-save');
      assert.ok(put, 'AI configuration must reach the admin endpoint');
      assert.equal(put.body.configuration.enabled, true);
      assert.equal(put.body.reason, 'Synthetic opt in');
    });
    await scenario('desktop and mobile layout', call => {
      const result = defaultReply(call);
      const name = 'fixture-' + 'a'.repeat(56);
      if (['skills', 'security'].includes(call.kind)) result.items[0].name = name;
      if (call.kind === 'quality') result.findings[0].message = 'Missing reference: references/' + 'm'.repeat(180) + '.md';
      if (call.kind === 'mcp') result.items[0].remoteUrl += '/' + 'p'.repeat(180);
      return result;
    }, async h => {
      await h.text('skills', 'fixture-');
      for (const width of [1440, 375]) {
        await h.page.setViewportSize({ width, height: 1000 });
        const size = await h.page.evaluate(() => ({ viewport: document.documentElement.clientWidth,
          content: Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) }));
        assert.ok(size.content <= size.viewport + 1,
          `${width}px layout overflows horizontally: ${size.content}px content / ${size.viewport}px viewport`);
      }
    });
    process.stdout.write(`PASS MCP dashboard: ${passed} scenarios (mocked API; desktop and mobile).\n`);
  } catch (error) {
    process.stderr.write(`FAIL MCP dashboard (${active}): ${String(error.message).replace(/\s+/g, ' ')}\n`);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
    if (assets) await new Promise(resolve => assets.server.close(resolve));
  }
}

main();
