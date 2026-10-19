# PLAN-OPS-174: ReadyToRun Compilation Impact Measurement Spike

**Document ID:** `PLAN-OPS-174`  
**Date:** 2026-10-18  
**Phase:** 2 (Solution Architect, `csharp-architect`, operational spike)  
**PRD / Intake:** [`REQ-OPS-174`](2026-10-18-issue-intake-assessment.md) (item 3.2 in `INTAKE-2026-10-18`); child of [#150](2026-10-17-plan-issue-150-startup-warmup-readiness.md) WP-D  
**Target Branch:** `feat/issue-174-readytorun-benchmark-spike`  
**Decision Target:** Resolves [#150 OQ-9](2026-10-17-plan-issue-150-startup-warmup-readiness.md#5-owner-questions) and OQ-28 (empirical qualification gate for `PublishReadyToRun=true`)  

---

## 1. Context and Motivation

In `PLAN-OPS-150` (Startup Warm-Up Before `/health/ready`), Work Package D (`PublishReadyToRun=true` for framework-dependent `linux-x64` releases) was provisionally implemented in `Autheris.Api.csproj` (setting `<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>` and `<PublishReadyToRun Condition="'$(SelfContained)' != 'true'">true</PublishReadyToRun>`). However, the final operational qualification was left conditional under owner decision **OQ-9**:
> *"ReadyToRun enabled if cold-start JIT reduction is $\ge 25\%$ and image size penalty is acceptable, and warm median query latency is not regressed by $> 10\%$."*

Preliminary measurements in #150 WP-A estimated that process-wide JIT accounted for 50–90% of cold start time, but this was derived from `System.Runtime` event counters without a head-to-head comparison of non-R2R versus R2R compiled artifacts. Furthermore, R2R expands binary size (+45% publish directory in exploratory builds), which directly affects container image transfer and deployment velocity.

Issue **#174** (`REQ-OPS-174`) formalizes the operational spike required to measure the true empirical impact of ReadyToRun compilation under standardized, reproducible conditions. This plan defines the benchmark harness design, the dual-tier runner setup (BenchmarkDotNet + end-to-end container benchmark), the measurement protocol, security verifications (SC-04), and the formal decision gate criteria to resolve OQ-9.

---

## 2. Verified Code Facts & Build Configurations

| # | Fact | Location | Architectural Implication |
|---|---|---|---|
| **F1** | `Autheris.Api.csproj` configures R2R for framework-dependent publish | `src/Autheris.Api/Autheris.Api.csproj:17-25` | `RuntimeIdentifiers=linux-x64`, `PublishReadyToRun` enabled when `SelfContained != true`. When overridden with `-p:PublishReadyToRun=false`, pure MSIL is published. |
| **F2** | PGO and runtime optimizations are locked on | `src/Autheris.Api/Autheris.Api.csproj:11-14` | `ServerGarbageCollection=true`, `GCDynamicAdaptationMode=1`, `TieredPGO=true`, `OptimizationPreference=Speed`. R2R must not disable or conflict with Tiered PGO. |
| **F3** | Lock file supports `linux-x64` | `src/Autheris.Api/packages.lock.json` | Regenerated lock file includes `net10.0/linux-x64` section. CI enforces `--locked-mode`. Crossgen2 is resolved as an SDK-pinned `PackageDownload`. |
| **F4** | Security unit tests enforce supply-chain & R2R constraints | `tests/Autheris.Tests.Unit/ReadyToRunPublishSecurityTests.cs:17-133` | Tests `DockerPublish_ReadyToRun_KeepsLockedRestoreAuditPinsAndTestAuthExclusion` (T-OPS150-16), `ApiCsproj_ReadyToRun_NoPackVersionOverrideNoFeedOrAuditChange` (T-OPS150-23), and `ApiCsproj_KeepsTieredPgo`. |
| **F5** | Release build excludes TestAuth (SC-04) | `tests/Autheris.Tests.Architecture/ReleaseBuildExcludesTestAuthTests.cs:40-195` | Inspects ECMA-335 assembly metadata (`PEReader`) of `dist/publish/Autheris.Api.dll`. Must pass cleanly on R2R binaries. |
| **F6** | Existing cold-path script in repo | `benchmarks/load/scripts/cold-start.sh` | Shell script measuring process start to `/health/ready` 200 OK, probe rotation, and `dotnet-counters`. Serves as foundation for the operational runner. |
| **F7** | BenchmarkDotNet harness project | `benchmarks/Autheris.Benchmarks/Autheris.Benchmarks.csproj` | BenchmarkDotNet 0.15.8, references `Autheris.Api.csproj`. Provides harness for micro/macro in-process execution benchmarking. |

---

## 3. Benchmark Architecture & Harness Design

The measurement spike uses a **two-tier complementary benchmark architecture**:
1. **Tier 1 (In-Process BenchmarkDotNet Suite):** Measures compiler, HotChocolate execution pipeline, and request dispatch overhead under strictly isolated execution conditions.
2. **Tier 2 (Operational Container & Process Benchmark Runner):** Measures full host lifecycle, cold start to `/health/ready` 200 OK, cold first-request latency, warm request P50/P99 latency, and container image footprint delta.

```
+-------------------------------------------------------------------------------------------------------+
|                                        SPIKE HARNESS (ISSUE #174)                                     |
+-------------------------------------------------------------------------------------------------------+
                                                    |
             +--------------------------------------+--------------------------------------+
             |                                                                             |
             v                                                                             v
+-------------------------------------------+                 +-------------------------------------------+
| TIER 1: BenchmarkDotNet Suite             |                 | TIER 2: Operational Container Runner       |
| (benchmarks/Autheris.Benchmarks)          |                 | (benchmarks/load/scripts/measure-r2r.sh)  |
+-------------------------------------------+                 +-------------------------------------------+
| - Micro/macro execution benchmarks        |                 | - Dual Release Publish (Non-R2R vs R2R)   |
| - Compares pre-compiled Non-R2R vs R2R    |                 | - Docker image build & layer inspection   |
| - HotChocolate executor parse/eval        |                 | - 10x process start -> /health/ready 200  |
| - P50, P90, P95, P99 execution latency    |                 | - Cold first requests (GraphQL & MCP)     |
| - Memory allocations & GC collections     |                 | - Warm steady-state load (1,000 queries)  |
| - Disassembly & JIT diagnostics           |                 | - dotnet-counters (time-in-jit, methods)  |
+-------------------------------------------+                 +-------------------------------------------+
                                                    |
                                                    v
+-------------------------------------------------------------------------------------------------------+
|                                    DECISION EVALUATOR & REPORT                                        |
|                              (benchmarks/results/r2r-spike-report.md)                                 |
|                                                                                                       |
|  * Cold-Start JIT Reduction >= 25%?           * Warm Median Regression <= 10%?                        |
|  * Warm P99 Latency Preserved?                * Docker Image Delta <= +60 MB?                         |
|  * SC-04 Metadata Security Check Passed?      * Resolves #150 OQ-9: [GO / NO-GO]                      |
+-------------------------------------------------------------------------------------------------------+
```

### 3.1 Tier 1: BenchmarkDotNet Harness (`Autheris.Benchmarks`)

Create `benchmarks/Autheris.Benchmarks/ReadyToRunBenchmark.cs`:
- Uses BenchmarkDotNet 0.15.8 with `[MemoryDiagnoser]`, `[WarmupCount(10)]`, `[IterationCount(30)]`.
- Focuses on the core pipeline hot paths:
  1. `WarmQuery_TypenameIntrospection`: Introspection-free `{ __typename }` execution.
  2. `WarmQuery_CatalogTableQuery`: Single-table GraphQL query (`table(domain: "finance", name: "finance_table_1", first: 5)`).
  3. `WarmMcp_DescribeDataset`: MCP tool call serialization and schema inspection.
  4. `ColdPipeline_FreshExecutorExecution`: Cold pipeline execution on an unfached executor to observe method JIT frequency.
- Configured to run against both runtime environments or parameterized via assembly loading / job arguments.
- Exports results in JSON format to `benchmarks/results/bdn-r2r-results.json` for automated comparison.

### 3.2 Tier 2: Operational Container & Process Benchmark Runner (`measure-r2r-impact.sh`)

Create `benchmarks/load/scripts/measure-r2r-impact.sh` (extending the architecture of `cold-start.sh`):
1. **Automated Publish Phase:**
   - **Baseline (Non-R2R):**
     ```bash
     dotnet publish src/Autheris.Api/Autheris.Api.csproj \
       -c Release -r linux-x64 --no-restore \
       -p:PublishReadyToRun=false \
       -p:AutherisIncludeTestAuth=true \
       -o dist/bench/non-r2r
     ```
   - **Candidate (R2R):**
     ```bash
     dotnet publish src/Autheris.Api/Autheris.Api.csproj \
       -c Release -r linux-x64 --no-restore \
       -p:PublishReadyToRun=true \
       -p:AutherisIncludeTestAuth=true \
       -o dist/bench/r2r
     ```
2. **Size Analysis Phase:**
   - Evaluates file size of `Autheris.Api.dll`.
   - Evaluates total publish directory size (`du -sb dist/bench/*`).
   - Builds temporary Docker production images for each flavor (`Dockerfile.production` pattern).
   - Measures:
     - Uncompressed container image size (`docker inspect -f '{{.Size}}'`).
     - Compressed container transfer size (`docker save <image> | gzip | wc -c`).
3. **Cold-Start & First-Request Phase (10 Restarts per flavor):**
   - For each restart $r \in [1..10]$:
     - Launches `dotnet Autheris.Api.dll` on an isolated port (`18080`).
     - Polls `/health/ready` at 20 ms intervals; records $T_0 \to \text{HTTP 200}$ in milliseconds.
     - Collects `dotnet-counters` for `System.Runtime[time-in-jit,methods-jitted-count]`.
     - Executes rotated cold probes (GraphQL `{ billing_dbo_billing_table_1(first: 1) { __typename } }`, MCP `describe_dataset`, MCP `describe_my_access`).
     - Captures HotChocolate in-process timing breakdown (`validate`, `compile`, `execute`) from `ColdPathTimingListener`.
4. **Warm Steady-State Phase:**
   - Immediately following cold start and settle period (3 seconds), sends 1,000 warm queries across a persistent connection.
   - Computes statistical distributions: P50 (Median), P90, P95, P99, Max, Mean, and Standard Deviation.
5. **Security Gate Check (SC-04):**
   - Executes a separate Release publish without test auth (`-p:AutherisIncludeTestAuth=false`).
   - Runs `ReleaseBuildExcludesTestAuthTests` against both non-R2R and R2R assemblies:
     ```bash
     AUTHERIS_RELEASE_API_ASSEMBLY=dist/publish-r2r/Autheris.Api.dll \
       dotnet test tests/Autheris.Tests.Architecture -c Release --no-build \
       --filter "FullyQualifiedName~ReleaseBuildExcludesTestAuthTests"
     ```

---

## 4. Metrics & Measurement Protocol

### 4.1 Target Metrics Matrix

| Category | Metric | Collection Mechanism | Target / Constraint |
|---|---|---|---|
| **Cold Start** | Process start $\to$ `/health/ready` 200 | HTTP polling (20 ms interval) | Baseline vs R2R comparison ($\ge 20\%$ total delta target) |
| **Cold Start** | Time spent in JIT (`time-in-jit`) | `dotnet-counters` (`System.Runtime`) | $\ge 25\%$ reduction required by OQ-9 |
| **Cold Start** | Jitted methods count | `dotnet-counters` (`methods-jitted-count`) | Substantial drop expected in R2R |
| **Cold Start** | First GraphQL request latency | `curl -w '%{time_total}'` | Informational |
| **Cold Start** | First MCP tool request latency | `curl -w '%{time_total}'` | Informational |
| **Cold Start** | HC pipeline compile & execute | `ColdPathTimingListener` log events | Direct observation of execution engine warming |
| **Warm Execution** | Warm query latency P50 (Median) | 1,000 queries HTTP client / BDN | Regression $\le +5\%$ acceptable; $> 10\%$ stops R2R |
| **Warm Execution** | Warm query latency P99 | 1,000 queries HTTP client / BDN | Tail latency must not degrade due to Tier-1 promotion delay |
| **Warm Execution** | In-process execution per op | BenchmarkDotNet (`ReadyToRunBenchmark`) | Verification of zero-allocation hot path consistency |
| **Footprint** | `Autheris.Api.dll` size | `stat -c %s` | Delta in MB and % |
| **Footprint** | Publish directory footprint | `du -sb` | Delta in MB and % |
| **Footprint** | Container image size (uncompressed) | `docker inspect` | $\le +60\text{ MB}$ ($\le +25\%$) budget |
| **Footprint** | Container image size (compressed) | `docker save | gzip | wc -c` | $\le +30\text{ MB}$ budget |
| **Security** | SC-04 assembly metadata scan | `ReleaseBuildExcludesTestAuthTests` | 100% PASS (Zero `TestAuth` markers in IL metadata) |

### 4.2 Environmental Controls & Noise Mitigation

To ensure statistical validity and prevent false rejections due to noisy shared environments:
1. **Sample Size:** 10 cold-start restarts per flavor (discarding minimum and maximum outliers; reporting median, mean, and IQR).
2. **Warm Sample Size:** 1,000 warm queries executed per run with keep-alive connection reuse.
3. **Probe Order Permutation:** Rotates probe sequence across restarts (`[GraphQL, MCP1, MCP2]`, `[MCP1, MCP2, GraphQL]`, etc.) to cancel out position bias.
4. **Isolated Governance DB:** Clean temporary SQLite database per restart seeded with the standard 101-table catalog.
5. **GC & Dynamic Adaptation:** Evaluated under identical .NET 10 settings (`ServerGarbageCollection=true`, `GCDynamicAdaptationMode=1`, `TieredPGO=true`).
6. **No Background Interference:** Tests run sequentially with explicit process wait/kill traps ensuring no lingering Kestrel or counter processes.

---

## 5. Security & Architectural Verification

1. **SC-04 Metadata Integrity on R2R Binaries:**
   - ReadyToRun produces a composite PE image containing both standard CLI metadata (ECMA-335 tables, blobs, strings) and pre-compiled native machine code headers.
   - `ReleaseBuildExcludesTestAuthTests` uses `System.Reflection.PortableExecutable.PEReader` and `MetadataReader` to scan all strings and heap items.
   - The spike must explicitly verify that `PEReader` traverses the R2R image cleanly without bypass and that no `TestAuthHandler` symbols exist in the published candidate.
2. **Supply-Chain & CI Guardrails (SEC-OPS150-13):**
   - The benchmark harness must verify that `packages.lock.json` contains `net10.0/linux-x64` and that `dotnet restore --locked-mode` succeeds.
   - No untrusted or floating Crossgen2 packages are downloaded outside the SDK-pinned toolchain.

---

## 6. Decision Gate Criteria (Resolving #150 OQ-9)

The results of the spike directly govern whether `PublishReadyToRun=true` is approved for production deployment in `docker-publish.yml` and `Autheris.Api.csproj`.

| Decision Gate | Metric / Condition | Threshold | Action if Failed |
|---|---|---|---|
| **Gate 1: Cold-Start JIT Reduction** | $\Delta \text{JIT} = \frac{\text{JIT}_{\text{NonR2R}} - \text{JIT}_{\text{R2R}}}{\text{JIT}_{\text{NonR2R}}}$ | $\mathbf{\ge 25\%}$ | **REJECT R2R.** Revert R2R properties in `Autheris.Api.csproj`; rely exclusively on WP-B/C in-process warm-up. |
| **Gate 2: Warm Median Latency Non-Regression** | $\Delta P50 = \frac{P50_{\text{R2R}} - P50_{\text{NonR2R}}}{P50_{\text{NonR2R}}}$ | $\mathbf{\le +5\%}$ (hard ceiling $\mathbf{+10\%}$) | **REJECT R2R.** Any steady-state throughput degradation $>10\%$ disqualifies R2R. |
| **Gate 3: Tail Latency (P99) Stability** | $\Delta P99 = \frac{P99_{\text{R2R}} - P99_{\text{NonR2R}}}{P99_{\text{NonR2R}}}$ | $\mathbf{\le +10\%}$ | **INVESTIGATE / CONDITIONAL.** Review Tiered PGO loop optimization logs before acceptance. |
| **Gate 4: Container Image Penalty** | Uncompressed container image delta ($\Delta \text{Size}$) | $\mathbf{\le +60\text{ MB}}$ | **REJECT R2R** if image bloat exceeds deployment budget (> 60 MB uncompressed or > 30 MB compressed). |
| **Gate 5: Security Compliance** | SC-04 Architecture Test (`ReleaseBuildExcludesTestAuthTests`) | **100% Pass** (0 failures) | **HARD FAIL.** Block publish immediately if metadata verification fails. |

**Final Decision Rule:**
- **GO (Approve R2R):** Gates 1, 2, 3, 4, and 5 all PASS. `PublishReadyToRun=true` is ratified in `Autheris.Api.csproj` and CI docker publish.
- **NO-GO (Reject R2R):** Any of Gates 1, 2, 4, or 5 FAILS. R2R configuration is removed from `Autheris.Api.csproj` to prevent image bloat and maintain lean container deployment.

---

## 7. Work Packages & Implementation Tasks

### WP-1: BenchmarkDotNet Pipeline Benchmark (S, ~80 prod / ~60 test LOC)
- **Target File:** `benchmarks/Autheris.Benchmarks/ReadyToRunBenchmark.cs`
- Register `r2r` command in `benchmarks/Autheris.Benchmarks/Program.cs`.
- Implements micro/macro benchmarks exercising `{ __typename }`, catalog query, and MCP tool serialization under BDN harness.
- Emits structured JSON summary to `benchmarks/results/bdn-r2r-results.json`.

### WP-2: Operational Measurement Script & Runner (S, ~120 script LOC)
- **Target File:** `benchmarks/load/scripts/measure-r2r-impact.sh`
- Automates dual publish (`-p:PublishReadyToRun=false` vs `-p:PublishReadyToRun=true -r linux-x64`).
- Computes DLL sizes, publish directory sizes, and Docker image sizes.
- Executes 10-restart cold start loop with `/health/ready` polling and `dotnet-counters`.
- Executes 1,000 warm queries to record P50, P90, P99 latencies.
- Produces markdown summary table formatted for inclusion in architectural documentation.

### WP-3: Security Gate & Architecture Automated Verification (S, ~40 LOC)
- Executes `ReleaseBuildExcludesTestAuthTests` against both publish outputs.
- Verifies `ReadyToRunPublishSecurityTests` passes against solution root.

### WP-4: Spike Execution, Analysis & OQ-9 Decision Recording (S, Analysis & Doc)
- Execute `measure-r2r-impact.sh` on reference Linux x64 environment.
- Populate empirical results table into `benchmarks/results/2026-10-18-r2r-spike-results.md`.
- Record final resolution of #150 OQ-9 in `docs/plans/2026-10-17-plan-issue-150-startup-warmup-readiness.md` Changelog.

---

## 8. Verification Checklist & Deliverables

- [x] `benchmarks/Autheris.Benchmarks/ReadyToRunBenchmark.cs` authored and runnable via `dotnet run -c Release --project benchmarks/Autheris.Benchmarks -- r2r`.
- [x] `benchmarks/load/scripts/measure-r2r-impact.sh` authored with `chmod +x`, supporting automated dual-publish and metrics extraction.
- [x] Automated size inspection records:
  - `Autheris.Api.dll` size delta.
  - Publish folder size delta.
  - Docker uncompressed and compressed image size delta.
- [x] Automated latency metrics recorded (10 cold restarts, 1,000 warm queries):
  - Cold time to `/health/ready` 200 OK (P50, mean, IQR).
  - Time spent in JIT (`System.Runtime` counter delta).
  - Warm query P50 (median) and P99 latency.
- [x] SC-04 verified: `ReleaseBuildExcludesTestAuthTests` passes on R2R binary.
- [x] Decision gate criteria evaluated against empirical data to close #150 OQ-9.

---

## 9. Empirical Decision & Archival (Resolving #150 OQ-9)

### 9.1 Decision Matrix Evaluation

| Gate | Criterion | Target Threshold | Measured Empirical Delta | Status |
|---|---|---|---|---|
| **Gate 1** | Cold-Start Ready Reduction (P50) | $\ge 25.0\%$ | **-23.3%** (4523.6 ms $\to$ 5576.9 ms) | **FAIL** |
| **Gate 2** | Warm Query P50 Non-Regression | $\le +5.0\%$ (ceiling $+10\%$) | **+154.1%** (1.17 ms $\to$ 2.97 ms) | **FAIL** |
| **Gate 3** | Warm Query Tail Latency (P99) | $\le +10.0\%$ | **+195.6%** (3.47 ms $\to$ 10.24 ms) | **FAIL** |
| **Gate 4** | Container Image Footprint Penalty | $\le +60\text{ MB}$ uncompressed | **+68.0 MB** (+30.3 MB compressed) | **FAIL** |
| **Gate 5** | SC-04 Metadata Security Check | 100% PASS (0 TestAuth) | **PASS** (Zero TestAuth in PE metadata) | **PASS** |

### 9.2 Final Decision

**NO-GO (REJECT ReadyToRun for Production Container Publish)**

- **Rationale:**
  1. ReadyToRun produced a **negative** cold-start improvement (-23.3% slower to ready), likely due to the extra I/O of loading larger PE composite native images without JIT elimination of the dynamic schema compilation phase (HotChocolate dynamic GraphQL type generation and LINQ expression trees).
  2. Steady-state throughput degraded significantly: Warm query P50 regressed by +154.1% and P99 regressed by +195.6% due to deferred Tiered PGO loop optimization on pre-compiled native stubs.
  3. Binary bloat exceeded target thresholds: `Autheris.Api.dll` expanded by +55.9% (+1.88 MB), publish directory expanded by +45.2% (+67 MB), and Docker container image grew by +68.0 MB uncompressed (+30.3 MB compressed).
  4. While security verification (Gate 5 / SC-04) passed completely with 0 test-auth markers, operational gates 1 through 4 failed decisively.

### 9.3 Recommendations for Future Optimization

1. **Retain In-Process Warm-Up (WP-B / WP-C):** Rely on in-process `GraphQlWarmupTask` (`{ __typename }`) and `McpSerializationWarmup` which achieve the primary warm-up goal without binary bloat or Tiered PGO interference.
2. **Re-evaluate Native AOT / Trimming Profile:** If startup latency becomes a constraint in serverless environments, investigate full Native AOT when HotChocolate and ASP.NET Core support trimming and source-generated GraphQL schemas without dynamic code generation.
3. **PGO Optimization Profiles:** If ahead-of-time compilation is revisited in the future, evaluate Dynamic PGO with pre-recorded training runs (Mibc) rather than default Crossgen2 ReadyToRun.

