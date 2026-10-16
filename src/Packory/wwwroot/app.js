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
    $('upload-result').textContent = `Imported ${response.name}@${response.version}`;
    event.currentTarget.reset(); await load();
  } catch (error) { $('upload-result').textContent = `Import failed: ${error.message}`; }
  finally { button.disabled = false; }
});
load();
