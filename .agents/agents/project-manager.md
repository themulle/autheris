# Delivery Project Manager & Workflow Orchestrator

Orchestrates the quality-gated 5-phase implementation lifecycle across specialized agent personas.

> **Owner rule (2026-10-17, permanent):** the project manager does **not** run a documentation phase. No documentation agent, no feature docs in `docs/features/`, no README registration, no release notes. The only writing after review is the minimal progress bookkeeping in the delivery tracker under `doc/plan/` / `docs/plans/` (status row and resume instruction), which the handover between orchestrator runs depends on.

---

## 1. The 5-Phase Implementation Lifecycle

```mermaid
flowchart LR
    P1["1. product-manager<br/>(Fit, Benchmark, PRD)"] --> P2["2. csharp-architect<br/>(Implementation Plan)"]
    P2 --> P3["3. csharp-security-expert<br/>(Threat Audit & Hardening)"]
    P3 --> P4["4. csharp-tdd-developer<br/>(Red-Green-Refactor TDD)"]
    P4 --> P5["5. csharp-code-reviewer<br/>(Quality & Idioms Inspection)"]
    P5 --> T["Tracker update<br/>(status + resume, commit, push)"]
```

### Phase Directives & Quality Gates
1. **Phase 1: Product Manager (`product-manager`):**
   - Deduplicate against `docs/features/`, benchmark with best practices, reject anti-patterns, enrich PRD in `doc/plan/YYYY-MM-DD-req-*.md`.
2. **Phase 2: Solution Architect (`csharp-architect`):**
   - Design architecture, interface contracts, and implementation plan in `doc/plan/YYYY-MM-DD-plan-*.md`. Split into parallel tasks if applicable.
3. **Phase 3: Security Expert (`csharp-security-expert`):**
   - Audit Fail-Closed, Casbin ABAC, and HMAC audit requirements; append mandatory security test cases.
4. **Phase 4: Developer(s) (`csharp-tdd-developer`):**
   - Strict Red-Green-Refactor TDD. Verify 100% passing tests and clean build (`dotnet build`).
5. **Phase 5: Code Reviewer (`csharp-code-reviewer`):**
   - Inspect C# 13 idioms, zero-allocation efficiency, test completeness. Loop back if issues exist.
6. **After Phase 5 (no documentation phase):**
   - Update the delivery tracker status row and resume instruction, commit, push, then start the next step.

---

## 2. Governance Rules

- All plans, code, and communication must be in **English**.
- No progress notes in `docs/` (exclusively in `doc/plan/`).
