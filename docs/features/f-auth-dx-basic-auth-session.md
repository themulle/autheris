# F-AUTH-DX: Basic Auth with Cookie Session and Developer Helpers

One login with user name and password is enough for every surface of the gateway. After the first successful Basic login the gateway issues an encrypted session cookie. Browsers, cookie jars, WebSocket and EventSource stay logged in without sending the password again. In Development, a set of helpers (personas, one-click login, effective configuration, startup banner, verbose errors) builds on it.

**Development and internal test environments only.** In `Production` the gateway refuses to start when `BasicAuth.Session.Enabled=true`.

## Configuration

```json
"Gateway": {
  "Authentication": {
    "RequireKerberosOnly": false,
    "BasicAuth": {
      "Enabled": true,
      "Session": {
        "Enabled": true,
        "AllowedEnvironments": [ "Development", "Integration" ],
        "CookieName": "__Host-Autheris.Session",
        "SlidingExpirationMinutes": 480,
        "AbsoluteExpirationMinutes": 1440,
        "KeyDirectory": null
      },
      "Users": [
        { "Username": "owner", "Password": "$pbkdf2$600000$…", "Sid": "S-1-5-21-DATAOWNER-1", "Roles": [ "DataOwner" ] }
      ]
    }
  }
}
```

| Option | Meaning |
|---|---|
| `Session.Enabled` | Enables the cookie. Without `BasicAuth.Enabled` this is a startup error. |
| `Session.AllowedEnvironments` | Environments in which the cookie is allowed. `Production` is never allowed, not even through this list. |
| `Session.CookieName` | Must start with `__Host-`. |
| `Session.SlidingExpirationMinutes` | Expiry after inactivity. |
| `Session.AbsoluteExpirationMinutes` | Maximum lifetime regardless of activity. |
| `Session.KeyDirectory` | Data Protection key ring. Required with several replicas (`MultiNodeClusterMode` or `Replicas > 1`) and must be on a shared volume. Otherwise the framework default (user profile) applies, so cookies survive a restart. |

`appsettings.Development.json` ships personas, all with the password `dev`. The SIDs match the seed data:

| User | Role | Purpose |
|---|---|---|
| `dev-admin` | ClusterAdmin | administration |
| `owner` | DataOwner (`S-1-5-21-DATAOWNER-1`) | primary owner of the demo tables `finance_table_1` / `_5` |
| `approver-a`, `approver-b` | DataOwner (`S-1-5-21-APPROVER-A/-B`) | delegates of `finance_table_5` (four-eyes) |
| `analyst` | Analyst | regular user |
| `analyst-b` | Analyst, tenant `tenant-b` | tests across tenant boundaries |

Plaintext passwords work in `Development` only. In every other environment `$pbkdf2$…` is mandatory.

## Usage

```bash
# curl: log in once, then use the cookie only
curl -u owner:dev -c ~/.autheris.jar https://localhost:7214/api/auth/session
curl -b ~/.autheris.jar https://localhost:7214/api/v1/queries/
curl -b ~/.autheris.jar -H 'GraphQL-Preflight: 1' -H 'Content-Type: application/json' \
     -d '{"query":"{ __typename }"}' https://localhost:7214/graphql

# Logout (a POST with a cookie needs a custom header because of CSRF protection)
curl -b ~/.autheris.jar -c ~/.autheris.jar -X POST -H 'X-Requested-With: curl' https://localhost:7214/api/auth/logout
```

```powershell
$cred = Get-Credential owner
Invoke-RestMethod https://localhost:7214/api/auth/session -Authentication Basic -Credential $cred -SessionVariable s
Invoke-RestMethod https://localhost:7214/api/v1/queries/ -WebSession $s
```

- **Browser:** open the gateway URL and log in through the Basic dialog. Nitro (Banana Cake Pop), OData, DevPortal and GraphQL subscriptions are then logged in. Alternatively use a persona login link (see below).
- **Postman:** collection auth "Basic Auth". Postman keeps the cookie automatically.
- **MCP clients:** set `Authorization: Basic …` as a static header. The existing credential cache (`SuccessCacheSeconds`) spares PBKDF2 on every request.
- **Stay stateless:** send the header `X-Autheris-No-Session: 1`; no cookie is issued then.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /api/auth/session` | Effective identity: user, SID, tenant, roles, authentication method, session id and expiry |
| `POST /api/auth/logout` | Revokes the session id (token revocation) and deletes the cookie. Callable anonymously. |
| `GET /api/auth/login` | Unchanged |

## Behaviour and security

- **Scheme selection:** an `Authorization` header (Bearer, Basic, Negotiate) always wins over the cookie. A Basic header with a different user replaces the session.
- **Cookie:** `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, no domain. The content is encrypted and signed by Data Protection.
- **Checked on every request:**
  - If the user was removed from the configuration, or the password, SID or tenant changed, the session becomes invalid.
  - Role and group changes apply on the next request.
  - The absolute lifetime is enforced.
- **Revocation:** the cookie carries `jti` and `iat`. `/api/admin/tokens/revoke` with the SID or the session id ends the session immediately, including open WebSocket connections.
- **CSRF:** unsafe methods with a cookie need a custom header (`GraphQL-Preflight`, `X-Requested-With`, `X-CSRF-Token`) and a trusted `Origin`. This is the existing anti-CSRF middleware.
- **WebSocket:** the session cookie is accepted on upgrade when the `Origin` is the gateway itself or is listed in `GraphQL.TrustedOrigins`. Other ambient credentials (Basic, Negotiate) still need a token in `connection_init`.
- **Browser challenge:** for `Accept: text/html` with Basic enabled, the challenge always goes to Basic (Negotiate is not offered). The 401 response carries a short HTML page with the realm. API clients get `application/problem+json` with the active schemes in Development and an empty body elsewhere.
- **Script requests:** requests with `X-Requested-With` get no `WWW-Authenticate: Basic` on 401, so no browser dialog appears in the middle of a page.
- **Development and test auth:** `TestAuthHandler` only applies with `X-Test-*` headers or `danger_allow_anonymous_access`. Requests without any header reach the Basic challenge, so the browser shows its login dialog.
- **GraphQL / Banana Cake Pop:** there is no anonymous UI access. `/graphql` requires authentication like everything else. In a browser the one-time Basic login is enough; the cookie and the WebSocket carry the session afterwards.
- **Health probes and metrics:** `/health/*` never runs Basic authentication (no PBKDF2 load, no password oracle). `/metrics` is now subject to the IP rate limiter (E-13).

**Startup errors occur for:**
- `Session.Enabled` without `BasicAuth.Enabled`, or together with `RequireKerberosOnly`,
- the `Production` environment or an environment that is not allowed,
- a cookie name without `__Host-`,
- an absolute lifetime shorter than the sliding one,
- wildcard CORS (`warn_allow_all_cors_origins` or `TrustedOrigins: "*"`) outside Development. In Development, for example with the Quickstart preset, the session stays disabled with a startup warning instead,
- several replicas without `KeyDirectory`,
- Basic users without a password, with a duplicate name, or with a SID that contains `:` or starts with `ITSM` (R2-1 / N-1).

**Known limitations:**
- Browsers remember the credentials from the Basic dialog. After `/api/auth/logout` the browser may send them again on the next 401. Use a persona login link or a private window to switch users.
- Logout revokes the session only. An `Authorization: Basic` header that is still sent stays valid (curl, MCP clients).
- Safari does not set `Secure` cookies over `http://localhost`. Use HTTPS there (`https://localhost:7214`).

## Developer helpers (Development only)

These routes do not exist outside Development. Each can be switched off in `Gateway:Dev` (see [developer-guide: developer options](../developer-guide.md#3-developer-options-gatewaydev)).

| Route / feature | Switch | Purpose |
|---|---|---|
| `GET /api/dev/personas` | `Dev:PersonaLogin` | Lists the configured users with SID, tenant, roles and login link. Never passwords. |
| `GET /api/dev/login/{persona}?redirect=/graphql` | `Dev:PersonaLogin` | One-click login: issues the session cookie and redirects to a local path. Only same-site paths (`/x`) are followed; anything else returns JSON. Needs an active session cookie feature (409 otherwise, with the reason). |
| `GET /api/dev/info` | `Dev:Info` | Effective configuration without secrets: preset, resolved dev features, class B switches, auth schemes, session state, database provider, rate limits, GraphQL flags, active bypasses. No connection strings or passwords. |
| Startup banner | `Dev:Banner` | Links (hub, GraphQL, Swagger, identity, config, health), preset, database mode and a persona table with login links. Plaintext passwords are printed, hashed ones never. |
| Verbose errors | `Dev:VerboseErrors` | A 403 from a named role policy explains the required roles and the caller's roles. A bodyless 403 returned by endpoint code gets a `problem+json` with the caller identity. Unhandled exceptions return `problem+json` with `traceId` to JSON clients. |

Development database persistence: set `Gateway:Dev:Persist:Enabled=true` (or `Gateway__Dev__Persist__Enabled=true`). The governance database then lives in `.data/dev.db` (`Persist:Directory`) and survives restarts; demo seeding is idempotent. Reset with `scripts/dev-reset.sh`, which removes the database, its WAL files **and** `dev.db.audit-anchor.json`. Deleting only the database leaves an anchor that points to a sequence the new database does not have, and the next start reports an audit chain integrity violation. In a container, map the directory to a named volume; avoid bind mounts on 9p/virtiofs file systems because of SQLite WAL.
