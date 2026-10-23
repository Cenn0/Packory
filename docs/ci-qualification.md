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
disappear. Configure **`Complete qualification` as an administrator-required
status check** and require independent approvals on current commits. A YAML
job alone does not establish branch-policy enforcement. Keep deletion and
force-push prohibitions. Repository rules and bypass actors still require
administrator review and a controlled negative/positive merge test (V5-01).

The automatically published development containers now require the full CI
gate. Publication rechecks the eight independent inventories before building,
in addition to checking that the CI source is still current main. Manual CI
dispatch inspects existing main-push qualification; it cannot manufacture
independent provider runs for an arbitrary untested branch.

## Protected main: administrative acceptance checkpoint (issue #76)

**Observed 23 October 2026:** The two readable active rulesets on
`refs/heads/main` are [main-1](https://github.com/Cenn0/Packory/rules/25258491)
and [main-2](https://github.com/Cenn0/Packory/rules/25258492).
The first protects against deletion/non-fast-forward updates; the second
configures pull-request approvals with explicit bypass capabilities.
**Neither readable ruleset currently declares `required_status_checks`.**
The legacy branch-protection REST read returned HTTP 403 to the installed
integration, so other effective protection cannot be excluded. Successful
CI runs or a rule named "approval" **do not** prove status-check enforcement.

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

Until all five are witnessed, issue #76 remains **open and not verified**.
This limitation applies even when the current integration reports green
build, native-client, privacy and storage checks. The integration cannot
change the repository's administrative rules through its available actions.

## Evidence index

The main integration job additionally uploads a seven-day
`client-environment-<SHA>-<attempt>` inventory. It records an allowlisted
OS/distribution/architecture and parsed versions for the known CLI commands.
Missing, failed, timed-out or unrecognized commands are explicitly unavailable.
No raw stdout/stderr, hostnames, paths, account data or credential values are
exported. It is labelled **versions-only**, not an operation/provider pass.
Tools provisioned inside a harness's temporary directory may not be on PATH;
their pinned versions and operation outcomes still require the harness record.

Each successful job uploads a seven-day `ci-qualification-<SHA>-<attempt>`
artifact with schema `packory.ci-qualification/1`. It contains only source
SHA, event, fixed workflow filenames, run/attempt/job IDs and canonical public
workflow URLs. It deliberately excludes API response bodies, account details,
runner names/labels, endpoints, configuration, logs and credentials.

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

This index proves a bounded observation of Actions results. It is not signed
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
