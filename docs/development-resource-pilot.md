# Development resource pilot

Reviewed: **21 November 2026**. Status: **prepared for a draft integration review; operational qualification remains open**.

This pilot adds six generic development skills and their shared guidance for repository, software, API, registry, security and test reviews. Packory's [repository instructions](../AGENTS.md), [architecture](architecture.md), [current roadmap](roadmap-v5.md), [privacy policy](public-privacy.md) and [release gates](release-gates.md) remain authoritative. These resources grant no product, connector, deployment or publication permissions.

## Exact scope and provenance

| Record | Pinned value |
| --- | --- |
| Resource source revision | **adedfd9016a01368b3d56f65965e39a5699242ca** |
| Packory baseline before this pilot | **cf6992f2c52099325dbdd3530270ed662f266b1e** |
| Installed manifest | [.ais-install-manifest.json](../.ais-install-manifest.json) |
| Manifest SHA-256 | **b0360219764f188e6dcbc38ef8fed13c0f305c8d95ed3322eddead4db08d63f9** |
| Managed scope | Six skills plus one policy companion; 21 files |
| Native agent definitions | None added by this pilot |

The source revision identifies the reviewed resource export. Its owner retains the corresponding source/export authorization evidence separately; this public document does not expose an origin URL or other projects. Reviewers must establish the source and publication authorization through that separate review record. A source SHA, a file's self-supplied hash, or a matching local manifest does **not** authenticate a publisher.

The [offline checker](../scripts/check_development_resources.py) pins the exact reviewed manifest bytes as well as its source revision and expected resource layout. Rewriting both a payload and its manifest hash therefore fails while the reviewed checker remains unchanged. Changing the checker pin is itself a source change requiring review; it is not a trust mechanism independent of repository governance.

## Selected skills

| Installed skill | Intended task |
| --- | --- |
| [audit-repository-baseline](../.agents/skills/audit-repository-baseline/SKILL.md) | Establish the actual source, outstanding work and evidence before a bounded change. |
| [software-engineering](../.agents/skills/software-engineering/SKILL.md) | Develop a scoped change with architecture, implementation and verification considered together. |
| [qualify-api-contracts](../.agents/skills/qualify-api-contracts/SKILL.md) | Review API and message contracts, compatibility, identity and failure behavior. |
| [qualify-package-registry](../.agents/skills/qualify-package-registry/SKILL.md) | Review native registry/client behavior, integrity, authorization and recovery evidence. |
| [review-security](../.agents/skills/review-security/SKILL.md) | Review concrete code and architecture security boundaries. |
| [design-effective-tests](../.agents/skills/design-effective-tests/SKILL.md) | Design independent outcomes, positive controls and useful negative cases. |

The shared [security guidance](../.agents/policy/security-boundaries.md) and [evidence rules](../.agents/policy/evidence.md) accompany the skills. Optional specialist references are recommendations, not a claim that every referenced skill is installed. Task selection and the host's actual discovery behavior must be checked independently.

This integration does not register a Packory plugin, upload a Skill through the product, enable MCP or database connectors, or invoke a device, registry or network endpoint. No credential is needed for its offline checks. It does not alter application APIs, storage, deployment configuration or repository instructions.

## Offline readback and drift checks

From the checkout root:

    PYTHONDONTWRITEBYTECODE=1 python3 scripts/check_development_resources.py
    PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tests -p test_development_resources.py -v

An explicitly selected local checkout can be checked with:

    PYTHONDONTWRITEBYTECODE=1 python3 scripts/check_development_resources.py --root /path/to/checkout

Exit **0** means the fixed manifest and managed file tree match this reviewed pilot. Exit **1** means the checker refused the input, observed drift or could not perform its safe reads. It emits one JSON result with fixed diagnostic categories and no file contents. It never repairs drift, accepts a new pin, installs resources or executes a skill.

The checker validates exact manifest types, unique JSON keys, source revision, resource names/kinds and the complete allowlisted file list. It compares SHA-256 for every installed file and rejects missing or extra entries throughout this pilot's managed resource tree. Additional resources need a separately reviewed scope update; they are not silently admitted.

Reads stay relative to retained directory descriptors. Symlink components, symlinked files, hardlinks, non-regular files and unsafe directory replacements are refused. Limits are **64 KiB** for the manifest, **256 KiB** per resource file, **2 MiB** of managed payload in total and **128** enumerated entries. Inputs do not become arbitrary paths or subprocess arguments. The implementation uses Python's standard library and performs no network or subprocess operation.

The locally exercised reference environment is Linux with Python 3.12.14 and the required directory-relative, no-follow and nonblocking read primitives. Missing primitives cause a failure; there is no weaker fallback. Other host/OS combinations require their own check. Run in a controlled checkout without concurrent writers: this is a bounded file-consistency check, not an atomic multi-file snapshot or a sandbox against a process with the same operating-system privileges.

## Observed evidence and remaining gates

| Check | Observation at preparation | Interpretation |
| --- | --- | --- |
| Initial installation | Completed with the pinned source and target baseline | Files were placed within the selected resource scope. |
| Installer readback and repeated plan | Matching readback and no-change repeat plan observed | Local installation/idempotence evidence only. |
| Offline checker | VERIFIED: 7 resources, 21 files, 49,568 payload bytes | Exact local bytes match the reviewed manifest pin. |
| Targeted checker regressions | 21 tests passed locally, including the checked-out pilot | Synthetic negative controls plus local file readback. |
| Actual skill discovery/load | NOT_RUN | A file's presence does not demonstrate host discovery. |
| Paired agent task outcomes | NOT_RUN | No model behavior or quality improvement is claimed. |
| Host/connector action boundaries | NOT_ESTABLISHED | Operational host qualification remains an open gate. |
| Exact pilot PR checks and independent review | Pending the draft PR's actual head | Local checks do not provide a remote CI result or approval. |
| Product/provider qualification | Outside this pilot | No DB, MCP, registry interoperability or deployment acceptance follows. |

The [negative tests](../tests/test_development_resources.py) cover changed/missing/extra files, coordinated payload/manifest rewriting, wrong provenance and types, duplicate JSON keys, malformed/deep JSON, traversal attempts, byte/entry limits, symlinks, hardlinks, FIFOs, replacement during a read and redacted diagnostics. Synthetic fixtures do not establish isolation of a live database or connector.

Keep operational pilot acceptance open until the actual receiving host demonstrates discovery, representative task outcomes and the required positive/negative permission controls with independent observations. Preserve the draft review state while the applicable gates remain unqualified. In line with [Roadmap V5](roadmap-v5.md), green file checks do not grant a supported-product or production-recommendation claim.

Before the public PR, run the existing documentation/privacy checks and inspect all added files:

    python3 scripts/check_public_docs.py
    python3 scripts/check_public_privacy.py

The privacy script checks tracked files and Git identity. Newly prepared untracked files also need explicit inspection before staging; a green tracked-file scan alone does not cover them. Do not put source credentials, personal identities, private project references or raw operational logs into this public evidence record or CI artifacts.

## Update and rollback

Updates must identify a new exact source revision, review the selected resource diff, preserve publication authorization and refresh the manifest, fixed checker pin and documentation together. Re-run readback, idempotence, negative controls and the applicable host/task gates. Do not replace a failed check with an automatic acceptance or overwrite command.

Rollback is a reviewed revert of the **actual pilot integration commit** once that commit exists. Confirm its exact SHA and inspect subsequent changes before using:

    git revert <reviewed-pilot-commit>

Resolve conflicts by preserving unrelated product changes and local edits. Do not delete the entire resource directory as a shortcut. The revert should remove or restore only the files introduced by the reviewed pilot, including its manifest, checker, tests and this record; compare the result against the known pre-pilot revision. If later resources have been installed, review their ownership before reverting shared policy files.

No pilot commit or rollback execution is invented here. The draft PR must supply the actual integration revision and its checks before any qualified merge or subsequent rollback is claimed.
