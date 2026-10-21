# Autheris Guidelines for Claude Code & AI Assistants

See [AGENTS.md](file:///root/autheris/AGENTS.md) for full project governance.

## Mandatory Directives

1. **Language Standard:**
   - All documentation, READMEs, PRDs, architecture plans, inline comments, and commit messages **MUST be written in English**, matching the root [README.md](file:///root/autheris/README.md).

2. **Strict Separation of Documentation vs. Development Progress:**
   - **`docs/` (outside `docs/plans/`) is ONLY for system reference & manuals** describing how the system currently works.
   - **NEVER put development progress, status tags (`Status: In Progress`, `Done`), WIP notes, checklists, or roadmaps inside `docs/` (outside `docs/plans/`)**.
   - **All development progress, implementation plans, and status tracking belong EXCLUSIVELY in `docs/plans/`**.

3. **Requirements & Plans Repository (`docs/plans/`):**
   - All new requirements, design proposals, roadmaps, and feature plans must be placed in `docs/plans/`.
   - The central origin plan is: [`docs/plans/00-master-plan-overview.md`](file:///root/autheris/docs/plans/00-master-plan-overview.md).

4. **Origin Plan Refinement Protocol:**
   - Always read and update `docs/plans/00-master-plan-overview.md`.
   - When refining requirements or implementing features, keep the origin plan synchronized with the current status and links to detailed child plans.

5. **Individual Feature Documentation (`docs/features/`):**
   - Feature documentation is **not** part of the implementation lifecycle (there is no documentation phase). Write feature docs only when the owner explicitly asks for them.
   - When a feature doc is written, it goes to `docs/features/` (e.g., `docs/features/f-<category>-<number>-<name>.md`), is registered in [`docs/features/README.md`](file:///root/autheris/docs/features/README.md), and describes how the feature works, configuration, and architecture (strictly without WIP or progress notes).

6. **The 5-Phase Standard Implementation Lifecycle:**
   - **Phase 1: Product Manager (`product-manager`)**: Qualify, benchmark with best practices, reject anti-patterns, enrich into PRD in `docs/plans/`.
   - **Phase 2: Solution Architect (`csharp-architect`)**: Draft architectural implementation plan in `docs/plans/`.
   - **Phase 3: Security Expert (`csharp-security-expert`)**: Security audit, Fail-Closed verification, threat hardening.
   - **Phase 4: Developer(s) (`csharp-tdd-developer`)**: Strict TDD Red-Green-Refactor, pass all unit & integration tests.
   - **Phase 5: Code Reviewer (`csharp-code-reviewer`)**: Code quality, C# 13 idioms, allocation/performance check.
   - **After Phase 5**: update the progress tracker / master plan in `docs/plans/`, commit, push. There is **no documentation phase** (owner rule 2026-10-17).

7. **Build & Test Verification:**
   - Solution: `Autheris.sln` (.NET 10.0)
   - Build: `dotnet build Autheris.sln`
   - Test: `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`
