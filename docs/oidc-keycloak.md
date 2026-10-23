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

## Remote MCP OAuth discovery

For a public HTTPS origin and configured OIDC issuer/audience, Packory now serves `GET /.well-known/oauth-protected-resource/mcp` under RFC 9728. It announces `/mcp` as the protected resource, the configured Keycloak issuer and the minimum `packory.read` scope. A missing or invalid Bearer credential receives a `WWW-Authenticate` 401 response with the metadata document URL.

Static-token and HTTP development profiles deliberately **do not** publish OAuth metadata. The public discovery document contains no secret, token or personal data. It does not establish that the identity provider supports OAuth 2.1, dynamic client registration, browser PKCE or automatic token renewal; these require separate provider-side configuration and live interoperability testing. See [MCP integration](mcp-security-integration.md).

## Native clients and browser dashboard

An API client may use `Authorization: Bearer <signed-JWT>`. Existing long
random admin/read/write tokens are still accepted and are not exchanged for JWTs.
Auth decisions and audit rows contain only a role label such as `oidc-write`,
not `sub`, email, access token or other personal claims.

**Not yet implemented:** interactive authorization-code/PKCE browser login, BFF
sessions, local user management, token renewal UI, per-package owner grants and
full Keycloak integration tests.

**Native API/JWT scheme isolation:** Packory accepts Packory scope and feed claims
only from a principal recorded by the `JwtBearer` validation callback for an
explicit `Authorization: Bearer` request. An authenticated browser cookie or
an arbitrary authenticated `HttpContext.User` does not confer read, write,
administrative, per-feed, namespace or validation rights, even when it carries
similarly named claims. This preserves an important **precondition** for a
future browser BFF; it does not add browser sessions, CSRF defenses or
interactive login. Unit tests use deliberately synthetic principals; actual
JWT signature/issuer/audience handling is separately tested by the signed
Keycloak workflow. The current JWT layer is an API resource-server
integration, **not a complete Keycloak SSO dashboard**. Native Cargo also
supports its protocol's bare Authorization token when it is one of Packory's
static credentials.

## Real Keycloak integration evidence

The dedicated [Keycloak OIDC CI](../.github/workflows/keycloak.yml) starts a
disposable Keycloak **26.7.3** with a generated local TLS certificate and a
throwaway service-account client. It obtains a real signed access token over
HTTPS, validates the configured audience and `packory.write` scope in the
fixture, then runs Packory JWT signature/issuer/audience validation through
HTTPS discovery and JWKS. CI checks that no credential is denied (401), a
properly signed bearer reads successfully, and write scope cannot reach the
admin-only audit route. No actual user accounts or personal details are used.

This proves a specific Keycloak machine-to-machine resource-server workflow.
It does **not** test interactive browser login, PKCE, refresh tokens, group
lifecycle, all Keycloak upgrades or multi-node session handling. Use a BFF with
HttpOnly/SameSite cookies and CSRF controls for future interactive dashboard
SSO; do not put long-lived administrative bearer tokens in localStorage.

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
