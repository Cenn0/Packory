# ADR 0002: Opt-in validation evidence boundary (Issue #64)

Status: Proposed — contract only, not enabled in production.

## Decision

Packory stays the only agent-facing MCP endpoint. External validation is independently executed, independently bound to immutable source/build revision and signed by a separately operated verifier. Packory may eventually expose *sanitized, operator-approved* results through two read-only tools, `list_validation_runs` and `get_validation_findings`, but shall never execute validation workloads, proxy arbitrary MCP traffic or accept agent-supplied paths/hosts/commands.

Phase A uses an offline, manually reviewed export. A fixed local import directory controlled by the operator contains only approved aliases mapped to content-addressed export documents. Import checks MUST verify the exact bytes, externally authenticated verifier signature, evidence freshness, revocation, expected audience and tenant, independently authenticated run-to-source binding, and schema version. A digest alone does NOT authenticate the producer or validate the underlying evidence. Signature verification MUST use pinned operator-controlled trust anchors and reject revoked signers. Unknown fields, malformed JSON, duplicate JSON keys, excessive size and unsupported versions fail closed.

Dedicated `packory.validation.read` permission is mandatory. Registry anonymous access, `packory.read`, generic read tokens and write tokens alone MUST NOT grant validation visibility. Authorization must be applied at the MCP tool dispatcher boundary, not only inside a service method. If the current MCP host cannot enforce per-tool authorization before execution, do not register either tool. Operator opt-in and independent security qualification precede registration.

## Threat model

| Threat | Required boundary |
| --- | --- |
| Agent-driven code execution / SSRF | Offline imports only; no arbitrary URL, path or command input |
| Poisoned result payload / prompt injection | Fixed schema and enumerated codes; findings are untrusted data, never agent instructions |
| Forged validation success | Independent cryptographic verifier authentication, run-binding and freshness, fail closed |
| Cross-tenant disclosure | Authenticated tenant and audience checks before list/get; opaque allowlisted aliases |
| Leaking private evidence | Explicit field allowlist, no original prompts, logs, hostnames, source paths or holdouts |
| Tampering/replay/rollback | Immutable digest-bound exports, signed revision, expiry and revocation checks |
| Availability exhaustion | Small bounded files and responses, pagination cap, per-tool throttling and timeouts |
| Audit log exfiltration | Only alias, actor class, decision code and timestamp; never payload or raw credentials |

A successful verification indicates **authenticity and binding of the sanitized export**, not that Packory ran tests or independently proved result correctness.

## Contract and operations

`docs/contracts/validation-export-v1.schema.json` defines an intentionally narrow payload envelope. It is **not** a sufficient security verifier. Signature material, trusted keys, revocation and freshness policies are operator-managed and out-of-band. All fields are synthetic and generic; no real test evidence is stored in this repository.

Operational implementation checklist:
1. Implement dedicated per-tool scope enforcement and negative tests with registry-read credentials.
2. Implement strict offline import: content digest, canonicalization policy, signature verification, issuer and key revocation, independently verified run-binding, expiry and allowed audience/tenant.
3. Return only bounded, operator-approved aliases with no other private identifiers; audit every permitted/denied request.
4. Test missing, tampered, stale, revoked, wrong-audience, unbound, oversized and prompt-injected exports; test SDK authorization end-to-end.
5. Document retention, operator disable switch, trust-anchor rotation and recovery procedure.
6. Requalify independently before enabling. Phase B CI triggering is explicitly excluded and requires a separate review.

## Rejected alternatives

* A general remote MCP proxy: expands trust and execution surfaces.
* Agent-selected network provider or filesystem path: permits SSRF/local file disclosure.
* Reusing catalog-read credentials: violates least privilege.
* Trusting a self-reported `verified: true` field or bare digest: does not authenticate independent evidence.
* CI validation requests in phase A: crosses execution and authorization boundaries.
