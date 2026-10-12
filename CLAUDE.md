# Autheris Guidelines for Claude Code & AI Assistants

See [AGENTS.md](file:///root/autheris/AGENTS.md) for full project governance.

## Mandatory Directives

1. **Language Standard:**
   - All documentation, READMEs, PRDs, architecture plans, inline comments, and commit messages **MUST be written in English**, matching the root [README.md](file:///root/autheris/README.md).

2. **Strict Separation of Documentation vs. Development Progress:**
   - **`docs/` is ONLY for system reference & manuals** describing how the system currently works.
   - **NEVER put development progress, status tags (`Status: In Progress`, `Done`), WIP notes, checklists, or roadmaps inside `docs/`**.
   - **All development progress, implementation plans, and status tracking belong EXCLUSIVELY in `doc/plan/`**.

3. **Requirements & Plans Repository (`doc/plan/`):**
   - All new requirements, design proposals, roadmaps, and feature plans must be placed in `doc/plan/` (or `docs/plans/`).
   - The central origin plan is: [`doc/plan/00-master-plan-overview.md`](file:///root/autheris/doc/plan/00-master-plan-overview.md).

4. **Origin Plan Refinement Protocol:**
   - Always read and update `doc/plan/00-master-plan-overview.md`.
   - When refining requirements or implementing features, keep the origin plan synchronized with the current status and links to detailed child plans.

5. **Individual Feature Documentation (`docs/features/`):**
   - Individual feature specifications and technical references **MUST always be documented in `docs/features/`** (e.g., `docs/features/f-<category>-<number>-<name>.md`).
   - Register every new feature in [`docs/features/README.md`](file:///root/autheris/docs/features/README.md).
   - Document how the feature works, configuration, and architecture (strictly without WIP or progress notes).

6. **The 6-Phase Standard Implementation Lifecycle:**
   - **Phase 1: Product Manager (`product-manager`)**: Qualify, benchmark with best practices, reject anti-patterns, enrich into PRD in `doc/plan/`.
   - **Phase 2: Solution Architect (`csharp-architect`)**: Draft architectural implementation plan in `doc/plan/`.
   - **Phase 3: Security Expert (`csharp-security-expert`)**: Security audit, Fail-Closed verification, threat hardening.
   - **Phase 4: Developer(s) (`csharp-tdd-developer`)**: Strict TDD Red-Green-Refactor, pass all unit & integration tests.
   - **Phase 5: Code Reviewer (`csharp-code-reviewer`)**: Code quality, C# 13 idioms, allocation/performance check.
   - **Phase 6: Documentation Expert (`documentation-expert`)**: Catalog feature in `docs/features/`, register in README, update master plan.

7. **Build & Test Verification:**
   - Solution: `Autheris.sln` (.NET 10.0)
   - Build: `dotnet build Autheris.sln`
   - Test: `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`
