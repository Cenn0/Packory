#!/usr/bin/env python3
"""Real Keycloak + Packory MCP Streamable HTTP authorization smoke, synthetic only.

Uses only Python stdlib; tokens are injected via ephemeral environment variables
and are never printed, stored in repository files or sent to arbitrary hosts.
"""
import json
import os
import urllib.error
import urllib.request

BASE = os.environ["PACKORY_TEST_MCP_BASE"]
REGULAR = os.environ["PACKORY_TEST_MCP_REGULAR_TOKEN"]
VALIDATION = os.environ["PACKORY_TEST_MCP_VALIDATION_TOKEN"]
assert BASE in ("http://127.0.0.1:18083", "http://localhost:18083")


def decode(content):
    text = content.decode("utf-8")
    if text.lstrip().startswith("{"):
        return json.loads(text)
    for line in text.splitlines():
        if line.startswith("data:"):
            item = json.loads(line[5:].strip())
            if item.get("id") is not None:
                return item
    raise AssertionError("MCP did not return a JSON-RPC response")


class Session:
    def __init__(self, token):
        self.token = token
        self.session_id = None
        result = self.post("initialize", {
            "protocolVersion": "2026-07-28",
            "capabilities": {},
            "clientInfo": {"name": "packory-validation-security-smoke", "version": "1.0.0"},
        }, 1)
        assert result["result"]["serverInfo"]["name"]
        self.post("notifications/initialized", None, None)

    def post(self, method, params, id_number):
        payload = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            payload["params"] = params
        if id_number is not None:
            payload["id"] = id_number
        headers = {
            "Authorization": "Bearer " + self.token,
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "Mcp-Protocol-Version": "2026-07-28",
        }
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
        request = urllib.request.Request(
            BASE + "/mcp", json.dumps(payload).encode("utf-8"),
            headers=headers, method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                if response.headers.get("Mcp-Session-Id"):
                    self.session_id = response.headers["Mcp-Session-Id"]
                body = response.read(128 * 1024 + 1)
        except urllib.error.HTTPError as exc:
            # Never include HTTP header dump or request details (contains token).
            return {"http_status": exc.code}
        if len(body) > 128 * 1024:
            raise AssertionError("MCP response exceeds bounded contract")
        if id_number is None:
            return {}
        return decode(body)


def call(session, tool, arguments, call_id):
    return session.post("tools/call", {"name": tool, "arguments": arguments}, call_id)


reader = Session(REGULAR)
ordinary_tools = reader.post("tools/list", {}, 2)
assert "result" in ordinary_tools, ordinary_tools
assert any(tool["name"] == "search_artifacts"
           for tool in ordinary_tools["result"]["tools"])
denied = call(reader, "get_validation_findings", {"alias": "synthetic-run"}, 3)
assert denied.get("error") or denied.get("result", {}).get("isError") \
    or denied.get("http_status") in (401, 403), "Catalog token accessed validation results"
assert "SYNTHETIC_PASS" not in json.dumps(denied)

validated = Session(VALIDATION)
listing = call(validated, "list_validation_runs", {}, 4)
assert "result" in listing and not listing["result"].get("isError"), listing
assert "synthetic-run" in json.dumps(listing), "Validated run not listed"
findings = call(validated, "get_validation_findings", {"alias": "synthetic-run"}, 5)
assert "result" in findings and not findings["result"].get("isError"), findings
body = json.dumps(findings)
assert "SYNTHETIC_PASS" in body and "rsa_pss_sha256_verified" in body, "Missing signed findings"
unknown = call(validated, "get_validation_findings", {"alias": "unknown"}, 6)
assert "unavailable" in json.dumps(unknown)
print("Real Keycloak MCP scope isolation, signed synthetic export and missing-alias tests passed.")
