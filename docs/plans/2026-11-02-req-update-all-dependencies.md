# Requirements: Update All Dependencies

## 1. Executive Summary & Problem Statement

Keeping software dependencies current is vital for security, performance, and stability:
- **Security Vulnerabilities (Dependabot Alerts)**: GitHub Dependabot flagged 1 open high-severity vulnerability in `follow-redirects` under `benchmarks/load/configs/postgraphile/`.
- **.NET 10 Servicing Updates**: Microsoft released servicing version `10.0.14` across core runtime packages (`Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, `System.*`, `Microsoft.Data.Sqlite`).
- **Third-Party Libraries**: Newer minor/patch versions are available for `HotChocolate` (16.7.3), `ModelContextProtocol` (2.3.0), `OpenTelemetry` (1.20.1), `Microsoft.Garnet` (2.2.5), `Oracle.ManagedDataAccess.Core` (23.26.400), `StackExchange.Redis` (3.4.29), `BitFaster.Caching` (2.6.1), `DuckDB.NET.Data.Full` (1.5.6.1), and `FsCheck.Xunit` (3.6.0).
- **Package Lock Synchronization**: Because Autheris enforces `RestorePackagesWithLockFile = true` and `RestoreLockedMode = true` in CI, all `packages.lock.json` files must be regenerated with `--force-evaluate`.

## 2. Goals & Invariants

- **GOAL-01**: Upgrade all .NET 10 Microsoft servicing packages to `10.0.14` across all projects in `Autheris.sln` and isolated projects (`Autheris.Tests.Parity`, etc.).
- **GOAL-02**: Upgrade third-party NuGet packages to their latest stable non-breaking versions (`HotChocolate` 16.7.3, `ModelContextProtocol` 2.3.0, `OpenTelemetry` 1.20.1, `Microsoft.Garnet` 2.2.5, `Oracle.ManagedDataAccess.Core` 23.26.400, `StackExchange.Redis` 3.4.29, `BitFaster.Caching` 2.6.1, `DuckDB.NET.Data.Full` 1.5.6.1, `FsCheck.Xunit` 3.6.0).
- **GOAL-03**: Update npm dependencies in `benchmarks/load/configs/postgraphile` to resolve the Dependabot security advisory for `follow-redirects`.
- **GOAL-04**: Regenerate all `packages.lock.json` lock files with `--force-evaluate` ensuring CI locked-mode restore succeeds.
- **INV-01 (Zero-Trust & Fail-Closed)**: No security invariant or policy rule is weakened.
- **INV-02 (Build & Test Green)**: `dotnet build Autheris.sln` with `TreatWarningsAsErrors=true` succeeds with 0 warnings and 0 errors, and all unit, architecture, and integration test suites pass.

## 3. Scope of Upgrades

### 3.1 Microsoft .NET 10 Servicing (10.0.13 -> 10.0.14)
- `Microsoft.AspNetCore.Authentication.JwtBearer`
- `Microsoft.AspNetCore.Authentication.Negotiate`
- `Microsoft.AspNetCore.Mvc.Testing`
- `Microsoft.Data.Sqlite`
- `Microsoft.Extensions.Caching.Memory`
- `Microsoft.Extensions.Configuration.Abstractions`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Hosting.Abstractions`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Logging.Abstractions`
- `Microsoft.Extensions.Options`
- `Microsoft.Extensions.ObjectPool`
- `System.IO.Hashing`
- `System.Numerics.Tensors`
- `System.Security.Cryptography.Pkcs`

### 3.2 Third-Party NuGet Packages
- `HotChocolate.AspNetCore`, `HotChocolate.Data`, `HotChocolate.Language`: 16.6.8 -> 16.7.3
- `ModelContextProtocol.AspNetCore`, `ModelContextProtocol.Core`: 2.2.0 -> 2.3.0
- `OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Extensions.Hosting`: 1.19.1 -> 1.20.1
- `Microsoft.Garnet`: 2.2.1 -> 2.2.5
- `BitFaster.Caching`: 2.5.4 -> 2.6.1
- `DuckDB.NET.Data.Full`: 1.5.6 -> 1.5.6.1
- `Oracle.ManagedDataAccess.Core`: 23.26.301 -> 23.26.400
- `StackExchange.Redis`: 3.4.5 -> 3.4.29
- `FsCheck.Xunit`: 3.4.0 -> 3.6.0

### 3.3 Node / NPM Packages
- `benchmarks/load/configs/postgraphile/`: `npm update` / audit fix for `follow-redirects`.

## 4. Acceptance Criteria
- [ ] All specified packages updated in `*.csproj` files.
- [ ] All `packages.lock.json` files refreshed via `dotnet restore --force-evaluate`.
- [ ] `benchmarks/load/configs/postgraphile/package-lock.json` updated with patched `follow-redirects`.
- [ ] Solution builds with 0 warnings, 0 errors under `TreatWarningsAsErrors`.
- [ ] All test suites (Unit, Architecture, Parity) green.
