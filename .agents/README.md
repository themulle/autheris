# Autheris Agent Roles & Skills Directory

This directory defines custom agent roles and skills for the Autheris ecosystem.

---

## Agent Guidelines & Standards

1. **English Language Standard:**
   - All documentation, code reviews, architectural blueprints, and execution plans authored by any agent role **must be written in English**, aligned with the repository's [README.md](../README.md).
   - See [AGENTS.md](../AGENTS.md) for full project governance rules.

2. **Strict Separation of Documentation vs. Development Progress:**
   - **Documentation (`docs/`) is strictly for actual system reference and operator manuals.**
   - Never write status tags, WIP notes, sprint backlogs, or progress reports inside `docs/`.
   - All progress tracking, roadmaps, and checklists belong **exclusively in `docs/plans/`**.

3. **Requirements & Planning Protocol:**
   - Any new requirement or initiative must be recorded under [`docs/plans/`](../docs/plans/).
   - The primary origin plan is [`docs/plans/00-master-plan-overview.md`](../docs/plans/00-master-plan-overview.md).
   - All agents (architects, developers, reviewers) must update and refine their respective child plans on this origin plan.

4. **Individual Feature Documentation (`docs/features/`):**
   - Feature documentation is not part of the implementation lifecycle (no documentation phase, owner rule 2026-10-17); it is written only on explicit owner request.
   - Such documents go to `docs/features/` (e.g. `docs/features/f-<category>-<number>-<name>.md`) and are registered in the catalog index [`docs/features/README.md`](../docs/features/README.md).

5. **Senior–Junior Gemini 4 Pairing & Local Compute Offloading:**
   - **Senior Orchestrator (`Orchestrator (Argon)`):** Pre-analyzes plans, ratchets, and integration impact; writes self-contained, cost-efficient briefs (never token-starving junior agents); verifies full suites on GitHub Actions.
   - **Junior TDD Developer (`Gemini 4 Hydrogen`):** Runs strict Red-Green-Refactor in `/work/wt/<name>` using **only** filtered local unit tests (`-m:2 -nodeReuse:false`), proactively updates affected integration tests, and compile-checks `Autheris.Tests.Integration` locally without executing Testcontainers or full suites.
   - **GitHub Actions Offloading:** Full unit test runs, Testcontainers integration suites, SQL Compiler Gate, Parity, and nightlies execute on GitHub Actions.

6. **Subdirectories & Personas:**
   - `agents/`: Custom persona definitions:
     - [`project-manager.md`](agents/project-manager.md): Senior Delivery Project Manager & Workflow Orchestrator (Argon).
     - [`product-manager.md`](agents/product-manager.md): Enterprise Product Manager & Strategic Gatekeeper (qualification, benchmarking, rejection of anti-patterns).
     - [`csharp-tdd-developer.md`](agents/csharp-tdd-developer.md): Phase 4 Junior TDD Developer (Hydrogen, filtered local unit tests + integration compile-check).
     - [`orchestrator-lead.md`](agents/orchestrator-lead.md): Senior Orchestrator Lead (Argon).
   - `skills/`: Specialized domain skills (also synced to `~/.gemini/config/skills/`):
     - [`project-manager`](skills/project-manager/SKILL.md): Senior Argon delivery orchestration, Hydrogen brief template, and GitHub CI offloading.
     - [`product-manager`](skills/product-manager/SKILL.md): Feature discovery, intake deduplication check (`docs/features/`), industry best-practice benchmarking, gatekeeper enrichment.
     - [`csharp-tdd-developer`](skills/csharp-tdd-developer/SKILL.md): Hydrogen Red-Green-Refactor TDD workflow, filtered local unit tests, proactive integration test adaptation.
     - `csharp-architect`, `csharp-code-reviewer`, `csharp-performance-engineer`, `csharp-security-expert`.
   - `claude-code/agents/`: Subagent definitions (`project-manager`, `product-manager`, `csharp-architect`, `csharp-security-expert`, `csharp-tdd-developer`, `csharp-code-reviewer`). Sync to `~/.claude/agents/` (`.claude/` is git-ignored).
