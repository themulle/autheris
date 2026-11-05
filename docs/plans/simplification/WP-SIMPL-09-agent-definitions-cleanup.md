# WP-SIMPL-09 Agent and skill definitions cleanup
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-09-agent-and-skill-definitions-cleanup) | Issues: #515 (Stage 0), #511 (via WP-SIMPL-03) | Owner decisions: D10 (default taken), OD-17 | Security conditions: SEC-SIMPL-74

## 1. Goal and outcome
Delete the four agent definitions without a product match and fix their references. Outcome: `git grep -n 'conversion-copywriter\|qa-cro-reviewer\|frontend-developer\|ui-ux-architect'` returns hits only in `docs/plans/` (historical plans), and no security guidance is lost.

## 2. Preconditions and dependencies
- WP-SIMPL-03 merged first (it removes `readme-copywriter` agent/skill and the AGENTS.md `readme-copywriter` role, AGENTS.md:127). Rebase on it.
- Parallel with WP-SIMPL-08.

## 3. Scope
### In scope
- Delete `.agents/agents/conversion-copywriter.md`, `.agents/agents/qa-cro-reviewer.md`, `.agents/agents/frontend-developer.md`, `.agents/agents/ui-ux-architect.md`.
- Fix `.agents/README.md:32` and any further reference found by the grep.
### Out of scope
- `readme-copywriter` (WP-SIMPL-03). Kept agents: product-manager, csharp-tdd-developer, project-manager, orchestrator-lead; all `.agents/skills/*` except readme-copywriter.
- Local `.claude/agents` mirrors outside the repo (owner concern; no `.claude` folder is tracked).

## 4. Current state (verified on 63cbb893)
- `.agents/agents/` holds 9 files incl. the four above and `readme-copywriter.md`.
- References: `.agents/README.md:32` ("`frontend-developer.md`, `orchestrator-lead.md`, `qa-cro-reviewer.md`, etc."); `AGENTS.md:127` (readme-copywriter, owned by WP-SIMPL-03); plan/PRD files in `docs/plans/` (keep).

## 5. Implementation steps
1. `git rm` the four files.
2. Edit `.agents/README.md:32` to list the remaining agents explicitly: `orchestrator-lead.md` only (plus the three already listed above it). Remove "etc.".
3. Run the grep in section 7; fix any non-`docs/plans/` hit.
4. Re-read the diff: no line mentioning fail-closed, Phase 3, security expert or advisory confidentiality is removed (SEC-SIMPL-74).

## 6. Tests
- No C# test. If WP-SIMPL-05 is merged, `DocsIntegrityTests.Backticked_source_paths_exist` (SEC-SIMPL-74 `Docs_reference_existing_source_paths`) and `DocsIntegrityTests.Relative_links_resolve` must stay green (they catch references and links to deleted files).

## 7. Verification commands
- `git grep -n -E 'conversion-copywriter|qa-cro-reviewer|frontend-developer|ui-ux-architect' -- ':!docs/plans/'` -> empty.
- `ls .agents/agents` -> csharp-tdd-developer.md, orchestrator-lead.md, product-manager.md, project-manager.md (readme-copywriter.md gone after WP-SIMPL-03).
- If WP-SIMPL-05 merged: `dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --filter "FullyQualifiedName~Docs"`.

## 8. Acceptance criteria
- [ ] Four files deleted; grep outside `docs/plans/` empty.
- [ ] AGENTS.md and `.agents/README.md` still describe the 5-phase lifecycle incl. Phase 3 security expert.

## 9. PR slicing
One PR, docs/agents only (-103 lines in the four files, ~+1/-1 in README).

## 10. Risks, rollback, must NOT
- Must NOT edit `docs/plans/` history or delete any skill other than readme-copywriter (WP-SIMPL-03). Must NOT touch security wording.
- Rollback: revert.

## 11. Estimate
0 src; -103 doc lines. 0.25 pd.

## Decisions taken in this spec
- Historical references inside `docs/plans/` stay (plans are records); the AC grep excludes them.
