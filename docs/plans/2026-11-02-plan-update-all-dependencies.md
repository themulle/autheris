# Implementation Plan: Update All Dependencies

Input PRD: [`2026-11-02-req-update-all-dependencies.md`](2026-11-02-req-update-all-dependencies.md)  
Branch: `chore/update-all-dependencies`

## 1. Work Breakdown

- **WP1**: Merge PR #438 (ARCH-MNT-18 & 24) once CI checks finish, switch to main, and branch `chore/update-all-dependencies`.
- **WP2**: Upgrade Microsoft .NET 10 packages from `10.0.13` to `10.0.14` across all `*.csproj` files.
- **WP3**: Upgrade third-party NuGet packages (`HotChocolate`, `ModelContextProtocol`, `OpenTelemetry`, `Garnet`, `Oracle`, `Redis`, `BitFaster`, `DuckDB`, `FsCheck`).
- **WP4**: Run `dotnet restore Autheris.sln --force-evaluate` and for isolated projects (`tests/Autheris.Tests.Parity/Autheris.Tests.Parity.csproj`) to regenerate lock files.
- **WP5**: Update npm lockfiles in `benchmarks/load/configs/postgraphile/` to resolve the `follow-redirects` vulnerability.
- **WP6**: Verification: `dotnet build Autheris.sln`, `dotnet test tests/Autheris.Tests.Architecture`, `dotnet test tests/Autheris.Tests.Unit`, push, open PR, and verify CI.
