# Review 06 - TrinoSqlEngine and test landscape

Read-only architecture review. Commit base: `88f7df1c` (main).

## 1. Scope and key metrics

### TrinoSqlEngine (src/TrinoSqlEngine)
| Metric | Value | Evidence |
|---|---|---|
| Hand-written C# (96 files, excl. obj/bin) | **21,750 LOC** | `find src/TrinoSqlEngine -name '*.cs' -not -path '*/obj/*'` |
| Generated ANTLR C# (per configuration) | **48,948 LOC** (Parser 30,979; BaseListener 5,242; Listener 4,452; BaseVisitor 4,178; Visitor 2,631; Lexer 1,466) | `src/TrinoSqlEngine/obj/{Debug,Release}/net10.0/SqlBase*.cs` |
| Why "~109k" | Generated code exists twice (Debug + Release obj) = 97,896 + 21,750 hand-written ~ 120k; the real distinct size is ~70.7k | same |
| Generation | Build-time via `Antlr4BuildTasks`; generated files are NOT committed (0 tracked), grammar `SqlBase.g4` 1,573 lines | `TrinoSqlEngine.csproj:19-23,28-33` |
| Legacy code still present | `RlsListener.cs` 980 LOC + `RunLegacyRewrite`/`ShadowDualRun` branch in `FastSqlEngine.cs` | see ARC-S-01 |
| Largest hand-written files | SqlAstBuilder 1,498; RlsListener 980; FastSqlEngine 749; PolicyExpressionParser 724; SqlAstRewriter 665 | wc -l |

Pipeline as implemented (`GovernedSqlCompiler.cs:155-231`): token guards -> ANTLR parse -> `SqlAstBuilder` (own records) -> `AstValidationVisitor` -> `AstSimplificationVisitor` -> `LiteralColumnTypeAnnotator` -> `AstSecurityVisitor` -> `SecurityCoverageVerifier` -> `DialectCapabilityValidator` -> dialect generator -> `EmittedSqlInvariantChecker` -> cache of value-free template.

### Tests (tests/, hand-written, excl. obj/bin; counts = `[Fact]`/`[Theory]` attributes)
| Project | Files | LOC | Tests | Role |
|---|---|---|---|---|
| Autheris.Tests.Unit | 445 | 101,302 | 3,627 | unit (+ some in-process "integration") |
| TrinoSqlEngine.Tests | 108 | 21,026 | 986 | engine unit/golden/property/differential |
| Autheris.Tests.Integration | 82 | 18,511 | 452 | WebApplicationFactory, Testcontainers, SQLite, Spark |
| Autheris.Extensions.Tests | 18 | 6,750 | 184 | extensions |
| Autheris.Tests.SqlCompilerGate | 31 | 4,369 | 89 | CI evidence gate (differential corpus, bench check) |
| Autheris.Tests.Architecture | 9 | 1,017 | 42 | NetArchTest + source-scanning seam tests |
| **Total** | 693 | ~152,975 | **~5,380** | |

Ratio: ~68% Unit, ~18% engine, ~8% integration, ~1.6% gate, ~0.8% architecture. Only 25 `Trait(` usages across all tests (13 in TrinoSqlEngine.Tests, 1 in Gate): essentially no category filtering.

## 2. Strengths
- Clean, strictly layered compile pipeline with immutable `record` AST (`Ast/Nodes/SqlNode.cs:6`, 77 record types), a separate builder boundary (`Ast/Builder/SqlAstBuilder.cs`) and a typed `CompiledSql` result - the parse tree does not reach the emitter.
- Bind-everything guarantee is enforced by an output-side scanner on every compile (`Ast/Emit/EmittedSqlInvariantChecker.cs:34-40`, "production, on every compile"), not by convention; emitter marks structural numeric ranges explicitly (`EmittedRange`, `:10-21`).
- Declarative per-dialect capability record (`Ast/Capabilities/DialectCapabilities.cs:~100`, `DialectCapabilityTable.cs:38-80`) with limits, marker style, tenant comparison style, function map; generator selection is a frozen-dictionary registry that fails closed (`Ast/Generators/SqlDialectGeneratorFactory.cs:10-35`).
- Fail-closed design throughout: unknown dialect/statement bits rejected (`GovernedSqlCompiler.cs:107-125`), policy simplifier ordering by construction (simplify user tree before injecting policy, `:176-184`), independent coverage verifier that "does not trust the injector" (`:200`).
- Value-free template cache with ordinal key comparison (not hash-only identity) and per-table fingerprint revalidation on hit (`Ast/Emit/CompiledSqlTemplate.cs:100-112,164-170`; `GovernedSqlCompiler.cs:228-239`).
- Strong test discipline around the compiler: dedicated architecture tests for seams (`tests/Autheris.Tests.Architecture/CompilerSeamArchitectureTests.cs`), differential gate project, golden tests (`tests/TrinoSqlEngine.Tests/Compiler/CatalogTypeMapGoldenTests.cs`), mutant seams.

## 3. Findings overview

| ID | Sev | Category | Title |
|---|---|---|---|
| ARC-S-01 | High | Consistency | Production still runs the legacy token-stream rewriter by default; the new `GovernedSqlCompiler` has zero production callers |
| ARC-S-02 | High | Layering | Two divergent AST pipelines: `GenerateGovernedSql` (unbound, no verifier/capabilities) vs `Compile` |
| ARC-S-03 | High | Performance | A new OS thread (16 MB stack) is created for every parse |
| ARC-S-04 | Medium | Performance | `new FastSqlEngine()` per call defeats the parser pool, template cache and compiler singleton |
| ARC-S-05 | Medium | Layering | ANTLR types are part of the public `ISqlEngine` contract and used by analyzers outside the parser boundary |
| ARC-S-06 | Medium | Maintainability | Release and Debug builds compile different security code (28 `#if DEBUG` seams in production sources) |
| ARC-S-07 | Medium | Extensibility | Adding a dialect touches 8+ places; parameter-marker logic exists twice and diverges |
| ARC-S-08 | Medium | Correctness | Unbound emission mode keeps inline literals and a text-restore parameter protocol |
| ARC-S-09 | Medium | Performance | Every syntax error is parsed twice (SLL then LL); every error becomes a ParseCanceledException |
| ARC-S-10 | Medium | Coupling | Standalone library is not standalone: governance/tenant/HMAC domain + Autheris names baked in |
| ARC-S-11 | Medium | Cohesion | God types: SqlAstBuilder (1,498), AstSecurityVisitor split over 6 partials, SqlDialectGeneratorBase over 3 partials + 600-line switch |
| ARC-S-12 | Low | Maintainability | AST records hold `IReadOnlyList<>` - record equality is reference equality for children |
| ARC-S-13 | Low | Performance | Cache key = full raw SQL text + reflection serialization of the whole request, built on every compile |
| ARC-S-14 | Low | Consistency | `TrustedSqlExpression` is still a raw-string AST node (planned for deletion) |
| ARC-S-15 | Low | Performance | Reflection-based AST walk (`AstReflection`) in compile path of DML/capability validation |
| ARC-S-16 | Low | Maintainability | Build depends on Antlr4BuildTasks (JRE download) and stale package identity (`FastSqlEngine`) |
| ARC-T-01 | High | Maintainability | Monolithic Autheris.Tests.Unit: 445 files / 101k LOC / 3,627 tests referencing every src project |
| ARC-T-02 | Medium | Maintainability | Test double duplication: `StubEpochValidationService` x8, `TestOptionsMonitor<T>` x6, etc. |
| ARC-T-03 | Medium | Testability | Timing-based tests (Task.Delay x52, Thread.Sleep) and wall-clock time (656 `DateTime.*Now`) vs 19 TimeProvider uses |
| ARC-T-04 | Medium | Testability | Real file system and loopback usage in unit tests (62 temp-path, 96 localhost lines) without categories |
| ARC-T-05 | Medium | Maintainability | Oversized, date/ticket-named test files (up to 1,456 LOC) organized by review date, not behavior |
| ARC-T-06 | Medium | Consistency | Legacy differential tests keep the legacy path alive (15 test files reference RewriteRls/RlsListener) |
| ARC-T-07 | Low | Testability | Integration + Gate suites forced single-threaded; no trait-based split in CI |
| ARC-T-08 | Low | Consistency | Architecture tests are source-text scanners mixed with NetArchTest; too few (42) for a 10-assembly solution |

## 4. Details - SQL engine

### ARC-S-01 Legacy path is the production default; the new compiler is not wired
- **Severity:** High | **Category:** Consistency
- **Evidence:** `src/TrinoSqlEngine/FastSqlEngine.cs:106` (`SqlRewriterEngine { get; set; } = "LegacyTokenStream"`); `FastSqlEngine.cs:655-690` (switch incl. `ShadowDualRun` and `RunLegacyRewrite` -> `new RlsListener`); `src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs:743` (`_sqlEngine.RewriteRls(...)`) and `:728` (`webSqlOptions.SqlRewriterEngine ?? "LegacyTokenStream"`); `src/Autheris.Domain/Options/GatewayOptions.cs:1612-1615`. `grep -rn "CompileRequestFactory\|\.Compile(" src` outside the engine finds no caller of `ISqlEngine.Compile` or `CompileRequestFactory.Create` (`src/Autheris.Application/Sql/Policy/CompileRequestFactory.cs:14` is defined but unused). Legacy remnants also in `PolicyPredicateRenderer.cs:43,71` (`TrustedSqlExpression`).
- **Problem:** ~6k LOC of finished compiler (GovernedSqlCompiler, capability table, binders, verifier, invariant checker) is untouched by any production request; the shipping path is the 980-line `RlsListener` token rewriter with string splicing. The X1 plan (`docs/plans/2026-10-10-plan-ast-dialect-generator.md:3 Status`, `:25-27`) states cutover is blocked on X0 packages.
- **Impact:** The security properties tested and reviewed (typed policy nodes, bind-everything, coverage proof) are not the ones enforced in production. Two engines must be maintained and differential-tested; risk of the big-bang switch grows with every feature added to only one side.
- **Recommendation:** Treat X1 as the top priority. Intermediate step: make `AstCompiler` the default via `RlsOptions.RewriterEngine`, and have `GovernedSqlRewriter` call `ISqlEngine.Compile` via `CompileRequestFactory` behind a per-datasource feature switch; mark `RlsListener`/`ShadowDualRun` `[Obsolete(error: true)]` outside tests. Delete `ShadowDualRun` immediately (it swallows all exceptions: `FastSqlEngine.cs:668-671`) as it provides no evidence in production and plan SD-2 already rejects it.
- **Effort:** L

### ARC-S-02 Two AST pipelines with different guarantees
- **Severity:** High | **Category:** Layering
- **Evidence:** `FastSqlEngine.cs:703-724` (`GenerateGovernedSql`: Build -> SecurityVisitor -> Simplify (after injection) -> Validate -> `generator.GenerateSql(simplifiedAst)`) versus `GovernedSqlCompiler.cs:155-231` (simplify before injection, annotator, verifier, capability validation, bound `Generate`, invariant checker, cache). Callers of the weaker path: `src/Autheris.Application/VirtualFilters/SqlFilterCompiler.cs:369`; interface `ISqlEngine.cs:38-48`. The plan itself lists the ordering as a known defect (plan finding F-11, `FastSqlEngine.cs:815-816` in old numbering).
- **Problem:** `GenerateGovernedSql` runs the simplifier on the already-secured tree (injected deny-all `1 = 0` can be folded), skips `SecurityCoverageVerifier`, `DialectCapabilityValidator`, `EmittedSqlInvariantChecker`, limit budgets and uses the unbound (string) emission mode. It is public on the `ISqlEngine` interface so any consumer can reach it.
- **Impact:** Same library, two security levels; the weaker one is the one reachable via the interface and one live caller.
- **Recommendation:** Remove `GenerateGovernedSql` from `ISqlEngine`; have it delegate to `Compile` with a synthesized `CompileRequest` or make it `internal` for tests. Migrate `SqlFilterCompiler` to `Compile`.
- **Effort:** M

### ARC-S-03 One new OS thread per parse
- **Severity:** High | **Category:** Performance
- **Evidence:** `FastSqlEngine.cs:208-240` (`RunWithParseBudget` -> `new Thread(..., ParseThreadStackSize)`); default stack `FastSqlEngine.cs:36` (16 MB); limiter `:42-48` (`ProcessorCount*2`).
- **Problem:** Every `Parse`/`ParseExpression` (also used for each policy filter parse in `AstSecurityVisitor.ParseFilterExpression`, `Ast/Visitors/AstSecurityVisitor.cs:111-118`) creates, starts and joins a dedicated thread with a 16 MB reserved stack; the ObjectPool of parsers (`:50-51`) is the only reuse. Parsing is CPU-bound so the thread only exists to give a bounded stack and a timeout.
- **Impact:** Thread creation (tens of microseconds plus kernel mmap of a 16 MB stack) dominates the cost of small queries; the engine's own perf budgets (plan NFR-2/3) are measured on the compile cache hit path, which hides this. The semaphore also makes parse concurrency a global static (`SharedParseConcurrencyLimiter`, `:48`) shared by all tenants.
- **Recommendation:** Run on the caller thread when `sql.Length` is below a threshold (e.g. 4 KB, where ANTLR recursion depth cannot exceed the default 1 MB stack given `MaxNestingDepth`), only escalate to a big-stack worker (pooled dedicated threads, e.g. a small custom `TaskScheduler` of long-lived threads) for larger inputs. Keep `ExecutionStackGuard` for depth.
- **Effort:** M

### ARC-S-04 `new FastSqlEngine()` per call
- **Severity:** Medium | **Category:** Performance
- **Evidence:** `src/Autheris.Application/VirtualFilters/SqlFilterCompiler.cs:73,172,369`; `src/Autheris.Api/Endpoints/GovernanceEndpoints.cs:673`; `src/Autheris.Application/Policy/Lint/PolicyParserSetup.cs:19`; `StructuredFilterSqlBuilder.cs:36` and `AstSecurityVisitor.cs:57` (`engine ?? new FastSqlEngine()`). `FastSqlEngine.cs:50-51` (pool per instance), `:630-634,637` (compiler and `CompiledSqlTemplateCache` per instance). A singleton exists (`FastSqlEngine.cs:33` `Default`, DI at `GatewaySqlEngineServiceExtensions.cs:59`).
- **Problem:** Each `new` allocates a fresh `DefaultObjectPool<SqlBaseParser>` (cold parser: first-use ATN deserialization cost / DFA cache warm-up per `ATNSimulator` instance), a new template cache and a new `Lazy` compiler, so none of the performance mechanisms apply on those paths. ANTLR's shared static DFA mitigates part of it, but not the parser allocation or caches.
- **Impact:** Virtual-filter compilation (a hot request path) pays full cold cost each time; cache hit rates for `CompileCache` are 0 on these paths.
- **Recommendation:** Make the constructor `internal`/obsolete for production use; inject `ISqlEngine` everywhere, use `FastSqlEngine.Default` in static helpers; add an architecture test banning `new FastSqlEngine()` in src outside the DI registration.
- **Effort:** S

### ARC-S-05 ANTLR leaks through the public contract
- **Severity:** Medium | **Category:** Layering
- **Evidence:** `src/TrinoSqlEngine/ISqlEngine.cs:5-6,53,61` (`Parse` returns `SqlBaseParser.SingleStatementContext` + `CommonTokenStream`); `Analysis/SqlQueryAnalyzer.cs:6,9,35` (a `SqlBaseBaseListener` with 75 ANTLR references), `Analysis/ISqlQueryAnalyzer.cs`; `Governance/PolicyExpressionParser.cs`; `AstSecurityVisitor.cs:12` (`using Antlr4.Runtime`), `GovernedSqlCompiler.cs:278-284` (classification on `SqlBaseParser.*Context`). Parse tree consumers outside the builder: 15 files.
- **Problem:** The AST boundary is respected by the emitter, but a second analysis stack (`SqlQueryAnalyzer`, `SqlParameterExtractor`, 285+547 LOC) walks the parse tree and re-implements table/CTE scoping, so semantics (aliases, CTE shadowing) exist twice: once in the listener, once in `SqlAstBuilder`/`AstSecurityVisitor`. The generated `SqlBaseParser` (public, 31k LOC) is part of the NuGet surface.
- **Impact:** Any grammar change breaks consumers; table-access authorization (analyzer) and RLS injection (AST) can disagree on what tables a query touches. Package cannot swap or upgrade the parser.
- **Recommendation:** Make `Parse`/`ParseExpression` `internal`; expose `ParseToAst(...)` returning `SqlStatement`; reimplement `SqlQueryAnalyzer` as an AST walker (the AST already has `NamedTableSource`, CTE nodes) and cross-check with a property test against the old output before deleting. Classification in `GovernedSqlCompiler:278` should use the AST statement type.
- **Effort:** L

### ARC-S-06 Debug and Release compile different compilers
- **Severity:** Medium | **Category:** Maintainability
- **Evidence:** 28 `#if DEBUG` blocks in src/TrinoSqlEngine, e.g. `GovernedSqlCompiler.cs:101-110,191-209`, `Ast/Emit/CompiledSqlTemplate.cs:84,235,316`, `CompilerTestSeams.cs:1`; `TrinoSqlEngine.csproj:36-38` (`InternalsVisibleTo Autheris.Application/Infrastructure` only in Debug).
- **Problem:** Security-control disable switches (`SecurityControl.CoverageVerifier`, `CacheRebindPolicyValues`, `DmlGuardMutator`, `FaultyInjector`) are compiled into Debug assemblies, which is what `dotnet test` runs by default (CLAUDE.md:7). The shipped Release binary has code paths (`#else` branches) that the default test run never executes, and Debug contains kill switches for security controls.
- **Impact:** Test-vs-prod divergence on exactly the code that is the security boundary; mutation-test harness lives inside the product assembly. The memory note "Release build with warnings as errors before every push" shows Release is a second-class test target.
- **Recommendation:** Replace `#if DEBUG` with an injected `ICompilerHooks` (null object in production, JIT-eliminated by a `static readonly bool` is not needed - a single virtual call per stage is cheap) defined in a separate test-support assembly; or run the full engine suite in Release in CI and add an architecture test that Release has no `CompilerTestSeams` type (the file already is compiled out - assert it).
- **Effort:** M

### ARC-S-07 Adding a dialect touches many places; marker logic duplicated
- **Severity:** Medium | **Category:** Extensibility
- **Evidence:** Places keyed on `TargetSqlDialect`: enum (`IRlsPolicyProvider.cs`), generator registry (`Ast/Generators/SqlDialectGeneratorFactory.cs:12-23`), capability table (`DialectCapabilityTable.cs:38-258`), function allowlist (`SqlFunctionAllowlists.cs:97-108,191-196`), binders (`Ast/Emit/CompiledSqlBinders.cs:32-37,47-49`), type map (`Ast/Security/CatalogTypeMap.cs:234,262,270`), emitter context markers (`Ast/Buffer/SqlEmitterContext.cs:275-322`), token guards (`SqlTokenSecurityOptions.cs:84-91`), security visitor (`AstSecurityVisitor.Tables.cs:216`, `.TypedDml.cs:186`, `.MaskGuards.cs:218,262`), `CompileRequest.cs:115`, `SqlIdentifierHelper.cs:67`, `Generators/...Base.Functions.cs:577`. Marker duplication: capability `ParameterMarkerStyle` (`SqlEmitterContext.cs:191,237-243`) versus hard-coded `switch (Dialect)` with `_ => "?"` fallback (`:275-322`, used from `SqlDialectGeneratorBase.Expressions.cs:305`). Databricks (capability `ColonNamedOrdinal`, `DialectCapabilityTable.cs:162`) has no case in that switch and silently falls into `?{n}`.
- **Problem:** The capability record says "data, not code" (`DialectCapabilities.cs` doc) yet behavior still branches on the enum in ~14 files, and one of those branches has a silent default, contradicting the fail-closed rule stated in `SqlDialectGeneratorFactory.cs:30`.
- **Impact:** Adding Snowflake-to-production or another dialect is a scavenger hunt; the Databricks inconsistency is a latent bug in the unbound path (`:290-320`).
- **Recommendation:** (1) Delete `NextParameterMarker`/`FormatNextParameterMarker`; drive from `Capabilities.MarkerStyle` only. (2) Move dialect-specific rules (`IsSqlServer` mask guards, Oracle upper-casing, quoting of identifiers) to `DialectCapabilities` flags or virtual members on the generator. (3) Add a registry test enumerating `Enum.GetValues<TargetSqlDialect>()` asserting every dialect has generator, capability, binder, allowlist (partially present).
- **Effort:** M

### ARC-S-08 Dual emission modes (bound vs unbound) keep inline literals
- **Severity:** Medium | **Category:** Correctness
- **Evidence:** `Ast/Generators/SqlDialectGeneratorBase.Expressions.cs:264-307` (`FormatParameter`: `if (context.IsBound) ... else` text placeholders `__param_name`), `:34,64,237,268`; `IsBound` branches in 20+ places (e.g. `OracleDialectGenerator.cs:209,223,242`, `SqliteDialectGenerator.cs:79,110,127,151,355`, `SqlServerDialectGenerator.cs:165,193`, `DatabricksDialectGenerator.cs:139`); `ISqlDialectGenerator.cs:17-18` exposes `GenerateSql(...)` string overloads; plan F-5.
- **Problem:** The bind-everything invariant is enforced only in bound mode (`EmittedSqlInvariantChecker` runs in `Generate`, not in `GenerateSql(string)`). Unbound mode inlines literals, uses named placeholders restored by regex in the caller, and has per-dialect divergent code in every generator.
- **Impact:** Doubles generator surface and test matrix; the "every value bound" guarantee is architecture-enforced only for callers that use `Compile`, convention otherwise.
- **Recommendation:** After X1, delete `GenerateSql(string)` and `IsBound`; make bound mode the only mode (the ParameterSource already supports client params) and make the invariant checker mandatory inside the generator base. Until then, mark the string API `internal` and forbid it with the architecture test in `CompilerSeamArchitectureTests`.
- **Effort:** M

### ARC-S-09 Syntax errors are parsed twice
- **Severity:** Medium | **Category:** Performance
- **Evidence:** `FastSqlEngine.cs:338-361` (SLL with Bail strategy `ParserPooledObjectPolicy.cs:11-15`; any `ParseCanceledException` -> `tokens.Reset(); parser.Reset(); ... PredictionMode.LL` full reparse); `:229-231`.
- **Problem:** The standard SLL->LL two-stage is right, but it cannot distinguish SLL conflicts from real syntax errors, so malformed input (and an attacker's fuzz input) always costs a second, slower LL pass, and the LL pass can be exponential on crafted ambiguous input. No fast pre-check and no negative cache for failed SQL.
- **Impact:** Amplification factor of 2 to LL cost per bad request, bounded only by the 5 s `ParseTimeout` (`:77`) and the 2x-CPU parse semaphore, which can then be saturated by invalid SQL.
- **Recommendation:** Keep LL fallback, but (a) record the SLL error position and re-run LL only if the SLL failure was an ambiguity (`ParseCanceledException.InnerException` is `InputMismatchException` vs `NoViableAlt`), (b) use `ThrowingErrorListener` in SLL for plainly invalid tokens, (c) cap LL fallback time to e.g. 1 s. Add a benchmark for invalid inputs.
- **Effort:** M

### ARC-S-10 Library is not standalone; domain concepts and product names inside
- **Severity:** Medium | **Category:** Coupling
- **Evidence:** No project reference to Autheris (OK: `TrinoSqlEngine.csproj` references only ANTLR, ObjectPool). But: meter `"Autheris.SqlCompiler"` (`CompilerTelemetry.cs:12`), `InternalsVisibleTo Autheris.Tests.Unit`, `Autheris.Application`, `Autheris.Infrastructure` (`TrinoSqlEngine.csproj:36-38`), tenant/mask/HMAC concepts (`Governance/TypedPolicyContext.cs`, `TenantComparisonStyle` in `DialectCapabilities.cs:~25-60`, `InDbHmac` flag), audit HMAC (`Ast/Emit/CompiledSqlTemplate.cs:328-344`), `RlsOptions`/`IRlsPolicyProvider.cs` (366 LOC) mixing options and engine API, PackageId `FastSqlEngine` with its own LICENSE/NOTICE (`TrinoSqlEngine.csproj:8-10`).
- **Problem:** The README/package position is a generic "Trino-compatible SQL parser and RLS rewriter"; in reality it is the Autheris governance kernel (typed policy, masks, DML guards, audit). Application InternalsVisibleTo (even Debug only) creates hidden coupling (see ARC-S-06).
- **Impact:** Cannot version/publish the parser separately; the hard split between "SQL front end + dialect emitter" (generic, ~14k LOC) and "governance" (Autheris specific) is missing, so the engine cannot be reused or benchmarked independently.
- **Recommendation:** Split into `TrinoSqlEngine.Core` (parser, AST, builder, generators, capabilities) and `TrinoSqlEngine.Governance` (security visitor, verifier, policy context, compiler, cache). Rename meter to a neutral name configurable by the host. Drop the Application/Infrastructure `InternalsVisibleTo`.
- **Effort:** L

### ARC-S-11 God classes / partial-file sprawl
- **Severity:** Medium | **Category:** Cohesion
- **Evidence:** `Ast/Builder/SqlAstBuilder.cs` 1,498 LOC (73 public members); `AstSecurityVisitor` in 6 files totalling ~2,250 LOC (`AstSecurityVisitor.cs` 250, `.Dml` 505, `.MaskGuards` 384, `.Support` 302, `.Tables` 247, `.TypedDml` 564); `SqlDialectGeneratorBase*.cs` 647+485+618 with a ~100-case `switch (expression)` (`SqlDialectGeneratorBase.Expressions.cs:20-230`); visitor interface with ~70 methods (`Ast/Visitors/ISqlAstVisitor.cs`) plus a type-switch dispatcher (`SqlAstRewriter.cs:35-...`).
- **Problem:** Partial classes hide size rather than separating responsibilities (RLS injection, DML check options, mask guards, tenant predicates are one visitor with shared mutable state `_options`, scope stack). Adding an AST node requires edits in builder, visitor interface, rewriter switch, validator, generator switch, verifier (`SecurityCoverageVerifier.cs:480`) and `AstReflection` consumers; the `default: throw NotSupportedException` (`Expressions.cs:226-229`) turns a missed case into a runtime failure.
- **Impact:** High change cost and review risk in the security-critical visitor; missed-node bugs only surface at runtime.
- **Recommendation:** Split `AstSecurityVisitor` into composable passes (`RowFilterInjector`, `MaskProjector`, `DmlGuardPass`) sharing a `SecurityScope` object; split `SqlAstBuilder` by statement/expression/table-source builders. Add an exhaustiveness unit test (reflection over `SqlNode` subclasses vs generator/rewriter/verifier handlers) - partially exists via `SecurityCoverageTests`.
- **Effort:** L

### ARC-S-12 Records with `IReadOnlyList` break value semantics
- **Severity:** Low | **Category:** Maintainability
- **Evidence:** `Ast/Nodes/Clauses.cs:5,35-42`, `Expressions.cs` (7), `SqlStatement.cs` (7), `TableSources.cs` (3) use `IReadOnlyList<>`; 0 `ImmutableArray` in `Ast/Nodes`, though `AstReflection.cs:10` handles `ImmutableArray`.
- **Problem:** Records advertise structural equality, but list members compare by reference, so `==`/`with`-based change detection (e.g. rewriter "unchanged" short-circuits) and snapshot tests compare incorrectly or require custom comparers; lists are mutable if a caller keeps the backing `List<T>`.
- **Impact:** Latent aliasing bugs; golden tests must serialize rather than compare nodes.
- **Recommendation:** Use `ImmutableArray<T>` (or a `EquatableArray<T>` wrapper) for child collections; the builder already creates them once.
- **Effort:** M

### ARC-S-13 Cache key is the raw SQL plus a reflective request dump
- **Severity:** Low | **Category:** Performance
- **Evidence:** `Ast/Emit/CompiledSqlTemplate.cs:238-250` (key = version + capability table version + all request properties + `rawSql.Length:rawSql`), `:301-320` (property reflection serialization), `GovernedSqlCompiler.cs:143-145` (`sql.ToString()`, key material string, SHA-256 `HashOf` `:128-155`), single global `lock (_gate)` for Find/CountHit/CountMiss/Add (`:128-176`), FIFO eviction (`_order` queue, `:189-197`).
- **Problem:** Each compile allocates the raw SQL string and a key string the size of the SQL plus policy (kilobytes), hashes it with SHA-256, and takes the global lock three times (Find, CountHit/Miss, Add). Eviction is FIFO, not LRU, so a hot template can be evicted by cold traffic.
- **Impact:** Contention on a multi-core gateway; cache hit path (the claimed performance fast path) is O(SQL length) and serialized.
- **Recommendation:** Key on `(request fingerprint computed once per datasource/policy epoch, xxHash128 of SQL)`, keep the full-text equality check; use `ConcurrentDictionary` + `Interlocked` counters; or `Microsoft.Extensions.Caching.Memory` with size limit and LRU.
- **Effort:** M

### ARC-S-14 `TrustedSqlExpression` remains a raw-text node
- **Severity:** Low | **Category:** Consistency
- **Evidence:** `Ast/Nodes/Expressions.cs:17`; emitted verbatim `Generators/SqlDialectGeneratorBase.Expressions.cs:222-226`; created at `Ast/Visitors/AstSecurityVisitor.cs:108` and `AstSecurityVisitor.Tables.cs:161`; allowed by the verifier `Ast/Security/SecurityCoverageVerifier.cs:480`; app-side producer `Application/Sql/Policy/PolicyPredicateRenderer.cs:43,71`. Plan SD-3 says it is removed.
- **Problem:** A raw SQL string node in the AST contradicts the "all values bound / no raw fragments" architecture; the invariant checker is the only backstop.
- **Impact:** Any future producer of this node bypasses AST typing; node is public.
- **Recommendation:** Make the record `internal`, constructor-restricted to the security visitor (`internal` factory), and delete with X1; until then add an architecture test that only 2 files may construct it.
- **Effort:** S

### ARC-S-15 Reflection-based AST walks on compile path
- **Severity:** Low | **Category:** Performance
- **Evidence:** `Ast/Security/AstReflection.cs:14-40` (property getter delegates cached by type); used by `DialectCapabilityValidator.cs:24,117`, `DmlMaskedReadGuard.cs:22`, `InsertCheckPolicy.cs:44`, `AstSecurityVisitor.TypedDml.cs:251,526`, `AstSecurityVisitor.Tables.cs:99`, `SecurityCoverageVerifier.cs:409`.
- **Problem:** Doc says "off the hot path" but `DialectCapabilityValidator.Validate` runs on every compile (`GovernedSqlCompiler.cs:214`) and DML guard walks per statement; each walk boxes enumerators and uses delegate getters per property.
- **Impact:** Moderate allocation per compile on cache miss; also hides the node-coverage problem from the compiler (no exhaustive switch).
- **Recommendation:** Generate a child-enumeration method per node via a source generator (or implement `IEnumerable<SqlNode> Children()` in the base record) and walk with an explicit stack; keep reflection only in an exhaustiveness test.
- **Effort:** M

### ARC-S-16 Build-time Java/ANTLR tooling and stale package identity
- **Severity:** Low | **Category:** Maintainability
- **Evidence:** `TrinoSqlEngine.csproj:15-16` (`Antlr4BuildTasks`, downloads/needs Java), `:8-10` (PackageId `FastSqlEngine`), duplicated generated output in `obj/Debug` and `obj/Release` (97,896 LOC), `:6,12-13` (`LangVersion 14` while CLAUDE.md demands C# 13 idioms - verify), German comment "Maximale JIT- & GC-Optimierung" violates the English-only mandate (`:12`), `FastSqlEngine.cs:329` ("Stufe 1/2" German comments).
- **Problem:** Clean-room builds need Java and network; generated code is invisible to reviewers and to coverage/mutation (Stryker) unless excluded.
- **Impact:** CI fragility; mutation/coverage noise from 49k generated lines.
- **Recommendation:** Exclude generated files via `[GeneratedCode]`/editorconfig `generated_code = true` and Stryker `mutate` globs (check `stryker-config.json`), pin the ANTLR tool jar via `Antlr4BuildTasks` `AntlrToolVersion`, translate remaining German comments.
- **Effort:** S

## 5. Details - Tests

### ARC-T-01 Monolithic Unit project
- **Severity:** High | **Category:** Maintainability
- **Evidence:** `tests/Autheris.Tests.Unit` 445 files, 101,302 LOC, 3,627 tests (66% of all tests); `Autheris.Tests.Unit.csproj` references Domain, Application, Infrastructure, GraphQL, Api, Extensions and TrinoSqlEngine (project refs list). `InternalsVisibleTo Autheris.Tests.Unit` in `TrinoSqlEngine.csproj:36`. Contains architecture-type tests (`tests/Autheris.Tests.Unit/ArchitectureAndCleanCodeTests.cs`) and security-review dumps next to unit tests. Largest folder-level files: `SecurityFindingsRemediationTests.cs` 1,409; `ProcedureEndpointTests.cs` 1,349; `SecurityReview20261002WebSqlTests.cs` 1,237.
- **Problem:** One assembly compiles against every layer, so (a) any change rebuilds all 101k lines, (b) layering violations in tests are invisible, (c) tests depending on Api/Infrastructure are labeled "unit".
- **Impact:** Slow feedback; no per-layer coverage/mutation gating; tests for engine behavior are split between this project and `TrinoSqlEngine.Tests` (e.g. `tests/Autheris.Tests.Unit/Sql/AstCompilerWebSqlTests.cs`, `Security/CorrelatedRowFilterAliasTests.cs`).
- **Recommendation:** Split into `Autheris.Domain.Tests`, `Autheris.Application.Tests`, `Autheris.Infrastructure.Tests`, `Autheris.Api.Tests` mirroring src; move compiler tests that reference `RlsOptions`/engine into `TrinoSqlEngine.Tests`. Keep a thin shared `Autheris.Tests.Common`.
- **Effort:** L

### ARC-T-02 Duplicated test doubles
- **Severity:** Medium | **Category:** Maintainability
- **Evidence:** `StubEpochValidationService` x8 (`tests/Autheris.Tests.Unit/ComprehensiveSecurityAttackVectorTests.cs:687`, `Governance/ConsentFourEyesSensitivityTests.cs:21`, `ItsmClientsAndRecertificationTests.cs:389`, `ItsmOutboxTests.cs:23`, `Security/SecurityReviewRemediationCoverageTests.cs:694`, `SecurityAttackVectorTests.cs:413`, `SecurityReview20261002EndpointTests.cs:731`, `SecurityReviewG5Tests.cs:468`); `TestOptionsMonitor<T>` x6 (`AuthenticationSecurityTests.cs:623`, `Governance/ClassificationEngineTests.cs:128`, `ClassificationWorkflowStateMachineTests.cs:212`, `ClassificationWorkflowTests.cs:268`, `WormConfigurationAuditServiceTests.cs:58`, `WormConfigurationStartupAuditorTests.cs:53`, `SecurityFindingsRemediationTests.cs:380`); `FakeSqlException` x3 (Unit x2, Extensions); `StubHandler` x3, `FakeHttpMessageHandler` x2, `TestTimeProvider` x2, `TestHostEnvironment` x2. `tests/Autheris.Tests.Unit/Common` contains only one file (`TableIdentifierConsistencyTests.cs`).
- **Problem:** No shared test-support library; each file re-declares private nested doubles (an interface change in `IEpochValidationService` requires 8 edits).
- **Impact:** Churn and drift; behavior of "the stub" differs per file.
- **Recommendation:** Create `tests/Autheris.Tests.Common` (builders, fakes: `FakeEpochValidationService`, `StaticOptionsMonitor<T>` -> use `Microsoft.Extensions.Options.Options.Create`/`OptionsMonitor` helpers, `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`, `StubHttpMessageHandler`). Add an architecture test banning private nested classes named `Stub*Epoch*`.
- **Effort:** M

### ARC-T-03 Timing-based and wall-clock tests
- **Severity:** Medium | **Category:** Testability
- **Evidence:** `Thread.Sleep(70)` against a 50 ms TTL (`tests/Autheris.Tests.Unit/GovernedSqlPlanCacheTests.cs:295`); `Task.Delay` in 52 lines / 29 files (23 Unit, 5 Integration, 1 engine), e.g. `tests/Autheris.Tests.Unit/CasbinHotReloadTests.cs:138` (`WhenAny(reloadTcs, Task.Delay(3000))`), `:303`; `tests/Autheris.Tests.Integration/FinOpsEndToEndSqliteTests.cs:124` (polling with `Task.Delay(100)`), `McpDatasetToolsEndToEndTests.cs:167`, `RowFilterChannelParityTests.cs:374`, `tests/Autheris.Tests.Unit/InfrastructureTests.cs:460`, `ChunkedQueryExecutorTests.cs:273,299`; wall clock `DateTime(Offset).(Utc)Now` in 656 lines (113 Unit files) vs only 19 `TimeProvider`/`FakeTimeProvider` mentions (7 Unit files).
- **Problem:** TTL, hot-reload and polling tests sleep real time; wall-clock use makes expiry tests sensitive to CI load.
- **Impact:** Classic flake sources on loaded runners; the 70 ms-vs-50 ms margin in `GovernedSqlPlanCacheTests.cs:295` is a documented race pattern.
- **Recommendation:** Inject `TimeProvider` into `CompiledSqlQueryPlanCache` & friends and advance `FakeTimeProvider`; replace polling with `TaskCompletionSource` signals and a shared `Eventually(...)` helper with generous timeout; ban `Thread.Sleep` in tests via analyzer (BannedSymbols).
- **Effort:** M

### ARC-T-04 Real file system and loopback in "unit" tests, no categorisation
- **Severity:** Medium | **Category:** Testability
- **Evidence:** `GetTempPath|GetTempFileName` in 62 lines/40 files (31 Unit, 8 Integration, 1 Extensions); `localhost:|127.0.0.1|TcpListener` in 96 lines (29 Unit files); `Environment.SetEnvironmentVariable` in Unit and Integration (process-global state, 1 each); Testcontainers referenced in `tests/Autheris.Tests.Unit/Security/SqlProcedureRowScopeResolverTests.cs:25` (comment) and 17 Integration files; `Trait(` used 25 times overall (13 engine, 1 gate, 0 Unit/Integration).
- **Problem:** Unit tests mix in-memory with temp-dir I/O and sockets and cannot be filtered; Unit runs parallel (no collection restriction) while sharing temp locations and env vars.
- **Impact:** Order/race failures under parallelism; cannot run a fast "pure unit" subset in the inner loop.
- **Recommendation:** Add `[Trait("Category","Unit|Integration|Docker|Slow")]` via a base attribute or `Directory.Build.props` source generator by folder; use a `TempDirectory : IDisposable` helper in the common test library; wrap env var mutations in a collection with `DisableParallelization`.
- **Effort:** M

### ARC-T-05 Oversized, date/ticket-named test files
- **Severity:** Medium | **Category:** Maintainability
- **Evidence:** Top sizes: `tests/TrinoSqlEngine.Tests/Compiler/CatalogTypeMapGoldenTests.cs` 1,456; `Autheris.Tests.Unit/SecurityFindingsRemediationTests.cs` 1,409; `Extensions.Tests/SecurityReview20261002ExtensionsTests.cs` 1,357; `Unit/ProcedureEndpointTests.cs` 1,349; `Unit/SecurityReview20261002WebSqlTests.cs` 1,237; `Extensions.Tests/ODataTests.cs` 1,227; `TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs` 1,124; `Unit/SecurityReview20261002InfraTests.cs` 1,114; `TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs`, `SecurityRemediationTests.cs` (940, 53 legacy refs), `Unit/SecurityReviewG5Tests.cs`.
- **Problem:** Tests are filed by review event (`SecurityReview20261002*`, `SecurityFindingsRemediation`, `MustFixRemediation`) rather than by unit under test, so tests of one component are scattered across 5+ files and cannot be found when behavior changes.
- **Impact:** Duplicate coverage, unclear ownership; dead tests survive refactors (e.g. legacy-path tests, ARC-T-06).
- **Recommendation:** Keep the finding id as `[Trait("Finding","SEC-xx")]` (a traceability catalog already exists: `tests/TrinoSqlEngine.Tests/Security/SecurityRegressionCatalog.cs`) and relocate the tests into per-component files; cap files at ~500 lines through an architecture test.
- **Effort:** L

### ARC-T-06 Legacy path is still heavily tested (cutover cost)
- **Severity:** Medium | **Category:** Consistency
- **Evidence:** 15 test files reference `RewriteRls`/`RlsListener`: `tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs` (53 hits), `TrinoParserComplianceTests.cs` (25), `SecurityReview20261002SqTests.cs` (17), `AnalysisAndMaskingTests.cs` (8), `Differential/LegacyVsAstDifferentialTests.cs`, `tests/Autheris.Tests.Unit/GovernedSqlPlanCacheTests.cs` (11), `Security/SecurityReviewRemediationCoverageTests.cs` (7), `Security/CorrelatedRowFilterAliasTests.cs` (5), Gate `DifferentialGateTests.cs`/`Harness/GateRunner.cs`.
- **Problem:** Security regression suites assert behavior of the legacy rewriter; when X1 deletes it these must be re-expressed against `Compile`. Parity coverage of the new path for the same cases is not tracked per test.
- **Impact:** X1 deletion will be blocked or will silently delete coverage.
- **Recommendation:** For each legacy-based test add (or convert to) a `Compile`-based theory first (parametrize over engine), mapping through `SecurityRegressionTraceabilityTests`; fail CI when a catalog entry has legacy-only coverage. Do this before X1, not during.
- **Effort:** L

### ARC-T-07 Serialized suites without selective execution
- **Severity:** Low | **Category:** Testability
- **Evidence:** `tests/Autheris.Tests.Integration/AssemblyAttributes.cs:3`, `xunit.runner.json:5` and `tests/Autheris.Tests.SqlCompilerGate/AssemblyAttributes.cs:3` (`DisableTestParallelization`, `MaxParallelThreads = 1`); Spark tests use a custom `SparkSkipException`/`Skip` (`tests/Autheris.Tests.Integration/SparkTestSupport.cs:55,73`, `AstCompilerDatabricksSparkDmlExecutionTests.cs:87`) instead of a first-class category; WebApplicationFactory in 40 files (119 lines), integration tests need `DOTNET_USE_POLLING_FILE_WATCHER=true` (memory note).
- **Problem:** Whole-assembly serialization hides shared-state coupling instead of isolating it, and 452+89 tests run strictly serially.
- **Impact:** Long integration wall-clock time; environment-dependent skips are reported as passes.
- **Recommendation:** Move parallelism control to explicit `[Collection]` groups only for tests that share DB/ports; share `WebApplicationFactory` through `IClassFixture`/collection fixtures (check for per-test host creation); report Spark skips via a `SkippableFact`-style attribute that surfaces as "Skipped".
- **Effort:** M

### ARC-T-08 Architecture tests are few and partly text scanners
- **Severity:** Low | **Category:** Consistency
- **Evidence:** 42 tests / 1,017 LOC in 9 files: `tests/Autheris.Tests.Architecture/ArchitectureTests.cs` (12 tests, NetArchTest), `CompilerSeamArchitectureTests.cs` (201 LOC, 9), `OracleDriverArchitectureTests.cs`, `RowPolicyArchitectureTests.cs`, etc.; additional architecture-style tests live in the Unit project (`tests/Autheris.Tests.Unit/ArchitectureAndCleanCodeTests.cs`).
- **Problem:** Rules are split across two projects; important invariants raised in this review are not asserted (no `new FastSqlEngine()` in src, no `TrinoSqlEngine` -> Autheris reference, no ANTLR namespace outside parser/builder, `#if DEBUG` budget, file-size cap).
- **Impact:** The target architecture (plan section 3) is protected only by review.
- **Recommendation:** Consolidate in `Autheris.Tests.Architecture`; add the rules above (all expressible with NetArchTest or a Roslyn analyzer), run them in the fast CI lane.
- **Effort:** S
