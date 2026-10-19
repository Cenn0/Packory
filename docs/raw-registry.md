# Raw / Generic artifact API — V4 experimental

Packory's first V4 adapter is a **global hosted, immutable, versioned binary artifact API**.
It is a generic content transport, **not** native Maven, Go, Helm, Terraform or other package-manager support.

**Status: experimental; not production qualified.** SQLite and PostgreSQL metadata are versioned at schema V5, with filesystem or opt-in S3 artifact bytes. Operator-specific real S3 backup/restore and all V3 release gates are still open.

## Coordinates and endpoints

A raw artifact is identified by a canonical lowercase \`name\` and a validated \`version\`.
Name: 1–128 ASCII characters, starting and ending in an alphanumeric character, with internal \`a-z 0-9 . _ -\` and no adjacent dots. Version uses the existing bounded Packory version validator without adjacent dots.
Raw coordinates are **global**. They have no per-feed or tenant isolation, and are only appropriate for deployments whose global read/auth policy matches all raw assets.

| Method | Path | Auth | Semantics |
| --- | --- | --- | --- |
| PUT | \`/raw/{name}/{version}\` | Write/admin; configured \`raw:\` namespace | Immutable byte stream; a duplicate version returns 409 |
| GET | \`/raw/{name}/{version}\` | Global read policy | Integrity-verified bytes, ETag, Range and attachment disposition |
| HEAD | \`/raw/{name}/{version}\` | Global read policy | Metadata headers after integrity validation |
| GET | \`/raw/{name}/{version}/metadata\` | Global read policy | SHA-256, size, MIME, publication timestamp |

The MIME type must be a bare valid \`type/subtype\` without parameters. Untrusted files are downloaded as attachments with \`nosniff\`; do not place an executable interpreter behind this endpoint. Upload size is bounded by \`MaxArtifactBytes\`. Do not place API tokens in tracked shell scripts.

Example (synthetic credentials only):

\`\`\`bash
# TOKEN is injected by the operator at runtime, never tracked in a repository.
curl --fail-with-body --request PUT \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/octet-stream' \
  --data-binary @artifact.bin https://packory.example.invalid/raw/demo.component/1.0.0

curl --fail-with-body -H "Authorization: Bearer $READ_TOKEN" \
  -o artifact.bin https://packory.example.invalid/raw/demo.component/1.0.0
\`\`\`

## Persistence and recovery

Published metadata records the SHA-256 checksum calculated against the persisted bytes, size and media type. A durable publication intent precedes the blob upload and is cleared after catalog/digest/audit metadata commit. The startup reconciliation and offline integrity inspector include raw references. No deletion/overwrite endpoint exists.

**Known limits:** The global catalog lists raw coordinates according to normal catalog visibility, not isolated per-feed permissions. An S3 local cache may not detect external remote mutation on normal GET; the maintenance integrity job reads remote S3 directly. Distributed writer fencing, coordinated real-provider recovery, feed-scoped quotas, Raw-specific UI and MCP authorization/discovery are not qualified. Retain backups of both metadata and blobs.

## Native-client distinction

Raw is deliberately **not** identified as \`maven\`, \`composer\`, \`go\`, \`terraform\` or \`helm\`. Use OCI for Helm charts. Dedicated adapters require native client and security acceptance before a support claim.

See [V3 roadmap](roadmap.md), [support matrix](support-matrix.md) and [recovery instructions](pg-s3-offline-recovery.md).
