# Packory UI rework — design deliverables

This directory contains the initial product-specific UI rework proposal and light/dark design-token reference. Both are **design references**, not a running frontend replacement.

## Committed material

- [Product design specification](../ui-rework-2026-10-25.md): routes, catalog, feed administration, review flow, honest states, mobile/accessibility and implementation acceptance scenarios.
- [Design tokens](assets/design-tokens.css): proposed standalone light/dark palette, typography and geometry variables. These are **not yet imported by the Packory app**.

## Additional deliverables prepared but not in this Git branch

The design exercise also produced the files listed below; binary assets have **not yet been transferred to GitHub**. Do not represent these as tracked files or completed runtime integration.

- `Packory-Designkonzept-2026-10-24.pdf` — 24-page visual report.
- `packory-prototype.html` — standalone interaction prototype with synthetic data; no production backend.
- `Packory-Designpaket-2026-10-24.zip` — original handoff package with prototype, report, screen previews, icon sprite, original design-token JSON/CSS and validation reports.
- `previews/*.png` — catalog, package detail, feed, review, dark mode and mobile preview screenshots.

The prepared handoff files are also stored in the project's ChatGPT Library folder `/Packory/`; the complete ZIP can be obtained from the conversation's earlier delivery. GitHub's current connected text-file operations do not upload these local binary files.

## Integrating the full assets

Extract the handoff ZIP and copy its `packory-design/` contents into this folder in a local checkout of `feature/ui-rework`. Before committing, review the manifest, verify the output file hashes, check that no personal data or secrets are included, and make sure the original Packory logo is preserved. Commit actual prototype and images as design/demo resources, not deployed runtime files. Do **not** automatically publish the private AiSet skill sources or brand references.

For a maintainable repository, consider keeping the 24-page PDF and representative previews while avoiding redundant tracked copies of the entire ZIP. If full archival delivery is required, use a separate release asset or Git LFS subject to verified support and repository policy.

## Implementation boundary

No production HTML, CSS or JS has been replaced. `main` remains untouched. All demo labels and measurements are synthetic; completed browser exercises on the prototype are not Packory runtime or CI tests.
