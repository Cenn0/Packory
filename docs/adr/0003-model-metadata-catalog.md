# ADR 0003: Reviewed model metadata without binary hosting

Status: selected for the first model-catalog slice; implementation and acceptance are bounded to metadata. No model-runtime or large-artifact support is claimed.

## Decision

Packory may catalog versioned model **descriptors** containing a name, format, declared SHA-256 and byte size, license identifier, HTTPS source reference, and optional provenance reference. A publisher stages an immutable descriptor; an administrator reviews the exact digest and explicitly publishes or revokes it. Only published metadata is readable through the catalog. The browser renders every field as text and never loads a model URL automatically.

The server never fetches, redirects to, hosts, executes, transforms, scans or validates the remote model bytes. A declared SHA-256 is a claim bound to the descriptor, not proof that the external file matches. The API and UI must say `bytesVerified=false` even after metadata approval. Publisher identity is not inferred from a claimed name or URL. Review states, authorization, audit reasons, immutable identity and bounded requests follow the existing staged MCP Registry pattern.

References must be HTTPS, contain no userinfo, query or fragment, and use a public DNS hostname rather than an IP literal, localhost or a private DNS suffix. This validation prevents obvious credential-bearing and internal URLs from being advertised but does not authenticate the remote operator. No server-side network request is made.

## Consequences and non-goals

- Metadata-only requests have a small fixed maximum; no multipart model upload, resume, storage quota, model serving or inference endpoint is introduced.
- An admin-reviewed descriptor is not a license, safety, provenance or model-output certification. Consumers independently authenticate the producer and verify downloaded bytes against the declared digest.
- Model references do not become global package artifacts, OCI manifests or Packory BlobStore objects. A future hosted model/binary profile requires its own capacity, signing, client contract, malware/safety policy and independent acceptance.
- The first implementation supports SQLite and PostgreSQL with identical state transitions and rejects unknown fields, duplicate JSON keys, noncanonical identifiers and unsafe URLs.
