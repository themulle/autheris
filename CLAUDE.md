# Autheris Guidelines for Claude Code & AI Assistants

See [AGENTS.md](AGENTS.md) for full project governance.

## Mandatory Directives

1. **Language Standard:**
   - All documentation, READMEs, PRDs, architecture plans, inline comments, and commit messages **MUST be written in English**, matching the root [README.md](README.md).

2. **Strict Separation of Documentation vs. Development Progress:**
   - **`docs/` is ONLY for system reference & manuals** describing how the system currently works.
   - **NEVER put development progress, status tags (`Status: In Progress`, `Done`), WIP notes, checklists, or roadmaps inside `docs/`** (and do not add new files to `docs/plans/`, which is retired).
   - **All development progress, implementation details/plans, work instructions (including code reviews), and status tracking belong EXCLUSIVELY in GitHub Issues (`gh issue`)**.

3. **GitHub-Issue-First Requirements & Plans:**
   - All new requirements, design proposals, implementation details, and agent work instructions must be tracked in **GitHub Issues** (`gh issue`), with implementation plans attached in the issue body or `## Implementation Details` comments.

4. **Multi-Agent Issue Claiming & Coordination:**
   - Before working on an issue, check it is not already claimed (`in-progress` label or active claim comment).
   - Immediately claim it with `gh issue edit <n> --add-label "in-progress"` and a claim comment (`host`, `agent role`, `branch`, `UTC timestamp`) so agents across machines coordinate cleanly.
   - Remove `in-progress` on handover or close the issue on completion.

5. **Individual Feature Documentation (`docs/features/`):**
   - Feature documentation is **not** part of the implementation lifecycle (there is no documentation phase). Write feature docs only when the owner explicitly asks for them.
   - When a feature doc is written, it goes to `docs/features/` (e.g., `docs/features/f-<category>-<number>-<name>.md`), is registered in [`docs/features/README.md`](docs/features/README.md), and describes how the feature works, configuration, and architecture (strictly without WIP or progress notes).

6. **The 5-Phase Standard Implementation Lifecycle (Senior Argon + Junior Hydrogen Pairing):**
   - **Phase 1: Product Manager (`product-manager`)**: Qualify, benchmark with best practices, reject anti-patterns, create/enrich GitHub Issue (`gh issue`).
   - **Phase 2: Solution Architect (`csharp-architect`)**: Attach architectural implementation details directly to the GitHub Issue (`## Implementation Details` comment or body) with Hydrogen-ready work packages and integration impact pointers.
   - **Phase 3: Security Expert (`csharp-security-expert`)**: Security audit, Fail-Closed verification, binding `SEC-*` conditions and mandatory `[Trait("Category", "Security")]` test cases appended to the GitHub Issue.
   - **Phase 4: Developer(s) (`csharp-tdd-developer` on `Gemini 4 Hydrogen`)**: Claim issue (`in-progress` label + comment) via Senior Orchestrator (`Orchestrator (Argon)`), apply strict TDD Red-Green-Refactor, run **only** filtered local unit tests (`-m:2 -nodeReuse:false`) and touched architecture ratchet tests, proactively update affected tests in `tests/Autheris.Tests.Integration` / `tests/Autheris.Tests.Parity`, and compile-check `tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj` locally without running integration tests.
   - **Phase 5: Code Reviewer (`csharp-code-reviewer`)**: Code quality, C# idioms, allocation/performance check, `SEC-*` compliance, and integration test adaptation check.
   - **After Phase 5**: commit, push, clean worktree, verify full CI suites on **GitHub Actions**, merge PR, remove `in-progress`, and close the GitHub Issue. There is **no documentation phase** (owner rule 2026-10-17).

7. **Build & Test Verification (Local CPU/RAM Constrained; GitHub Actions for Full/Integration):**
   - Solution: `Autheris.sln` (.NET 10.0)
   - Local Unit Test (filtered only): `NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~<TargetTestClass>" --logger "console;verbosity=minimal" -v q --nologo -m:2 -nodeReuse:false`
   - Local Integration Compile Check (no execution): `NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -v q --nologo -m:2 -nodeReuse:false`
   - Full & Integration Execution: Offloaded to **GitHub Actions** (`gh run list --branch <branch>`, `gh pr checks`, `gh run view <id> --log-failed`).
