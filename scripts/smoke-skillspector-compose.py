#!/usr/bin/env python3
"""Synthetic end-to-end smoke for the Compose scanner and MCP distribution.

Uses only ephemeral test credentials from the runtime environment. Never prints tokens
or a potentially sensitive real scan report. Intended for an isolated CI environment.
"""
import hashlib
import io
import json
import os
import time
import urllib.error
import urllib.request
import zipfile

BASE = "http://127.0.0.1:8080"
NAME = "compose-e2e-skill"
VERSION = "1.0.0"


def http(method, path, data=None, mime=None, *, mcp_method=None, mcp_name=None):
    headers = {"Authorization": "Bearer " + os.environ["PACKORY_ADMIN_TOKEN"]}
    if mime:
        headers["Content-Type"] = mime
    if mcp_method:
        headers.update({
            "Mcp-Protocol-Version": "2026-07-28",
            "Mcp-Method": mcp_method,
            "Accept": "application/json, text/event-stream",
        })
    if mcp_name:
        headers["Mcp-Name"] = mcp_name
    request = urllib.request.Request(BASE + path, method=method, data=data, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read()


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def wait_ready(deadline):
    while time.monotonic() < deadline:
        try:
            code, _ = http("GET", "/health/live")
            if code == 200:
                return
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            pass
        time.sleep(1)
    raise TimeoutError("Packory never became responsive")


def zip_data():
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        archive.writestr(
            NAME + "/SKILL.md",
            "---\nname: " + NAME +
            "\ndescription: Synthetic inert Compose smoke fixture\n---\n"
            "# Test skill\nThis document is an inert integration-test fixture.\n",
        )
    return buffer.getvalue()


def send_upload(archive):
    boundary = "packory-synthetic-boundary"
    form = (
        ("--" + boundary + "\r\nContent-Disposition: form-data; name=\"version\"\r\n\r\n" +
         VERSION + "\r\n" +
         "--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"skill.zip\"\r\n" +
         "Content-Type: application/zip\r\n\r\n").encode()
        + archive + ("\r\n--" + boundary + "--\r\n").encode()
    )
    status, body = http("POST", "/api/skills", form, "multipart/form-data; boundary=" + boundary)
    require(status == 202, "Skill was not staged with HTTP 202")
    payload = json.loads(body)
    require(payload["status"] == "QUEUED", "Scan was not queued")
    require(payload["sha256"] == hashlib.sha256(archive).hexdigest(), "Staged digest differs from upload")
    return payload["sha256"]


def wait_scan(deadline):
    while time.monotonic() < deadline:
        code, body = http("GET", "/api/security/skills/" + NAME + "/" + VERSION)
        require(code == 200, "Admin review endpoint did not return HTTP 200")
        details = json.loads(body)
        state = details["skill"]["state"]
        if state in ("NEEDS_REVIEW", "QUARANTINED", "SCAN_FAILED"):
            return state
        time.sleep(2)
    raise TimeoutError("Scanner never returned a terminal result")


def decision(action, digest, reason):
    data = json.dumps({"sha256": digest, "reason": reason}).encode()
    return http("POST", "/api/security/skills/" + NAME + "/" + VERSION + "/" + action,
                data, "application/json")


def jsonrpc_payload(response, request_id):
    """The Streamable HTTP transport can return JSON or an SSE event stream."""
    text = response.decode("utf-8")
    if text.lstrip().startswith("{"):
        data = json.loads(text)
        require(data.get("id") == request_id, "MCP response ID mismatch")
        return data
    for line in text.splitlines():
        if not line.startswith("data:"):
            continue
        data = line[5:].strip()
        if not data or data == "[DONE]":
            continue
        payload = json.loads(data)
        if isinstance(payload, dict) and payload.get("id") == request_id:
            return payload
    raise ValueError("MCP returned no JSON-RPC response event")


def check_mcp():
    request = {
        "jsonrpc": "2.0", "id": 3, "method": "skills/list",
        "params": {"_meta": {
            "io.modelcontextprotocol/protocolVersion": "2026-07-28",
            "io.modelcontextprotocol/clientInfo": {"name": "SyntheticCheck", "version": "1.0"},
            "io.modelcontextprotocol/clientCapabilities": {},
        }},
    }
    code, response = http("POST", "/mcp", json.dumps(request).encode(), "application/json", mcp_method="skills/list")
    require(code == 200, "MCP skills/list failed")
    payload = jsonrpc_payload(response, 3)
    skills = payload.get("result", {}).get("skills", [])
    entry = next((x for x in skills if x.get("frontmatter", {}).get("name") == NAME), None)
    require(entry is not None, "Approved Skill missing from MCP discovery")
    manifest = next((x for x in entry["resources"] if x["uri"] == entry["uri"]), None)
    require(manifest is not None and manifest["digest"].startswith("sha256:"),
            "Skill manifest missing SHA-256")

    # Verify the dedicated Skills Extension lookup independently of listing.
    get_request = {
        "jsonrpc": "2.0", "id": 5, "method": "skills/get",
        "params": {"uri": entry["uri"], "_meta": request["params"]["_meta"]},
    }
    code, get_bytes = http("POST", "/mcp", json.dumps(get_request).encode(),
                           "application/json", mcp_method="skills/get",
                           mcp_name=entry["uri"])
    require(code == 200, "MCP skills/get HTTP " + str(code))
    get = jsonrpc_payload(get_bytes, 5).get("result", {})
    skill = get.get("skill")
    require(skill is not None and skill["uri"] == entry["uri"],
            "skills/get did not return exact identity")
    require(skill["resources"] == entry["resources"] and
            skill["frontmatter"] == entry["frontmatter"],
            "skills/get metadata differs from listing")
    require(get.get("ttlMs") == 0 and get.get("cacheScope") == "private",
            "Skill metadata cache must be revocation-safe")

    read_request = {
        "jsonrpc": "2.0", "id": 4, "method": "resources/read",
        "params": {
            "uri": entry["uri"],
            "_meta": {
                "io.modelcontextprotocol/protocolVersion": "2026-07-28",
                "io.modelcontextprotocol/clientInfo": {"name": "SyntheticCheck", "version": "1.0"},
                "io.modelcontextprotocol/clientCapabilities": {},
            },
        },
    }
    code, result_bytes = http(
        "POST", "/mcp", json.dumps(read_request).encode(),
        "application/json", mcp_method="resources/read", mcp_name=entry["uri"]
    )
    require(code == 200, "MCP resources/read HTTP " + str(code) + ", response: " + result_bytes[:350].decode("utf-8", "replace"))
    result = jsonrpc_payload(result_bytes, 4)
    contents = result.get("result", {}).get("contents", [])
    item = next((x for x in contents if x.get("uri") == entry["uri"]), None)
    require(item is not None and isinstance(item.get("text"), str),
            "MCP manifest resource could not be read as text")
    read_digest = "sha256:" + hashlib.sha256(item["text"].encode("utf-8")).hexdigest()
    require(read_digest == manifest["digest"],
            "MCP resources/read bytes do not match Skill manifest digest")



def check_mcp_revocation():
    metadata = {
        "io.modelcontextprotocol/protocolVersion": "2026-07-28",
        "io.modelcontextprotocol/clientInfo": {"name": "SyntheticCheck", "version": "1.0"},
        "io.modelcontextprotocol/clientCapabilities": {},
    }
    def query(method, ident, params, uri=None):
        request = {"jsonrpc": "2.0", "id": ident, "method": method,
                   "params": {"_meta": metadata, **params}}
        code, data = http("POST", "/mcp", json.dumps(request).encode(),
                          "application/json", mcp_method=method, mcp_name=uri)
        require(code == 200, "Unexpected HTTP status after revocation: " + str(code))
        return jsonrpc_payload(data, ident)

    listed = query("skills/list", 6, {})
    require(all(item.get("frontmatter", {}).get("name") != NAME
                for item in listed.get("result", {}).get("skills", [])),
            "Revoked Skill remains in MCP listing")
    uri = "skill://packory/" + VERSION + "/" + NAME + "/SKILL.md"
    for method, ident in (("skills/get", 7), ("resources/read", 8)):
        answer = query(method, ident, {"uri": uri}, uri)
        require("error" in answer and "result" not in answer,
                "Revoked Skill metadata/bytes available via " + method)


def main():
    require(len(os.environ["PACKORY_ADMIN_TOKEN"]) >= 32, "Ephemeral admin token missing")
    wait_ready(time.monotonic() + 90)
    archive = zip_data()
    digest = send_upload(archive)

    # Queued content must be invisible in every public delivery path.
    code, _ = http("GET", "/api/skills/" + NAME + "/" + VERSION + "/download")
    require(code == 404, "Unapproved Skill archive became downloadable")

    status = wait_scan(time.monotonic() + 240)
    require(status == "NEEDS_REVIEW", "Inert fixture was not eligible for manual review: " + status)
    code, body = decision("approve", digest, "Synthetic offline integration review")
    require(code == 200, "Approved snapshot could not be published")
    code, retrieved = http("GET", "/api/skills/" + NAME + "/" + VERSION + "/download")
    require(code == 200 and hashlib.sha256(retrieved).hexdigest() == digest,
            "Approved skill bytes could not be downloaded unchanged")
    check_mcp()

    code, _ = decision("revoke", digest, "Synthetic revocation regression check")
    require(code == 200, "Revocation was not accepted")
    code, _ = http("GET", "/api/skills/" + NAME + "/" + VERSION + "/download")
    require(code == 404, "Revoked Skill remained downloadable")
    check_mcp_revocation()
    print("Compose end-to-end: staged, scanned, approved, MCP-list/get/read checked, revoked")


if __name__ == "__main__":
    main()
