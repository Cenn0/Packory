# Static Skill quality assessment

Packory assesses the documentation of a published, immutable Skill and returns **concrete findings, suggestions and coverage**. This complements the security scanner and integrity checks. It does not run the Skill, change its approval, contact external links or invoke a language model.

## Use it

- MCP tool: `assess_skill_quality(name, version)`.
- REST: `GET /api/v1/skills/{name}/{version}/quality`.
- Dashboard: choose **Assess quality** next to a published Skill.

The MCP endpoint always requires a global read credential. REST follows explicit `PACKORY_ANONYMOUS_READ`; when anonymous reading is disabled, namespace/feed-scoped managed tokens cannot access global Skill reports. The endpoint returns `Cache-Control: no-store`. Unknown, revoked and tampered revisions all return 404 through REST and `status: unavailable` through the MCP tool. Authorization failures happen before the expensive-read rate limiter. REST has a separate fixed-window budget of 60 requests/minute per credential role, without a queue; it cannot exhaust the MCP transport's separate budget. These are per-process controls.

Example request, using a runtime token:

```bash
curl --fail-with-body \
  -H "Authorization: Bearer $PACKORY_READ_TOKEN" \
  http://localhost:8080/api/v1/skills/example-skill/1.0.0/quality
```

## Report interpretation

The profile identifier is **`packory.skill-quality/1`**. A report carries the exact archive and manifest SHA-256, artifact name/version, whether approval enforcement is enabled, coverage, independent dimensions and bounded findings. Each finding includes a controlled code/message/suggestion, its basis and a location expressed as file SHA-256 plus line. It does not echo file excerpts, external URLs or submitted metadata values into the findings.

`assessmentStatus: completed` means the selected static checks ran. **It does not mean the Skill passed every check.** Read `dimensions` and `findings`: a completed report can contain `issues_found`. `assessmentStatus: incomplete` means an inspection budget, invalid text or parser limit prevented complete coverage of the profile. No aggregate quality or safety score is fabricated.

| Dimension | Checked behavior | Meaning of a finding |
| --- | --- | --- |
| Frontmatter | Required name/description; provided optional fields have the specified type/size | Format requirements are distinguished from recommendations |
| Instructions | Valid UTF-8, a nonempty instruction body, optional manifest length guidance | Empty content is a useful documentation defect; exceeding 500 lines is guidance rather than a format rejection |
| Local references | Recognized inline/reference Markdown links target existing archive files/directories with portable paths | Broken references produce a specific repair suggestion; no remote URL is fetched |
| License metadata | Presence/type of optional licensing information | Missing optional metadata is a suggestion; legal license rights are not verified |
| Coverage | Attempted/inspected Markdown files/bytes, link counts and skipped work | Limits and omissions remain visible even when some findings were already generated |

Reference inspection understands bounded inline and reference-link forms, encoded filenames and balanced parentheses. Code fences, indented examples, inline code and HTML comments are excluded. It is a documented static profile, not a complete CommonMark implementation. Anchor existence, bare file references and live remote availability are not checked.

## Resource bounds and release state

The existing MCP Skill snapshot is bounded to 512 files and 16 MiB uncompressed bytes; Packory's upload admission profile may be stricter. Quality assessment additionally caps Markdown at **256 KiB per file**, **1 MiB total attempted text decoding**, **2,048 recognized links** and **100 returned findings**. Total dimension counts remain available when `findingsTruncated` is true. Cancellation is observed before/through/after assessment. Approval and the immutable archive digest are checked again before the report is returned.

The profile explicitly does not assess behavioral correctness, non-Markdown file contents, script/agent/connector execution, malware safety, publisher identity or legal license rights. Script presence is not evidence that the script was tested. See the [MCP roadmap](mcp-roadmap.md) for isolated functional testing, agent/connector contracts and reviewable improvement proposals.

## Verification and sources

`SkillQualityAssessmentTests` covers deterministic findings, optional fields, malformed/oversized text, reference cases, findings/link budgets, cancellation, in-flight revocation, tampered storage and native MCP/REST parity. It also tests real managed credential scopes, authorization before throttling and separation of anonymous, authenticated and MCP request budgets.

Run `node scripts/test-mcp-dashboard.cjs` with Playwright 1.62.1 and its Chromium browser installed. `PACKORY_CHROMIUM_EXECUTABLE` may select a separately installed Chromium binary. The script uses the actual JS/CSS and CSP with synthetic API responses, verifying safe text rendering, pagination, delayed-response invalidation and desktop/mobile layout. It is a UI regression suite; backend authorization is tested in .NET.

The distinction between required frontmatter fields, optional metadata and writing recommendations follows the [Agent Skills specification](https://agentskills.io/specification). Exact-byte resource/digest binding follows the [MCP Skills Extension](https://skills.extensions.modelcontextprotocol.io/specification/stable/skills). Passing this profile is not an upstream certification.


## MCP Skills inventory pagination

The `skills/list` extension and `list_approved_skills` MCP tool now use **provider-side approved-release keyset pages** instead of materializing every Skill and checking each revision in a separate query. The tool accepts a bounded `limit` (1–99) and an opaque `cursor`; default page size is 50. Continuations are bound to that tool and cannot be reused for other methods. The extension scans at most 200 candidate revisions per request, returns up to 25 actual verified resource entries and may produce an empty page with a continuation if a run of corrupted/absent archives exhausts its work budget. Every page rechecks current approval and ZIP integrity; a cursor is not an authorization grant. An exactly full final page is not given a false continuation cursor.

The selected-provider test `McpSkillKeysetProviderTests` loads 800 synthetic Skills with 630 non-approved entries before the first eligible one, then revokes an earlier entry and the cursor marker. It verifies that the database checks approval before applying the page limit, continuation is stable, and the PostgreSQL collation order matches SQLite. This is a **bounded-query correctness test**, not a throughput benchmark. Capacity under large real ZIPs, concurrent clients, and selected provider I/O remains to be measured for MCP-P7.
