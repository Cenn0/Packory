# Agent Skill ZIP import boundary

Packory does **not** run imported Skill archives. It performs structural
validation before making a version visible. This is not a malware or
prompt-injection scan and does not establish a publisher's identity.

On import, Packory now verifies the full archive up to the 20 MiB
uncompressed budget, not just the size claimed by ZIP central-directory
metadata. Every non-directory entry must decompress to its declared length;
oversized or truncated streams fail closed. Duplicate and case-conflicting
paths are rejected, together with traversal, unsafe Unix special-file
types/symlinks, path controls and ambiguous separators. A `SKILL.md` must
be UTF-8 and may not repeat the `name` or `description` frontmatter keys.

**Compatibility:** Previously accepted ZIPs with case-distinct filenames
or malformed/truncated bodies are now rejected on *new* uploads. Existing
published ZIPs are not retrospectively rewritten, rescanned, approved or
quarantined by this change.

**Limitations:** The structural check cannot detect malicious scripts,
insecure instructions, prompt injection, untrusted dependencies or claims
about signatures and provenance. Manual approval, isolation, repeatable
sandboxed scanners, policy gates and storage-integrity checks remain
separate Roadmap V3 M5.1/M5.6 obligations. No embedded content is executed.

Tests: `SkillArchiveSafetyTests` plus existing native Skills API tests.

Related: [roadmap](roadmap.md), [release gates](release-gates.md).
