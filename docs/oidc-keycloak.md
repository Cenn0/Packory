# OIDC / Keycloak API access (optional)

Packory can act as an **OAuth 2.0 JWT-protected resource server** while retaining
the existing environment-provided static credentials for native package clients.
Keycloak is not mandatory. No OIDC parameters or user identifiers are included in
the public repository by default.

## Configure

Provide both runtime variables (values below are non-functional placeholders):

```bash
PACKORY_OIDC_AUTHORITY=https://identity.example.test/realms/packory
PACKORY_OIDC_AUDIENCE=packory-api
```

Packory rejects unencrypted HTTP discovery, credentials in issuer URLs and missing
issuer/audience configuration. ASP.NET Core JWT Bearer middleware validates the
token signature against the issuer's published signing keys, exact issuer and
audience, expiry and lifetime. Discovery and JWKS are loaded via HTTPS.

In Keycloak, configure a confidential or public client for your chosen access-token
flow and an **audience mapper** that includes `packory-api` in the token audience.
Configure scopes in the **access token** (not only the ID token) to grant precisely
the desired permissions:

| Access-token scope | Permissions |
| --- | --- |
| `packory.read` | Read packages/catalog when anonymous reads are disabled |
| `packory.write` | Read and publish/upload |
| `packory.admin` | Read, publish and administrative audit access |

Authentication without one of these explicit scopes grants **no** Packory access.
Scopes may appear as space-separated `scope` or `scp` claims. Keycloak's
`realm_access.roles` and `resource_access` roles are **not implicitly trusted**;
an operator must explicitly map the appropriate scopes in Keycloak.

## Native clients and browser dashboard

An API client may use `Authorization: Bearer <signed-JWT>`. Existing long
random admin/read/write tokens are still accepted and are not exchanged for JWTs.
Auth decisions and audit rows contain only a role label such as `oidc-write`,
not `sub`, email, access token or other personal claims.

**Not yet implemented:** interactive authorization-code/PKCE browser login, BFF
sessions, local user management, token renewal UI, per-package owner grants and
full Keycloak integration tests. The current JWT layer is an API resource-server
integration, **not a complete Keycloak SSO dashboard**. Native Cargo also
supports its protocol's bare Authorization token when it is one of Packory's
static credentials.

## Deployment and security

- TLS is required for Keycloak discovery and all external token transport.
- Match Keycloak public issuer URLs exactly; do not disable token signature,
  issuer, audience, expiry or key validation.
- Restrict which Keycloak clients receive `packory.write` and
  `packory.admin`; static tokens remain fully privileged until rotated.
- Do not expose raw tokens in logs, CI variables, fixtures or screenshots.
- Test against an actual Keycloak realm and client credentials before considering
  the integration qualified for your deployment.

Primary references:
- https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication
- https://www.keycloak.org/docs/latest/server_admin/
