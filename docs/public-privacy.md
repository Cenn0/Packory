# Public repository privacy gate

The Packory repository is **public**. No personal email addresses, personal identities, private contact details, operational secrets, private environment files, real endpoints or production data may be committed, posted in issues or included in CI artifacts.

The `Public source and Git identity privacy` CI job inspects **tracked text files and the checked-out HEAD author/committer addresses**. It rejects non-noreply/non-example email addresses, common recognizable credential formats, private key markers and tracked credential-bearing files. Findings disclose **only paths, line numbers and finding categories**, never matched values. The job is required for every new PR but branch rules must separately make it a **required check**.

## Developer procedure

- Configure git with an authorized GitHub-provided **noreply** identity; do not copy historical personal metadata.
- Load secrets via runtime secret managers/environment. Never commit `.env`, `.npmrc`, cloud credentials or private keys.
- Run `python3 -m unittest discover -s tests -p test_public_privacy.py -v` and `python3 scripts/check_public_privacy.py` before a public PR.
- Independently review docs, uploaded images/binaries, CI logs, history and PR/issue bodies. This lightweight scanner cannot prove privacy, find every token family or erase historical commits.
- If a real credential was published, revoke/rotate immediately; removing it from HEAD does **not** revoke it or erase Git history.

## Limitations

Only the current checkout and current Git author/committer are checked. Existing history, issues, PR comments, workflow logs, archives and binary files need independent review. Allowed `example.*` addresses are placeholders, not live contact destinations. This gate is additive to `scripts/check_public_docs.py` and is not a comprehensive SAST/secret detection certificate. For release assurance see [release gates](release-gates.md).
