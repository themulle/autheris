# PLAN-NET11-PERF: .NET 11 and C# 15 performance opportunities

Status: PLANNED (analysis only, no code changed)
Date: 2026-11-17
Parent: [`00-master-plan-overview.md`](00-master-plan-overview.md), follows [`2026-11-16-plan-dotnet-11-upgrade.md`](2026-11-16-plan-dotnet-11-upgrade.md) (`PLAN-NET11`, flip merged).

## 1. Scope and method

Question: which new .NET 11 runtime, JIT, BCL, ASP.NET Core and EF Core features, and which C# 15 features, can make Autheris faster (gateway and governance hot paths, startup, footprint)?

Method:

1. Feature facts were read from the official Microsoft Learn "What's new" pages (runtime, libraries, ASP.NET Core 11, EF Core 11, C# 15), fetched 2026-11-17. Nothing below is quoted from memory. Where Microsoft gives no number, this plan says "not quantified".
2. Code evidence comes from read-only inspection of `src/` and `benchmarks/` at `origin/main` (`ecb956312`).
3. No benchmark was run for this plan. Every "expected gain" is either a Microsoft figure (marked MS) or a hypothesis (marked H) that a named benchmark must confirm or reject before any change merges. Autheris has no production deployment, so behaviour changes are allowed, but the existing perf gates still apply.

Existing measurement assets: `benchmarks/Autheris.Benchmarks` (`AccessDecisionPointBenchmark` with `[MemoryDiagnoser]`, `SqlCompilerBenchmarks`, `SqlParserCacheBenchmarks`, `VirtualFilterEnforcementBenchmark`, `VirtualFilterResolverBenchmark`, `ColumnMaskingBenchmark`, `ConsentCacheBenchmark`, `ConsentResolutionBenchmark`, `CompilePathBenchmark`, `GatewayLoadBenchmark`, `ScaleCatalogBenchmark`), `benchmarks/budgets.json` (cold start P50 5.0 s, warm P99 3.47 ms, image 440 MB, 10 percent tolerance), `benchmarks/scripts/footprint.sh`, `benchmarks/load/scripts/cold-start.sh` and `measure-r2r-impact.sh`. Last footprint on file (2026-11-05, R2R spike candidate): 441.8 MB image, cold P50 5577 ms, warm P99 10.24 ms. The budget baseline is the non-R2R build (373 MB, 4524 ms, 3.47 ms).

## 2. What is new (verified)

Runtime and JIT (Learn: .NET 11 runtime):

- Runtime Async V2: compiler-free async state machines, enabled per project with `<Features>runtime-async=on</Features>`; BCL itself is built with it; continuation reuse, tiered async compilation, `Task.FromResult`/`ValueTask.FromResult` folding, tail-await. No `EnablePreviewFeatures` needed on `net11.0`. Opt out with `<UseRuntimeAsync>false</UseRuntimeAsync>`.
- Continuations skip ExecutionContext capture/restore when there is nothing to restore (automatic, all `Task`/`ValueTask`).
- JIT: more bounds-check elimination (`i + cns < len`, `^1`, empty-span guard), redundant branch/check removal, switch-expression folding to branchless checks, constant-folding of `string ==`/`SequenceEqual` on constants after inlining, devirtualization of generic virtual methods and default interface methods, `Activator.CreateInstance<T>()` exact type.
- ReadyToRun: `Comparer<T>.Default` and `EqualityComparer<T>.Default` are specialised in R2R images (MS: up to 20x for collection operations using default comparers); R2R target raised to x86-64-v3 (AVX2, BMI, FMA) on Linux/Windows; async inlining in crossgen2.
- VM: `Guid.NewGuid()` on Linux uses `getrandom()` with batch caching (MS: about 12 percent more throughput).
- Minimum hardware: x86-64-v2 baseline; .NET 11 fails to start on older CPUs.
- No GC changes are documented on the runtime page; none are assumed here.

Libraries (Learn: .NET 11 libraries):

- System.Text.Json: `Utf8JsonWriter.Reset(...)` for pooled writers; source-gen coverage for non-public accessors, inaccessible constructors, `in/ref/out` parameters; generic `GetTypeInfo<T>()`; open-generic converters without a factory; closed-hierarchy polymorphism metadata at compile time; `SerializeAsyncEnumerable` for JSONL streaming. Not quantified.
- Span-based Base64 (`EncodeToUtf8`, `DecodeFromUtf8`, ...), `Utf16.IsValid`, `Utf8.IndexOfInvalidSubsequence`, `INumberBase<T>.TryParsePartial`, `BitArray.PopCount`, `BitArray(ReadOnlySpan<...>)`, `EqualityComparer<T>.Create(keySelector)`, new vector lane APIs (`Zip`, `Unzip`, `Concat*`). Not quantified.
- Span-based streamless Deflate/ZLib/GZip encoders and decoders with `Reset()`, Zstandard dictionaries, `ReadOnlyMemoryStream`/`StringStream` stream views.
- System.IO.Pipelines contention reduction (high fan-in). Not quantified.
- Generic `[LoggerMessage]` (avoids boxing), `MemoryCache` `TrackStatistics` metrics, `Math.BigMul`, `Matrix4x4.GetDeterminant` (MS: about 15 percent, irrelevant here).
- Non-backtracking regex fix for super-linear nested loops.
- No change is documented for SHA-2/HMAC speed on Linux.

ASP.NET Core 11 (Learn release notes, first 100 KB read; the last 8 KB, where DataProtection and startup notes would sit, were not read):

- Kestrel HTTP/1.1 parser no longer throws for malformed requests (MS: up to 20 to 40 percent for malformed traffic only).
- `ResponseBufferingStream` pooling in HTTP logging; `[ShortCircuit]` for endpoints after routing; HTTP/3 starts requests without waiting for SETTINGS; Zstandard response compression and request decompression (on by default); `IOutputCachePolicyProvider`.

EF Core 11:

- Fewer joins and no redundant `ORDER BY` keys for to-one includes (MS: 29 percent split query, 22 percent single query in their scenario), no-op `CAST` stripping, SQL Server 2025 `JSON_CONTAINS`, vector columns not loaded by default (MS: about 9x local), Cosmos on System.Text.Json. SQL Server and SQLite specifics.

C# 15 (Learn: What's new in C# 15):

- Collection expression arguments (`[with(capacity: n), ..xs]`, `[with(StringComparer.OrdinalIgnoreCase), ...]`), union types, closed hierarchies, extension indexers, labeled `break`/`continue`, non-virtual static interface members on older runtimes, memory-safety preview. The page claims no performance benefit for any of them; gains below are second-order (fewer re-allocations, simpler control flow).

## 3. Codebase facts that shape the ranking

- `Autheris.Api.csproj`: Server GC, `GCDynamicAdaptationMode=1` (DATAS), `TieredPGO=true`, `OptimizationPreference=Speed`, framework-dependent publish, R2R removed (#509, spike NO-GO on .NET 10: cold start and warm latency both got worse, image +68 MB; `benchmarks/results/r2r-spike-report.md`).
- About 2,200 `async` methods, about 5,300 `ConfigureAwait` calls, 4 `AsyncLocal` slots (`TelemetryTenantContext`, `SourceMetadataGuard`, `UsageRequestScope`, `CatalogWriteScope`), 14 `Channel` uses.
- STJ: 209 files use `JsonSerializer`/`JsonDocument`/`JsonNode`, about 377 `Serialize`/`Deserialize` call sites, but only one `JsonSerializerContext`. The rest is reflection-based metadata generation.
- Already modern: `[GeneratedRegex]` everywhere (no `new Regex`), `SHA256.HashData` one-shot APIs, `Convert.ToHexStringLower`, about 270 `FrozenDictionary`/`SearchValues`/`FrozenSet` uses, `ConcurrentDictionary`-based caches, `IMemoryCache` in `ConsentCacheService`.
- Per-decision allocations found: `AccessDecisionPoint.DecideAsync` builds `new HashSet<string>(TenantRoles.Concat(ClusterRoles), OrdinalIgnoreCase)` on every read decision (line 167). Hashing sites do `SHA256.HashData(Encoding.UTF8.GetBytes(string))`, which allocates a byte array and the hex string (`AuditChainEngine`, `AuditCanonicalizer`, `TrustedDocumentNormalizedHashCache`, `VirtualFilterModels`, `CallerAttributes`, `CatalogSchemaModel`). Roughly 680 `new HashSet<string>`/`new Dictionary<string,...>` sites in Application and Domain, and about 280 `ToLowerInvariant`/`ToUpperInvariant` calls in Api, Application, GraphQL.
- EF Core is not a hot path (SQL goes through the Autheris compiler and provider drivers; only `Autheris.Application.csproj` references the EF package). EF 11 gains are therefore out of scope.

## 4. Prioritised opportunities

Legend: Gain MS = Microsoft figure, H = hypothesis to be measured. Effort in person-days of Hydrogen work including measurement. "Quick win" = safe to implement now behind the existing benchmark and test gates.

### P1. Runtime Async on `net11.0` for Api, Application, Infrastructure, GraphQL (`<Features>runtime-async=on</Features>`)

- Where: `Directory.Build.props` (opt-in per project via property); candidates `src/Autheris.Api`, `src/Autheris.Application`, `src/Autheris.Infrastructure`, `src/Autheris.GraphQL`. About 2,200 async methods, nearly all request-path code.
- Expected gain: H: fewer per-await allocations (no state machine boxing on suspension, continuation reuse), smaller JIT/IL footprint (cold start), shallower live stacks. MS gives no numbers and asks for field reports. The library layer (HttpClient, Kestrel, SqlClient dependencies on `Microsoft.*`) is already on it, so the app-side gain is bounded.
- Risk: medium. `AsyncLocal` slots (4) must keep flowing across awaits (add a unit test per slot, including `UsageRequestScope` and `CatalogWriteScope`). Behavioural diffs in stack traces, profilers, and exception filters. Analyzers (`CA2012` etc.) and Stryker/mutation tooling may not understand the feature flag; check G7 nightly. Keep a per-project opt-out (`UseRuntimeAsync=false`).
- Measure: `AccessDecisionPointBenchmark` and `GatewayLoadBenchmark` with `[MemoryDiagnoser]` before/after (allocated bytes per op, mean), `benchmarks/load` ramp script for P99, `cold-start.sh` for cold P50. Accept only if no regression beyond 10 percent budget tolerance and allocation drops.
- Effort: 2 to 3. Not a quick win (needs a full integration/parity run on GitHub Actions), but it is a one-line flip per project and trivially reversible.

### P2. Re-run the ReadyToRun spike on .NET 11 with scoped R2R (Autheris assemblies only) and `TieredPGO`/`TieredCompilation` variants

- Where: `src/Autheris.Api/Autheris.Api.csproj` (R2R removal comment, #509), `benchmarks/load/scripts/measure-r2r-impact.sh`, `benchmarks/results/r2r-spike-report.md`, `Dockerfile` publish step.
- Why now: three .NET 11 R2R changes target exactly the spike's failure modes: default comparers specialised in R2R images (MS: up to 20x for collection operations; the old spike showed warm P50 +154 percent, which is consistent with R2R code stuck on slow generic/comparer paths until tier-up), R2R target raised to x86-64-v3 on Linux (AVX2 code in precompiled images), and async inlining in crossgen2. Baseline to beat: cold P50 4.52 s (budget 5.0 s), warm P99 3.47 ms, image budget 440 MB (non-R2R image 373 MB).
- Expected gain: H: cold start P50 reduction of the spike's original target (25 percent) is plausible if the warm regression is gone; unknown until measured.
- Risk: footprint gate (+68 MB on .NET 10 would exceed the 440 MB budget); security checks (`ReadyToRunPublishSecurityTests`, SC-04 no TestAuth in published assemblies) must stay green. Scope R2R to Autheris assemblies (framework assemblies are already R2R) and compare against composite and partial R2R before deciding.
- Measure: same five gates as the .NET 10 report (cold P50, warm P50, warm P99, image size, SC-04), 10 cold restarts, 1000 warm queries, same host.
- Effort: 2 (script exists). Spike only, no merge unless all gates pass. Not a quick win.

### P3. Reflection-based STJ to source generation on hot payloads, plus pooled `Utf8JsonWriter`

- Where: `src/Autheris.Application/Mcp/Services/McpResultJson.cs` (MCP result serialization with a custom contract modifier), `src/Autheris.Api/Middleware/GatewayProblemWriter.cs`, `src/Autheris.Api/Errors/ApiProblems.cs`, audit details JSON in `Autheris.Domain/Audit`, GraphQL result writers, the 377 `JsonSerializer.*` sites (rank by request frequency first). Only one `JsonSerializerContext` exists today.
- Feature: .NET 11 source-gen supports non-public accessors, inaccessible constructors and `in/ref/out` parameters (removes common reasons for staying on reflection), generic `GetTypeInfo<T>()`, `Utf8JsonWriter.Reset(...)`.
- Expected gain: H: removes first-call reflection metadata build (cold start and first-request latency) and per-call overhead; allocations from writer creation avoided by `Reset`. MS gives no figures.
- Risk: low to medium. Source-gen contracts change `JsonSerializerOptions` semantics (naming policy, `OmitEmptyMembers` modifier in `McpResultJson`, polymorphism); need golden-output tests per payload. MCP and GraphQL wire formats must stay byte-identical (parity suite).
- Measure: new BenchmarkDotNet class `JsonSerializationBenchmark` (MCP result, problem details, audit details) with `[MemoryDiagnoser]`, plus `cold-start.sh` first-request time.
- Effort: 3 to 5 for the top 5 payloads. Not a quick win as a whole; one payload at a time is safe.

### P4. Per-decision allocation cleanup in the PDP and audit hashing (quick wins, mostly independent of .NET 11)

- Where and what:
  - `src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs:167`: `new HashSet<string>(subject.TenantRoles.Concat(subject.ClusterRoles), OrdinalIgnoreCase)` per decision. Replace with a pre-sized set built without LINQ (C# 15 `[with(capacity: a.Count + b.Count, StringComparer.OrdinalIgnoreCase), ..a, ..b]` reads well and avoids rehash growth) or cache the role set on the immutable `Subject`.
  - `Autheris.Domain/Audit/AuditChainEngine.cs:68, 208, 212` and `AuditCanonicalizer.cs:115`: `Encoding.UTF8.GetBytes(string)` plus `SHA256.HashData` plus `Convert.ToHexString`. Use `ArrayPool<byte>`/`stackalloc` for the UTF-8 buffer, `HashData(ReadOnlySpan<byte>, Span<byte>)`, `Convert.ToHexStringLower(ReadOnlySpan<byte>)`; build the canonical payload with a pooled writer instead of an intermediate `string` where `BuildCanonicalPayload` allows it. Hash bytes must stay identical (chain verification tests are the oracle).
  - Same pattern in `TrustedDocumentNormalizedHashCache.cs:120`, `VirtualFilterModels.cs:583`, `CallerAttributes.cs:87,130`, `CatalogSchemaModel.cs:119,149`, `ConsentDescriptionText.cs:32`.
- Where .NET 11 helps: JIT bounds-check and redundant-check elimination speeds the span code; constant-folded `SequenceEqual` helps ordinal compares on literals after inlining; Base64/UTF-8 span APIs are available if tokens are touched. SHA-2 speed itself is unchanged.
- Expected gain: H: 1 to 3 fewer allocations per decision or audit entry; ADP and audit-append throughput up by a few percent. Small but safe.
- Risk: low if hash outputs are covered by existing audit-chain unit tests plus a golden-vector test.
- Measure: `AccessDecisionPointBenchmark` (allocated bytes/op), audit-append micro-benchmark (new, small `AuditChainBenchmark` over `LinkBatch`).
- Effort: 1 to 2. Quick win.

### P5. Pipeline: `[ShortCircuit]` for cheap endpoints, Zstandard compression, HTTP logging pooling

- Where: `src/Autheris.Api` endpoint registration for health, readiness, Prometheus scrape (`prometheus-net`), static swagger assets; response compression setup for large JSON/Arrow results; `GraphQlEnumerationShieldMiddleware`, `TrustedDocumentsOnlyMiddleware`, `BasicAuthSessionMiddleware` ordering.
- Feature: `[ShortCircuit]` skips remaining middleware (auth, CORS, tenancy, shield) after routing; Zstandard response compression on by default (quality 1 to 22); pooled `ResponseBufferingStream` if HTTP logging is on.
- Expected gain: H: removes middleware cost on probes and scrapes (negligible on the governance path); Zstd can cut bytes for large result sets at lower CPU than gzip/brotli at comparable ratios, which would matter for the Arrow/streaming and GraphQL paths. No Microsoft figure.
- Risk: medium for compression (BREACH-class issues with secrets in compressed responses must be reviewed by the security phase; client Accept-Encoding support), low for `[ShortCircuit]` (must not bypass auth on anything non-public; fail closed, explicit allow-list plus architecture test).
- Measure: `benchmarks/load` ramp (P50/P99 and CPU), bytes on the wire for a large result set.
- Effort: 2. Not a quick win (security review needed for compression).

### P6. C# 15 collection-expression arguments and JIT-friendly idioms across the 680 `HashSet`/`Dictionary` creation sites

- Where: `src/Autheris.Application` and `src/Autheris.Domain` (about 680 `new HashSet<string>`/`new Dictionary<string,...>` sites). Prioritise sites on the compile and decision paths: `src/Autheris.Application/Sql`, `src/Autheris.Application/VirtualFilters`, `src/Autheris.Application/Policy`.
- Feature: `[with(capacity: n, comparer), ..]` presizes and sets comparers in one expression. Labeled `break`/`continue` replaces flag variables in nested scans (SQL compiler walkers) with simpler IL. Union types and `closed` hierarchies give exhaustive `switch` over policy decisions and result shapes without defensive `default` throws.
- Expected gain: H: small. Presizing avoids rehash growth; unions/closed give correctness, not speed. Do not expect measurable throughput change; do it opportunistically when touching a hot method, not as a sweep.
- Risk: low. Caveat: record the SDK/LangVersion minimum; `LangVersion=latest` already gives C# 15 on `net11.0`.
- Measure: `SqlCompilerBenchmarks`, `VirtualFilterResolverBenchmark` allocated bytes/op.
- Effort: 1 per touched hot method. Quick win only when bundled with P4.

### P7. Free JIT and BCL gains to verify (no code change)

- `Guid.NewGuid()` on Linux about 12 percent faster (MS); Autheris has 243 call sites (id generation in catalog, consent, audit). No action; confirm in `ScaleCatalogBenchmark`.
- Default-comparer and bounds-check improvements apply to every `Dictionary<string,...>`/`HashSet<string>` and span loop automatically after the flip. Establish a .NET 10 vs .NET 11 delta by re-running all benchmarks in `benchmarks/Autheris.Benchmarks` on the pre-flip and post-flip commits and storing both under `benchmarks/results/`. This is also the baseline every other item here needs.
- Expected gain: H: low single-digit percent on ADP/compiler benchmarks; MS 12 percent on GUID generation only.
- Risk: none. Effort: 0.5. Quick win (measurement only, do first).

### P8. Observability aids that make later tuning cheaper

- `MemoryCache` `TrackStatistics` (hit/miss/eviction/size instruments) for `ConsentCacheService` L1 (about 450 bytes per entry budgeting there) and other `IMemoryCache` users (23 references). Replaces hand-rolled counters, lets the load runs prove cache sizing. Generic `[LoggerMessage]` removes boxing for generic log methods.
- Expected gain: indirect. Risk: low. Effort: 1. Quick win.

## 5. Evaluated and rejected or deferred

- EF Core 11 query improvements: not on a hot path (see section 3).
- NativeAOT or trimming of the gateway: out of scope (plugin loading with `AssemblyLoadContext`, reflection-heavy GraphQL/OData stack).
- C# 15 memory-safety preview and `unsafe` expressions: preview, no perf gain claimed by Microsoft; no.
- Vector lane APIs, `Matrix4x4`, `Half`/FP16, SVE2: no matching workload.
- GC tuning: .NET 11 documents no GC change on the runtime page; keep Server GC + DATAS as is until a measurement says otherwise.
- Minimum hardware x86-64-v2: not an optimisation, but a compatibility note for the owner: .NET 11 refuses to run on pre-2013-class CPUs.

## 6. Recommended order

1. P7 (baseline: all benchmarks on pre-flip and post-flip commits, both stored).
2. P4 and P8 (quick wins, one PR each, filtered unit tests plus the matching benchmark).
3. P1 (Runtime Async spike branch, measure, merge only if gates pass).
4. P2 (R2R spike rerun with the five gates).
5. P3 (source-gen, payload by payload), P5 (security review first), P6 (opportunistic).

## 7. Work packages

| WP | Content | Verification | Effort | Quick win |
|---|---|---|---|---|
| WP-PERF11-01 | Baseline benchmark run pre/post flip, store JSON under `benchmarks/results/` | Benchmarks run locally one at a time (disk budget), CI nightly job for compiler benches | 0.5 | yes |
| WP-PERF11-02 | PDP role-set and audit/canonical hash allocation cleanup (P4) | Filtered unit tests (`AuditChain*`, `AccessDecisionPoint*`), golden hash vectors, benchmark delta | 1 to 2 | yes |
| WP-PERF11-03 | `MemoryCache.TrackStatistics` and generic `[LoggerMessage]` (P8) | Unit tests for meter names, no behaviour change | 1 | yes |
| WP-PERF11-04 | Runtime Async spike (P1) | ADP and load benchmarks, `AsyncLocal` flow tests, full CI | 2 to 3 | no |
| WP-PERF11-05 | R2R spike rerun on .NET 11 (P2) | Five gates of `r2r-spike-report.md` | 2 | no |
| WP-PERF11-06 | STJ source-gen for MCP result, problem details, audit details (P3) | Golden JSON tests, parity suite, `JsonSerializationBenchmark` | 3 to 5 | no |
| WP-PERF11-07 | `[ShortCircuit]` for probes and scrapes, Zstandard evaluation (P5) | Architecture test for allow-list, security review (SEC-*), load ramp | 2 | no |
| WP-PERF11-08 | Opportunistic C# 15 idioms on touched hot methods (P6) | Compiler and resolver benchmarks | 1 per method | with WP-02 |

## 8. Open questions for the owner

- Q-PERF11-1: Is a Runtime Async spike acceptable given the four `AsyncLocal` scopes and the Stryker G7 tooling? (Recommended: yes, behind a per-project switch.)
- Q-PERF11-2: May the R2R spike use partial/composite R2R of Autheris assemblies only, with the 440 MB image budget unchanged?
- Q-PERF11-3: Is response compression (Zstandard) wanted at all for gateway responses, given the security review it triggers?

## Sources

Microsoft Learn, fetched 2026-11-17: What's new in .NET 11 runtime, What's new in .NET 11 libraries, ASP.NET Core 11 release notes (first 100 KB), What's new in EF Core 11, What's new in C# 15.
