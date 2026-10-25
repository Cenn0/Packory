# OIDC / Keycloak: API tokens and optional browser SSO

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

**Browser SSO is opt-in:** Packory now has an Authorization Code + PKCE
confidential web-client flow with a server-side (per-process) ticket store,
HttpOnly session cookie and antiforgery validation. Native protocols and MCP
continue using their existing static or signed JWT credentials. This is a
bounded **single-instance browser SSO implementation**, not production
qualification or full Keycloak interoperability certification. Local user
management, token renewal UI, per-package ownership, continuous group
reconciliation and full browser E2E/provider/logout compatibility remain open.

**Scheme isolation:** a signed JWT only authorizes an explicit
`Authorization: Bearer` request through the JwtBearer validation callback.
A browser cookie **never** authorizes native registry routes, MCP, or the
validation subsystem. Dashboard `/api/*` calls may use the validated browser
ticket only with `X-Packory-Browser: 1` and no native Authorization header.
Mutating browser API calls additionally require a CSRF token. No credential
is read from `HttpContext.User` alone. Synthetic boundary tests cover this
separation; actual signed-JWT checks remain in Keycloak CI. Browser E2E tests
are not yet included in that workflow. Native Cargo also
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

## Interactive dashboard SSO (opt-in)

Provision a **separate confidential OIDC web client**, not a public SPA client.
Configure these settings server-side (no actual credentials in the repository):

```bash
PACKORY_OIDC_AUTHORITY=https://identity.example.test/realms/packory
PACKORY_OIDC_AUDIENCE=packory-api
PACKORY_OIDC_CLIENT_ID=packory-dashboard
PACKORY_OIDC_CLIENT_SECRET_FILE=/run/secrets/packory_oidc_client_secret
PACKORY_TRUSTED_PROXY_IP=192.0.2.10
PACKORY_PUBLIC_BASE_URL=https://packages.example.test
```

For an identity provider that emits Packory role claims through a custom scope
(such as an authentik OAuth2/OIDC scope mapping), also set
`PACKORY_OIDC_WEB_SCOPE=packory` and assign that scope mapping to the provider.
The scope must be requested by the browser client and configured to include its
claims in the signed ID token. Keycloak deployments that map `packory_role`
directly into the ID token can leave this setting unset.

The API audience is still required for JWT resource-server validation; the
**web client ID is a different token audience**. Enabling SSO requires a
confidential client secret with at least 16 characters and an HTTPS public
origin (HTTP is permitted only on loopback for development). Provide either
`PACKORY_OIDC_CLIENT_SECRET` or preferably its operator-managed `_FILE`
alternative, not both. Omit both client ID and secret to disable browser
login without disabling bearer JWTs.

When TLS terminates at a reverse proxy, set `PACKORY_TRUSTED_PROXY_IP` to the
proxy's exact IP as seen by Packory. Only that peer can supply
`X-Forwarded-Proto`; do not use a CIDR, wildcard or arbitrary client address.
This lets secure antiforgery cookies work behind HTTPS ingress without trusting
spoofed forwarded headers. An authenticated browser session authorizes dashboard
API calls through its server-managed cookie and CSRF token; the UI hides the
API-token fallback while signed in. Native registry and MCP calls retain their
separate credential requirements.

### Identity provider mapping

In Keycloak register a confidential OIDC client with **Standard Flow**
(authorization code), PKCE S256 and the exact redirect URI
`https://packages.example.test/auth/callback`.
Register `https://packages.example.test/auth/signed-out` as a post-logout
redirect if provider logout is supported. Use only HTTPS on a shared
installation. Disable implicit and direct-access-grant flows unless another
client explicitly needs them.

Explicitly map dedicated **Packory client roles** from Keycloak into the
**signed ID token** for this specific browser client, under repeatable claim
`packory_role` with values `read`, `write`, or `admin`. Optional
`packory_namespace` and `packory_feed` claims in this ID token constrain
publication and private-feed access. Invalid grants are filtered.
**Do not use** email, arbitrary realm roles, `realm_access`, OAuth access-token
scopes, or roles from an unrelated client as browser administration rights.
A signed-in user with no mapped Packory role gets no privileged access.

The existing *access-token* scopes `packory.read/write/admin` remain separate
for native/API bearer authorization. They are not silently converted to web
roles. Browser session claims intentionally omit subject, name, email and
OAuth access/refresh tokens. The UI only receives a role label and CSRF token.

### Endpoints and session protections

| Route | Purpose |
| --- | --- |
| `GET /auth/session` | `enabled`, `authenticated`, role and per-session CSRF request token; `no-store` |
| `GET /auth/login` | Starts OIDC code + PKCE; always returns to `/`, no user-controlled redirect |
| `POST /auth/logout` | Requires the CSRF form token; signs out the local cookie and attempts provider end-session |
| `/api/*` | Cookies only with `X-Packory-Browser: 1`; unsafe methods additionally validate `X-Packory-Csrf` |
| `/mcp`, native registry routes | Unchanged Bearer/static-token authentication; cookies are rejected as privilege sources |

Tickets are held in a bounded **in-memory server-side store** with 15-minute
absolute expiry. The protected HttpOnly/SameSite=Lax cookie contains an opaque
random session reference, not the ID or access token. Logout explicitly removes
the local session; a process restart also expires all sessions. Keycloak
logout may vary by provider configuration and is **not** proof that other
services' existing SSO sessions have been terminated. Users may need to
explicitly end their IdP session. Role changes in Keycloak do not proactively
revoke an existing Packory session; revoke it at logout, restart or within
15 minutes. No refresh-token storage, silent renewal or multiple active
application nodes are supported by this store.

ASP.NET Core Data Protection keys for OIDC correlation and CSRF are persisted
under `PACKORY_DATA_DIRECTORY/identity-keys`. Back up and restrict this directory
to the application identity; encrypt the volume/key store at rest. Do not
commit generated keys. In reverse-proxy deployments ensure the configured
public HTTPS origin matches browser URLs, preserve Host and use trusted proxy
header configuration. Do not enable unsigned JWTs or disable issuer/audience
validation to work around an incorrect realm/client configuration.

### Verification limits

The synthetic `BrowserSsoTests` check explicit claim mapping, least
privilege, session revocation/expiry and native/MCP privilege boundaries.
They **do not** demonstrate a real Keycloak browser redirect, cookie/CSRF
attack resistance under hostile browser conditions, TLS/reverse-proxy
correctness, multi-node persistence or standards certification. Run a
separate live integration/browser test with the actual realm before rollout.

Primary sources: [Microsoft ASP.NET Core OIDC web login](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication),
[Keycloak OIDC protocol mappers](https://www.keycloak.org/admin-api/protocol-mappers).
