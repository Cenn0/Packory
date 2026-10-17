# Identifier validation boundaries

NuGet IDs, shared version strings, npm names, Skill names and configured S3
bucket/region/key-prefix fields use full-string validation. Leading, embedded or
trailing control characters are not trimmed into another identifier. In particular,
an otherwise valid value ending with a newline is rejected rather than matched
only up to the newline. Null and empty common package identifiers return false.
Existing accepted ASCII grammar, case rules and length limits are preserved.

This closes the final-newline gap in the previous dollar-anchored expressions;
it does not expand native protocol coverage or make arbitrary names safe for
unrelated SQL, shell, URI or filesystem contexts. Authentication, namespace
reservations and adapter-specific validation remain separate checks.

`IdentifierBoundaryTests` exercises controls, null/empty inputs, original boundary
lengths, S3 settings and private namespace reservations. Existing native client
smokes remain required before merge. No database or artifact migration is performed.
A configuration previously containing a trailing line feed must be corrected;
silently normalizing it would hide a malformed value.

Primary semantics: [Microsoft .NET regular-expression anchors](https://learn.microsoft.com/en-us/dotnet/standard/base-types/anchors-in-regular-expressions).
