# Autheris Agent Roles & Skills Directory

This directory defines custom agent roles and skills for the Autheris ecosystem.

---

## 🌐 Agent Guidelines & Standards

1. **English Language Standard:**
   - All documentation, code reviews, architectural blueprints, and execution plans authored by any agent role **must be written in English**, aligned with the repository's [README.md](file:///root/autheris/README.md).
   - See [AGENTS.md](file:///root/autheris/AGENTS.md) for full project governance rules.

2. **Strict Separation of Documentation vs. Development Progress:**
   - **Documentation (`docs/`) is strictly for actual system reference and operator manuals.**
   - Never write status tags, WIP notes, sprint backlogs, or progress reports inside `docs/`.
   - All progress tracking, roadmaps, and checklists belong **exclusively in `doc/plan/`**.

3. **Requirements & Planning Protocol:**
   - Any new requirement or initiative must be recorded under [`doc/plan/`](file:///root/autheris/doc/plan/).
   - The primary origin plan is [`doc/plan/00-master-plan-overview.md`](file:///root/autheris/doc/plan/00-master-plan-overview.md).
   - All agents (architects, developers, reviewers) must update and refine their respective child plans on this origin plan.

4. **Individual Feature Documentation (`docs/features/`):**
   - Individual feature specifications and technical references **must always be written to `docs/features/`** (e.g. `docs/features/f-<category>-<number>-<name>.md`).
   - Every feature must be registered in the catalog index [`docs/features/README.md`](file:///root/autheris/docs/features/README.md).

5. **Subdirectories & Personas:**
   - `agents/`: Custom persona definitions:
     - [`project-manager.md`](file:///root/autheris/.agents/agents/project-manager.md): Delivery Project Manager & Workflow Orchestrator (orchestrates the 6-phase implementation lifecycle).
     - [`product-manager.md`](file:///root/autheris/.agents/agents/product-manager.md): Enterprise Product Manager & Strategic Gatekeeper (qualification, benchmarking, rejection of anti-patterns).
     - [`documentation-expert.md`](file:///root/autheris/.agents/agents/documentation-expert.md): Technical Documentation Specialist & Architecture Writer.
     - [`csharp-tdd-developer.md`](file:///root/autheris/.agents/agents/csharp-tdd-developer.md): Test-Driven Development (.NET 10 / C# 13, Red-Green-Refactor).
     - `frontend-developer.md`, `orchestrator-lead.md`, `qa-cro-reviewer.md`, etc.
   - `skills/`: Specialized domain skills:
     - [`project-manager`](file:///root/autheris/.agents/skills/project-manager/SKILL.md): 6-phase delivery orchestration: (1) Product Manager -> (2) Solution Architect -> (3) Security Expert -> (4) TDD Developer(s) -> (5) Code Reviewer -> (6) Documentation Expert.
     - [`product-manager`](file:///root/autheris/.agents/skills/product-manager/SKILL.md): Feature discovery, intake deduplication check (`docs/features/`), industry best-practice benchmarking (OWASP, RFC, OpenFGA), gatekeeper rejection/enrichment.
     - [`documentation-expert`](file:///root/autheris/.agents/skills/documentation-expert/SKILL.md): Technical documentation, clean docs separation, feature cataloging, English standards.
     - [`csharp-tdd-developer`](file:///root/autheris/.agents/skills/csharp-tdd-developer/SKILL.md): Strict TDD workflow, xUnit, FluentAssertions, Clean Architecture.
     - `csharp-architect`, `csharp-code-reviewer`, `csharp-performance-engineer`, `csharp-security-expert`.
