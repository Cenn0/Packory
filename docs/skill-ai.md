# Optional SkillSpector AI assessment (experimental)

The standard NVIDIA SkillSpector worker remains offline and mandatory for the
normal enforce/review workflow. AI-assisted analysis is **disabled by default**,
does not execute submitted Skills, and never approves or distributes a Skill.
An authorized reviewer is still responsible for the final decision. See
[security integration](mcp-security-integration.md),
[Skill security workstream](skill-security-roadmap.md), and
[the support matrix](support-matrix.md).

## Architecture and data flows

- The existing networkless `skillspector` sidecar performs a deterministic
  offline static scan. Its report and blocking policy are unchanged.
- With the optional Compose overlay, a second **networkless**
  `skillspector-ai` sidecar consumes bounded digest-verified staged ZIP copies.
  Its local HTTP listener is loopback-only **inside that container**, not exposed
  to the host/Packory network; there are no provider keys, hosted tool calls or
  arbitrary server-side skill scripts in the scanner container.
- The trusted ASP.NET Core writer polls immutable request/response exchange
  files, validates the model request, applies an operator-owned allowlist and
  connects to the selected provider. Provider credentials never enter the
  browser, scanner or exchange volume. Redirects/proxies are disabled and
  DNS answers are vetted before connecting to the selected numeric address.
- The AI assessment persists separately from static findings, attached to
  artifact SHA-256, config revision, scanner pin, and analysis profile. The
  existing human approval transition remains authoritative. If the optional
  `requireForApproval` policy is set, approval additionally requires a
  completed nonblocking AI result for the exact revision and artifact digest.
- Configuration changes cancel pending/running jobs. Unfinished, invalid,
  timed-out, budget-limited, or unavailable AI analyses never create clearance.

Scanner pin for the initial experimental bridge: NVIDIA SkillSpector
**2.13.0**, commit
`091a509b3d6ba8ff5be528966e1f43c2e68cf214`.
Update only after checking its CLI, environment variables, report fields,
transitive dependencies and license/SBOM inventory.

## Enable on a self-hosted instance

1. Provision two **operator-owned files outside source control**:
   - An allowed endpoint inventory, e.g. `/etc/packory/skill-ai-endpoints.json`.
   - An encryption keyring, e.g. `/etc/packory/skill-ai-keyring.json`.
     Create a random 32-byte key using an appropriate secrets facility;
     the schema is `{"active":"k1","keys":{"k1":"<base64 of 32 random bytes>"}}`.
     Never publish a real key, include it in a shell transcript or check it in.
     Restrict the file permissions to the deployment operator.
2. The endpoint inventory is a JSON array. Example **without secrets**:

   ```json
   [
     {"id":"openai","provider":"openai","baseUrl":"https://api.openai.com/v1"},
     {"id":"anthropic","provider":"anthropic","baseUrl":"https://api.anthropic.com"},
     {"id":"local","provider":"ollama","baseUrl":"http://ollama:11434/v1","allowedPrivateCidrs":["172.16.0.0/12"]}
   ]
   ```

   This Ollama subnet is an **example for a controlled Docker-only network**,
   not a recommended general permission; the Ollama profile rejects public
   IP destinations even when its hostname resolves externally; restrict the CIDR to the specific
   container or address range used by the deployment. `localhost` inside the
   API container is not the host machine. Do not expose a private local
   model/API endpoint beyond the networks you control. Allowlists are
   deployment-managed, **not edited through the browser**.
3. Set `PACKORY_SKILL_AI_ENDPOINTS_PATH` and
   `PACKORY_SKILL_AI_KEYRING_PATH` to the two existing host files.
   Launch:

   ```sh
   docker compose -f compose.yaml -f compose.skill-ai.yaml up -d --build
   ```

4. In the dashboard, enter an administrator credential in the existing
   **memory-only** field and open **Security Center → SkillSpector AI settings**.
   Choose a provisioned endpoint and supported model, configure usage limits,
   and explicitly enable AI. External providers additionally require an API
   credential, nonzero operator-specified input/output token prices and a
   positive monthly reservation budget. Tick consent for external Skill
   content transmission. Save with an audit reason.
5. Optionally run **Test provider connection**, then queue a specific
   static-scanned Skill assessment from its review page. Refresh the
   assessment jobs to inspect the result. Configured automatic triggers are
   `manual`, `suspicious` (nonzero static score or quarantine), and
   `always` (completed static scans awaiting review). Only `manual`
   or `always` may be paired with an enforced AI preapproval gate.

**Disable or rollback:** turn off the UI switch and save an audit reason.
Existing queued/running jobs are cancelled. The offline scanner and the
manual approval model continue. To remove the feature entirely, stop the
overlay stack and redeploy the base `compose.yaml` configuration after
confirming expected persistent data volumes.

## Supported providers and limitations

The API supports the pinned SkillSpector's OpenAI and Anthropic-compatible
text-only chat requests through an allowlisted provider relay. An
`ollama` profile uses an operator-configured **OpenAI-compatible API
endpoint**. An `openai-compatible` profile supports similarly compatible
HTTPS services with an API key. Interoperability depends on the actual
model/provider and must be smoke-tested separately. **Azure OpenAI and
provider-specific non-chat APIs are not qualified in this profile.**
Do not assume that a listed provider means a specific model or billing
format is officially supported.

The relay permits text prompts and constrained function-shaped response
schemas, **not** hosted tool execution, multimodal content, arbitrary
HTTP paths, streamed responses or browser-supplied endpoints. The
transport has bounded response size, timeout, concurrency and requests
per scan. Browser configuration also provides an operator-estimated
monthly reservation budget. Reservations occur before egress and
are not refunded on ambiguous failed requests; provider token accounting
may be absent, and this **is not a guarantee of actual invoice limits**.

## API and operational diagnostics

The administrative endpoints are under `/api/admin/skill-ai`:

| Method | Path | Purpose |
| --- | --- | --- |
| GET / PUT | `/settings` | Config, non-secret endpoint inventory, revision checks and audit |
| POST | `/probe` | Queue provider connectivity test |
| POST / GET | `/assessments` | Queue or list assessments |
| GET | `/jobs/{id}` | Status, risk and bounded AI report |
| POST | `/jobs/{id}/cancel` | Cancel a job with reason |
| GET | `/audit` | Recent policy/job decisions |

JSON mutating requests require existing Packory admin authentication and
explicit reasons. The browser never receives a saved API key. A
credential in an individual browser tab is still a privileged bearer
credential: this dashboard does **not** implement full OIDC browser
login/BFF/CSRF protection. Serve privileged administration only in a
trusted HTTPS operator environment until those release gates are met.

AI status `COMPLETED` means a structurally complete reviewed LLM
assessment, **not** an installation/execution safety certificate.
`BLOCKED` represents AI HIGH/CRITICAL/DO_NOT_INSTALL findings.
`FAILED`, `TIMED_OUT`, `CANCELLED` and
`BUDGET_EXCEEDED` do not satisfy an enforced AI gate.
Never combine the LLM assessment score with the static risk score into
a single quantitative safety claim.

## Verification

Run these at the exact candidate commit:

```sh
dotnet restore Packory.slnx
dotnet build Packory.slnx -c Release --no-restore -warnaserror
dotnet test Packory.slnx -c Release --no-build
python3 -m py_compile workers/skillspector/ai_worker.py
python3 -m unittest discover -s tests -p test_skill_ai_worker.py -v
node --check src/Packory/wwwroot/app.js
PACKORY_SKILL_AI_ENDPOINTS_PATH=/tmp/packory-ai-endpoints-placeholder.json \
PACKORY_SKILL_AI_KEYRING_PATH=/tmp/packory-ai-keyring-placeholder.json \
docker compose -f compose.yaml -f compose.skill-ai.yaml config --quiet
```

The browser regression suite exercises the real assets against synthetic API
responses and deliberately hostile model text. Python worker tests use local
networkless loopback fixtures. .NET tests cover configuration changes,
authorization-sensitive policy gating, DNS address policy, secret AEAD
binding and report validation. The existing static SkillSpector Compose smoke
tests the unchanged offline reviewer path. **External model inference,
end-to-end Azure compatibility, real provider charges, real S3-backed AI
staging and multi-writer operation are not certified by these tests.**

Packory is a public project: never commit personal email addresses, real
operator secrets, proprietary Skill contents or production AI transcripts.
