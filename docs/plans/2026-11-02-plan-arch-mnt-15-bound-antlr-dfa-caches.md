# Plan: ARCH-MNT-15 - Bounded ANTLR DFA Caches

**Document ID:** `PLAN-ARCH-MNT-15`  
**Parent:** `docs/plans/2026-10-26-plan-architecture-maintainability.md` (Section 3.1, Row ARCH-MNT-15), `docs/plans/2026-11-02-req-arch-mnt-15-bound-antlr-dfa-caches.md`  
**Phase:** Phase 2 (`csharp-architect`) & Phase 3 (`csharp-security-expert`)  
**Base:** `origin/main`  
**Branch:** `feat/arch-mnt-15-bound-antlr-dfa-caches`  

---

## 1. Architectural Approach: Generational Swap

Rather than per-instance DFA (which wastes memory and creates cold-parse latency spikes) or global reader/writer locks (which stall parses and risk race conditions in ANTLR's internal simulator), Autheris implements an atomic generational swap pattern:

1. **`AntlrCacheGeneration`**:
   An immutable reference container holding:
   - `DFA[] ParserDfa`
   - `DFA[] LexerDfa`
   - `PredictionContextCache ContextCache`
   - `long GenerationId`
2. **`AntlrCacheCoordinator`**:
   Maintains `private static volatile AntlrCacheGeneration s_current`.
   - On lease/rent from the pool, binds the parser or lexer simulator to `s_current`.
   - Tracks parse counts and evaluates cumulative state counts every `GenerationCheckInterval` parses.
   - If cumulative states exceed `MaxDfaStatesPerGeneration` and `MinParsesPerGeneration` has elapsed, publishes a new `AntlrCacheGeneration` via `Interlocked.CompareExchange`.
   - In-flight parses finish on their acquired generation; unreferenced generations are collected cleanly by the .NET garbage collector.
3. **Factory & Pool Migration**:
   - Centralize lexer and parser creation in `SqlEngineGrammarFactory`.
   - Migrate all five raw `new SqlBaseLexer` sites (`FastSqlEngine.cs`, `SqlParameterExtractor.cs`, `AstSecurityVisitor.Support.cs`, `AggregationNegationGuard.cs`, `PiiShadowingRedactor.cs`) to the factory.
   - Add architecture test banning raw constructor calls outside the factory.

---

## 2. Binding Security Conditions (Phase 3 Audit)

1. **ARCH-MNT-15-SC-01**: Centralized factory enforcement; forbidden raw construction elsewhere in `src`.
2. **ARCH-MNT-15-SC-02**: Immutability of generations; zero explicit `ClearDFA()` calls while parses are in flight.
3. **ARCH-MNT-15-SC-03**: Total-state cap per generation. Mid-parse overflow completes on current generation; swap triggers post-parse.
4. **ARCH-MNT-15-SC-04**: Configuration options `MaxDfaStatesPerGeneration`, `GenerationCheckInterval`, and `MinParsesPerGeneration` strictly validated at startup (`ValidateOnStart`).
5. **ARCH-MNT-15-SC-05**: Fail-closed parse rejection upon binding/swap error. Discard instances; zero fallbacks to static unmanaged arrays.
6. **ARCH-MNT-15-SC-06**: Guaranteed downstream execution of `AstSecurityVisitor` and `SqlSecurityValidator`.
7. **ARCH-MNT-15-SC-07**: Parse results strictly identical across cold, warm, and swapped states.
8. **ARCH-MNT-15-SC-08**: Rebound parsers always return to `PredictionMode.SLL` with listeners intact.
9. **ARCH-MNT-15-SC-09**: Zero SQL text, hash, or tenant IDs in metrics or logs. Fixed low-cardinality tags only.
10. **ARCH-MNT-15-SC-10**: Swap frequency rate-limited by `MinParsesPerGeneration`.
11. **ARCH-MNT-15-SC-11**: Latency regressions gated via `SqlGate.BenchCheck`.

---

## 3. Mandatory Security Test Suite

- **ARCH-MNT-15-T-01**: `Parse_CorpusTreesAndErrors_IdenticalAcrossWarmColdAndSwappedGenerations`
- **ARCH-MNT-15-T-02**: `Generation_StateCount_StaysBoundedForDistinctStatements`
- **ARCH-MNT-15-T-03**: `Generation_ConcurrentParsesDuringSwap_NoExceptionsAndEquivalentResults`
- **ARCH-MNT-15-T-04**: `Generation_OldGenerationNotCleared_WhileParseInFlight`
- **ARCH-MNT-15-T-05**: `Swap_FaultInjected_RejectsParse_DoesNotReturnInstanceToPool`
- **ARCH-MNT-15-T-06**: `AstSecurityVisitor_RunsOnEveryParse_IncludingFirstParseAfterSwap`
- **ARCH-MNT-15-T-07**: `Parser_AfterRebind_UsesSllModeAndSameListeners`
- **ARCH-MNT-15-T-08**: `Options_InvalidCapIntervalOrMinParses_FailStartup`
- **ARCH-MNT-15-T-09**: `SwapMetricsAndLogs_ContainNoSqlTextOrTenantIdentifiers`
- **ARCH-MNT-15-T-10**: `Swap_AdversarialDistinctStatements_RespectsMinParsesPerGeneration`
- **ARCH-MNT-15-T-11**: `SqlBaseLexerAndParser_AreConstructedOnlyThroughTheFactory` (Architecture test)
- **ARCH-MNT-15-T-12**: `SqlGate.BenchCheck` cold-cache performance verification
