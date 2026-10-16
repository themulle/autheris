# REQ-BUG-NTLM-01: SQL Server Windows Authentication (NTLM) Failures Are Not Diagnosable

**Date:** 2026-10-16 · **Branch:** `fix/sqlserver-ntlm-catalog-diagnostics` · **Reporter version:** v1.1.14 container
**Related features:** [F-AUTH-04](../features/f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md), [F-API-09](../features/f-api-09-datasource-connectivity-testing.md)
**Origin plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md)

## 1. Phase 1: Product Requirements (product-manager)

### 1.1 Problem

A SQL Server data source with `WindowsAuth.Mode=Ntlm` (`Domain`, `Username`, `PasswordRef`) and `Integrated Security=true` makes every query fail with HTTP 403. The only log line is
`Catalog metadata load failed ... (InvalidOperationException); the table is denied.` The operator cannot tell whether the cause is the credentials, the NTLM path, the native GSS stack or the network. The connectivity test endpoint reports success regardless. Repeated retries on every query additionally risk locking the Active Directory service account.

### 1.2 Verified root causes

Line numbers: "main" is `88e992d2^`, "branch" is the fix commit and the follow-up gate commits.

| # | Root cause | Evidence on main | Resolution on branch |
|---|-----------|------------------|----------------------|
| RC-1 | `WindowsAuth.UseManagedNtlm` was a `bool` defaulting to `true`; `EnableManagedNtlmSwitch` forced the AppContext switch, so `DOTNET_System_Net_Security_UseManagedNtlm=false` had no effect and the gss-ntlmssp path was unreachable. | `DatabaseOptions.cs` (`UseManagedNtlm { get; init; } = true`), `SqlServerWindowsAuthConfigurator.cs:269` | `bool?` (`DatabaseOptions.cs:163`), `ManagedNtlmPolicy.Resolve`, configurator `:270` |
| RC-2 | `ManagedNtlmSspiContextProvider.GenerateContext` swallowed every failure (`catch { return false; }`), so SqlClient only reported "Cannot generate SSPI context". | `SqlServerWindowsAuthConfigurator.cs:698-742` | `TryGenerateContext` records `ManagedNtlmFailure` (step, status, exception type) |
| RC-3 | `ClassifyOpenException` mapped "Cannot generate SSPI context" to Kerberos/SPN even when managed NTLM was in use. | `SqlServerWindowsAuthConfigurator.cs:487-493` | category `NtlmNegotiation` when `managedNtlmUsed` |
| RC-4 | `GovernedCatalogProvider` logged only the exception type and denied tables silently in four admission branches (no connection, provider, catalog mismatch, reader). | `GovernedCatalogProvider.cs:115` and `LoadAsync` | `LogLoadFailure`, `LogAdmissionDenied` |
| RC-5 | `Environment.SetEnvironmentVariable` on Linux changes only .NET's managed copy; libgssapi / gss-ntlmssp read the native `environ` (`acquire_cred ENOENT`). | configurator `:69`, `:183` (`Environment.SetEnvironmentVariable`) | `NativeEnvironment.Set` (libc `setenv`) |
| RC-6 | `POST /api/v1/catalog/datasources/{id}/test` for SQL was a stub: always success, host `sql-engine`, TLS 1.3. | `DatasourceTestingService.cs:396` | real open via `ISqlConnectionFactory` |

### 1.3 Scope

In scope:

- Classified, credential-free failure diagnostics for SQL Server Windows authentication (catalog log, probe, exception fields).
- A working choice between managed NTLM and gss-ntlmssp (option and environment variable).
- Native environment variables for the GSS path.
- A real connectivity probe for SQL data sources that also reports the effective login name.
- Lockout protection (owner requirements R-3, R-4): probe retry lock and a connection circuit breaker.
- Documentation (owner requirement R-5).

Out of scope / still open:

- **OPEN-1:** SQL Server error 18452 (login from an untrusted domain) on the gss-ntlmssp path. Cause is on the server/domain side or in the NTLM domain name sent by gss-ntlmssp; not reproducible without the reporter's server.
- **OPEN-2:** Whether managed NTLM succeeds against the reporter's server. It has never been verified against a real Windows SQL Server. One probe with the new image answers it (`POST .../test`).
- Kerberos end-to-end verification, Windows hosts, non-SQL-Server dialects (the breaker is SQL Server only).
- A cluster-wide (cross-replica) breaker. Each replica has its own breaker.

The reporter suggested a custom `SspiContextProvider`. Autheris already has one (`ManagedNtlmSspiContextProvider`), now with failure recording; its success against the reporter's server is unverified until the probe in OPEN-2.

### 1.4 Requirements and acceptance criteria

Each criterion maps to a unit test (all in `tests/Autheris.Tests.Unit`).

| ID | Acceptance criterion | Test |
|----|---------------------|------|
| AC-1 | `UseManagedNtlm=false` creates no provider and never sets the switch. | `SqlServerWindowsAuthConfiguratorTests.PrepareConnection_UseManagedNtlmFalse_CreatesNoProviderAndDoesNotSetSwitch` |
| AC-2 | Unset option follows `DOTNET_System_Net_Security_UseManagedNtlm`; an explicit option wins. | `ManagedNtlmPolicy_ExplicitOptionWinsOverEnvironmentVariable`, `PrepareConnection_OptionUnsetAndEnvironmentFalse_UsesGssPath` |
| AC-3 | A managed NTLM failure is recorded with step, status and exception type, never with secrets. | `SqlServerWindowsAuthConfiguratorTests.ManagedNtlmProvider_InvalidChallengeBlob_ReturnsFalseAndRecordsFailure` |
| AC-4 | A managed NTLM "Cannot generate SSPI context" is classified `NtlmNegotiation`, not Kerberos. | `SqlServerWindowsAuthConfiguratorTests.ClassifyOpenException_SspiContextWithManagedNtlm_ReturnsNtlmNegotiationWithRecordedStatus` (and `..WithoutManagedNtlm_ReturnsKerberosNegotiationCategory`) |
| AC-5 | The catalog logs classified reason, missing connection and every silent admission denial. | `GovernedCatalogProviderDiagnosticsTests` (classified, missing connection, unclassified, unsupported provider, breaker-open without Warning) |
| AC-6 | GSS environment variables are set natively on Linux/macOS. | `SqlServerWindowsAuthConfiguratorTests.NativeEnvironment_Set_IsVisibleToNativeGetenv_OnUnix` |
| AC-7 | The SQL probe opens a real connection, reports real host/port/auth path and a classified failure without secrets; not-configured fails without a fabricated host. | `DatasourceTestingServiceSqlTests` |
| AC-8 (R-3) | The probe returns the effective login name (`DOMAIN\user` or SQL login, never a password). | `DatasourceTestingServiceSqlTests.OpenSucceeds_...`, `SqlLoginCircuitBreakerTests.Describer_ReportsEffectiveLoginWithoutPassword` |
| AC-9 (R-3) | After a login failure the probe is refused with a clear message and remaining time, without contacting the server. | `DatasourceTestingServiceSqlTests.OpenBlockedByLoginBreaker_...`, `SqlLoginCircuitBreakerTests` |
| AC-10 (R-4) | Login failures (18456/18452/18451, `LoginRejected`, credential-type NTLM statuses) open a per-credential-identity breaker (default 300 s, `LoginFailureCooldownSeconds`, `0` disables); a changed configuration, success or expiry closes it. | `SqlLoginCircuitBreakerTests` |
| AC-11 (R-4) | Network, timeout, GSS-unavailable and Kerberos failures never open the breaker. | `SqlLoginCircuitBreakerTests.NetworkAndTimeoutFailures_...`, `GssAndKerberosFailures_...` |
| AC-12 (R-4) | Two GSS-path sources with different accounts are refused (identity confusion); managed NTLM never overwrites a GSS identity. | `SqlLoginCircuitBreakerTests.GssIdentityRegistry_...` |
| AC-13 (R-5) | F-AUTH-04 documents path selection, `PasswordRef` rules, file modes, breaker, probe lock and the verification gap. | Documentation review (Phase 6) |

### 1.5 Owner questions

- **Q-1:** Is 300 s the right default for `LoginFailureCooldownSeconds`? (AD lockout windows are commonly 15-30 min with a threshold of 3-10 attempts.)
- **Q-2:** Should the breaker also cover Kerberos failures (KDC/SPN problems do not increment AD counters on the SQL side, so they currently do not open it)?
- **Q-3:** Should a cluster-wide breaker (shared state across replicas) follow? With N replicas the worst case is N attempts per cool-down.

### 1.6 Testing gap and proposal: opt-in integration test against a real SQL Server

Managed NTLM has not been verified against a real Windows SQL Server. Proposal: an integration test in `tests/Autheris.Tests.Integration` (trait `RealSqlServerAuth`) that is skipped unless these environment variables are set: `AUTHERIS_IT_MSSQL_HOST`, `AUTHERIS_IT_MSSQL_PORT`, `AUTHERIS_IT_MSSQL_DOMAIN`, `AUTHERIS_IT_MSSQL_USER`, `AUTHERIS_IT_MSSQL_PASSWORD_FILE` (a `file:` secret under a temp secrets directory). It builds a `DataSourceConnectionOptions` for both paths (managed NTLM, `UseManagedNtlm=false`), opens one connection each through `SqlConnectionFactory`, runs `SELECT SUSER_SNAME()` and asserts the expected `DOMAIN\user`. Hard rules: never in CI, exactly one attempt per path (no retries; the breaker makes a second attempt fail closed), failure output only the classified category. Not implemented in this track (needs an AD-joined test server); tracked as follow-up.

## 2. Phase 2: Design (csharp-architect)

### 2.1 Chosen design

| Concern | Design |
|---------|--------|
| Path selection | `ManagedNtlmPolicy.Resolve(bool? configured, getEnv)` in `Autheris.Domain.Options` is the single rule: explicit option, else `DOTNET_System_Net_Security_UseManagedNtlm` (`false`/`0` selects GSS), else managed NTLM. Used by the configurator, `DescribeAuthPath` and the factory log. |
| Failure model | `SqlServerWindowsAuthException` (Infrastructure, derives from `InvalidOperationException` for compatibility) with category and non-secret fields. `ManagedNtlmSspiContextProvider` records `ManagedNtlmFailure`; `SqlConnectionFactory` passes it to `ClassifyOpenException`. |
| Cross-layer contract | `ISqlSourceDiagnosticException` (`CategoryName`, `DiagnosticFields`), `SqlSourceNotConfiguredException`, `SqlSourceLoginBlockedException`, `SqlSourceDescription` and `ISqlSourceDescriber` live in `Autheris.Application/Interfaces/ISqlSourceDiagnostics.cs`. |
| Native environment | `NativeEnvironment` (Infrastructure, `LibraryImport` `setenv`/`getenv`, `AllowUnsafeBlocks`) updates the managed and the native copy under a lock; unchanged values are not rewritten. |
| Probe | `DatasourceTestingService.TestSqlDatasourceAsync` opens a connection through `ISqlConnectionFactory` (the same factory as queries), describes host/port/auth path/login through `ISqlSourceDescriber`, redacts messages with `SecretScrubber`, maps `ISqlSourceDiagnosticException` to `ProbeDetails`. |
| Lockout protection (R-3, R-4) | `SqlLoginCircuitBreaker` inside `SqlConnectionFactory` (SQL Server dialect). One breaker therefore covers catalog build, query execution and the probe. Keyed by a SHA-256 fingerprint of provider, connection string, `PasswordKeyVaultRef` and all `WindowsAuth` fields (never a resolved secret); checked before `PrepareConnection`, so no secret is resolved and no server contact happens while open. Opens on 18456/18452/18451, `LoginRejected` and credential-type NTLM statuses (`InvalidCredentials`, `UnknownCredentials`, `CredentialsExpired`); closes on success, expiry or a changed fingerprint. Per-replica, in memory (no cluster abstraction needed for this track). Option `DataSources:Connections:<name>:LoginFailureCooldownSeconds` (default 300, `0` disables). |
| Process-global GSS identity | `GssIdentityRegistry`: the first GSS-path source claims `(domain, user)`; a different account is refused (`SecurityException`, fail closed). Managed NTLM sources do not overwrite the environment once a GSS identity exists. |

Non-login failures (network, timeout, DNS) do not open the breaker: they do not increment AD counters, and blocking on them would turn short outages into 5-minute outages. This is a decision, not an omission.

Logging: the breaker logs one Warning when it opens (reason, cool-down). Blocked retries in the catalog log at Debug, so every query does not repeat a Warning. The factory logs the auth path once per data source configuration.

### 2.2 Layering check

`Autheris.Application` does not reference `Autheris.Infrastructure`. The Application layer only knows the interfaces and exception types in `ISqlSourceDiagnostics.cs`; `SqlServerWindowsAuthException`, `SqlSourceDescriber`, `NativeEnvironment`, `SqlLoginCircuitBreaker` and `GssIdentityRegistry` are Infrastructure; `ManagedNtlmPolicy` and the options are Domain. `DatasourceTestingService` detects classified failures by walking the inner-exception chain for `ISqlSourceDiagnosticException`, not by an Infrastructure type. This satisfies the intent of the architecture tests (`ISqlConnectionFactory` is the established Application port for the same purpose; the lead's run of the architecture suite, 110/110, covers it). `SqlSourceDescriber` is registered in `GatewayStorageServiceExtensions` next to `ISqlConnectionFactory`.

### 2.3 Alternatives rejected

| Alternative | Reason |
|-------------|--------|
| Only set the variables in the container (`ENV NTLM_USER_FILE=...`) | Per-data-source credentials cannot be expressed in one image-level variable; does not fix RC-1/RC-2/RC-3 and gives no diagnostics. |
| `execve` self-restart or wrapper script to inject the environment | Fragile, breaks hosting model, still process-global. |
| Per-connection GSS isolation through a separate process | Disproportionate; managed NTLM already provides per-connection credentials. |
| Always forcing managed NTLM (remove the GSS path) | The reporter needs gss-ntlmssp for comparison and some servers/policies require it; the path stays selectable. |
| Rate-limiting the probe endpoint only (HTTP rate limiter) | Does not protect the catalog and query paths, which retry on every query. A breaker at the factory covers all callers and keys by credential identity. |
| Cluster-wide breaker (Redis) | Not trivially available for this scope; each replica has its own breaker (documented, owner question Q-3). |

### 2.4 Compatibility

- `SqlServerWindowsAuthOptions.UseManagedNtlm` changes from `bool` (default `true`) to `bool?`. Existing configuration files with `true`/`false` bind unchanged. Unset now follows the environment variable; since all images set it to `true`, the image default is unchanged.
- `DatasourceTestingService` constructor gains optional trailing parameters (`ISqlConnectionFactory?`, `IOptions<GatewayOptions>?`, `ISqlSourceDescriber?`); existing callers compile. Behaviour change: the SQL probe now fails when it cannot connect (previously always success). The result shape is unchanged; `HttpStatusCode` is `null` for SQL probes.
- `SqlConnectionFactory` gains an optional trailing `TimeProvider?` constructor parameter.
- `DataSourceConnectionOptions.LoginFailureCooldownSeconds` is new with a safe default. A data source that fails to log in is now blocked for 300 s per replica: intended, documented, switchable.
- `SqlSourceDescription` gains an optional trailing `LoginName`.

## 3. Phase 3: Security Review (csharp-security-expert)

Scope: the fix commit `88e992d2` plus the gate changes below. Verdict: **APPROVED WITH CONDITIONS**; all conditions are implemented in this track except where marked "accepted" or "documented".

### 3.1 Findings and binding conditions

| ID | Area | Finding | Condition / status |
|----|------|---------|--------------------|
| SEC-NTLM-01 | Secret leakage | Classified exception messages, `DiagnosticFields`, probe results and catalog logs contain only categories, SQL numbers, NTLM status names and exception type names. Passwords, connection strings, users and raw driver text are absent (tests assert `hunter2`, database and password text never appear). The raw driver exception stays as `InnerException`. | Binding: only `Message`/`DiagnosticFields` of a classified exception may be logged or returned; never `ToString()` of the inner exception on request paths. Accepted for generic exception handlers that log whole exceptions (pre-existing). |
| SEC-NTLM-02 | Probe disclosure | The probe returns host, port, auth path and (R-3) the login name `DOMAIN\user` / SQL login. The password is never returned. The endpoint stays `GovernanceAdmin` with `AuditLevel.Full`; the target is the configured connection of a registered table, never caller-supplied (no SSRF). Timeout capped at 15 s. | Binding: login name only for `GovernanceAdmin`; unchanged. Implemented (`SqlSourceDescription.LoginName`). |
| SEC-NTLM-03 | Credential stuffing / AD lockout | The probe (and, before this track, every catalog cache miss and every query) retried a failing login on each call, which drives the AD bad-password counter. The probe could be called in a loop. | Binding (owner R-3/R-4): `SqlLoginCircuitBreaker` in `SqlConnectionFactory`. After 18456/18452/18451, `LoginRejected` or a credential-type NTLM status, further opens fail closed without secret resolution or server contact for `LoginFailureCooldownSeconds` (default 300; `0` disables). Keyed by credential identity fingerprint; success, expiry or a changed configuration closes it. Network/timeout failures never open it. Implemented and unit-tested. Residual: per replica, so at most N attempts per cool-down with N replicas (owner Q-3). A rotated secret behind an unchanged `PasswordRef` is only picked up after expiry. |
| SEC-NTLM-04 | Native `setenv` | `setenv` is not thread-safe against concurrent `getenv` in native code (glibc may reallocate `environ`). Mitigations present: writes are serialized, unchanged values are not rewritten, and with SEC-NTLM-05 the values are constant after the first claim. No password is ever placed in the environment (only file path, user name, NetBIOS domain, Kerberos paths). | Accepted with documentation: for production the variables should be provided by the container at start (`ENV`/Compose/Kubernetes) so the runtime write is a no-op. Documented in F-AUTH-04 section 3.6. |
| SEC-NTLM-05 | Identity confusion on the GSS path | `NTLM_USER_FILE`, `NTLMUSER` and `NETBIOS_DOMAIN_NAME` are process-global. Two SQL Server sources with different accounts on the gss-ntlmssp path would authenticate as whichever source wrote the variables last (silent wrong identity). | Binding, implemented: `GssIdentityRegistry` lets the first GSS source claim `(domain, user)` and refuses a different account with a `SecurityException` (fail closed, no secret in the message); managed NTLM sources never overwrite the variables once a GSS identity exists. Different accounts require managed NTLM. Tested. |
| SEC-NTLM-06 | Managed provider per connection | The managed provider carries credentials per connection (safe across sources). Verified in the SqlClient 7.1.1 documentation: **the `SspiContextProvider` is part of the connection pool key** and must have a stable identity. A new provider per open with reference equality would create one pool per open (no reuse, extra NTLM logins, pool-group growth). Equality by credential identity also guarantees two accounts never share a pool. | Binding, implemented: `ManagedNtlmSspiContextProvider.Equals/GetHashCode` by a SHA-256 identity of domain, user, password, SPN and package. Tested. Residual: the process-wide AppContext switch `System.Net.Security.UseManagedNtlm` is global; mixing managed and GSS sources in one process is not supported (documented). |
| SEC-NTLM-07 | Materialized `ntlm_users` | The temp file was created with the process umask and then chmod'ed, leaving a short window with a readable plaintext password. | Binding, implemented: created with `UnixCreateMode=0600` (`FileStreamOptions`), then moved atomically. On Windows bind mounts the mode cannot be enforced (0777 reported); a named volume is recommended (documented). |
| SEC-NTLM-08 | P/Invoke | `LibraryImport` source-generated marshalling, UTF-8, `SetLastError`, name/value never null (argument guards), return code checked, `getenv` pointer copied immediately and never freed; `libc` resolves to `libc.so.6` / musl via the runtime. Non-Unix platforms never call it. | No change. Unit test calls native `getenv` after `Set`. |
| SEC-NTLM-09 | Fail closed | Breaker open, missing connection, mismatched catalog entry, missing reader, classification failure: every path denies the table or fails the open; nothing falls back to a weaker authentication or an unauthenticated read. The probe failing never grants anything. | Verified; binding: no code path may treat a blocked or failed open as "available". |

### 3.2 Changes made in this gate

- `SqlLoginCircuitBreaker` (new), wired in `SqlConnectionFactory` (check before `PrepareConnection`, record on classified failure, clear on success); `SqlSourceLoginBlockedException` (Application interfaces); option `LoginFailureCooldownSeconds`.
- `SqlSourceDescription.LoginName` + `SqlSourceDescriber.DescribeLogin`; probe `ProbeDetails["LoginName"]`.
- `GssIdentityRegistry` (new) used by `SqlServerWindowsAuthConfigurator`.
- `ManagedNtlmSspiContextProvider` identity equality; `MaterializeNtlmUserFile` creates the file with `0600`.
- Catalog logs a blocked retry at Debug, not Warning per query.
- Tests: `SqlLoginCircuitBreakerTests` (new), additions to `DatasourceTestingServiceSqlTests` and `GovernedCatalogProviderDiagnosticsTests`.

## 4. Phase 4: TDD Verification (csharp-tdd-developer)

All acceptance criteria AC-1 to AC-12 map to unit tests (table in section 1.4). The lead ran the suite on `88e992d2` (unit 7818/7818, architecture 110/110 with one skipped); the gate commits add tests that still have to be built and run.

Tests added in the gates: `SqlLoginCircuitBreakerTests` (breaker open/close/cool-down/identity/disable, credential vs. non-credential statuses, `GssIdentityRegistry`, describer login name, provider pool-key identity, `ntlm_users` mode and no-rewrite), `DatasourceTestingServiceSqlTests.OpenBlockedByLoginBreaker_...` and login name assertion, `GovernedCatalogProviderDiagnosticsTests` (unsupported provider, breaker-open without Warning).

Known gaps (accepted, no feasible unit test):

- A real `SqlException` (18456/18452/18451) cannot be constructed without the driver internals; the number check in `SqlLoginCircuitBreaker.IsLoginFailure` and `ClassifyOpenException` is covered only through `SqlServerWindowsAuthException`. Covered by the opt-in real-server test (section 1.6).
- `SqlConnectionFactory` wiring of the breaker (check before `PrepareConnection`, record, clear) needs a reachable SQL Server; the breaker class is tested in isolation.
- The 30 s Warning window of the catalog is not asserted (needs a time source; `Environment.TickCount64`).
- Managed NTLM against a real server (OPEN-2) and the 18452 case (OPEN-1).
- Integration tests were not run locally.

## 5. Phase 5: Code Review (csharp-code-reviewer)

| Finding | Decision |
|---------|----------|
| Log spam: the catalog logged a Warning for every table on every cache miss while a source is down. | Fixed: one Warning per data source and reason per 30 s, repeats at Debug (`NextLoadFailureLevel`); breaker-open retries at Debug; the breaker itself logs once on open. |
| `MaterializeNtlmUserFile` rewrote the file on every connection open (read, rewrite, move) next to gss-ntlmssp readers. | Fixed: an unchanged entry is not rewritten. |
| Provider created per open without a stable identity breaks SqlClient pooling. | Fixed (SEC-NTLM-06). |
| `NativeEnvironment.Set` does managed and native `getenv` plus a lock per variable per open. | Accepted: four variables at most, microseconds against a network login; writes only on change. |
| Breaker hot path: fingerprint hashing on every open. | Fixed by design: the fingerprint is computed only when an entry exists (`_entries.IsEmpty` early exit); the failure path is rare. |
| `LogLoadFailure` builds a list per failure. | Accepted: failure path only. |
| `SqlSourceDescriber.DescribeLogin` allocates a key array per call. | Accepted: probe path only. |
| C# 13 idioms: `Lock`, `LibraryImport`, collection and pattern matching used consistently; naming follows the surrounding code (`SqlServer*`, `Sql*`). | OK. |
| `ManagedNtlmSspiContextProvider` instances are never disposed by the factory (last `NegotiateAuthentication` is released by the GC). | Accepted: small, unchanged behaviour; follow-up could dispose after open. |
| Pre-existing typo `"Mechanismitched"` in the GSSAPI classification string match. | Accepted, not changed in this track (a match on a message fragment that never occurs; the other conditions of the branch still apply). Follow-up. |
| `ClassifyOpenException` for a SQL-login source (no integrated security) returns the raw `SqlException`; the breaker still recognizes 18456 through `FindSqlException`-style chain walking. | OK. |

## 6. Phase 6: Documentation (documentation-expert)

- [F-AUTH-04](../features/f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md): verified against the code; added the applying combination, `PasswordRef` rules per environment (as enforced by `SecretReferenceResolver` and `DefaultEnvironmentSecretProvider`), file modes and the Windows bind mount note, the login circuit breaker (section 3.7), probe login name and lock, the `setenv` thread-safety note and the verification gap. No progress or WIP notes.
- [F-API-09](../features/f-api-09-datasource-connectivity-testing.md): SQL section corrected (real probe, login name, retry lock).
- `docs/configuration-guide.md`: `UseManagedNtlm` and `LoginFailureCooldownSeconds` noted at the `WindowsAuth` example.
- Searched `docs/`, `README.md`, Dockerfiles for the old fake SQL probe (`sql-engine`, TLS 1.3) and for a `UseManagedNtlm` default: none remain. The Dockerfiles still set `DOTNET_System_Net_Security_UseManagedNtlm=true`, which now matches the policy rather than overriding it.
- No new feature id: the track extends F-AUTH-04 and F-API-09, so `docs/features/README.md` needs no new registration.
- Master plan updated: [`00-master-plan-overview.md`](00-master-plan-overview.md), row `REQ-BUG-NTLM-01`.
