# Requirements: ARCH-MNT-15 - Bounded ANTLR DFA Caches

**Document ID:** `REQ-ARCH-MNT-15`  
**Parent:** `docs/plans/2026-10-26-plan-architecture-maintainability.md` (Section 3.1, Row ARCH-MNT-15)  
**Phase:** Phase 1 (`product-manager`)  
**Base:** `origin/main`  
**Branch:** `feat/arch-mnt-15-bound-antlr-dfa-caches`  

---

## 1. Executive Summary & Problem Statement

In `TrinoSqlEngine`, the generated `SqlBaseParser` and `SqlBaseLexer` rely on process-wide statics `protected static DFA[] decisionToDFA` and `static PredictionContextCache sharedContextCache`.
These structures monotonically grow with each distinct syntactic shape and token stream. An adversary or high-entropy workload sending distinct parameterized SQL statements will continuously expand DFA state counts over the life of the process, presenting an unbounded memory growth and denial-of-service (DoS) vulnerability.

---

## 2. Goals & Invariants

- **GOAL-01 (Generational Bounded Memory):** Bound cumulative memory consumed by ANTLR DFA and prediction context caches by replacing static process-wide caches with atomic generational swapping (`AntlrCacheGeneration`).
- **GOAL-02 (Zero Lock Contention on Parse Path):** Ensure parsing executes without global lock stalls during cache rotation.
- **GOAL-03 (Factory Encapsulation):** Route all parser and lexer instantiations through a central factory that binds the active cache generation.
- **INV-01 (Deterministic Parse Semantics):** Parse results, error codes, and AST structures must remain strictly identical whether executing against a cold, warm, or newly swapped cache generation.
- **INV-02 (Fail-Closed Enforcement):** Any unexpected fault in generation binding or swap calculation must fail-closed, rejecting the parse rather than falling back to unmanaged statics.
- **INV-03 (Zero Information Leakage):** Telemetry, logs, and metrics emitted during cache rotation must never contain SQL text, statement hashes, tenant IDs, or principal identifiers.

---

## 3. Success Metrics

1. **State Bound:** The cumulative DFA state count is strictly bounded by the configured `MaxDfaStatesPerGeneration` plus per-parse slack.
2. **Leak Elimination:** Eliminate all 5 raw `new SqlBaseLexer(` invocations in favor of the factory.
3. **Architecture Verification:** Pass all 12 mandatory security tests (`ARCH-MNT-15-T-01` through `T-12`) and prevent any regression in AST pushdown or security visitor rules.
