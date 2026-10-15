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
  - `DataSourceConnectionOptions.WindowsAuth` (`SqlServerWindowsAuthOptions`) supports `Mode` (`None`, `Auto`, `Ntlm`, `Kerberos`), `Domain`, `Username`, `PasswordRef`, `NtlmUserFilePath`, `KerberosConfigPath`, `KerberosKeytabPath`, `KerberosCachePath`, and `ServicePrincipalName`.
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

---

## 4. Security, Zero-Trust & Fail-Closed Behavior

- **AD Lockout Guard:** Any SQL Server connection string containing `User ID=DOMAIN\user` without `Integrated Security=true` is rejected fail-closed at startup and before connection open.
- **No Startup Network Login:** Startup validation checks file presence, permissions (`0400`/`0600`), and `DOMAIN:USER:PASSWORD` structure locally without contacting SQL Server or Active Directory.
- **Zero Secret Leakage:** Passwords and `NTLM_USER_FILE` contents are never written to logs, exception messages, or connection strings.
