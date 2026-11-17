# Plan: upgrade the solution from .NET 10 to .NET 11 (`PLAN-NET11`)

**Date:** 2026-11-16 · **Phase:** 2 (Solution Architect) · **Origin plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md) (Active Tracks rank 25, Plan Matrix)
**Base:** `origin/main` @ `8ed5cef4a` (v1.8.0 changelog, migration `0022_docs_store`) · **Ids:** work packages `WP-NET11-00..14`, risks `R-NET11-*`, proposed security test areas `S-NET11-*`, owner questions `Q-NET11-*`
**Sources:** every external claim carries a source id `[Sn]` (list at the end). Claims I could not verify are marked **OPEN**.

---

## PRD

**Goal.** Move every project, container image, CI workflow and gate from .NET 10 (SDK `10.0.401` pinned, runtime image `aspnet:10.0.15`) to .NET 11 (SDK `11.0.1xx`, runtime `11.0.x`) in one reviewable, revertible flip PR, with all release gates green on the branch before merge.

**Non-goals.**
- Adopting C# 15 features (unions, `closed`, collection `with(...)` arguments) or new .NET 11 APIs. A later plan may do that once Stryker and CodeQL support C# 15 (see R-NET11-3, R-NET11-4).
- Multi-targeting `net10.0;net11.0` (decision 1.3).
- Switching base image family (Alpine, chiseled, Azure Linux) or adding Native AOT / trimming.
- Re-measuring the frozen legacy side of the G8 baseline (impossible: the legacy rewriter is deleted).

**Key facts (verified 2026-11-16).**
- .NET 11.0.0 is GA since 2026-11-10 with SDK `11.0.100`; RC1 2026-09-08, RC2 2026-10-13, RC3 2026-10-25 [S1][S2][S4].
- .NET 11 is an **STS** release with end of support **2028-11-14**; .NET 10 is **LTS** with end of support **2028-11-14**, the same day [S1]. Staying on .NET 10 costs no support time. .NET 8 and .NET 9 reached end of support on 2026-11-10 [S1].
- 11.0.0 fixed 22 CVEs; .NET 10 got 10.0.15 / SDK 10.0.404 on the same day [S1][S2].
- Default container images are Ubuntu 26.04 "resolute" (`11.0`, `11.0.0`, `latest`); Alpine 3.24, Azure Linux 3.0/4.0 and resolute-chiseled variants exist; **no `noble` (Ubuntu 24.04) variant for 11.0** [S8].
- CodeQL documents C# up to 14 and .NET up to 10; a .NET 11 issue is open (buildless mode; we use a manual build) [S10]. **OPEN** whether CodeQL extracts C# 15 / net11 builds cleanly.
- Stryker.NET 5.1.0 (2026-11-10) targets the .NET 10 runtime; .NET 11 support is not announced, the dependency bump to .NET 11 is open upstream [S11]. **OPEN** whether Stryker mutates net11 projects.
- Dependabot already opened PR #760 bumping `dotnet/sdk` 10.0.403 -> 11.0.100 and `dotnet/aspnet` 10.0.15 -> 11.0.0 in `deploy/containers/gqlgateway-api` and `benchmarks/load/docker`, although `.github/dependabot.yml` ignores semver-major for `mcr.microsoft.com/dotnet/*`: the ignore names do not match the names Dependabot uses (`dotnet/sdk`, `dotnet/aspnet`). The "no .NET 11 major bumps" rule is therefore not enforced today.

**Risks (summary; full register in section 4).** Toolchain gaps (CodeQL, Stryker), base OS change Noble -> Resolute (apt package names, Trivy ignore list, image size), NU1510 warnings-as-errors for nine `Microsoft.Extensions.*` packages that moved into the shared framework, runtime behaviour changes that touch fail-closed paths (configuration empty strings, TLS AIA, Kestrel strictness), G7/G8 gate drift, lockfile churn colliding with other PRs.

**Timing recommendation: wait for the first servicing release, then upgrade behind explicit GO criteria. Do not upgrade on 11.0.0, and do not wait for .NET 12.**
Reasons: (a) STS and LTS end on the same day, so there is no support penalty and no support gain; the decision rests on engineering cost versus benefit. (b) The product has no production deployment and breaking changes are accepted, so early adoption is cheap compared with doing it after the first customer. (c) Two release gates (CodeQL, Stryker G7) do not yet state .NET 11 support, and 11.0.0 shipped with 22 CVE fixes. Waiting for 11.0.1 gives the tools a servicing cycle. (d) `main` just absorbed the Wave 7 rollup (PR #756) and the nightlies still have open issues (#741, #745, #746). A platform flip on top would blur failure attribution.

**GO criteria (all must hold; Orchestrator checks them in WP-NET11-00):**
- **GO-1** 11.0.1 is published in [S1] and its `aspnet`/`sdk` resolute images are on MCR. Expected on the December 2026 Patch Tuesday, 2026-12-08; this date is an inference from the monthly cadence and is **OPEN**.
- **GO-2** CodeQL lists .NET 11 / C# 15 support [S10], **or** a `codeql.yml` run dispatched on the upgrade branch completes with no extractor errors and a database whose file count is at least 95% of the latest `main` run.
- **GO-3** A `sql-compiler-gate-nightly.yml` run dispatched on the branch completes all 16 G7 shards and passes the aggregate ratchet (72%) and the mutant floor (3200). Alternatively, a Stryker.NET release states .NET 11 support [S11].
- **GO-4** `main` is quiet: no DDL PR, rollup or other `*.csproj`/`packages.lock.json` PR in flight; `parity-nightly` and `sql-compiler-gate-nightly` are green on `main` for 3 consecutive nights.
- **Fallback:** if GO-1..GO-4 do not all hold by **2027-01-31**, re-plan. Either keep .NET 10 LTS until .NET 12 (expected November 2027 by cadence; **OPEN**) or upgrade with a documented gate waiver from the owner.

---

## 1. Target and scope

1. **TFM:** `net11.0` for all 25 projects (`src/` 8, `modules/` 2, `tests/` 11, `tools/` 3, `benchmarks/` 1). Today `Directory.Build.props` sets `net10.0` **and** all 24 project files repeat `<TargetFramework>net10.0</TargetFramework>`. Decision: remove the 24 per-project lines and keep the single value in `Directory.Build.props`. A new ratchet test enforces this (WP-NET11-02).
2. **Language:** keep `<LangVersion>latest</LangVersion>` (resolves to C# 15 on SDK 11). Keep `TreatWarningsAsErrors=true`, `Nullable=enable` and `EnforceCodeStyleInBuild=true`. Pin `AnalysisLevel` to `10.0-recommended` in the flip PR, then raise it to `latest-recommended` in a separate PR (WP-NET11-09). New .NET 11 CA rules then never block the flip.
3. **No multi-targeting.** It doubles build and test time against the < 30 min PR median budget (`benchmarks/budgets.json`, OD-6/OD-37). The NU1510 package removals would need TFM conditions, and the 23 lockfiles would carry two TFM sections each. There are no production consumers of `Autheris.Extensions` / `Autheris.Modules.Abstractions` (Q-NET11-3).
4. **SDK:** `global.json` `11.0.1xx` (the patch present at execution time), `rollForward: latestPatch` (unchanged), no prerelease.
5. **Images:** `mcr.microsoft.com/dotnet/aspnet:11.0.<patch>-resolute@sha256:<index digest>` and `dotnet/sdk:11.0.1xx-resolute@sha256:<index digest>`. Keep a full Ubuntu image because the runtime stage installs Kerberos/NTLM packages via apt (`Dockerfile`), so chiseled is not possible. Do not use Alpine: Trivy documents Alpine only up to 3.22 [S12], and musl adds risk for the Oracle and DuckDB native assets.
6. **One flip PR.** WP-NET11-01..08 and 13 ship as one PR, `chore(platform): upgrade to .NET 11`. Locked-mode restore, the published runtime image and the SDK pin must change together, or CI or the image breaks. WP-NET11-00a ships before it; WP-NET11-09..11 and 14 follow after it.

## 2. Dependency matrix

Versions are from the NuGet API on 2026-11-16 [S13]. "Asset" is the best TFM asset a `net11.0` consumer gets.

| Package (current) | Target | Asset | Status |
|---|---|---|---|
| `Microsoft.Extensions.{Configuration.Abstractions, DependencyInjection.Abstractions, Hosting.Abstractions, Logging.Abstractions, Options}` 10.0.14 (in `Autheris.Application`, `Autheris.Extensions`) | **remove** | shared framework | **Blocker if left in:** NU1510 under `TreatWarningsAsErrors` [S14] |
| `Microsoft.Extensions.{Caching.Memory, Http, ObjectPool}`, `Microsoft.AspNetCore.Authentication.{JwtBearer, Negotiate}`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Data.Sqlite`, `System.IO.Hashing`, `System.Numerics.Tensors`, `System.Security.Cryptography.Pkcs` 10.0.14 | 11.0.0 (or 11.0.x) | net11.0 | Ready. `Mvc.Testing` 11 and `Sqlite` 11 ship **only** net11.0, so they must move in the flip PR |
| `Microsoft.Extensions.TimeProvider.Testing` 10.11.0 | 11.0.0 | net11.0 | Ready |
| `System.ComponentModel.Annotations` 5.0.0 | check NU1510 | framework | Remove if NU1510 fires |
| `HotChocolate.*` 16.7.3 | 16.7.5 | net11.0 | Ready |
| `OpenTelemetry*` 1.20.1 | 1.21.0 | net11.0 | Ready (semantic-convention tag change, S-NET11-6) |
| `Npgsql` 10.0.3 | keep | net10.0 | Runs on net11; no Npgsql 11 yet. Covered by integration shards |
| `Microsoft.Data.SqlClient` 7.1.1 | 7.1.2 | net9.0 | Runs on net11. **OPEN:** confirm the ASP.NET Core "SqlClient AD authentication moved" note [S6] is the 7.x split we already have |
| `Oracle.ManagedDataAccess.Core` 23.26.400 | keep | net8.0 | **OPEN:** runtime check via `oracle-smoke` |
| `DuckDB.NET.Data.Full` 1.5.6.1 | keep | net10.0 | Native library; **OPEN:** glibc on Ubuntu 26.04 via integration shards and footprint |
| `Antlr4.Runtime.Standard` 4.13.1 / `Antlr4BuildTasks` 12.14.0 | keep | netstandard2.0 | Ready (build task needs Java, unchanged) |
| `ModelContextProtocol.*` 2.3.0 | 2.4.0 optional | net10.0 | Ready |
| `Apache.Arrow` 23.0.0, `Parquet.Net` 6.1.0, `MemoryPack` 1.21.4, `StackExchange.Redis` 3.4.29, `BitFaster.Caching`, `FluentValidation`, `YamlDotNet`, `Konscious.*Argon2`, `prometheus-net.AspNetCore` 8.2.1 (net6.0) | keep | <= net10.0 | Runs on net11 by forward compatibility; covered by unit and integration tests |
| `xunit` 2.9.3, `xunit.runner.visualstudio` 4.0.1, `Microsoft.NET.Test.Sdk` 18.11.0, `Shouldly`, `NSubstitute`, `FsCheck.Xunit`, `coverlet.collector`, `NetArchTest.Rules`, `SharpFuzz` | Test.Sdk 18.12.0; rest keep | net8.0 / netstandard | Ready. VSTest drops Newtonsoft.Json [S5]: 0 usages in `*.cs` |
| `Testcontainers.*` 4.16.0 | 4.17.0 optional | net10.0 | Ready |
| `Microsoft.CodeAnalysis.CSharp` 4.14.0 (Architecture tests) | 5.12.0 | net10.0 | Needed before any C# 15 syntax appears (the parser would reject it) |
| `BenchmarkDotNet` 0.15.8 | keep | net8.0 | **OPEN:** G8 runs in-process (`benchmarks/baselines/sql-compiler-legacy.json`), so no toolchain moniker is needed; confirm on the branch run |
| `dotnet-stryker` 5.0.0 (`.config/dotnet-tools.json`, `rollForward: false`) | 5.1.0 | tool on .NET 10 runtime | **Gate (GO-3).** The tool needs the .NET 10 runtime on the runner (WP-NET11-06) |
| CodeQL action v4.39.1 | latest | n/a | **Gate (GO-2)** [S10] |
| Trivy (fs and image scans) | unchanged | n/a | Ready: Ubuntu "all versions supported by Canonical" [S12] |
| `actions/setup-dotnet` v6.0.0 | v6.1.0 (SHA-pinned) | n/a | Ready; `global-json-file` already used in every workflow [S9] |
| ONNX runtime, EF Core | n/a | n/a | Not referenced (0 hits in csproj/lockfiles); the EF Core 11 breaking-change list does not apply |

## 3. Work packages (in order)

Rules for every package: TDD Red-Green-Refactor where behaviour changes. Locally only filtered unit tests run, using the CLAUDE.md command with `-m:2 -nodeReuse:false`, plus the touched architecture ratchets. `Autheris.Tests.Integration` gets a compile-only check. Integration, parity, nightly, CodeQL and container jobs run on GitHub Actions. Local SDK 11 sits side by side with SDK 10 under `~/.dotnet` (`dotnet-install.sh --channel 11.0`). `global.json` selects it per worktree. Set `NUGET_SCRATCH=/work/nuget-scratch` for every build so SDK 10 and SDK 11 restores share one lock directory; SDK 11.0.200 moves the Linux lock directory [S5, nuget-locks-directory-linux].

**WP-NET11-00 (Orchestrator, before Phase 4): GO check.** Check GO-1..GO-4 and record the result in this file under "Execution log" (`docs/plans/` only). No code. Output: go/no-go and the exact SDK/runtime patch plus image index digests (`docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:11.0.<p>-resolute`).

**WP-NET11-00a (can merge now, independent): make the Dependabot rule effective.**
- `.github/dependabot.yml`: in both `docker` blocks, change `dependency-name` to `"dotnet/sdk"` and `"dotnet/aspnet"`, or add those names next to the existing ones. In the `nuget` block, add `ignore` entries with `update-types: ["version-update:semver-major"]` for `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, `Microsoft.Data.Sqlite`, `System.IO.Hashing`, `System.Numerics.Tensors` and `System.Security.Cryptography.Pkcs`. Their 11.x versions either drop net10.0 (`Mvc.Testing`, `Sqlite`) or must move together with the TFM.
- Close Dependabot PR #760 unmerged with a comment pointing to this plan. Its nginx/node parts come back next week.
- Test: none (config). Verification: the next Dependabot run opens no 11.x major PR.
- **Lifting the rule (decision):** the flip PR (WP-NET11-07) keeps the semver-major ignores. From then on they block .NET 12 instead of .NET 11. The owner rule becomes "runtime majors only through a planned upgrade PR". Patch and minor 11.0.x bumps flow normally from the day the flip merges.

**WP-NET11-01: SDK pin and local toolchain.** `global.json` -> `"version": "11.0.1xx"` (from WP-00), `rollForward: latestPatch`. `docs/developer-guide.md` line 23 -> ".NET SDK 11.0 (`net11.0`)", since reference docs describe the current system. Test: `dotnet --version` in the worktree prints `11.0.1xx`.

**WP-NET11-02: TFM flip, package bumps, lockfiles (same PR; sub-steps for one Hydrogen brief).**
- 02a `Directory.Build.props`: `<TargetFramework>net11.0</TargetFramework>`, `<AnalysisLevel>10.0-recommended</AnalysisLevel>` (temporary, WP-09 raises it). Delete `<TargetFramework>` from all 24 `*.csproj`.
  - New ratchet `tests/Autheris.Tests.Architecture/.../SingleTargetFrameworkRatchetTests.cs`: no `*.csproj` declares `TargetFramework(s)`, and the props value equals the `global.json` major. Filter: `--filter "FullyQualifiedName~SingleTargetFrameworkRatchetTests"`.
- 02b Package bumps per section 2, and removal of the eight shared-framework `Microsoft.Extensions.*` references (five packages) from `src/Autheris.Application/Autheris.Application.csproj` and `src/Autheris.Extensions/Autheris.Extensions.csproj`. Bump `Microsoft.CodeAnalysis.CSharp` to 5.12.0 in `tests/Autheris.Tests.Architecture`.
- 02c Path literals: `benchmarks/load/scripts/cold-start.sh` (`bin/Release/net10.0`), `benchmarks/load/scripts/measure-r2r-impact.sh` (inline `aspnet:10.0.13` -> the WP-05 tag), and `git grep -n 'net10\.0'` over `*.sh`, `*.py`, `*.yml`, `*.json` (excluding lockfiles) until empty.
- 02d **Lockfile regeneration.** Run `NUGET_PACKAGES=/work/nuget NUGET_SCRATCH=/work/nuget-scratch dotnet restore Autheris.sln --force-evaluate -m:1` once. Then verify with `dotnet restore Autheris.sln --locked-mode -m:1`. Never hand-edit a lockfile; commit all 23 `packages.lock.json` together with 02a/02b. The Docker build (`--locked-mode` in `deploy/containers/gqlgateway-api/Containerfile`) and CI (`RestoreLockedMode` on `GITHUB_ACTIONS`) prove the result.
- **Serialization rule (like one-DDL-PR):** only one lockfile-churning PR may be in flight. While the flip PR is open: hold Dependabot `nuget` PRs (merge #764 **before** the flip or close it), open no other PR that touches `*.csproj`, `Directory.Build.props` or `packages.lock.json`, and land no DDL PR (it would need a rebase onto net11 lockfiles anyway). After merge, every open branch rebases and runs 02d. The handover owner rules (section 2) get this line.
- Local check: compile `Autheris.Tests.Integration`, `tools/SqlGate.BenchCheck` and `tests/Autheris.Tests.SqlCompilerGate` with `--no-dependencies` after one `src` build.

**WP-NET11-03: compile fixes (driven by the build, no behaviour change).** Fix only errors the flip produces. Expected sources:
- NU1510 leftovers.
- New obsoletions SYSLIB0064 / SYSLIB0066 (0 hits for `RSACryptoServiceProvider` / `IStartupValidator` today) [S5].
- Roslyn 18.3-18.10 changes [S7]: span collection-expression safe-context, `nameof(this.)` in attributes, `with` in switch arms, `closed`/`union` contextual keywords (0 type declarations named so today).
- Options validation generator SYSLIB1205 [S5].

Each fix is the minimal source change. If an analyzer is wrong, a `NoWarn` entry with a justification comment needs Phase 5 approval. Unit test: the existing class covering the touched file, run filtered. Budget: stop and escalate to the architect if this exceeds 40 files.

**WP-NET11-04: runtime behaviour audit and pinning tests (TDD; one test class per item, Red on SDK 10 is not required, Green on 11).** Repo hits are from `git grep` on `main`.

| # | Change [S5]/[S6] | Repo touchpoint | Test class (unit unless noted) |
|---|---|---|---|
| a | Chained configuration: empty string overrides earlier provider | config binding of `Gateway:*` options; `GatewayStartupValidator` | `GatewayStartupValidatorGoldenTests` + new `EmptyConfigurationValueFailClosedTests` (Security) |
| b | `IHost.RunAsync/StopAsync` throw when a `BackgroundService` fails (23 `BackgroundService` files, default `StopHost`) | `TableCacheBackgroundWorker`, warmup, sweeps | new `BackgroundServiceFailureStopsHostTests` |
| c | DI scope disposal aggregates exceptions | disposal-asserting tests | adapt the failing tests found by the CI run |
| d | `DateOnly`/`TimeOnly.TryParse*` throw on invalid styles (9 files) | `ListParameterNormalizer`, `UsageEndpoints.Tariffs`, `GovernedAggregationService.Buckets` | `ListParameterNormalizerTests` (+ cases with styles from user input -> typed 400, never 500) |
| e | `Math.Round` / decimal-BigInteger conversions correctly rounded; saturating FP-to-small-int | 13 `Math.Round` files (analytics, catalog discovery, query analysis) | `GovernedAggregationServiceSecurityTests` boundary cases; parity nightly is the oracle |
| f | Tensor shape alignment / storage-layout validation | `VectorSearchIndex` (`System.Numerics.Tensors`) | `VectorSearchIndexTests` (empty index, zero vector, dimension mismatch) |
| g | ZIP entry CRC32 validation | `FileSystemUniversalDocStore` | new `FileSystemUniversalDocStoreZipTests` (corrupt entry -> typed failure) |
| h | Kestrel tighter HTTP compliance | ingress and header handling | integration: `tests/Autheris.Tests.Integration` HTTP security suites; adapt expected status codes only after the security review agrees |
| i | `SslStream` server AIA off; Linux `X509Chain` max two AIA fetches | `EnterpriseClaimsTransformation`, `GatewayStartupValidator`, outbound TLS (`SecureOutboundHttp`, DB drivers) | `EnterpriseClaimsTransformationDegradedTests` + new chain-without-intermediate case |
| j | Hosting emits OpenTelemetry HTTP semantic-convention tags; HttpClient high-cardinality metrics become observable counters | `TelemetryAttributes`, Prometheus dashboards | new `TelemetryTagRedactionTests` (Security) |
| k | `ConcurrencyLimiter` middleware removed | none: `FastSqlEngine` uses a `SemaphoreSlim` named `SharedParseConcurrencyLimiter` | no test (verified no-op) |

Integration and parity impact pointers: `tests/Autheris.Tests.Integration` (shards 1-6, Oracle, Spark), `tests/Autheris.Tests.Parity` (numeric rounding, item e). Hydrogen inspects and adapts both before CI runs, without executing them locally.

**WP-NET11-05: containers, digests, Trivy.**
- `Dockerfile`, `Dockerfile.production`, `Dockerfile.staging`: `FROM mcr.microsoft.com/dotnet/aspnet:11.0.<p>-resolute@sha256:<index>`.
- `deploy/containers/gqlgateway-api/Containerfile` and `benchmarks/load/docker/Dockerfile.gql`: the same, plus `sdk:11.0.1xx-resolute@sha256:<index>`.
- Re-check the apt block on Ubuntu 26.04. The `--only-upgrade install libpcre2-8-0 apt libapt-pkg6.0t64` line is Noble-specific: drop it, or rename the packages. `gss-ntlmssp`, `krb5-user`, `libgssapi-krb5-2`, `libkrb5-3`, `zlib1g` must exist (**OPEN**; the image build fails fast).
- Keep `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` and `DOTNET_System_Net_Security_UseManagedNtlm=true`. Check that `APP_UID=10001` still differs from the image's built-in `app` user.
- `.trivyignore`: delete the 15 Ubuntu 24.04 Noble base-image entries (libpcre2, libapt-pkg, perl-base, libc; lines 6-27, all Noble-specific). Re-run the `pr-container-scan` job. Any new Resolute finding needs the usual issue/CVE, justification and expiry format, and goes to Phase 3.
- Digest policy: pin the multi-arch **index** digest, never a per-arch digest; tag and digest change together; Dependabot keeps them fresh after the flip.
- Test: `docker-publish.yml` build plus `pr-container-scan` green; `footprint-budget.yml` within `coreImageUncompressedMB <= 325`, `coldStartP50Seconds <= 3.0` and `warmP99Milliseconds <= 3.47` (10% tolerance). If the Ubuntu 26.04 base pushes the image over budget, the fix is in this PR (trim apt) or Q-NET11-5.

**WP-NET11-06: CI workflows.** `setup-dotnet` reads `global.json` in all workflows, so SDK selection follows WP-01 automatically.
- Bump the `actions/setup-dotnet` pin to v6.1.0 (SHA) everywhere.
- In the Stryker jobs only (`sql-compiler-gate-nightly.yml`, jobs `sql-compiler-gate-g7` and `sql-compiler-gate-g7-aggregate` if it runs the tool), add `dotnet-version: '10.0.x'` next to `global-json-file`. The `dotnet-stryker` tool is pinned with `rollForward: false` and needs the .NET 10 runtime [S11]. Remove the line once a Stryker release targets .NET 11.
- No NuGet cache exists (`actions/cache` 0 hits), so there is nothing to re-key.
- `codeql.yml` is unchanged (manual `dotnet build`). If the extractor fails, GO-2 is not met: stop and do not waive silently.
- Check every `--filter`/`--results-directory` path that embeds `net10.0`.
- Test: the PR's `ci.yml` (build-and-test, integration shards 1-6, oracle-smoke, spark-integration-tests, helm-render, sql-compiler-gate) is green.

**WP-NET11-07: Dependabot after the flip.** No new edit beyond WP-00a. Confirm that the ignore rules now block 12.x. Note in the handover owner rules: "runtime major bumps only via a planned upgrade plan".

**WP-NET11-08: Helm and runtime configuration.**
- `deploy/helm/autheris`: no .NET-specific values (image tag falls back to `appVersion`). Keep the `ASPNETCORE_*` env.
- Review the probes' `initialDelaySeconds` and `startupProbe` against the cold-start result from WP-05.
- `deploy/podman-compose.yaml` and `docker-compose*.yml` build from the Dockerfiles, so nothing to change.
- New hardware floor: x86-64-v2 [S5, minimum-hardware-requirements]. Add one line to `deploy/README.md` system requirements (reference doc).
- Test: CI `helm-render`.

**WP-NET11-09 (follow-up PR, after merge): analyzer level.** `AnalysisLevel` back to `latest-recommended`. Fix new diagnostics in `src/`. Test and benchmark hits may go to `NoWarn` with a count in the existing comment, the same pattern as CS0108/CA2012. Unit: the classes touched, filtered.

**WP-NET11-10: G8 allocation baseline on .NET 11.**
- Run `sql-compiler-gate-nightly.yml` via `gh workflow run ... --ref <branch>` and download the `benchmarks/results` artifact.
- G8 compares the AST allocation with the frozen legacy column (`MaxAllocationRatio` 1.25) and with the frozen AST column (+10%, `MaxAllocationRegressionAgainstBaseline`) (`tools/SqlGate.BenchCheck/BenchEvaluator.cs`).
- Rule: the **legacy** columns never change. If the AST bytes per bucket **drop**, re-freeze the AST column downward in the flip PR: update `astAllocatedBytesPerOp`, append to `notes` ("re-frozen on .NET 11.0.p, CI runner, in-process") and set `machine`. Allocation is runtime-deterministic, so the CI measurement is the source.
- If any bucket **rises** beyond a limit, the flip PR must not loosen the limit. The architect writes a short addendum with the per-bucket delta and its cause (runtime vs. code). The **owner** decides whether to re-freeze upward or to fix allocations first.
- `ZeroGen2Buckets` and absolute P99 limits stay. Phase 5 checks that no `BenchModel.cs` threshold changed.

**WP-NET11-11: Stryker G7 on .NET 11.** Mutation scope (`stryker-config.json` `mutate`, `tools/sqlgate/g7-scope.sh`, `g7-weights.json`) is unchanged.
- Run G7 on the branch (all 16 shards).
- If the mutant count drops below `G7_MUTANT_FLOOR=3200` only because of compiler or runtime changes, or the score drops below the 72% ratchet, the owner decides (same rule as G8). `g7-weights.json` may be re-measured when the shard balance degrades by more than 20% wall time.
- Constraint until Stryker states C# 15 support: no C# 15 syntax inside the G7 `mutate` scope (`src/TrinoSqlEngine`). Phase 5 checks this.

**WP-NET11-12: branch acceptance on GitHub Actions** (wall-clock about one night). Dispatch on the flip branch: `parity-nightly.yml`, `sql-compiler-gate-nightly.yml` (G7, G8, G9 Databricks), `integration-nightly.yml` (Oracle, Stress, Spark), `simulation-nightly.yml`, `footprint-budget-nightly.yml`, `codeql.yml`, `trivy.yml`. The release `Nightly gate` reads only `--branch main`, so red branch runs do not block releases. Collect results in the PR description.

**WP-NET11-13: reference docs in the flip PR.** `docs/developer-guide.md` (SDK), `deploy/README.md` (base image, x86-64-v2), README prerequisites if they name .NET 10, and `THIRD-PARTY-NOTICES.md` if the package set changed. No progress words outside `docs/plans/`.

**WP-NET11-14 (after merge): watch and record.**
- Watch "CI Build & Test" on the merge commit and repair first.
- Before the next release tag, require 3 consecutive green `parity-nightly` and `sql-compiler-gate-nightly` runs on `main`.
- Update the master plan row and this file's execution log, and the handover owner rules (serialization line, Dependabot rule).
- Re-scan `.trivyignore` expiries.

## 4. Risk register and rollback

| Id | Risk | L/I | Mitigation |
|---|---|:---:|---|
| R-NET11-1 | NU1510 and new analyzer diagnostics break the warnings-as-errors build | H/M | Remove the five packages (WP-02b); pin `AnalysisLevel` 10.0 and raise it later (WP-09) |
| R-NET11-2 | Ubuntu 24.04 -> 26.04 base: apt names, glibc/ICU/OpenSSL versions, new Trivy findings, image size | H/M | WP-05 build and scan on the PR; footprint gate; Q-NET11-5 |
| R-NET11-3 | Stryker.NET cannot mutate net11/C# 15 code, so G7 is red and releases are blocked | M/H | GO-3 before merge; .NET 10 runtime side by side; no C# 15 syntax in G7 scope |
| R-NET11-4 | CodeQL extractor does not handle .NET 11, so security scanning is lost | M/H | GO-2; no silent waiver; Q-NET11-4 |
| R-NET11-5 | Runtime behaviour changes alter fail-closed paths (config empty strings, TLS AIA, Kestrel) or results (rounding: parity) | M/H | WP-04 pinning tests; Phase 3 review; parity nightly on the branch |
| R-NET11-6 | G8 allocation shifts, so the gate is red or is relaxed by an agent | M/M | WP-10 rule: tighten only; loosening needs the owner |
| R-NET11-7 | Lockfile churn collides with Dependabot/DDL/feature PRs | H/L | Serialization rule (WP-02d); merge or close #764 first; close #760 |
| R-NET11-8 | Mixed SDK 10/11 restores corrupt the shared `/work/nuget` on the local machine (lock directory moves in 11.0.200) | L/M | `NUGET_SCRATCH` fixed for all builds; two build slots unchanged |
| R-NET11-9 | Third-party native assets (DuckDB, Oracle, SNI) misbehave on net11 or Resolute | L/H | Integration shards, `oracle-smoke`, Spark and Lakehouse e2e on the branch |
| R-NET11-10 | CI duration grows past the 30 min median | L/M | Footprint `ciPrWallClockMedianMinutes` gate; compare 5 PR runs before and after |

**Rollback.** The flip is one merge commit. Revert it with `git revert -m 1 <merge-sha>` in a `revert/dotnet-11` PR; it goes through CI (about 30 min), with no `--admin`. This restores `global.json`, the props, all lockfiles, the 10.0.15 image digests and the `.trivyignore` entries at once. Published images and tags are immutable and unaffected, and there is no production deployment to roll back. Data compatibility: governance DB migrations are runtime-independent. **OPEN:** confirm that Data Protection keys written by .NET 11 stay readable by .NET 10 (format believed stable; test in WP-04 if Phase 3 requires it). Keep the .NET 10 digests in this file's execution log until 14 days after merge.

## 5. Test strategy and acceptance

- **Local (Hydrogen):** filtered unit classes named in WP-02/03/04, `SingleTargetFrameworkRatchetTests`, and touched architecture ratchets. Run `ReadOnlyTokenGetRouteTests` and `HostCompositionScopeValidationTests` in isolated invocations (handover section 3). Add the compile-only checks: `Autheris.Tests.Integration`, `tools/SqlGate.BenchCheck` and `tests/Autheris.Tests.SqlCompilerGate`. Never run the solution or unfiltered suites. At most 2 dotnet processes.
- **Snapshots:** DI composition snapshots and the security inventory may change with framework-registered services. Regenerate them with `AUTHERIS_UPDATE_SNAPSHOTS=1` / `AUTHERIS_UPDATE_SECURITY_INVENTORY=1` (stage both inventory files), then re-run clean.
- **GitHub Actions acceptance (all green on the flip branch before merge):**
  - `ci.yml`: build-and-test (full unit, architecture), integration shards 1-6, `oracle-smoke`, `spark-integration-tests`, `helm-render`, `sql-compiler-gate`.
  - `docker-publish.yml`: image build, `pr-container-scan`, `module-e2e`.
  - `footprint-budget.yml` and `codeql.yml` (GO-2).
  - The WP-12 dispatches: parity nightly; SQL compiler gate G7 (16 shards plus aggregate), G8, G9; integration nightly (Oracle, Spark, Stress); simulation nightly; Trivy.
- **After merge:** the same nightlies are green on `main` for 3 nights before the next release (the `release.yml` Nightly gate enforces the last run of parity and the SQL gate).

## 6. Security phase notes (input for Phase 3; binding `SEC-*` conditions come from the security expert)

- **S-NET11-1 Configuration empty strings** [S5 chained-configuration-empty-string]. An empty value from a later chained provider now wins. Required security settings (issuer, audience, TLS mode, grant `MaxDuration`, secrets) must treat empty or whitespace as missing and fail startup. Test: `[Trait("Category","Security")]` `EmptyConfigurationValueFailClosedTests`, one case per required security key.
- **S-NET11-2 TLS chain building** [S5 sslstream-aia-downloads-disabled, aia-fetch-limit-linux]. Client certificates without intermediates are now rejected (fail-closed, desired). Outbound TLS to sources with incomplete chains may fail. Test: Security case in `EnterpriseClaimsTransformationDegradedTests` ("missing intermediate -> unauthenticated, no fallback identity"). Ops note in `deploy/README.md`.
- **S-NET11-3 Kestrel protocol strictness** [S6]. More requests are rejected before middleware. Verify that rejected requests still get no stack traces or internal detail, and that ingress allow lists behave the same. Test: integration security suite, expected codes reviewed, never relaxed.
- **S-NET11-4 Host failure semantics** [S5 ihost-runasync...]. A failing security-relevant `BackgroundService` (revocation, grant expiry sweep, SCIM sync) now ends the process with a non-zero exit code. Test: `BackgroundServiceFailureStopsHostTests` (Security) asserts that no request is served after the failure.
- **S-NET11-5 Parsing exceptions** [S5 dateonly-timeonly...]. User-controlled styles or formats must map to typed 400 problem details, never 500 with internals. Test: Security cases in `ListParameterNormalizerTests`.
- **S-NET11-6 Telemetry tags** [S6 OpenTelemetry semantic-convention tags; S5 http-metrics]. New default tags must not carry query strings, tokens or PII. Test: `TelemetryTagRedactionTests` (Security): `url.query` and `Authorization`-derived values are absent.
- **S-NET11-7 Rounding** [S5 math-round-digits, decimal-biginteger...]. Masking, bucketing and small-group suppression must not change outcome at .5 boundaries. Test: boundary cases in `GovernedAggregationServiceSecurityTests`.
- **S-NET11-8 Regex, JSON, culture.** The .NET 11 list has no `System.Text.RegularExpressions` or `System.Text.Json` breaking change [S5]: regex timeouts and `JsonSerializerOptions` defaults are unchanged; keep the existing tests. ICU changes with the Ubuntu 26.04 image. Ordinal comparison is enforced by CA1307/CA1309/CA1310 as errors in `src/`, so only culture-sensitive display code is exposed. No new test unless Phase 3 asks for one.
- **S-NET11-9 Supply chain.** New base image, new Trivy baseline, digests pinned, locked-mode restore and `NuGetAudit` (`all`, `low`, NU1901-1904 as errors) unchanged. Upgrading also brings the 22 CVE fixes of 11.0.0 [S2].

## 7. Effort (agent-hours) and critical path

| WP | Hours | Owner role |
|---|---:|---|
| 00 GO check | 1 | Orchestrator |
| 00a Dependabot fix | 0.5 | Hydrogen |
| 01 SDK pin | 0.5 | Hydrogen |
| 02 TFM, packages, lockfiles | 3 | Hydrogen |
| 03 Compile fixes | 2-6 | Hydrogen |
| 04 Behaviour tests | 5 | Hydrogen (2 briefs: a-e, f-k) |
| 05 Containers and Trivy | 3 | Hydrogen |
| 06 CI workflows | 1.5 | Hydrogen |
| 07/08/13 Dependabot, Helm, docs | 1.5 | Hydrogen |
| Phase 3 + Phase 5 | 4 | Security expert, reviewer |
| 12 Branch acceptance | 2 (+1 night) | Orchestrator |
| 10/11 G8/G7 evaluation | 2 (+ owner if loosening) | Architect |
| 09 Analyzer raise (follow-up) | 2-4 | Hydrogen |
| 14 Post-merge | 1 | Orchestrator |
| **Total** | **29-35** | |

**Critical path:** 00 (GO) -> 00a -> 01 -> 02 -> 03 -> {04, 05, 06} in parallel -> Phase 3 -> Phase 5 -> 12 (one night) -> 10/11 decisions -> merge -> 14 (3 nights) -> 09. About 4-5 working days of wall clock once GO holds.

## 8. Open questions for the owner (recommended default in bold)

- **Q-NET11-1 Timing.** **Default: wait for 11.0.1 and GO-1..GO-4; re-plan if not met by 2027-01-31.**
- **Q-NET11-2 Dependabot.** Close #760 now and fix the ignore names (WP-00a)? **Default: yes.**
- **Q-NET11-3 Plugin and module SDK.** Should `Autheris.Extensions` / `Autheris.Modules.Abstractions` multi-target `net10.0;net11.0` for external plugin authors? **Default: no** (no production consumers; plugins built for net10 still load in a net11 host).
- **Q-NET11-4 CodeQL gap.** If CodeQL cannot analyse net11 by 2027-01-31: wait, or waive CodeQL for at most 30 days? **Default: wait (no waiver).**
- **Q-NET11-5 Image budget.** If Ubuntu 26.04 pushes the image above 325 MB, raise the budget or trim? **Default: trim first; budget change only by owner decision with the measured delta.**
- **Q-NET11-6 Gate drift.** Loosening the G8 AST baseline, `G7_MUTANT_FLOOR` or the ratchet because of the runtime? **Default: owner decides per addendum; agents may only tighten.**
- **Q-NET11-7 C# 15 adoption.** **Default: separate plan after Stryker and CodeQL state C# 15 support.**

## Execution log

(Filled by WP-NET11-00 and WP-NET11-14: GO results, chosen SDK/runtime patch, image index digests, and .NET 10 rollback digests.)

Exploratory branch `chore/dotnet-11-upgrade` (2026-11-16), started before GO-1..GO-4 hold; draft only, the merge decision is the owner's.

- **WP-01 (2026-11-16):** `global.json` pinned to the exploratory SDK `11.0.100` (GA, `rollForward: latestPatch`); the 11.0.1xx servicing patch is not yet published (the releases index lists SDK 11.0.100 / runtime 11.0.0 as latest). The local SDK is installed side by side with `dotnet-install.sh --channel 11.0` into `/work/dotnet11` (no system change). Rollback reference: the .NET 10 pin was SDK `10.0.401`, runtime image `aspnet:10.0.15`.
- **WP-02 (2026-11-16):** `Directory.Build.props` now carries the single `net11.0` and `AnalysisLevel 10.0-recommended`; the TFM line was removed from all 25 project files (the plan said 24; there are 25 `*.csproj`); `SingleTargetFrameworkRatchetTests` (2 tests) added. Packages bumped per section 2 (11.0.0 for the framework-aligned set, `TimeProvider.Testing` 11.0.0, HotChocolate 16.7.5, OpenTelemetry 1.21.0, SqlClient 7.1.2, Test.Sdk 18.12.0, `Microsoft.CodeAnalysis.CSharp` 5.12.0). Removed references: `Microsoft.Extensions.{Configuration.Abstractions, DependencyInjection.Abstractions, Hosting.Abstractions, Logging.Abstractions, Options}` from `Autheris.Application` and `Autheris.Extensions`, and `System.ComponentModel.Annotations` from `Autheris.Domain` (NU1510 fired only for the latter on restore). `dotnet restore Autheris.sln --force-evaluate` regenerated 19 `packages.lock.json`; the other four (`Sim.ApiHost`, `Tests.Fuzz`, `Tests.Parity`, `Tests.Simulation`) are not part of `Autheris.sln`, so the plan's solution-level step does not cover them (drift) and they were regenerated by restoring each project (committed in the WP-03 commit). All 23 lockfiles changed; `--locked-mode` restore passes for the solution and for those four projects. `NUGET_PACKAGES=/work/nuget` works with SDK 11. Path literals `net10.0` replaced in `cold-start.sh`, `run-api-fuzz.sh`, `SimApiHostProcess.cs`, two draft issue notes and `ReadyToRunPublishSecurityTests`.
- **WP-03 (2026-11-16):** error classes the flip produced, each fixed minimally with no behaviour change:
  - `CA1307` (about 100 sites in `Autheris.Domain`, `TrinoSqlEngine`, `Autheris.Application`, `Autheris.Extensions`, `Autheris.GraphQL`, `Autheris.Infrastructure`): .NET 11 adds `StringComparison` overloads for the char forms of `StartsWith`, `EndsWith`, `IndexOf`, `LastIndexOf`; fix is `StringComparison.Ordinal` (same ordinal semantics as the char overloads). The count exceeds the plan's 40-file budget; the change is mechanical.
  - `CA1860` (2 sites in `GovernancePolicyFactory`, 8 in unit tests): `.Any()` on `IReadOnlyList` replaced by `.Count > 0`.
  - `SYSLIB0066` (2 unit tests): `IStartupValidator` is obsolete. Plan drift: the plan said 0 hits; there are 2 in `ColdPathTimingTests` and `StartupWarmupTests`, both migrated to `IAsyncStartupValidator.ValidateAsync` (tests became async). `src/` has no use.
  - No NU1510 leftovers, no SYSLIB0064, no SYSLIB1205, no Roslyn 18.x breaks.
  - Local environment only (not a repo issue): `Antlr4BuildTasks` 12.14.0 threw `IOException` from a named `Mutex` under SDK 11 because the .NET 11 runtime now rejects a shared-memory directory not owned by the current uid (`/root/.dotnet/corefx` lives on a 9p mount owned by uid 1000, we run as root). Worked around locally by replacing that directory with a symlink to a root-owned `/work/corefx` (mode 1777); the original is kept as `/root/.dotnet/corefx.net10-orig`. CI runners are unaffected.
  - Local verification (WP-03 state): all 25 projects compile (Api, Unit, Architecture, Integration, Parity, SqlCompilerGate, Simulation, Fuzz, TrinoSqlEngine.Tests, Extensions.Tests, Sim.ApiHost, Lakehouse.Tests, ModuleSigner, SqlCorpusExtractor, SqlGate.BenchCheck, Benchmarks). Filtered unit run: `ReadyToRunPublishSecurityTests`, `ColdPathTimingTests`, `StartupWarmupTests`, `Issue143RestrictedScopeTests`, `GovernedTreeQueryServiceTests`, `HitlStepUpAndArrowSerializationFeatureTests`, `GovernancePolicyFactory*`: 197 passed; `SingleTargetFrameworkRatchetTests`: 2 passed.

- **WP-04 (2026-11-16): runtime-behaviour audit.** Repository hits via `git grep`, one pinning test class per item, all green on SDK/runtime 11.0.0 locally (about 52 new cases; the first run of two classes needed test-design corrections, see a and d):

  | Item | Hits and result | Test |
  |---|---|---|
  | a empty config strings | `GatewayStartupValidator` treats empty/whitespace `DataMasking:HmacSecretKeyVaultRef` and `Audit:ChainAnchorPath` from a later provider as missing and fails startup. Observed .NET 11 behaviour: an empty string for a `bool` key (e.g. `Authentication:EnableTestAuthHandler=`) now makes binding throw `InvalidOperationException` naming the key, so startup aborts (fail-closed; a compose or Helm value left empty for a boolean would stop the host). No empty boolean values exist in `deploy/` or the compose files | `EmptyConfigurationValueFailClosedTests` (Security) |
  | b host failure | no `BackgroundServiceExceptionBehavior` is configured in `src/`; the default `StopHost` ends `RunAsync` with the worker exception and fires `ApplicationStopping` | `BackgroundServiceFailureStopsHostTests` (Security) |
  | c scope disposal | no local failures; CI decides | none |
  | d DateOnly/TimeOnly parse | every `TryParseExact` in `src/` uses the fixed style `DateTimeStyles.None`, `ListParameterNormalizer` uses fixed `AssumeUniversal` styles: no client-controlled style reaches the parser. Hostile strings map to the typed `ListParamType` error. Note (all runtimes): `DateTime.TryParse` ignores trailing NUL characters | `ListParameterDateParsingTypedFailureTests` (Security) |
  | e Math.Round | 20 `Math.Round` sites; masking and tariff paths use explicit `MidpointRounding.AwayFromZero`; exact .5 midpoints unchanged on .NET 11; parity nightly is the oracle for non-representable doubles | `MaskRoundingBoundaryTests` (Security) |
  | f tensors | `VectorSearchIndex` guards the dimension itself; zero and NaN vectors never match | 4 cases added to `VectorSearchIndexTests` |
  | g ZIP CRC32 | `FileSystemUniversalDocStore.ExtractBundleAsync` now gets `InvalidDataException` (or a failed result) for an entry whose data does not match its CRC; it is never a success. Open: for a non-tenant bundle, entries before the corrupt one stay on disk (pre-existing behaviour for any mid-extraction failure) and the exception is not mapped to a typed result | `FileSystemUniversalDocStoreZipTests` (Security) |
  | h Kestrel strictness | integration only; CI decides, no local run | none |
  | i TLS AIA | no `X509Chain`, `ClientCertificateValidation` or AIA code in `src/`; client certificates are validated by Kestrel/ingress, outbound TLS uses the platform chain. The platform behaviour (missing intermediate means no trusted chain) is pinned; deployments with incomplete outbound chains need the intermediate installed | `ChainWithoutIntermediateFailClosedTests` (Security) |
  | j telemetry tags | `SpanRedactionProcessor` already removes `url.query`, the query of `url.full`, request/response headers and `db.statement` from third-party activities (`Microsoft.AspNetCore`); pinned for the new hosting tags | `TelemetryTagRedactionTests` (Security) |
  | k ConcurrencyLimiter | no hit | none |

  Regenerated `security-test-inventory.txt` and `security-tests-base.txt` with `AUTHERIS_UPDATE_SECURITY_INVENTORY=1` (+19 lines each). Architecture project, unfiltered, 640 passed / 2 skipped. One earlier run failed `CutoverArchitectureTests.NoCommandTextAssignmentOutsideTheAllowlist` (allowlist entry `ProcedureInvoker` reported unused); the IL does contain the `set_CommandText` calls (verified with Cecil), the class passed in isolation (23/23) and the whole project passed on rerun, so this is a flaky race in the shared Cecil module cache under parallel test classes, not a .NET 11 behaviour change.

- **WP-05 (2026-11-16): containers.** Multi-arch **index** digests read from MCR on 2026-11-16 (`Accept: application/vnd.docker.distribution.manifest.list.v2+json`; the tags `11.0.0` and `11.0.0-resolute` resolve to the same digest):
  - `mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242` in `Dockerfile`, `Dockerfile.production`, `Dockerfile.staging`, `deploy/containers/gqlgateway-api/Containerfile`, `benchmarks/load/docker/Dockerfile.gql`.
  - `mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea` in the two build stages.
  - Rollback digests (.NET 10, kept for 14 days after any merge): `aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e`, `sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b`.
  - Ubuntu 26.04 apt check (Launchpad `getPublishedBinaries`, series resolute/amd64): `gss-ntlmssp` 1.2.0, `krb5-user`, `libgssapi-krb5-2`, `libkrb5-3` 1.22.1, `zlib1g`, `libpcre2-8-0` 10.46 all exist. `libapt-pkg6.0t64` does **not** exist (it is `libapt-pkg7.0` 3.2.1), so the `--only-upgrade` line in `Dockerfile` would have failed the build; renamed to `libapt-pkg7.0` (still upgrade-only, installs nothing new). `Dockerfile.production`/`.staging` never had that line. `APP_UID=10001` and `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` / `UseManagedNtlm=true` unchanged; the .NET images keep their own `app` user (UID 1654), so no clash. `benchmarks/load/scripts/measure-r2r-impact.sh` inline tag moved from `aspnet:10.0.13` to `aspnet:11.0.0-resolute`.
  - `.trivyignore` **not changed**: the 15 Noble entries (lines 6-27) are probably obsolete, but removing them is only justified by a Trivy run against the new image, and no container daemon runs locally. Trivy (`pr-container-scan`) on the branch decides: any entry that is still needed would reappear as a finding. No new ignore added.
  - Not verifiable locally (no container daemon): image build, `docker-publish.yml` `pr-container-scan`, image size against `coreImageUncompressedMB <= 325`, cold start P50 <= 3.0 s, warm P99 <= 3.47 ms (`footprint-budget.yml`); x86-64-v2 hardware floor is documented in `deploy/README.md` (WP-08).
  - Local test: `SupplyChainAndDeploymentSecurityTests` (digest pinning of every Dockerfile/Containerfile) green.

- **WP-06 (2026-11-16): CI workflows.** All 19 `Setup .NET 10` step names renamed to `Setup .NET 11` (cosmetic; every job selects the SDK through `global-json-file`, so the pin follows `global.json`). Only functional change: job `sql-compiler-gate-g7` of `sql-compiler-gate-nightly.yml` also installs `dotnet-version: '10.0.x'` because `dotnet-stryker` 5.0.0 is pinned with `rollForward: false` and needs the .NET 10 runtime. The aggregate job runs no .NET tool. No `net10.0` literal remains in any workflow. `actions/setup-dotnet` stays at the pinned v6.0.0 SHA (`a98b568...`): the v6.1.0 bump (`0f4a354dd5547a28c70b7853dbd95d5b67886e73`, resolved with `git ls-remote`) is not needed for the flip and was left out to keep the change minimal. No gate, threshold, timeout or Stryker/CodeQL configuration changed; `CiWorkflowArchitectureTests` (24) green. The runners stay `ubuntu-24.04` (host OS, independent of the container base).
- **WP-08 (2026-11-16): Helm and runtime configuration.** `deploy/helm/autheris` has no .NET-specific values (image tag falls back to `appVersion`), `podman-compose.yaml` and the compose files build from the changed Dockerfiles: no change. Probe timings are not reviewed: they need the cold-start result of `footprint-budget.yml` on the branch. `deploy/README.md` system requirements gained the x86-64-v2 floor, the outbound TLS note (no AIA download) and the empty-configuration-value note.
- **WP-13 (2026-11-16): reference docs.** `docs/developer-guide.md` (SDK 11.0, WP-01), `README.md` (title line and prerequisite link), `deploy/README.md` (diagram, base image, system requirements), `docs/configuration-guide.md` (`aspnet:11.0`), `benchmarks/protocol/README.md` (SDK row), `benchmarks/Autheris.Benchmarks/Program.cs` (environment banner text). Intentionally not changed: `CLAUDE.md`, `AGENTS.md`, `.agents/**` (agent role descriptions say ".NET 10 / C# 13"; governance files, owner decision), `benchmarks/load/**` hcloud/Hetzner provisioning scripts that install a .NET 10 SDK on benchmark hosts, `docs/features/**` (frozen by rule), the German prose of `docs/configuration-guide.md` line 11 (".NET 10 Options-Pattern", framework-version-neutral), `THIRD-PARTY-NOTICES.md` (not regenerated; package set changed only by version bumps and the removal of shared-framework packages).

- **CI round 1 (PR #777, 2026-11-17):** `origin/main` merged once (1.8.1 changelog, footprint fix, MCP backlog, Dependabot merges). Only the eight workflow files conflicted (Dependabot action SHA bumps against the step renames); resolved by taking main's versions and re-applying the "Setup .NET 11" renames and the Stryker `dotnet-version: '10.0.x'` line. `actions/setup-dotnet` is now v6.1.0 through main. New code from main needed 3 more `CA1307` ordinal fixes. Failures and outcomes:
  1. `StrykerConfigGuardTests.Workflow_G7Job_AppliesTheShardScopeBeforeStryker...`: my explanatory comment in the G7 job contained the text `dotnet-stryker`, and the guard takes the first line containing that text as the Stryker step. Fix: the comment no longer names the tool. The guard is unchanged.
  2. `CompositionDescriptorSnapshotTests` (dev, production-like): diff compared as sets, ignoring the index numbers. All changes are framework or OpenTelemetry-internal registrations: options validation (`DataAnnotationValidateOptionsAdapter/Registration<T>` for six options types, `IValidateOptions<T>` now a transient factory, `IStartupValidator` singleton factory, new `IAsyncStartupValidator` and `StartupValidator` singleton), `ActivityListenerBuilder`, OpenTelemetry 1.21 (`TracerProviderBridge`, `IOptionsChangeTokenSource<SelfDiagnosticsOptions>`). No Autheris registration changed. Regenerated with `AUTHERIS_UPDATE_SNAPSHOTS=1`.
  3. `AutherisDistributedContextPropagatorTests.Inject_...`: .NET 11 sets the W3C trace-context level 2 random-trace-id flag, so generated ids carry flags `03`, not `01`. The test now asserts the ids exactly and that the sampled bit (0x01) is propagated. No production change.
  4. `UsageQuotaSecurityTests.Soft_limit_emits_warning_header_and_allows_request`: **real behaviour change.** .NET 11 converts `double`/`float` to `decimal` exactly: `(decimal)0.8d` is `0.8000000000000000444...` (was `0.8`), `(decimal)0.1f` is `0.100000001490116...`. `UsageQuotaService` compared `used >= Limit * (decimal)SoftLimitRatio`, so 8 of 10 no longer reached the 80% soft limit. Fix: new `Autheris.Domain.Common.DecimalConversions` (`FromDouble` rounds to 15 and `FromSingle` to 7 significant digits, trailing zeros stripped, `ToDecimal(object)` replaces `Convert.ToDecimal(x, Invariant)`), applied at every double/float to decimal site in `src/` that handles data or thresholds: quota soft limit, filter value coercion, streaming row-filter evaluator, policy predicate evaluator, composite keys, virtual-filter key canonicalizer, aggregate bucket and GraphQL aggregate mappers, mask rounding input, semantic metrics, Arrow/Parquet export, Databricks cost collector and command, plus an internal copy for the SQL parameter binder in `TrinoSqlEngine` (`Numerics/`, outside the Stryker `mutate` globs). Not changed on purpose: `UsageStore` and `SyntheticDataTableReader` (database or in-memory decimals, not binary floating point). Pinned by `DecimalConversionsTests` (Security). Local filtered run of the affected areas (decimal conversions, usage, masking, filters, aggregation, policy predicates, snapshots): 2179 passed.
  5. `Container Scan (pull request)`: the image built (the `libapt-pkg7.0` rename worked), Trivy then exited 1 with HIGH/CRITICAL findings, but the action writes SARIF only and the job log lists no CVE ids. **Not resolved:** the findings are visible only in the uploaded SARIF/code-scanning alerts or a Trivy table run; no `.trivyignore` change was made without ids. The 15 Noble entries are not matched by the Ubuntu 26.04 image and can be removed together with any needed Resolute entries once the ids are known.
  6. Integration shard 4 `SqlServerGovernanceStoresContractTests.Recert_PendingPastDeadline_ReturnsOnlyExpiredPendingItems`: failed in 1 ms in `InitializeAsync` with migration `0013 scim_identity_correlation` on SQL Server (error 102, syntax); the other 501 tests of the shard passed, including the other tests of that class. No net11 relation found in the log (the test fixture failed at class initialisation of one test, 15 minutes into the shard); treated as a flake pending a rerun.

## Sources

- [S1] .NET release index (11.0: 11.0.0, SDK 11.0.100, `sts`, EOL 2028-11-14; 10.0: 10.0.15, SDK 10.0.404, `lts`, EOL 2028-11-14): https://github.com/dotnet/core/blob/main/release-notes/releases-index.json; policy https://aka.ms/dotnetcoresupport
- [S2] .NET 11 releases (preview.6 to GA dates, CVE lists): https://github.com/dotnet/core/blob/main/release-notes/11.0/releases.json
- [S4] What's new in .NET 11: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview; announcement https://devblogs.microsoft.com/dotnet/?p=61205
- [S5] Breaking changes in .NET 11 (each item under `https://learn.microsoft.com/en-us/dotnet/core/compatibility/<area>/11/<item>`): https://learn.microsoft.com/en-us/dotnet/core/compatibility/11
- [S6] Breaking changes in ASP.NET Core 11: https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/11/overview
- [S7] Roslyn breaking changes for .NET 11: https://github.com/dotnet/roslyn/blob/main/docs/compilers/CSharp/Compiler%20Breaking%20Changes%20-%20DotNet%2011.md
- [S8] .NET container tags (`11.0` = Ubuntu 26.04 resolute; Alpine 3.24; Azure Linux 3.0/4.0): https://github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md; MCR tag list https://mcr.microsoft.com/v2/dotnet/aspnet/tags/list
- [S9] actions/setup-dotnet releases (v6.1.0, 2026-11-03): https://github.com/actions/setup-dotnet/releases
- [S10] CodeQL supported compilers (C# up to 14, .NET up to 10): https://github.com/github/codeql/blob/main/docs/codeql/reusables/supported-versions-compilers.rst; .NET 11 issue https://github.com/github/codeql/issues/23133
- [S11] Stryker.NET releases (5.1.0 on 2026-11-10; 5.0.0 targets the .NET 10 runtime): https://github.com/stryker-mutator/stryker-net/releases; .NET 11 bump https://github.com/stryker-mutator/stryker-net/issues/3994
- [S12] Trivy OS coverage (Ubuntu: all Canonical-supported versions; Alpine up to 3.22): https://github.com/aquasecurity/trivy/blob/main/docs/guide/coverage/os/index.md
- [S13] NuGet package versions and TFMs: `https://api.nuget.org/v3-flatcontainer/<id>/index.json` and `.nuspec` per package (queried 2026-11-16)
- [S14] Microsoft.Extensions packages in the shared framework (NU1510): https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/11/extensions-in-shared-framework
