# Exact-commit CI qualification

The `Complete qualification` CI job requires all five main CI jobs and the
following independent workflows for the **same source commit and event**:

| Workflow file | Mandatory jobs |
| --- | --- |
| `keycloak.yml` | Signed Keycloak JWT |
| `mcp-typescript.yml` | Independent TypeScript SDK HTTP conformance |
| `native-matrix.yml` | Both Cargo/pip Python 3.12 and 3.13 profiles plus npm/pnpm/Yarn Classic lockfiles |
| `postgres.yml` | PostgreSQL integration |
| `public-docs.yml` | Documentation checks, without path-filter skips |
| `s3.yml` | S3/Moto integration and coordinated restore tests |
| `skillspector-smoke.yml` | Offline scanner, on PRs and main pushes |
| `source-evidence.yml` | Exact source archive and privacy checks |

`scripts/qualification_gate.py` reads GitHub Actions with `actions: read`.
It selects the newest matching run of each fixed workflow path, requires a
successful completed run and every expected job, checks exact commit/event/
repository identity, paginates attempt-specific jobs, rejects unsuccessful
or skipped jobs and re-reads each run to detect an intervening rerun.
Missing or running workflows can be waited for, within a 30-minute bound;
failed/canceled/neutral/skipped results block immediately. An earlier green
attempt cannot substitute for a later red attempt. API failures fail closed.

The job runs even if a main CI dependency fails, so its check cannot silently
disappear. **`Complete qualification` is now an administrator-required status
check on `main`**, bound to GitHub Actions, with an up-to-date branch requirement
and no bypass actors in its ruleset. A YAML job alone would not establish this:
the administrative readback and negative merge proof are recorded below.
Independent current-code approval remains a separate review obligation.

The automatically published development containers now require the full CI
gate. Publication rechecks the eight independent inventories before building,
in addition to checking that the CI source is still current main. Manual CI
dispatch inspects existing main-push qualification; it cannot manufacture
independent provider runs for an arbitrary untested branch.

## Protected main: administrative acceptance checkpoint (issue #76)

**Observed 23 October 2026, 19:47 UTC:** The active rulesets on
`refs/heads/main` are [main-1](https://github.com/Cenn0/Packory/rules/25258491)
and [main-2](https://github.com/Cenn0/Packory/rules/25258492).
The first protects against deletion/non-fast-forward updates; the second
configures pull-request approvals with explicit bypass capabilities.
The additional [required-ci-qualification ruleset](https://github.com/Cenn0/Packory/rules/25751077)
requires `Complete qualification` from GitHub Actions (integration ID 15368),
with `strict_required_status_checks_policy=true`, enforcement on branch
creation, and an empty bypass list. It applies only to `refs/heads/main`.
The existing deletion/non-fast-forward and review rules were preserved.
Review-rule bypass actors cannot bypass this separate required-CI ruleset.

The installed GitHub integration still cannot read the legacy protection
endpoint. A separately authorized administrator CLI read returned HTTP 404
(`Branch not protected`): at this checkpoint enforcement is provided by the
three repository rulesets, rather than an additional legacy protection rule.
No token, personal identity or private administration log is retained here.

[Synthetic PR #148](https://github.com/Cenn0/Packory/pull/148), head
`657477b39f17c4c85e58751cd045d08435b0fcc7`, deliberately failed the
[required check](https://github.com/Cenn0/Packory/actions/runs/40014015482/job/120621313385).
The ordinary merge API through the same integration used for approved PRs
returned **HTTP 405**, explicitly identifying
`Required status check "Complete qualification" is failing.` Readback showed
`merged=false` and `mergeable_state=blocked`; main remained
`1d92c373f6c911623e1bbadaac509fdff00f50c8`. The synthetic PR was closed without
merging. Its deliberately reduced workflow never reached main.

The positive proof is a separately reviewed, current-main PR with its full
exact-head qualification green and a successful ordinary merge under this
unchanged ruleset. Retain that PR/check/merge readback in [issue #76](https://github.com/Cenn0/Packory/issues/76).
This administrative control does not prove dependency safety, independent
human approval, actual-provider recovery or production readiness.

An authorized repository administrator must, in a controlled test:

1. Inspect **all effective** repository/organization rulesets and legacy
   protection and audit the intended automation and emergency bypass access.
   Do not publish identities, permission tokens or private administration logs.
2. Require the stable **`Complete qualification`** job context on `main`.
   It must run on **every PR**, with all eight independent workflow jobs
   represented by [the exact source gate](../scripts/qualification_gate.py).
   Avoid requiring a conditional/renamed job that can remain pending forever.
   Consider independent required contexts only where their PR triggering
   behavior is guaranteed; merely marking a workflow as required is not
   evidence that it executes.
3. Use an isolated, non-sensitive synthetic pull request with an intentionally
   failed check and verify merge is blocked for the intended automation
   identity **without an unrestricted bypass**. Restore the fixture branch
   without changing production data.
4. Repeat with the same test change and a green check, then verify the
   merge eligibility policy. Independently test the deletion/non-fast-forward
   prohibitions and record only redacted results, timestamp, rule IDs,
   source SHA and non-sensitive check names.
5. Review whether approvals are actually independent and current, especially
   after new commits. Store approval/testing evidence with [release gates](release-gates.md).

The required-CI negative proof is now witnessed. Issue #76 remains open until
the positive proof and final rule readback are attached. The review ruleset
retains its existing bypass capabilities and does not dismiss stale approvals;
operators must still obtain independent approval of the current change for
release acceptance. Neither a green CI index nor this control is that approval.

## Evidence index

The main integration job additionally uploads a seven-day
`client-environment-<SHA>-<attempt>` inventory. It records an allowlisted
OS/distribution/architecture and parsed versions for the known CLI commands.
Missing, failed, timed-out or unrecognized commands are explicitly unavailable.
The container job separately records Docker daemon, Compose and OpenSSL versions
in `container-client-environment-<SHA>-<attempt>` after its native HTTPS,
offline backup, isolated restore and same-schema rollback smoke. Its scope
is described in [single-node qualification](single-node-qualification.md).
No raw stdout/stderr, hostnames, paths, account data or credential values are
exported. It is labelled **versions-only**, not an operation/provider pass.
Tools provisioned inside a harness's temporary directory may not be on PATH;
their pinned versions and operation outcomes still require the harness record.

Each successful job uploads a seven-day `ci-qualification-<SHA>-<attempt>`
artifact with schema `packory.ci-qualification/2`. It contains only source
SHA, event, fixed workflow filenames, run/attempt/job IDs, a fixed allowlist
of required step names/numbers reported successful by GitHub Actions, and
canonical public workflow URLs. A green job with a missing, skipped, failed,
duplicate or still-running named step now fails qualification.
It deliberately excludes API response bodies, account details, runner
names/labels, endpoints, configuration, logs and credentials.

To independently recheck a commit in an authorized environment:

```bash
python3 scripts/qualification_gate.py --sha <EXACT_COMMIT_SHA> --event push \
  --output <NEW_LOCAL_INDEX_FILE>
```

The read-only API credential is supplied through `GH_TOKEN`; keep it outside
tracked files. Output must be a new file, preventing reuse of a stale index.
For a pull request use `--event pull_request` and its exact head SHA. API
semantics are documented in GitHub's official [workflow runs](https://docs.github.com/en/rest/actions/workflow-runs)
and [attempt jobs](https://docs.github.com/en/rest/actions/workflow-jobs) references.

This index proves a bounded observation of Actions results and named step
status. A PR can change a workflow step's command while retaining its name;
the index cannot verify that the intended test actually ran. Workflow files,
the gate and invoked scripts therefore require independent source review before
their results are used for a release decision. This index is not signed
provenance, a client-version inventory, independent approval, an immutable
history or operational evidence. A later rerun can change status. A successful
job can contain intentionally unexercised operations; consult its actual
coverage, skipped provider tests and pinned versions. Source archives have a
separate [source-only integrity verifier](ci-supply-chain.md). Production
qualification still requires [release gates](release-gates.md), authentic
real-provider restores, signed review and measured operational evidence.

## Integration and recovery scope

The integration branch combines #118 and #119, resolves all three textual
merge conflicts and changes the old Cargo-absence regression to require the
new scoped V14 behavior. Architecture and both README schema descriptions
now match V14. npm tag and Cargo yank overlays remain mutable, audited state
beside immutable package bytes.

Change-scoped review also found that the existing privacy guard omitted
`.cjs` and `.mjs` sources even though those formats contain the dashboard and
native MCP test harnesses. Both extensions are now inspected; a regression
fixture proves contact/credential detection without echoing their values.
This closes that scanner coverage gap, not all possible secret patterns.

The coordinated PostgreSQL/Moto fixture now also restores V14, including two
private Cargo states and npm live tags plus deletion tombstones, comparing
exact restored metadata fingerprints and reference inventories. This is
emulator/tooling acceptance; it does not establish a real S3 provider's
retention semantics or native client operation against restored production
infrastructure.

Public Maven tooling bootstrap retries only recognized transport failures and
HTTP 429/502/503/504, including Maven 3.10's `HTTP Status: 429` spelling. At most
three attempts use 15/30-second backoff and `-U` so cached failed transfers
cannot suppress the next network attempt. Missing artifacts (404), compiler and
validation failures do not qualify for retry. The hosted/private protocol
assertions and checksum gates are unchanged; persistent upstream throttling
still fails qualification rather than producing a pass.
