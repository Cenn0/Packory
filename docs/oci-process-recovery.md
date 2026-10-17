# Native OCI process-crash acceptance profile

Run after `dotnet build Packory.slnx -c Release` on Linux/POSIX:

```sh
python3 -m unittest discover -s tests -p test_oci_recovery_harness.py -v
python3 scripts/smoke_oci_recovery.py
```

The helper tests verify safe configuration and same-origin locations. Only the
second command launches the real compiled application and provides process-crash
evidence. The existing CI executes both commands, with failures blocking the job.
No external provider, production credentials, registry proxy or public package
is used. Test identity is randomly generated, and only a disposable loopback
SQLite/filesystem single-writer instance is started or killed.

## Required observations

The test publishes an initial three-byte chunk and verifies its acknowledged
Range. It sends part of a larger real HTTP request, keeps the TCP connection open,
and waits until uncommitted bytes are visible in staging while the persisted
CommittedLength remains three. Without this observation the test fails; it does
not claim a crash test based on killing an idle server or manually editing a file.

The exact child process receives SIGKILL. After restart on the same data directory,
the upload must return the previous committed offset, discard the unacknowledged
tail, accept the next chunk, reject its replay with 416 and successfully publish
and download the exact expected bytes. A second incomplete TCP upload is then
aborted without restarting the server; its bytes must roll back to the committed
offset. Cancellation must remove both the session metadata and staging file.

The final JSON summary contains only the acceptance profile and tested results.
Tokens, private directories, server logs and environment values are not printed
or uploaded. Cleanup targets only the child and directory created by this run.

## Scope boundaries

This adds **real process termination and TCP disconnect** evidence to the
[in-process fault tests and upload contract](oci-upload-recovery.md). It does
not prove PostgreSQL/S3 commit-boundary recovery, host/device power-loss durability,
S3-provider conformance, multi-node transfer, split-brain protection or native
Docker/ORAS/Helm version-matrix coverage. The separate Docker smoke remains
required. This test does not close MS1/MS3/MS6 as a whole.
