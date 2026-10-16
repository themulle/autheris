# F-AUTH-04: SQL Server Windows Authentication (NTLM & Kerberos) in Containers

## 1. Executive Summary & Problem Statement

Enterprise SQL Server deployments frequently mandate Windows Authentication (Active Directory domain service accounts) and disable SQL Server Authentication (`sa` / SQL logins). While `Integrated Security=true` works out of the box on domain-joined Windows hosts via Windows SSPI, running **Autheris** inside a hardened non-root Linux container (`mcr.microsoft.com/dotnet/aspnet:10.0`, UID `10001:10001`) requires:

1. **GSSAPI & NTLM Mechanism Plugins:** `Microsoft.Data.SqlClient` on Linux delegates `Integrated Security=true` / `Integrated Security=SSPI` to `libgssapi_krb5.so.2`. When authenticating via NTLM (for example when connecting by IP address or without a Kerberos KDC/SPN), the GSS-NTLMSSP plugin (`gss-ntlmssp`) and its mechanism registration (`/etc/gss/mech.d/mech.ntlmssp.conf` and `/etc/gss/mech`) must be installed and readable by the non-root container user.
2. **Zero-Plaintext Credential Provisioning:** Domain passwords must never be stored in `appsettings.json`, connection strings, or logs.
3. **Active Directory Lockout Prevention:** Passing a domain-qualified username (`User ID=DOMAIN\user;Password=...`) without `Integrated Security=true` causes `SqlClient` to transmit the credential as a SQL Server login, which fails and increments the Active Directory bad-password counter (`badPwdCount`), rapidly locking out the production service account.

---

## 2. Architecture & Component Wiring

- **Container Runtime Layer (`Dockerfile`, `Dockerfile.production`, `Dockerfile.staging`):**
  - Pre-installs `libgssapi-krb5-2`, `gss-ntlmssp`, `krb5-user`, and `libkrb5-3`.
  - Ensures `/etc/gss/mech.d/mech.ntlmssp.conf` and `/etc/gss/mech` have mode `0644` so UID `10001` can load `/usr/lib/x86_64-linux-gnu/gssntlmssp/gssntlmssp.so` (OID `1.3.6.1.4.1.311.2.2.10`).
  - Creates `/app/data/gss` owned by `10001:10001` (`chmod 0700`) for runtime-materialized NTLM credential files.
- **Domain Configuration (`src/Autheris.Domain/Options/Storage/DatabaseOptions.cs`):**
  - `DataSourceConnectionOptions.WindowsAuth` (`SqlServerWindowsAuthOptions`) supports `Mode` (`None`, `Auto`, `Ntlm`, `Kerberos`), `Domain`, `Username`, `PasswordRef`, `NtlmUserFilePath`, `KerberosConfigPath`, `KerberosKeytabPath`, `KerberosCachePath`, `ServicePrincipalName`, and `UseManagedNtlm` (nullable, see 3.3). `DataSourceConnectionOptions.LoginFailureCooldownSeconds` configures the login circuit breaker (see 3.7).
- **Connection Factory & GSS Configurator (`src/Autheris.Infrastructure/Persistence/SqlServerWindowsAuthConfigurator.cs`, `SqlConnectionFactory.cs`):**
  - `SqlServerWindowsAuthConfigurator.PrepareConnection` resolves `PasswordRef` via `SecretReferenceResolver` (`file:/run/secrets/...` or Key Vault), atomically materializes `/app/data/gss/ntlm_users` (`0600`, `DOMAIN:USER:PASSWORD` format), sets `NTLM_USER_FILE`, `NTLMUSER`, and `NETBIOS_DOMAIN_NAME`, and forces `Integrated Security=true` while stripping any `User ID`/`Password` attributes from the connection string.
  - `SqlServerWindowsAuthConfigurator.ClassifyOpenException` translates raw GSSAPI/SSPI/SQL Server 18456 errors into actionable operator diagnostics without exposing credentials.
- **Startup Validation (`src/Autheris.Api/Extensions/DependencyInjection/GatewayStartupValidator.cs`):**
  - `ValidateSqlServerWindowsAuth` validates configuration, secret file existence, and `NTLM_USER_FILE` syntax at startup **without opening a network connection** (preventing startup loops from locking out AD accounts).

---

## 3. Configuration & Usage Examples

### 3.1 Docker Compose with Mounted Secret (`PasswordRef`)

```yaml
services:
  autheris:
    image: ghcr.io/themulle/autheris:latest
    environment:
      Gateway__DataSources__Connections__LweBi__Provider: SqlServer
      Gateway__DataSources__Connections__LweBi__ConnectionString: "Server=10.160.2.94,1433;Database=Reporting;Integrated Security=true;Encrypt=True;TrustServerCertificate=True;"
      Gateway__DataSources__Connections__LweBi__WindowsAuth__Mode: Ntlm
      Gateway__DataSources__Connections__LweBi__WindowsAuth__Domain: LWEW2K
      Gateway__DataSources__Connections__LweBi__WindowsAuth__Username: lwesvcbidbreportread
      Gateway__DataSources__Connections__LweBi__WindowsAuth__PasswordRef: "file:/run/secrets/lwe_bi_password"
    secrets:
      - source: lwe_bi_password
        target: /run/secrets/lwe_bi_password
        uid: "10001"
        gid: "10001"
        mode: 0400

secrets:
  lwe_bi_password:
    file: ./secrets/lwe_bi_password.txt
```

### 3.2 Kubernetes Secret with `NTLM_USER_FILE`

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: sql-ntlm-credentials
type: Opaque
stringData:
  ntlm_users: |
    LWEW2K:lwesvcbidbreportread:REPLACE_WITH_SECRET_PASSWORD
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: autheris-gateway
spec:
  template:
    spec:
      securityContext:
        runAsUser: 10001
        runAsGroup: 10001
        fsGroup: 10001
      containers:
        - name: autheris
          image: ghcr.io/themulle/autheris:latest
          env:
            - name: NTLM_USER_FILE
              value: /run/secrets/ntlm/ntlm_users
          volumeMounts:
            - name: sql-ntlm
              mountPath: /run/secrets/ntlm
              readOnly: true
      volumes:
        - name: sql-ntlm
          secret:
            secretName: sql-ntlm-credentials
            defaultMode: 0400
```

### 3.3 Choosing the NTLM path: managed NTLM vs gss-ntlmssp

Two NTLM implementations exist. Exactly one is used per data source:

| Path | Selected when | What happens |
|------|---------------|--------------|
| **Managed NTLM** (default) | `WindowsAuth.UseManagedNtlm` is `true`, or it is not set and the environment variable `DOTNET_System_Net_Security_UseManagedNtlm` is not `false`/`0` | Autheris sets the AppContext switch `System.Net.Security.UseManagedNtlm` and attaches a per-connection `SspiContextProvider` backed by `NegotiateAuthentication`. No native library is involved. |
| **gss-ntlmssp (GSSAPI)** | `WindowsAuth.UseManagedNtlm` is `false`, or it is not set and `DOTNET_System_Net_Security_UseManagedNtlm` is `false`/`0` | The switch is never set and no provider is created. `Microsoft.Data.SqlClient` uses `libgssapi_krb5` with the `gss-ntlmssp` mechanism and `NTLM_USER_FILE` / `NTLMUSER` / `NETBIOS_DOMAIN_NAME`. |

Rules:

- An explicit `WindowsAuth.UseManagedNtlm` always wins over the environment variable (`ManagedNtlmPolicy.Resolve`).
- The container images (`Dockerfile`, `Dockerfile.production`, `Dockerfile.staging`) ship with managed NTLM as the default, so `gss-ntlmssp` is only used when managed NTLM is switched off. To debug `gss-ntlmssp` (`GSSNTLMSSP_DEBUG`), set `WindowsAuth.UseManagedNtlm=false` (or the environment variable to `false` and leave the option unset); otherwise the gss-ntlmssp log stays empty because it is never called.
- The same rule applies to the ambient path (`Integrated Security=true` without a `WindowsAuth` section but with `NTLM_USER_FILE`).
- Kerberos mode (`Mode=Kerberos`, or `Auto` with a keytab/cache) never uses managed NTLM.

Which combination applies to SQL Server:

- **Image default:** managed NTLM through the custom `ManagedNtlmSspiContextProvider` (`SspiContextProvider` backed by `NegotiateAuthentication`). The images set `DOTNET_System_Net_Security_UseManagedNtlm=true`; credentials are held per connection, not in the process environment.
- **GSS (gss-ntlmssp) only** when `UseManagedNtlm` resolves to `false`. This path uses the process-wide variables `NTLM_USER_FILE`, `NTLMUSER` and `NETBIOS_DOMAIN_NAME`, so it supports exactly one Windows account per process: a second data source with a different account is refused at connection time (`GssIdentityRegistry`, fail closed). Use managed NTLM for several accounts. Do not mix managed and GSS data sources in one process: the AppContext switch `System.Net.Security.UseManagedNtlm` is process-wide.
- The provider is part of the SqlClient connection pool key; it compares by credential identity (domain, user, password, SPN, package), so equal credentials share a pool and different accounts never do.
- Managed NTLM has not yet been verified against a real Windows SQL Server. The first probe (section 3.5) against a real server is the verification; the failure category tells which step failed.

#### `PasswordRef` per environment

`PasswordRef` is resolved by `SecretReferenceResolver` through the registered `IKeyVaultSecretProvider` (`DefaultEnvironmentSecretProvider`):

- **Outside Development (and when the host environment is unknown):** the value must be a reference that resolves to a non-empty secret different from the reference itself: `file:/run/secrets/<name>` (the path must be inside the secrets directory, default `/run/secrets`; symlinks leaving it are refused; trailing newlines and spaces are trimmed) or a name/URI that the secret provider resolves from its configuration (for example environment variables or Key Vault). A plaintext password in `PasswordRef` is rejected (fail closed, `SecurityException`).
- **Development only:** if the reference does not resolve, the configured value itself is used as a plaintext password and a warning is logged.
- Startup validation additionally requires that a `file:` `PasswordRef` exists, and rejects a `PasswordRef` without `Username`.
- The password must not contain newline characters.

#### File modes of `NTLM_USER_FILE`

The generated `ntlm_users` file (default `/app/data/gss/ntlm_users`) is created with mode `0600` from the start and is not rewritten when the entry is unchanged. A pre-mounted `NTLM_USER_FILE` with group or other permissions logs a warning (recommended `0400`/`0600`). On a Windows bind mount (Docker Desktop, WSL) the file system reports mode `0777` and the permissions cannot be enforced, so the warning is expected there; a named volume is recommended for `/app/data` and for the secrets directory.

### 3.4 Diagnostics

Authentication failures are classified into `SqlServerWindowsAuthException` (derives from `InvalidOperationException`). Its message never contains passwords, connection strings or raw driver text. Categories:

| Category | Meaning |
|----------|---------|
| `GssapiUnavailable` | `libgssapi_krb5` or the `gss-ntlmssp` mechanism is missing or unusable |
| `KerberosNegotiation` | SSPI/Kerberos/SPN failure (`Cannot generate SSPI context` on the GSSAPI path) |
| `NtlmNegotiation` | Managed NTLM failed; the message names the step (`negotiate` or `challenge`) and the `NegotiateAuthenticationStatusCode` or exception type recorded by `ManagedNtlmSspiContextProvider.LastFailure` |
| `LoginRejected` | SQL Server rejected the login (error 18456/18452); repeated attempts can lock the AD account |
| `LoginBlocked` | The login circuit breaker is open (section 3.7); the server was not contacted |

Diagnostic fields (`SqlNumber`, `SqlState`, `SqlClass`, `NtlmStatus`, `NtlmStep`, `NtlmExceptionType`, `InnerExceptionType`) never contain secrets.

Log lines:

- `Information`, once per data source: `SQL Server data source authentication path: {AuthPath} (managed NTLM setting: {UseManagedNtlm}).` with `AuthPath` one of `SqlPassword`, `Integrated-ManagedNtlm`, `Integrated-GSSAPI`, `Integrated-Kerberos`.
- `Warning` from the governed catalog when a table's metadata cannot be loaded (the table is denied, fail closed): `Catalog metadata load failed for data source {DataSource}, table {Table}: {Message} [Category={Category}; {Fields}]; the table is denied.` For unclassified failures the exception type chain and, for `SqlException`, number/state/class are logged instead of the raw driver message. A missing data source configuration is reported as `The data source '<name>' has no configured connection.`

### 3.5 Testing a SQL Server data source

`POST /api/v1/catalog/datasources/{id}/test` opens a real connection through the same connection factory used for queries (including the NTLM/Kerberos preparation). It reports:

- success or failure, and the measured latency of the connection open;
- `Diagnostics.TargetHost` / `TargetPort` taken from the connection string (no user, password or database; port `0` when none is configured);
- `ProbeDetails`: `Dialect`, `LoginName`, `AuthPath` (`SqlPassword`, `Integrated-ManagedNtlm`, `Integrated-GSSAPI`, `Integrated-Kerberos`), and on failure `Category` (the categories above, plus `ConnectionFailed`, `Timeout`, `NotConfigured`, `LoginBlocked`) with the diagnostic fields. The error message is the classified, secret-redacted text.
- TLS details are not observable through the driver and are reported as not determined (`TlsHandshakeSuccess=false`, no protocol version).

The probe timeout defaults to 5 seconds and is capped at 15 seconds. Authorization and audit behaviour are the same as for HTTP data sources.

The probe also reports `ProbeDetails.LoginName`: the effective login, either `DOMAIN\user` of the configured Windows account, the SQL login user id, or a marker for ambient credentials. The password is never returned. After a login failure the probe is refused while the circuit breaker (section 3.7) is open and reports category `LoginBlocked` with `RemainingSeconds`; it does not contact the server, so the probe cannot be used to drive the AD bad-password counter.

### 3.6 Native environment and troubleshooting

- On Linux, `Environment.SetEnvironmentVariable` only changes .NET's managed copy. Native libraries (`libgssapi_krb5`, `gss-ntlmssp`, `krb5`) read the process `environ` through `getenv()`. Autheris therefore sets `NTLM_USER_FILE`, `NTLMUSER`, `NETBIOS_DOMAIN_NAME`, `KRB5_CONFIG`, `KRB5_CLIENT_KTNAME` and `KRB5CCNAME` through `NativeEnvironment.Set`, which updates both the managed copy and (on Linux/macOS) the native `environ` via libc `setenv`. Unchanged values are not rewritten and the call is serialized. `setenv` is not thread-safe against concurrent native `getenv`; in production provide the variables at container start so the runtime write is a no-op. No password is ever placed in the environment.
- `gssntlm_acquire_cred_from ... ENOENT` means gss-ntlmssp found no credentials: the native `NTLM_USER_FILE` was not set or not readable.
- Known open point: on the GSS path SQL Server can answer with error 18452 (login from an untrusted domain), and SqlClient's default GSS path may first fail with `The target principal name is incorrect. Cannot generate SSPI context`. Prefer managed NTLM (the default), check that `NETBIOS_DOMAIN_NAME` / `WindowsAuth.Domain` is the NetBIOS domain name (not the DNS name), and that the SQL Server accepts NTLM from that domain.

### 3.7 Lockout protection (login circuit breaker)

`SqlConnectionFactory` holds a circuit breaker per credential identity for SQL Server data sources. It is shared by the catalog build, query execution and the connectivity probe.

- **Opens on:** SQL error 18456, 18452 or 18451, category `LoginRejected`, and `NtlmNegotiation` failures with a credential status (`InvalidCredentials`, `UnknownCredentials`, `CredentialsExpired`).
- **Does not open on:** network, DNS, timeout, `GssapiUnavailable` and Kerberos/SPN failures. They do not increment the AD bad-password counter, and blocking on them would stretch short outages.
- **While open:** every open fails closed with `SqlSourceLoginBlockedException` (category `LoginBlocked`) before any secret is resolved and without contacting the server. The catalog denies the table; the probe reports the remaining seconds. One Warning is logged when the breaker opens (reason and cool-down); blocked retries log at Debug.
- **Duration:** `DataSources:Connections:<name>:LoginFailureCooldownSeconds` (default `300`, range 0-86400, `0` disables the breaker).
- **Identity:** the breaker is keyed by a hash of provider, connection string and all `WindowsAuth` settings (never a resolved secret). A changed configuration lifts the block at once; a successful open or the expiry closes it. A rotated secret behind an unchanged `PasswordRef` is picked up when the cool-down expires.
- **Scope:** in memory, per replica. With N replicas up to N failed attempts can occur per cool-down.

Catalog load failures other than the blocked case log a Warning at most once per 30 seconds per data source and reason; repeats are logged at Debug.

---

## 4. Security, Zero-Trust & Fail-Closed Behavior

- **AD Lockout Guard:** Any SQL Server connection string containing `User ID=DOMAIN\user` without `Integrated Security=true` is rejected fail-closed at startup and before connection open.
- **No Startup Network Login:** Startup validation checks file presence, permissions (`0400`/`0600`), and `DOMAIN:USER:PASSWORD` structure locally without contacting SQL Server or Active Directory.
- **Zero Secret Leakage:** Passwords and `NTLM_USER_FILE` contents are never written to logs, exception messages, or connection strings.
