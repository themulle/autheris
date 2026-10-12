# Requirements & Planning Hub (`doc/plan/`)

This directory (`doc/plan/` / `docs/plans/`) is the central repository for all project requirements, architectural concepts, technical designs, and execution plans for the Autheris platform.

---

## 🎯 Core Operating Rules for AI Agents

All AI agents (Antigravity, Claude, Copilot, Cursor, etc.) and contributing developers must follow these standard procedures:

### 1. English-Only Documentation Standard
* All documents created or modified in `doc/plan/` **must be written in English**.
* Existing German documents may remain for historical reference, but all new requirements, revisions, additions, and status updates must be in English.

### 2. Strict Separation: Documentation (`docs/`) vs. Development Progress (`doc/plan/`)
* **Documentation (`docs/`)** is ONLY for user-facing, operator, and architecture reference describing how the software currently works.
* **Development progress DOES NOT belong in `docs/`:** Never place status tags (`Status: In Progress`, `Done`), WIP notes, sprint backlogs, or implementation roadmaps in `docs/` (e.g. `docs/architecture/` or `docs/features/`).
* **`doc/plan/` is the SOLE location** for development status, roadmaps, task breakdowns, checklists, and implementation tracking.

### 3. The Origin Master Plan (`00-master-plan-overview.md`)
* The single source of truth for all current and future work is:
  👉 **[`00-master-plan-overview.md`](file:///root/autheris/doc/plan/00-master-plan-overview.md)**
* Before starting work on any task, agents must read this overview to understand active tracks, dependencies, and priorities.

### 4. Lifecycle for New Requirements
Whenever a new user requirement or feature request is introduced:
1. **Create the Requirement / Concept Document:**
   Store it in `doc/plan/` following the naming convention:
   * `YYYY-MM-DD-req-<feature-name>.md` (for business/product requirements)
   * `YYYY-MM-DD-concept-<feature-name>.md` (for high-level technical architecture)
   * `YYYY-MM-DD-plan-<feature-name>.md` (for detailed implementation tasks)
2. **Register in the Master Plan:**
   Add an entry into the table in [`00-master-plan-overview.md`](file:///root/autheris/doc/plan/00-master-plan-overview.md) with:
   * Document ID / Link
   * Topic & Scope
   * Addressed Components
   * Initial Status (`PROPOSED`, `IN PROGRESS`, etc.)
   * Next Milestone

### 5. Refining & Updating Plans
Agents must refine the initial plan iteratively rather than spawning disconnected or duplicated files:
* **Elaboration:** When breaking down a plan into concrete implementation tasks, update the existing plan document with subtasks, test cases, and edge cases.
* **Sync State:** Update the status in [`00-master-plan-overview.md`](file:///root/autheris/doc/plan/00-master-plan-overview.md) as milestones are reached.
* **Traceability:** Cross-reference related files, commits, and tests.

---

## 📝 Plan Template

When drafting a new plan or requirement, use the following structure:

```markdown
# [Title: Feature / Component Name]

**Document ID:** `PLAN-<TRACK>-<NUMBER>`  
**Date:** YYYY-MM-DD  
**Status:** PROPOSED | IN PROGRESS | IN REVIEW | COMPLETED  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Author / Lead:** [Agent Persona / Engineer]  

---

## 1. Executive Summary & Requirements
- What user need or system goal does this address?
- Scope and non-goals.

## 2. Technical Design & Architecture
- Proposed architectural changes (C#, .NET 10, Hot Chocolate, Casbin, DuckDB).
- Interface and data contracts.
- Security & Fail-Closed guarantees.

## 3. Implementation Steps & Checklist
- [ ] Task 1: Domain models & interfaces
- [ ] Task 2: Core implementation
- [ ] Task 3: Unit and integration tests
- [ ] Task 4: Documentation & verification

## 4. Verification & Testing
- How is this feature validated? (Test commands, test fixtures, benchmarks)

## 5. Changelog & Refinement Notes
- YYYY-MM-DD: Initial draft created.
```
