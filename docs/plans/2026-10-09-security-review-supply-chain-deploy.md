# Security Review: Build, Deploy and Supply Chain

Date: 2026-10-09. Branch: feat/ast-target-dialect-generator (HEAD a87fac0). Defensive, authorized review.

## 1. Scope and method

In scope: `Dockerfile`, `docker-compose.yml`, `docker-compose.dev.yml`, `deploy/` (Containerfiles, podman compose, nginx, SQL, scripts), `scripts/`, `benchmarks/load/`, `tools/`, `.github/`, `NuGet.config`, `Directory.Build.props`, csproj package versions, `appsettings*.json`, secrets in repo/tests/docs, startup validation in `GatewayServiceCollectionExtensions.cs`, TLS policy, logging, test-only handlers, test layout and CI gates.

Method: static reading plus targeted grep (credential patterns, private-key and token signatures, `IsDevelopment` gates). `dotnet list package --vulnerable` was attempted, but the sandbox is offline. It only reported "All projects are up-to-date for restore" and no advisory data was available. Package currency was therefore reasoned from versions and lock files only. Nothing was built or run.

"Confirmed" means seen directly in the cited file. "Suspected" means it depends on runtime behavior or external state that was not verified.

## 2. Summary

The posture is strong for a repository of this kind:

- Actions and base images are pinned by digest or SHA.
- Lock files are committed and restore runs in locked mode.
- NuGet audit warnings are errors.
- The published image runs as non-root, in Production, loopback-bound by default.
- Production startup validators fail closed.
- Committed secrets are limited to placeholders and test fixtures.

No real credential, private key or live token was found in tracked files. There are no critical findings.

The main residual risks are:

- Supply-chain gating gaps: no image scan, no signing, no SAST, no secret scanning.
- Mutable image tags.
- The benchmark stack, which is in tension with the production validators and bakes dev-style settings into images.
- The test-only auth handler is compiled into production binaries; it is blocked only by runtime checks.

Counts: 0 critical, 1 high, 6 medium, 8 low/info.

## 3. Findings

| ID | Severity | Status | Location | Description | Fix |
|---|---|---|---|---|---|
| SC-01 | High | Confirmed | `.github/workflows/docker-publish.yml:75-98` | The image is pushed to GHCR with mutable tags (`latest`, `getting-started`) on every push to main. There is no vulnerability scan (Trivy/Grype), no cosign signature, and no gate on CI success. The workflow does re-run tests itself. Provenance and SBOM are attached (positive), but nothing verifies or enforces them. `docker-compose.yml:13` consumes the mutable `getting-started` tag, so consumers cannot pin the image. | Add an image scan step that fails on high/critical findings. Sign with cosign (keyless OIDC). Publish a digest and document `image@sha256` pinning in compose. Push `latest` only from `v*` tags. |
| SC-02 | Medium | Confirmed | `.github/workflows/release.yml:36-80` | Release binaries are built with `contents: write`. `SHA256SUMS` is uploaded in the same release, so it gives no integrity guarantee if the release is tampered with. There is no signing and no attestation (`actions/attest-build-provenance`). Self-contained single-file builds for four RIDs ship without an SBOM. | Add build provenance attestation and a detached signature (cosign/minisign). Generate an SBOM. Use the narrowest permissions per job (`id-token`, `attestations`). |
| SC-03 | Medium | Confirmed | `.github/workflows/ci.yml:9-36` | The CI gates cover locked restore, vulnerability audit and tests only. There is no CodeQL/SAST, no secret scanning (gitleaks), no dependency-review on PRs, no Dockerfile/IaC lint, and no coverage threshold. `coverlet.collector` is referenced (`tests/Autheris.Tests.Integration/*.csproj:12`) but CI never collects or enforces coverage. There is no `CODEOWNERS` and no `SECURITY.md`. Branch protection could not be verified. | Add CodeQL (C#), gitleaks and `actions/dependency-review-action`. Collect coverage with a floor on security namespaces. Add `CODEOWNERS` for `src/Autheris.Api/Security/**`, `.github/**`, `deploy/**`. Add `SECURITY.md`. |
| SC-04 | Medium | Confirmed | `src/Autheris.Api/Security/TestAuthHandler.cs:11-60`; `GatewayServiceCollectionExtensions.cs:777-784, 122-123` | `TestAuthHandler` (header-driven identity via `X-Test-User-Sid`, `X-Test-AppId`, `X-Test-Tenant`) is compiled into the production assembly. It is protected by defense in depth: registration only when `IsDevelopment()` (line 778), an options validator (line 122), a runtime check inside `HandleAuthenticateAsync` (line 32), and the container-Development opt-in guard (line 1243). A misconfigured `ASPNETCORE_ENVIRONMENT=Development` plus `AUTHERIS_ALLOW_DEV_IN_CONTAINER=true` remains a full auth bypass. | Move the handler to a test or Dev-only assembly, or wrap it in `#if DEBUG` / exclude it from Release publish. Keep the runtime guards as a second layer. |
| SC-05 | Medium | Confirmed | `benchmarks/load/docker/Dockerfile.gql:29-37` | The benchmark gateway image sets `ASPNETCORE_ENVIRONMENT=Development`, runs as root (no `USER`), and ships `appsettings.bench.json` only as `appsettings.Production.json` (line 37), which Development never loads. The bench config enables `EnableTestAuthHandler` and `danger_bypass_consent_checks` (`appsettings.bench.json:32,44`). Port 5000 is exposed. If the container is reachable beyond loopback (`GATEWAY_BIND_IP` override), the result is unauthenticated, consent-bypassed access. The SEC C-04 guard will probably abort startup without the opt-in, so the image may be non-functional as written. | Add a non-root `USER`. Do not default to Development. Mark the image clearly as benchmark-only and keep it off any registry. Keep the `127.0.0.1` bind defaults. Fix the `Development`/`Production` file mismatch. |
| SC-06 | Medium | Confirmed | `deploy/containers/gqlgateway-api/Containerfile:50-52` | The benchmark `appsettings.Benchmark.json` is copied to `appsettings.json`, `appsettings.Production.json` and `appsettings.Development.json`. That file contains `"Insecure.warn_allow_unsigned_s3_requests": true` (line ~89), `RequireKerberosOnly=false`, and `TrustServerCertificate=True` for the CRM connection (`podman-compose.yaml:242`). The Postgres connection string has no `SSL Mode` (`podman-compose.yaml:241`). `ConnectionTlsPolicy.Validate` (`ConnectionTlsPolicy.cs:20-47`, applied at `GatewayServiceCollectionExtensions.cs:1495-1500`) should reject both in Production, so the stack either fails closed at startup or something else relaxes the check. | Confirm how the benchmark stack starts. Provide the TLS-relaxing flag explicitly (`danger_allow_insecure_transport`), logged as DANGER, or give the benchmark containers proper TLS. Do not copy the same file to a Development name. |
| SC-07 | Medium | Confirmed | `deploy/podman-compose.yaml:230-238` | One secret (`GATEWAY_HMAC_SECRET`) is reused as the masking HMAC key, the OpenMetadata webhook secret, the Catalog webhook secret, the ITSM webhook secret, and `GQL_PROD_BENCHMARK_SECRET_KEY`. Compromise of any one integration compromises pseudonymization (masking) integrity as well. Secrets are passed as environment variables, which are visible via `inspect` and `/proc`. | Use a distinct secret per purpose. Prefer file/secret mounts (`*_FILE`, podman secrets) or the existing `...KeyVaultRef` mechanism over env vars. |
| SC-08 | Medium | Confirmed | `deploy/containers/reverse-proxy/nginx.conf:89-158` | The proxy listens on plain HTTP with no TLS. `/metrics` (line 109-113) is proxied unauthenticated through nginx, although the gateway applies `RequireAuthorization` on `MapMetrics` (`GatewayApplicationBuilderExtensions.cs:352`). The nginx role-header geo block trusts the whole `10.89.77.0/24` network, so any container in that subnet can set `X-Benchmark-Role: Admin` and receive `GovernanceAdmin` (lines 33-67). It is a mapping that depends on the network boundary and is documented as benchmark-only. | Keep the compose network private and prevent other containers from joining it. Remove the Admin mapping or restrict it to loopback. Add TLS or state clearly that it must never be internet-facing. |
| SC-09 | Low | Confirmed | `Dockerfile:34-39` | The main image has no `HEALTHCHECK` and no read-only root filesystem recommendation. `docker-compose.yml:12-26` sets no `cap_drop: [ALL]`, no `security_opt: no-new-privileges:true`, no `read_only: true`, no memory/CPU/pids limits. | Add `cap_drop: ALL`, `no-new-privileges`, `read_only: true` with tmpfs `/tmp`, resource limits and a healthcheck (`/health/live`). |
| SC-10 | Low | Confirmed | `deploy/containers/load-generator/Containerfile:5`; `deploy/podman-compose.yaml:30,57,127,202,317,337`; `deploy/podman-compose.openmetadata.yaml:21,59`; `benchmarks/load/docker/docker-compose.yml:12,54` | Several images are unpinned by digest: `grafana/k6:latest` (mutable), Postgres `16-alpine`, Redis `7-alpine`, Prometheus `v2.54.1`, Grafana `11.2.0`, OpenSearch `2.11.0`, OpenMetadata `1.5.0`, Hasura `v2.44.0`. MinIO `RELEASE.2024-10-02` and OpenSearch 2.11.0 are old and likely carry unpatched advisories (suspected, not verified). OpenSearch runs with `DISABLE_SECURITY_PLUGIN=true` (`podman-compose.openmetadata.yaml:27`) and port 9200 published. The `sqlserver` and `mock-extensions` Containerfiles use tags or no digest. | Pin by digest, replace `:latest`, update stale images, and remove the host port publish for OpenSearch. |
| SC-11 | Low | Confirmed | `deploy/containers/mock-extensions/Containerfile:5-18`; `deploy/containers/lakehouse-seed/Containerfile`; `deploy/containers/sqlserver/Containerfile:3`; `deploy/containers/reverse-proxy/Containerfile` | The mock-extensions, lakehouse-seed and load-generator containers run as root (no `USER`). The SQL Server init container switches to `USER root` before reverting to `mssql`. `MSSQL_SA_PASSWORD` is passed via env and on the `sqlcmd -P` command line (`entrypoint.sh:12,21`; `podman-compose.yaml:117`), visible in process listings. | Add non-root users. Use `SQLCMDPASSWORD` instead of `-P`. |
| SC-12 | Low | Confirmed | `deploy/containers/gqlgateway-api/Containerfile:30`; `benchmarks/load/docker/Dockerfile.gql:15` | The benchmark build uses `dotnet restore` without `--locked-mode` and publishes with `/p:TreatWarningsAsErrors=false`, so `NU190x` vulnerability errors from `Directory.Build.props:15` are downgraded and the lock files are not enforced inside the image build. The build also stages sources from outside the repo (`stage_sources.sh:7-12`, e.g. `/root/gql`), so image contents are not tied to a commit. | Use `--locked-mode`, keep warnings-as-errors, and build from the committed tree. |
| SC-13 | Low | Confirmed | `deploy/scripts/seed_openmetadata.py:148`; `src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.cs:71`; `src/Autheris.Extensions/DataCatalog/PurviewDataCatalogClient.cs:133`; `deploy/containers/gqlgateway-api/appsettings.Benchmark.json:83,104,109,115` | Hardcoded dev/bench credentials exist: `Password123!` (OpenMetadata seed), `sa`/`Autheris_Dev_Passw0rd!` (governance repo dev fallback), `purview-dev-mock-bearer-token`, and `bench-*-token-2026` values. All are gated: the code fallbacks apply only in Development/Test (`SqlServerGovernanceRepository.cs:65-72`, `PurviewDataCatalogClient.cs:125-133`) and the bench values target mock services. None is a real secret. | Replace with generated values or placeholders where practical, so scanners stay quiet and nobody copies them. Add a gitleaks allowlist for the intentional ones. |
| SC-14 | Low | Confirmed | `README.md:535,544`; `docs/configuration-guide.md:474-475,915,1011`; `docs/extensions/lakehouse-connector-guide.md:63`; `docs/features/f-sql-02-governed-stored-procedures.md:368,380` | Documentation contains copy-pasteable weak examples: `SecretPassword123!`, `minioadmin`, `StrongPassword123!`, `UltraSecureProductionPassword!`, `OM-WEBHOOK-HMAC-SECRET-2026`. The last one looks like a production password and may be copied. | Use obvious placeholders (`<set-me>`) and add a "generate with `openssl rand`" note. |
| SC-15 | Low | Confirmed | `.github/dependabot.yml:1-26` | Dependabot coverage gaps: no entries for the other Containerfile directories (`deploy/containers/*`, `benchmarks/load/docker`), pip (`deploy/containers/*/*.py` has an unpinned `boto3==1.43.108` only in `lakehouse-seed/Containerfile:10`), or the `tools/*` and `benchmarks/*` NuGet projects. No grouped/security-only update policy. | Add the missing ecosystems and directories. |
| SC-16 | Info | Suspected | `src/Autheris.Api/Assets/swagger-ui/README.txt:1` | `swagger-ui-dist 5.18.2` is vendored and shipped in the production binary. It is not covered by NuGet audit or Dependabot, and it has no CSP-independent integrity check. Newer 5.x releases may fix advisories. | Track it with a pinned npm manifest or a Dependabot entry, and re-vendor on each release. |
| SC-17 | Info | Suspected | `src/Autheris.Api/Properties/launchSettings.json:6-18`; `Directory.Build.props:14` | `NuGetAuditMode=all` with `NuGetAuditLevel=moderate` gives good coverage, but the vulnerability-audit step in `ci.yml:29-33` greps a message string that can change between SDK versions, so the gate can silently stop failing. The dev launch profile uses `Development` on `localhost` only (fine). | Parse `--format json` output instead of grepping text. |
| SC-18 | Info | Confirmed | `benchmarks/load/hcloud/cloud-init.yaml:65-92` | The cloud-init script installs Docker, NodeSource and dotnet-install via downloaded scripts without checksums or signature checks (documented in comments, no `curl | bash`). Hetzner firewalls are not created by the provisioning scripts. | Pin versions, verify checksums where available, add a firewall restricting the database and gateway ports to the private network. |

## 4. Package versions

All `.csproj` versions are exact pins and every project has a `packages.lock.json` (`src/*`, `tests/*`, `tools/*`, `benchmarks/*`), with locked restore in CI (`Directory.Build.props:12-13`, `ci.yml:25`). Offline, vulnerability status could not be confirmed. Items worth checking in an online run:

- `Microsoft.Data.SqlClient 7.1.1`, `Npgsql 10.0.3`, `StackExchange.Redis 3.3.1`, `Microsoft.Garnet 2.2.0`, `Konscious.Security.Cryptography.Argon2 1.3.1`.
- `YamlDotNet 18.1.0`, `Casbin.NET 2.21.3`, `DuckDB.NET.Data.Full 1.5.6`, `MemoryPack 1.21.4`, `Antlr4.Runtime.Standard 4.13.1`.
- `System.ComponentModel.Annotations 5.0.0` (very old, `src/Autheris.Domain`, low risk).
- Test packages: `Oracle.ManagedDataAccess.Core 23.7.0`, `xunit 2.9.3` (xunit v2, superseded by v3).

`NuGet.config` clears sources, uses nuget.org only, and has package source mapping `*`. This is good. Mapping is trivial, so there is no dependency-confusion protection against a later added private feed. `Directory.Build.props` also suppresses a large set of analyzer warnings (`NoWarn`, line 17), which weakens `AnalysisLevel=latest-recommended`.

## 5. Positive controls

- Workflow actions are pinned to full commit SHAs (`ci.yml:17,20`, `docker-publish.yml:24,27,45,48,90`, `release.yml:23,26,76`).
- `permissions: read-all` on CI and policy lint; `contents: read` plus `packages: write` only on publish.
- The workflow tag input is validated with a regex and passed via `env` (`docker-publish.yml:56-68`), which prevents script injection.
- SLSA provenance (`mode=max`) and an SBOM are attached to the image (`docker-publish.yml:96-97`).
- Base images in the Dockerfile and Containerfiles are digest-pinned (`Dockerfile:6`, `gqlgateway-api/Containerfile:7,33`, Python/nginx images).
- The root `Dockerfile` defaults to Production, uses numeric non-root `USER $APP_UID`, exposes only 8080, and has no TLS or secrets baked in. Garnet is bound to `127.0.0.1` (`Dockerfile:22-24`).
- `.dockerignore` excludes `.env`, keys, DBs, `.git`, `tests`, `docs`, `deploy`.
- Compose files bind ports to `127.0.0.1`, require secrets via `${VAR:?}`, and `.env` is git-ignored (`.gitignore:81-84`, `deploy/.gitignore`). `generate-env.sh` writes random secrets with `umask 077`. No tracked `.env`, `.pem`, `.key` or `.pfx` file exists.
- Production fail-closed startup validation: test auth only in Development, anonymous access, untrusted certificates, wildcard or non-HTTPS CORS origins, ForwardAuth secret and trusted-proxy requirements, HMAC key vault reference, plugin integrity manifest, demo ReBAC seeds, and the Development-in-container guard (`GatewayServiceCollectionExtensions.cs:100-170, 1240-1250`).
- Database TLS policy: Npgsql `VerifyCA/VerifyFull`, SQL Server `Encrypt` and no `TrustServerCertificate` outside Development (`ConnectionTlsPolicy.cs`), also applied to the governance DB (`SqlServerGovernanceRepository.cs:76-87`).
- Outbound HTTP: `DangerousAcceptAnyServerCertificateValidator` only when `danger_allow_untrusted_certificates` is set, and that is rejected outside Development (`GatewayServiceCollectionExtensions.cs:362-371, 1489-1491`).
- `SensitiveLogPropertyEnricher` redacts query strings and URLs, with framework request logging at Warning (`Logging/SensitiveLogPropertyEnricher.cs`). `ForwardAuth` log messages never print the secret. `/metrics` requires authorization (`GatewayApplicationBuilderExtensions.cs:352`). `ErrorSanitizingFilter` and the verbose-error switch are tied to Development.
- Nginx strips client-supplied identity headers (`x-autheris-*`, `X-Forwarded-Tenant`) and sets `X-Forwarded-For` from `$remote_addr` (`nginx.conf:123,141-146`).
- Benchmark secrets are generated per run and never committed (`benchmarks/load/scripts/lib_secrets.sh`). Hasura console and dev mode are off.
- Dependabot is configured for Actions, NuGet, Docker and two npm projects. `npm ci --ignore-scripts` is used in `Dockerfile.apollo`.
- A Casbin policy lint workflow gates policy changes (`policy-lint.yml`).
- `.gitattributes` enforces LF endings; scripts include a history-clean tool for leaked secrets (`scripts/dep12-clean-git-history.*`).

## 6. Test-coverage assessment

Layout: `tests/Autheris.Tests.Unit` (284 .cs files), `Autheris.Tests.Integration` (48, Testcontainers for PostgreSQL, SQL Server, Oracle), `Autheris.Extensions.Tests` (18), `TrinoSqlEngine.Tests` (33), `Autheris.Tests.Architecture` (1 file, NetArchTest). No `Skip =` attributes were found.

Well covered by name (references found in tests): `TestAuthHandler` (28 files), `CasbinEnforcementService` (19), `TenantResolutionMiddleware` (6), `BasicAuthAttemptGuard` (4), `EnvoyExtAuthz` (3), `McpEndpoints` (3), `AuditWormExportService` (3), `ConnectionTlsPolicy` (2), `SecureOutboundHttp` (2), `JwtSocketTokenValidator` (2). There are dedicated security suites (`ComprehensiveSecurityAttackVectorTests`, `SecurityFindingsRemediationTests`, `SecurityReview20261002*Tests`, `GovernedDataPathsG4Tests`, `ArrowExportSecurityTests`, `CloudEventWebhookSecurityTests`).

Thin by name (a single reference each, so check depth): `ForwardAuthAuthenticationHandler`, `ForwardAuthSecretStartupValidator`, `EntraTokenPolicy`, `PasswordHasher`, `SecretReferenceResolver`, `RateLimitingMiddleware`, `SensitiveLogPropertyEnricher`, `LegacySwitchGuard`. `TokenRevocationMiddleware` has no direct reference by that name (only indirect hits in three files that mention token revocation); treat it as a likely gap (suspected).

Gaps:

- **No deploy/IaC tests.** Nothing verifies `Dockerfile`, compose files, nginx config or `appsettings*.json` (for example that the shipped `appsettings.json` has no secrets, or that the image runs non-root). Add a config-lint test, hadolint, and a compose-policy check.
- **No coverage measurement.** CI never runs coverlet and there is no threshold (SC-03).
- **Integration tests need containers.** They depend on Docker on the runner, and the repo notes a podman host override locally. Check that the CI job actually executes them rather than silently skipping on failure.
- **Architecture tests are one file.** Layer rules exist, but there is no rule such as "test-only types must not be referenced from `Autheris.Api` in Release" (see SC-04).
- **No fuzz or dependency-confusion tests** for the SQL/AST path beyond the property tests that exist (`FsCheck`).
- **CI gating is not provably required.** Without a verified branch protection rule, failing tests would not block a merge to main.
