'use strict';
const $ = id => document.getElementById(id);
const state = { token: '', csrf: null };
function headers() { return state.token ? { Authorization: `Bearer ${state.token}` } : {}; }
function element(tag, text, css) { const node = document.createElement(tag); node.textContent = text; if (css) node.className = css; return node; }
async function request(path, options = {}) {
  const unsafe = ['POST', 'PUT', 'PATCH', 'DELETE'].includes((options.method || 'GET').toUpperCase());
  const csrf = unsafe && !state.token && state.csrf ? { 'X-Packory-CSRF': state.csrf } : {};
  const response = await fetch(path, { ...options, headers: { ...headers(), ...csrf, ...(options.headers || {}) } });
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
    $('upload-result').textContent = `Imported ${response.name}@${response.version}`;
    event.currentTarget.reset(); await load();
  } catch (error) { $('upload-result').textContent = `Import failed: ${error.message}`; }
  finally { button.disabled = false; }
});
load();

// V2 control-plane views use the same in-memory credentials as the legacy catalog.
function feedback(id, value) { $(id).textContent = value; }
function feedEndpoint(item) {
  if (item.protocol === 'raw') return `/r/raw/${item.slug}/{version}/{path}`;
  if (item.protocol === 'nuget') return `/r/nuget/${item.slug}/v3/index.json`;
  if (item.protocol === 'npm') return `/r/npm/${item.slug}/`;
  if (item.protocol === 'cargo') return `/r/cargo/${item.slug}/index/`;
  if (item.protocol === 'pypi') return `/r/pypi/${item.slug}/simple/`;
  return `/api/v1/repositories/${item.slug}`;
}
async function loadControlPlane() {
  feedback('v2-feedback', 'Loading V2 repositories…');
  const results = await Promise.allSettled([
    request('/api/v1/repositories'),
    request('/v0.1/servers'),
    request('/api/v1/jobs'),
    request('/api/v1/audit')
  ]);
  const repositories = $('repositories'); repositories.replaceChildren();
  const tokenFeed = $('new-token-feed');
  tokenFeed.replaceChildren();
  const global = document.createElement('option'); global.value = ''; global.textContent = 'Global (administrators only)'; tokenFeed.append(global);
  if (results[0].status === 'fulfilled') {
    for (const feed of results[0].value.items) {
      const tr = document.createElement('tr');
      const endpoint = document.createElement('td'); endpoint.className = 'repo-endpoint';
      endpoint.append(element('code', feedEndpoint(feed)));
      tr.append(element('td', feed.slug), element('td', feed.protocol), element('td', feed.mode), element('td', feed.visibility), endpoint);
      repositories.append(tr);
      const option = document.createElement('option'); option.value = feed.slug; option.textContent = feed.slug;
      tokenFeed.append(option);
    }
    feedback('v2-feedback', `${results[0].value.items.length} repositories configured.`);
  } else {
    feedback('v2-feedback', `Repository API: ${results[0].reason.message}. Provide a read or admin token.`);
  }
  const mcp = $('v2-mcp'); mcp.replaceChildren();
  if (results[1].status === 'fulfilled') {
    const servers = results[1].value.servers;
    if (!servers.length) mcp.append(element('p', 'No approved MCP servers.', 'muted'));
    for (const record of servers) {
      const server = record.server;
      const node = element('div', '', 'skill-row');
      node.append(element('span', `${server.name} · ${server.version}`)); mcp.append(node);
    }
  } else mcp.append(element('p', 'No access to MCP registry.', 'muted'));
  const jobs = $('v2-jobs'); jobs.replaceChildren();
  if (results[2].status === 'fulfilled') {
    if (!results[2].value.items.length) jobs.append(element('p', 'No maintenance jobs yet.', 'muted'));
    for (const job of results[2].value.items.slice(0, 12)) jobs.append(element('div', `${job.type} · ${job.state} · #${job.id}`, 'skill-row'));
  } else jobs.append(element('p', 'Admin privileges required.', 'muted'));
  const audit = $('v2-audit'); audit.replaceChildren();
  if (results[3].status === 'fulfilled') {
    for (const entry of results[3].value.items.slice(0, 20)) audit.append(element('div', `${entry.at} · ${entry.action} · ${entry.target}`, 'skill-row'));
  } else audit.append(element('p', 'Admin privileges required.', 'muted'));
}
$('v2-refresh').addEventListener('click', loadControlPlane);
$('token').addEventListener('input', () => { clearTimeout(debounce); debounce = setTimeout(loadControlPlane, 350); });
$('create-feed').addEventListener('submit', async event => {
  event.preventDefault();
  const f = event.currentTarget;
  const button = f.querySelector('button'); button.disabled = true;
  feedback('feed-result', 'Creating repository…');
  const raw = Object.fromEntries(new FormData(f).entries());
  const body = { slug: raw.slug, protocol: raw.protocol, mode: raw.mode,
    visibility: raw.visibility, upstream: raw.mode === 'proxy' ? raw.upstream : null };
  try {
    const created = await request('/api/v1/repositories', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    feedback('feed-result', `Created ${created.slug}.`); f.reset(); await loadControlPlane();
  } catch (e) { feedback('feed-result', `Could not create feed: ${e.message}`); }
  finally { button.disabled = false; }
});
$('create-token').addEventListener('submit', async event => {
  event.preventDefault();
  $('token-secret').replaceChildren();
  const f = event.currentTarget;
  const button = f.querySelector('button'); button.disabled = true;
  const values = Object.fromEntries(new FormData(f).entries());
  const days = Math.max(1, Math.min(365, Number(values.days)));
  try {
    const output = await request('/api/v1/tokens', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: values.name, feed: values.feed || null, scopes: [values.scope],
        expiresAt: new Date(Date.now() + days * 86400000).toISOString() }) });
    feedback('token-result', `Token ${output.id} created. Copy the secret now; it will not be displayed again.`);
    $('token-secret').append(element('code', output.secret));
  } catch (e) { feedback('token-result', `Token creation failed: ${e.message}`); }
  finally { button.disabled = false; }
});
$('reconcile').addEventListener('click', async () => {
  try {
    const queued = await request('/api/v1/maintenance/reconcile', { method: 'POST' });
    feedback('v2-feedback', `Maintenance queued: job #${queued.jobId}`);
    await loadControlPlane();
  } catch (e) { feedback('v2-feedback', `Maintenance: ${e.message}`); }
});
loadControlPlane();

async function initializeSession() {
  try {
    const auth = await request('/api/v1/auth-config');
    if (!auth.oidc) return;
    $('oidc-login').hidden = false;
    try {
      const session = await request('/api/v1/session');
      if (session.authenticated) {
        state.csrf = session.csrf;
        $('session-state').textContent = 'Signed in';
        await loadControlPlane();
      }
    } catch (_) { $('session-state').textContent = 'Not signed in'; }
  } catch (_) { /* older servers have no OIDC configuration endpoint */ }
}
initializeSession();
