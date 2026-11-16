# Autheris Guidelines for Claude Code & AI Assistants

See [AGENTS.md](AGENTS.md) for full project governance.

## Mandatory Directives

1. **Language Standard:**
   - All documentation, READMEs, PRDs, architecture plans, inline comments, and commit messages **MUST be written in English**, matching the root [README.md](README.md).

2. **Strict Separation of Documentation vs. Development Progress:**
   - **`docs/` (outside `docs/plans/`) is ONLY for system reference & manuals** describing how the system currently works.
   - **NEVER put development progress, status tags (`Status: In Progress`, `Done`), WIP notes, checklists, or roadmaps inside `docs/` (outside `docs/plans/`)**.
   - **All development progress, implementation plans, and status tracking belong EXCLUSIVELY in `docs/plans/`**.

3. **Requirements & Plans Repository (`docs/plans/`):**
   - All new requirements, design proposals, roadmaps, and feature plans must be placed in `docs/plans/`.
   - The central origin plan is: [`docs/plans/00-master-plan-overview.md`](docs/plans/00-master-plan-overview.md).

4. **Origin Plan Refinement Protocol:**
   - Always read and update `docs/plans/00-master-plan-overview.md`.
   - When refining requirements or implementing features, keep the origin plan synchronized with the current status and links to detailed child plans.

5. **Individual Feature Documentation (`docs/features/`):**
   - Feature documentation is **not** part of the implementation lifecycle (there is no documentation phase). Write feature docs only when the owner explicitly asks for them.
   - When a feature doc is written, it goes to `docs/features/` (e.g., `docs/features/f-<category>-<number>-<name>.md`), is registered in [`docs/features/README.md`](docs/features/README.md), and describes how the feature works, configuration, and architecture (strictly without WIP or progress notes).

6. **The 5-Phase Standard Implementation Lifecycle (Senior Argon + Junior Hydrogen Pairing):**
   - **Phase 1: Product Manager (`product-manager`)**: Qualify, benchmark with best practices, reject anti-patterns, enrich into PRD in `docs/plans/`.
   - **Phase 2: Solution Architect (`csharp-architect`)**: Draft architectural implementation plan in `docs/plans/` with Hydrogen-ready work packages and integration impact pointers.
   - **Phase 3: Security Expert (`csharp-security-expert`)**: Security audit, Fail-Closed verification, binding `SEC-*` conditions and mandatory `[Trait("Category", "Security")]` test cases.
   - **Phase 4: Developer(s) (`csharp-tdd-developer` on `Gemini 4 Hydrogen`)**: Receive pre-analyzed, self-contained task briefs from the Senior Orchestrator (`Orchestrator (Argon)`), apply strict TDD Red-Green-Refactor, run **only** filtered local unit tests (`-m:2 -nodeReuse:false`) and touched architecture ratchet tests, proactively update affected tests in `tests/Autheris.Tests.Integration` / `tests/Autheris.Tests.Parity`, and compile-check `tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj` locally without running integration tests.
   - **Phase 5: Code Reviewer (`csharp-code-reviewer`)**: Code quality, C# 13 idioms, allocation/performance check, `SEC-*` compliance, and integration test adaptation check.
   - **After Phase 5**: update the progress tracker / master plan in `docs/plans/`, commit, push, clean worktree, and verify full unit + integration/parity runs on **GitHub Actions**. There is **no documentation phase** (owner rule 2026-10-17).

7. **Build & Test Verification (Local CPU/RAM Constrained; GitHub Actions for Full/Integration):**
   - Solution: `Autheris.sln` (.NET 10.0)
   - Local Unit Test (filtered only): `NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~<TargetTestClass>" --logger "console;verbosity=minimal" -v q --nologo -m:2 -nodeReuse:false`
   - Local Integration Compile Check (no execution): `NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -v q --nologo -m:2 -nodeReuse:false`
   - Full & Integration Execution: Offloaded to **GitHub Actions** (`gh run list --branch <branch>`, `gh pr checks`, `gh run view <id> --log-failed`).
