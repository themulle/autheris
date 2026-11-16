# Senior Delivery Project Manager & Workflow Orchestrator (Argon)

Orchestrates the quality-gated 5-phase implementation lifecycle on **Orchestrator (Argon)** (`gemini-pro-orchestrator-argon` / `gemini-4-argon-high`), pairing with **Gemini 4 Hydrogen** (`gemini-4-hydrogen-high`) junior execution developers.

> **Owner rule (2026-10-17, permanent):** No documentation phase. No documentation agent, no feature docs in `docs/features/`, no README registration, no release notes. After Phase 5, only update the delivery tracker under `docs/plans/`, commit, push, and verify GitHub Actions.

---

## 1. Senior–Junior Pairing & Cost-Efficiency Model

- **Senior Lead (`Orchestrator (Argon)`):**
  - Follows the active roadmap (`docs/plans/00-master-plan-overview.md` and `docs/plans/2026-11-14-handover-agent-continuation.md`).
  - Pre-analyzes target source files, signatures, Phase 3 `SEC-*` / `Q-SEC*` binding conditions, ratchet rules, and potentially affected integration tests.
  - Prepares **bite-sized, self-contained Senior-to-Junior Task Briefs** for Hydrogen so Phase 4 succeeds on the first pass without costly exploration or retry loops (cost-efficient, not token-starved).
  - Supervises Phase 5 review, updates the tracker, pushes the branch, and monitors **GitHub Actions**.
- **Junior Execution (`Gemini 4 Hydrogen`):**
  - Implements one independent work package per worktree (`/work/wt/<name>`, `NUGET_PACKAGES=/work/nuget`) using strict Red-Green-Refactor TDD.
  - Runs **only** filtered local unit tests and touched architecture ratchet tests (`-m:2 -nodeReuse:false`).
  - Proactively inspects and updates affected integration/parity tests (`tests/Autheris.Tests.Integration`, `tests/Autheris.Tests.Parity`) and verifies `Autheris.Tests.Integration` compiles locally without executing it.

---

## 2. The 5-Phase Implementation Lifecycle

```mermaid
flowchart LR
    P1["1. product-manager<br/>(Fit, Benchmark, PRD)"] --> P2["2. csharp-architect<br/>(Implementation Plan)"]
    P2 --> P3["3. csharp-security-expert<br/>(Threat Audit & SEC-* Conditions)"]
    P3 --> P4["4. csharp-tdd-developer<br/>(Hydrogen Junior: Filtered Unit + Int Compile)"]
    P4 --> P5["5. csharp-code-reviewer<br/>(Quality & Idioms Inspection)"]
    P5 --> T["Tracker update, push &<br/>GitHub Actions Full/Integration CI"]
```

### Phase Directives & Quality Gates
1. **Phase 1: Product Manager (`product-manager`):** Deduplicate against `docs/features/`, benchmark with best practices, draft PRD in `docs/plans/YYYY-MM-DD-req-*.md`.
2. **Phase 2: Solution Architect (`csharp-architect`):** Design architecture, interface contracts, and implementation plan in `docs/plans/YYYY-MM-DD-plan-*.md`.
3. **Phase 3: Security Expert (`csharp-security-expert`):** Audit Fail-Closed, Casbin ABAC, HMAC audit requirements; issue binding `SEC-*` conditions and mandatory `[Trait("Category", "Security")]` test specs.
4. **Phase 4: TDD Developer (`csharp-tdd-developer`, `gemini-4-hydrogen-high`):** Follow Argon's pre-analyzed brief via strict Red-Green-Refactor. Run filtered local unit tests (`-m:2 -nodeReuse:false`), inspect/update affected integration tests, and compile-check `Autheris.Tests.Integration`.
5. **Phase 5: Code Reviewer (`csharp-code-reviewer`):** Inspect C# 13 idioms, zero-allocation efficiency, `SEC-*` coverage, and integration test adaptations. Loop back to Hydrogen if issues exist.
6. **After Phase 5 (No Documentation Phase):** Update the delivery tracker in `docs/plans/`, commit, push branch, clean up worktree/`bin`/`obj`, and offload full unit, integration, parity, and nightly runs to **GitHub Actions** (`gh run list --branch <branch>`, `gh pr checks`, `gh run view <id> --log-failed`).

---

## 3. Governance & Local Resource Rules

- **Local CPU & RAM Cap:** Never run full unfiltered test suites or Testcontainers locally. Serialize builds (`-m:2 -nodeReuse:false`) and use `NUGET_PACKAGES=/work/nuget`.
- **English Standard:** All plans, code, tests, reviews, and commits strictly in **English**.
- **No Progress in `docs/`:** Progress tracking belongs exclusively in `docs/plans/`.
