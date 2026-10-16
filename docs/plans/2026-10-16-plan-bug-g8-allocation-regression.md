# Implementation Plan: BUG - Nightly SQL Compiler Gate G8 Allocation Regression

**Document ID:** `PLAN-BUG-G8-01` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`, performance focus)
**Parent Plan:** [00-master-plan-overview.md](../../doc/plan/00-master-plan-overview.md) (priority item 1.4)
**Status:** PROPOSED
**Scope:** root-cause analysis and fix plan. No source, benchmark, baseline or gate change is part of this document.

---

## 1. What G8 Measures

| Item | Fact | Source |
|---|---|---|
| Workflow | `SQL Compiler Gate (nightly, cutover, G9)`, cron `0 2 * * *`, step 9 "G8 performance benchmark" then step 10 "G8 thresholds" | `.github/workflows/sql-compiler-gate-nightly.yml` |
| Benchmark | `dotnet run -c Release --project benchmarks/Autheris.Benchmarks -- --filter "*SqlCompiler*" --job short --inProcess --exporters json --artifacts benchmarks/results` | same file |
| Benchmark body | `SqlCompilerBenchmarks.Ast()` = `new FastSqlEngine().Compile(NextQuery(), request, ct)`: a **fresh engine per operation**, i.e. always a plan cache miss. Three buckets (`Bucket` = `0.5KB`, `4KB`, `64KB`), deterministic corpus of 200 joins over `orders`/`entitlements` with a flat projection, a `WHERE` and an `ON` join condition, SQL Server dialect, `NoPolicy` row filters, `NoMasks`. `AstCached` is informational and not gated. | `benchmarks/Autheris.Benchmarks/SqlCompilerBenchmarks.cs:23-62,115-135` |
| Evaluator | `SqlGate.BenchCheck` reads the BenchmarkDotNet JSON (`BytesAllocatedPerOperation`) and applies `BenchEvaluator` | `tools/SqlGate.BenchCheck/` |
| Allocation rules (post cutover, no legacy engine is measured) | (a) `allocation-ratio`: AST bytes/op divided by the **frozen legacy** bytes/op must be <= `MaxAllocationRatio` = **1.25** (`BenchModel.cs:31`, `BenchEvaluator.cs:116`); (b) `allocation-regression`: AST bytes/op must be <= frozen AST bytes/op x (1 + `MaxAllocationRegressionAgainstBaseline` = **0.10**) (`BenchModel.cs:33`, `BenchEvaluator.cs:117-121`); (c) Gen2 rule (`CheckGen2`, ratio limit 1.25) and p95/p99 time ratios | `tools/SqlGate.BenchCheck/BenchEvaluator.cs` |
| Baseline | `benchmarks/baselines/sql-compiler-legacy.json`, captured 2026-10-11 (`72c767bc`, AMD Ryzen 7 8840U, WSL2, .NET 10.0.12, ShortRun in-process). Legacy numbers are frozen forever (the legacy rewriter is deleted). The AST numbers were re-frozen at the end of the G8 performance pass. | file header |

Baseline numbers (bytes per operation): legacy 105,290 / 997,527 / 15,512,901; AST 126,969 / 1,041,873 / 16,173,528 for 0.5KB / 4KB / 64KB. Note that the 0.5KB bucket was **already at 1.206x** of legacy when frozen, so it has only 3.6 % headroom to the 1.25x limit (131,612 B), while 4KB (1.044x) and 64KB (1.043x) have large headroom.

## 2. Evidence

### 2.1 Runs (GitHub Actions API, read-only)

| Run | Commit | Trigger | Result |
|---|---|---|---|
| 38697560193 | `a4137626` | schedule, 2026-10-15 | cancelled |
| 38701779406 | `25a174ff` | workflow_dispatch, 2026-10-15 | cancelled |
| **38864316616** | `70b75e27` (merge of #109) | schedule, 2026-10-16 06:58Z | **failure** at step 10 "G8 thresholds"; steps 5-9 (gate tests, differential, architecture, benchmark) green; G7 Stryker skipped because of the failure |

There is **no green nightly run in the retained history**: the two earlier runs were cancelled, so this is the first completed run of the post-cutover nightly. "Last green commit" cannot be named from CI; the only valid reference is the baseline capture (`72c767bc`, 2026-10-11).

### 2.2 Numbers from the job log (job 116853534151)

```
0.5KB  Ast  mean 118.5 us  p95 120.3 us   alloc   130.7 KB  gen2/1000 0.00
4KB    Ast  mean 786.2 us  p95 787.9 us   alloc  1065.2 KB  gen2/1000 0.00
64KB   Ast  mean 22139.3 us p95 22560.4 us alloc 16513.5 KB  gen2/1000 156.25
G8 violation [allocation-ratio] 0.5KB: AST/legacy is 1.27x, limit 1.25x.
```

Derived against the frozen baseline (KB x 1024):

| Bucket | Measured B/op | Baseline AST B/op | Delta | vs legacy | Verdict |
|---|---|---|---|---|---|
| 0.5KB | 133,837 | 126,969 | +6.9 KB (+5.4 %) | **1.27x** | FAIL (limit 1.25x) |
| 4KB | 1,090,765 | 1,041,873 | +48.9 KB (+4.7 %) | 1.094x | pass |
| 64KB | 16,909,824 | 16,173,528 | +736 KB (+4.5 %) | 1.090x | pass |

Observations:
1. The regression is **uniform and proportional to input size** (about 12 extra bytes per source byte in all three buckets, +4.5 % to +5.4 %). It is not a fixed per-compile cost (a fixed cost would be invisible at 64KB) and not a per-engine-construction cost. Something walks the AST (or token stream) once more per compile and allocates per node or per projection.
2. The `allocation-regression` rule (10 % over frozen AST) still passes everywhere (max +5.4 %). Only the ratio-to-legacy rule at 0.5KB fails, because the baseline left only 3.6 % headroom there. Time ratios and Gen2 passed (no violation reported).
3. Hardware independence: allocation is deterministic per operation, so runner variance is excluded as the main cause. A .NET runtime patch difference (baseline 10.0.12; CI uses `global.json` 10.0.401 with `rollForward: latestPatch`, a change since the baseline) can shift ANTLR and BCL allocations slightly and is kept as hypothesis H3.

### 2.3 Is it still failing on `main`?

Very likely yes. Between `70b75e27` and `main` (`83853479`) nothing under `src/TrinoSqlEngine`, `benchmarks/` or `tools/SqlGate.BenchCheck` changed (`git diff 70b75e27..origin/main --stat -- src/TrinoSqlEngine benchmarks tools/SqlGate.BenchCheck` is empty), and allocation is deterministic. No nightly ran after 2026-10-16 06:58Z, so this is inferred, not observed. Confirm with a `workflow_dispatch` run (profile `nightly`) on `main` or with the local command in section 5.

### 2.4 Commit range: the PRs named in the report are not the cause

| PR | Compile-path change |
|---|---|
| #108 (`e861c4d9`, key-set cache) | adds `Governance/IPolicyKeyQueryCompiler.cs` and `PolicyKeyQueryCompiler.cs` (+63 lines) under `src/TrinoSqlEngine`; not on the benchmarked `FastSqlEngine.Compile` path (no policy, no `PolicyKeyQueryCompiler` call). |
| #109 (`70b75e27`, CDC fan-out) | no change under `src/TrinoSqlEngine`; adds `CasbinDecisionBenchmark.cs` (different benchmark filter). |
| #110 (`9583c7a7`, scale wave 1) | `GovernedSqlRewriter.cs:68,175-183` stage-1 analysis cache, `CompiledSubRuleCache.cs`: all in `Autheris.Application`/`Infrastructure`, not in the benchmark (the benchmark calls `FastSqlEngine` directly, not `GovernedSqlRewriter`). |

The relevant range is therefore **baseline `72c767bc` (2026-10-11) .. `70b75e27`**: `git diff 72c767bc..70b75e27 --stat -- src/TrinoSqlEngine` shows 67 files (about +2,840 / -3,189), none of which was gated by a green G8 run in between (the nightly was only wired to the post-cutover evaluator at the cutover and the first two runs were cancelled). The PR merge that "turned the gate red" is the first completed run, not the cause.

## 3. Root-Cause Hypotheses (ranked)

Static analysis only (no benchmark was run for this document). H1 is the top hypothesis; it is consistent with every observation in 2.2, but must be confirmed by the bisect in section 5 before code is changed.

### H1 (most likely): per-`QuerySpecification` security guard passes added after the baseline, each with fresh collections and a growing work stack

Evidence:
- `AstSecurityVisitor.Select.cs:151-152`: `VisitQuerySpecification` now calls `EnsureNoMaskedColumnsInAggregatesAndGrouping(node)` and `EnsureNoWholeRowReferencesInProjections(node)` on **every** query, even when `NoMasks` is configured. Both default to enabled (`AstSecurityVisitorOptions.cs:47,53`). They arrived with `57ec8294` (2026-10-14, "reject masked columns in aggregates/GROUP BY and whole-row refs in SELECT") and were reworked in `4748354b` (2026-10-15, "harden masked column guards", `AstSecurityVisitor.MaskGuards.cs` +632/-... lines). Both postdate the baseline.
- `AstSecurityVisitor.MaskGuards.cs:246-249` allocates two `List<(string, string?)>` and `List<(string, string?, HashSet<string>)>` plus `new Stack<SqlNode>(16)` (line ~257) per query specification, and `:303-308` allocates another `List` plus `new Stack<SqlNode>(16)`. The corpus emits many projection items per query (growing with the bucket size), so the stack array doubles repeatedly (16 -> 32 -> ... ), giving an allocation that grows with the projection count, matching observation 1.
- The predicate guard in the same method (`Select.cs:122-148`) also allocates `tables` and `derived` lists per query; together with the two new guards the same two lists are built three times per `QuerySpecification` (`CollectTableSources` / `CollectDerivedMaskedSources` called from `Select.cs:125-127`, `MaskGuards.cs:246-249`, `MaskGuards.cs:303-305`).
- The sibling guard already shows the intended pattern: `MaskGuards.cs:38-42` reuses a visitor-level `_maskGuardStack` ("one visitor per compile: the work stack is reused by every call"), the new guards do not. This is also the pattern of the earlier G8 pass (`bfb7738f` "cut allocations and latency of the AST cache-miss compile path"; `AstReflection.Children<TState>` exists specifically to avoid per-node closures).
- Outside the benchmark path but of the same family and worth fixing together: `AstSecurityVisitor.Tables.cs:78` and `AstSecurityVisitor.Expressions.cs:254` use `AstReflection.Collect<ColumnReference>(...).Any(c => ...)` (a `List` plus a closure per call) and `SecurityCoverageVerifier.cs:255` (new `VerifyNoWholeRowOfMaskedSource`) and `:470` use the reflection-based `AstReflection.Walk` with a capturing lambda per projection item. They only run with policy subqueries or masked sources, so they do not explain the benchmark, but they will hit the first real workload.

Proposed fix (no behavior change):
1. Hoist one reusable per-visitor `Stack<SqlNode>` (and one `tables`/`derived` pair) for `VisitQuerySpecification` so the three guard blocks share a single `CollectTableSources` / `CollectDerivedMaskedSources` result per query specification; reuse `_maskGuardStack`-style fields, clearing on entry (the visitor is single-use per compile, so no concurrency issue; keep it that way and say so in a comment).
2. Short-circuit before any allocation: compute once `bool anyMaskedTable = _options.TablesWithMaskedColumns is { Count: > 0 } || derived.Count > 0` and skip the two projection traversals entirely when no table in the `FROM` has masked columns (the whole-row and aggregate guards are vacuous then). Check `HasMaskingForTable` semantics so the skip stays fail-closed (the guard is a second line of defence; the primary mask rewrite is unchanged).
3. Replace `AstReflection.Collect<T>(...).Any(...)`/closure `Walk` in the policy-only paths (`Tables.cs:78`, `Expressions.cs:254`, `SecurityCoverageVerifier.cs:255,470`) with the stateful `AstReflection.Children<TState>` or an allocation-free early-exit finder.
Expected effect: removes the proportional share of the +4.5-5.4 %, enough to bring 0.5KB below 1.25x (needs at most -2 % of 133,837 B, i.e. about -2.3 KB, to reach 131,612 B; target is back to the frozen 126,969 B +-1 %).

### H2: per-compile cost of the pooled parse hand-off (`613a3b69`, 2026-10-14)

Evidence: `FastSqlEngine.RunWithParseBudget` (`FastSqlEngine.cs:250-330`) now allocates per compile a `ManualResetEventSlim`, a `ParseWork` item, the capturing `Func`/closure for `work`, a `ParseCancellation`, and (inline path only, off by default) an interpolated deadline message (`:267`); the baseline used a dedicated `Thread` per parse. Also `private readonly SyntaxErrorNegativeCache _syntaxErrors = new()` (`FastSqlEngine.cs:134`) is created per `new FastSqlEngine()` and the benchmark creates one engine per operation.
Assessment: these are **fixed** per-compile costs, replacing a more expensive `Thread` allocation, so they cannot explain a regression that scales with input size. They can still account for part of the 0.5KB bucket (1-2 KB is 1-1.5 % of 130 KB). Treat as secondary; fix only if the bisect shows them: lazy-create the negative cache, `static` deadline message, avoid the per-call closure for the shared-engine production path.

### H3: runtime/dependency drift between the baseline capture and CI

Evidence: baseline captured with .NET 10.0.12 on WSL2; CI resolves SDK `10.0.401` with `rollForward: latestPatch` (changed from `latestFeature` since); `Microsoft.Extensions.ObjectPool` 10.0.12 -> 10.0.13 in `TrinoSqlEngine.csproj`/`packages.lock.json` (`dfc56f3c`). A runtime or BCL patch can move ANTLR/collection allocation by a few percent, uniformly.
Assessment: cannot be excluded statically; it would show as the **same numbers on the pre-#57ec8294 commit** when benchmarked on today's SDK (see the bisect in section 5). If H3 is the only cause, the baseline must be re-frozen (section 4, option B) and that is an owner decision.

### H4 (low): literal folding and security-visitor split

`AstSimplificationVisitor.cs` now calls `LiteralComparison.TryFold` (new `Ast/Visitors/LiteralComparison.cs`, `10a9f782`), replacing `Equals(l.Value?.ToString(), r.Value?.ToString())` (which allocated strings). It can only reduce allocation or stay neutral; the benchmark has no literal-vs-literal comparisons. Rated unlikely, listed so the bisect result can rule it out. Likewise the `AstSecurityVisitor` split into partial files and the Release-mode `CompilerHooks.Current` check (`CompilerHooks.cs:83`, a volatile read plus `AsyncLocal` read, no allocation).

## 4. Fix Options and the Gate Limit

| Option | Content | Decision |
|---|---|---|
| A (recommended) | Fix H1 (and H2 if confirmed), no change to gate, thresholds or baseline. | Developer, no owner decision needed. |
| B | Re-freeze the AST side of `benchmarks/baselines/sql-compiler-legacy.json` only if the bisect proves the added bytes are a mandatory, reviewed cost (a security guard that cannot be made cheaper) or pure runtime drift (H3). The legacy numbers are never touched. | **Owner decision.** The fix PR must state the new numbers, the cause and why A is impossible. |
| C | Raise `MaxAllocationRatio` above 1.25. | **Not recommended.** It would hide the regression and the 0.5KB headroom problem would return at the next added guard. Only with explicit owner sign-off and a written justification in this plan. |

Independent hardening (small, separate commit, still no limit change): the 0.5KB bucket has been sitting at 1.206x since the freeze, i.e. 3.6 % headroom. After fix A, the owner may consider (not part of this fix) whether the 0.5KB AST baseline should be tightened by the gained savings so `allocation-regression` (10 %) rather than the ratio rule is the effective alarm.

## 5. Verification (developer, before and after)

All commands from the repository root of the fix branch. Run benchmarks one at a time (no parallel builds, per the project build rule), in Release, and compare **bytes per operation**, which is deterministic:

1. Reproduce on the failing head (before):
   `dotnet run -c Release --project benchmarks/Autheris.Benchmarks -- --filter "*SqlCompiler*" --job short --inProcess --exporters json --artifacts benchmarks/results`
   then `dotnet run -c Release --project tools/SqlGate.BenchCheck -- benchmarks/results benchmarks/baselines`
   Expected: `G8 violation [allocation-ratio] 0.5KB` about 1.27x. Record all three bucket numbers.
2. Bisect (only `Ast`, allocation is enough): run the same command on `72c767bc` (baseline), `57ec8294`^ and `57ec8294`, `4748354b`^ and `4748354b`, `613a3b69`^ and `613a3b69`. The first commit that raises 0.5KB by more than 1.5 KB names the cause. Running the baseline commit on the current SDK separates runtime drift (H3) from code (H1/H2): if `72c767bc` itself already measures near 133 KB today, H3 is the cause.
3. Optional confirmation of H1 without bisect: BenchmarkDotNet `--memory` plus `dotnet-trace`/`dotnet-counters` allocation sampling on `Ast`, filter on `Stack<SqlNode>`, `List<ValueTuple<...>>`, closure display classes under `VisitQuerySpecification`.
4. After the fix: same command; `SqlGate.BenchCheck` must print `G8: all thresholds met.` with 0.5KB below 1.25x and, as the acceptance target, each bucket within +1 % of the frozen AST baseline (126,969 / 1,041,873 / 16,173,528 B).
5. Security non-regression (must stay green, all existing): `dotnet test tests/TrinoSqlEngine.Tests -c Debug --filter "Category=SqlGate"`, the Release run of the same filter, `dotnet test tests/Autheris.Tests.SqlCompilerGate -c Release -p:AutherisIncludeTestAuth=true`, `dotnet test tests/Autheris.Tests.Architecture -c Release -p:AutherisIncludeTestAuth=true`, and the Stryker G7 job (85 % break threshold) because the guards are in the mutation scope. Before every push: Release build with warnings as errors.
6. After merge, trigger `sql-compiler-gate-nightly.yml` via `workflow_dispatch` with profile `nightly` (not `cutover`) on `main` and confirm step 10 green.

## 6. TDD and Regression Guard

- **Red first (Phase 4, `csharp-tdd-developer`):** add an allocation unit test in `tests/TrinoSqlEngine.Tests` (category `SqlGate`, so it runs in the nightly and PR gate jobs): compile a fixed 4KB-style corpus query on a fresh `FastSqlEngine` with `NoMasks` after warm-up and assert `GC.GetAllocatedBytesForCurrentThread()` delta (inline parse path enabled via `InlineParseMaxLength` so the work stays on the test thread) stays below a ceiling derived from the baseline (+5 %). It is expected to fail before the fix, if the measured delta exceeds the ceiling, otherwise it documents the current value and the bisect result decides the ceiling.
- **Behavior tests that must not change:** existing mask-guard suites for aggregates, GROUP BY, whole-row references (SR-L-24), predicate guards, and the `SecurityCoverageVerifier` whole-row second line of defence; add one new case per optimization proving that a masked table still triggers `MaskedAggregationException` / whole-row rejection when the skip condition in H1.2 is evaluated (fail-closed, Phase 3 review point), and that a table without masks takes the allocation-free path.
- **Gate guard:** a cheap PR-time check of the allocation (the new unit test) so a +5 % regression is caught at PR time rather than the next 02:00 nightly; the nightly stays the authoritative gate and is not weakened.
- **Process guard:** record in the PR description the before/after bytes per bucket (section 5, steps 1 and 4).

## 7. Security Notes (Phase 3 input)

The affected guards are second lines of defence (primary protection is the mask rewrite and `SecurityCoverageVerifier`). Any short-circuit in H1.2 must be proven equivalent (no masked column reachable means the guard cannot fire) and fail closed when `TablesWithMaskedColumns` is unknown (`null` keeps the guard active). No gate limit is lowered, no control is switched off, no new `CompilerHooks` bypass is introduced.

## 8. Size and Order

| Work package | Content | Size |
|---|---|---|
| WP-G8-1 | Bisect and confirm cause (section 5, steps 1-3), numbers into the PR | XS (about 1-2 h, benchmark runs of about 10 min each) |
| WP-G8-2 | H1 fix (shared collections, skip when no masks, allocation-free finders) plus tests | S (about 0.5-1 day) |
| WP-G8-3 | H2 trims if the bisect shows them | XS, only if needed |
| WP-G8-4 | Owner decision on baseline re-freeze (option B) only if A cannot reach the target | XS |

Overall size: **S**. Phase 3 review can be short (guard equivalence, section 7). No documentation under `docs/features/` changes; the master plan row 1.4 stays the status tracker.
