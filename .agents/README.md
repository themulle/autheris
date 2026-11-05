# Autheris Agent Roles & Skills Directory

This directory defines custom agent roles and skills for the Autheris ecosystem.

---

## 🌐 Agent Guidelines & Standards

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

5. **Subdirectories & Personas:**
   - `agents/`: Custom persona definitions:
     - [`project-manager.md`](agents/project-manager.md): Delivery Project Manager & Workflow Orchestrator (orchestrates the 5-phase implementation lifecycle; no documentation phase).
     - [`product-manager.md`](agents/product-manager.md): Enterprise Product Manager & Strategic Gatekeeper (qualification, benchmarking, rejection of anti-patterns).
     - [`csharp-tdd-developer.md`](agents/csharp-tdd-developer.md): Test-Driven Development (.NET 10 / C# 13, Red-Green-Refactor).
     - [`orchestrator-lead.md`](agents/orchestrator-lead.md): Orchestrator Lead.
   - `skills/`: Specialized domain skills:
     - [`project-manager`](skills/project-manager/SKILL.md): 5-phase delivery orchestration: (1) Product Manager -> (2) Solution Architect -> (3) Security Expert -> (4) TDD Developer(s) -> (5) Code Reviewer, then tracker update; no documentation phase (owner rule 2026-10-17).
     - [`product-manager`](skills/product-manager/SKILL.md): Feature discovery, intake deduplication check (`docs/features/`), industry best-practice benchmarking (OWASP, RFC, OpenFGA), gatekeeper rejection/enrichment.
     - [`csharp-tdd-developer`](skills/csharp-tdd-developer/SKILL.md): Strict TDD workflow, xUnit, FluentAssertions, Clean Architecture.
     - `csharp-architect`, `csharp-code-reviewer`, `csharp-performance-engineer`, `csharp-security-expert`.
   - `claude-code/agents/`: Claude Code subagent definitions (`project-manager`, `product-manager`, `csharp-architect`, `csharp-security-expert`, `csharp-tdd-developer`, `csharp-code-reviewer`). Install by copying them to `~/.claude/agents/` (`.claude/` is git-ignored). Each definition makes the context-mode tools (`ctx_batch_execute`, `ctx_execute`, `ctx_search`) mandatory for token efficiency and sets a per-role output budget. PM and lane runs use `project-manager`, never a general-purpose agent.
