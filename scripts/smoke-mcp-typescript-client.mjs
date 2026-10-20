// Real independent @modelcontextprotocol/client@2.3.1 against Packory over HTTP.
// Never log the test token, HTTP headers, server logs or user-supplied data.
import assert from 'node:assert/strict';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';

const url = new URL(process.env.PACKORY_TEST_MCP_URL);
const token = process.env.PACKORY_TEST_TOKEN;
assert.equal(url.hostname, '127.0.0.1', 'Test transport must remain on loopback');
assert.match(token || '', /^[0-9a-f]{64}$/, 'Missing ephemeral test credential');

for (const headers of [{}, { Authorization: 'Bearer incorrect-test-token' }]) {
  const denied = await fetch(url, { method: 'GET', headers });
  assert.equal(denied.status, 401, 'MCP transport must reject absent/invalid tokens');
}

async function exercise(label, versionNegotiation, expectedEra) {
  const client = new Client(
    { name: 'packory-typescript-interop-ci', version: '1.0.0' },
    { versionNegotiation }
  );
  const transport = new StreamableHTTPClientTransport(url, {
    requestInit: { headers: { Authorization: 'Bearer ' + token } }
  });
  try {
    await client.connect(transport);
    assert.equal(client.getProtocolEra(), expectedEra, label + ': wrong negotiated era');
    assert.ok(client.getServerVersion()?.name, label + ': server metadata missing');
    const { tools } = await client.listTools();
    const toolNames = new Set(tools.map(t => t.name));
    for (const name of ['search_artifacts', 'artifact_versions', 'list_approved_skills']) {
      assert.ok(toolNames.has(name), label + ': missing ' + name + ' tool');
    }
    for (const tool of tools) {
      assert.equal(tool.inputSchema?.type, 'object', label + ': malformed input schema');
    }
    const search = await client.callTool({
      name: 'search_artifacts', arguments: { query: 'nonexistent-smoke-artifact', limit: 2 }
    });
    assert.notEqual(search.isError, true, label + ': read-only search tool failed');
    assert.ok(Array.isArray(search.content), label + ': invalid tool response');
    const { prompts } = await client.listPrompts();
    assert.ok(prompts.some(p => p.name === 'discover_packages'), label + ': prompt absent');
    const prompt = await client.getPrompt({
      name: 'discover_packages', arguments: { query: 'packory-smoke' }
    });
    assert.ok(prompt.messages?.length > 0, label + ': prompt returned no messages');
    const { contents } = await client.readResource({ uri: 'packory://catalog/info' });
    assert.equal(contents.length, 1, label + ': catalog returned wrong resource count');
    assert.equal(JSON.parse(contents[0].text).name, 'Packory', label + ': catalog schema');
    const templates = await client.listResourceTemplates();
    assert.ok(Array.isArray(templates.resourceTemplates), label + ': templates schema');
    console.log(label + ': tools/resources/prompts, discovery and read-only calls passed');
  } finally {
    await client.close();
  }
}

await exercise('MCP 2025 legacy', { mode: 'legacy' }, 'legacy');
await exercise('MCP 2026 current', { mode: { pin: '2026-07-28' } }, 'modern');
