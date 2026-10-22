# Exact-commit CI qualification

The `Complete qualification` CI job requires all five main CI jobs and the
following independent workflows for the **same source commit and event**:

| Workflow file | Mandatory jobs |
| --- | --- |
| `keycloak.yml` | Signed Keycloak JWT |
| `mcp-typescript.yml` | Independent TypeScript SDK HTTP conformance |
| `native-matrix.yml` | Both Cargo/pip Python 3.12 and 3.13 profiles |
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

## Evidence index

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
