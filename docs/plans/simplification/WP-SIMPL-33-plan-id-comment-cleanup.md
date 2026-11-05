# WP-SIMPL-33 Plan-id comment cleanup
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-33-plan-id-comment-cleanup-req-simpl-02d) | Issues: #515 | Owner decisions: none specific | Security conditions: SEC-SIMPL-03 (no code change, no test loss)

## 1. Goal and outcome
Remove plan/ticket ids (e.g. `WP-*`, `ARCH-MNT-*`, `API-*`, `SEC-*`, `REQ-*`, `REV-*`, `AST-X1`, `PLAN-*`; exact regex in section 4) from code comments in `src/` and `modules/`; only GitHub issue references (`#123`) for known limitations remain. A ratchet test pins the count, lowered per PR, at 0 after the last PR. Governance repository LOC <= 6,000 afterwards.

## 2. Preconditions and dependencies
- Merged: WP-SIMPL-26..29, 31 and 32 (do not clean code twice; WP-SIMPL-30 is cancelled, OD-21). Precedes WP-SIMPL-34.

## 3. Scope
### In scope
- `tools/strip-plan-ids.py` (new), per-project cleanup PRs, `ProgramRatchetTests.Plan_id_comments_do_not_grow` (baseline lowered per PR, 0 in the last PR), residual governance-repository comment cleanup.
### Out of scope
- `docs/`, tests' display names, string literals, error codes.

## 4. Current state (verified on 63cbb893)
- Regex `//.*\b(WP-[A-Z]+-[0-9]+|ARCH-MNT-[0-9]+|API-[0-9]+|SEC-[A-Z]+-[0-9]+|REQ-[A-Z]+-[0-9]+)` matches 684 lines in `src/*.cs`; PRD estimate ~1,860 lines / >= 521 ids with the full id set (incl. XML doc comments).
- The single plan-id regex used by the script (step 2) and the ratchet test (step 1), applied to comment trivia (`//`, `///`, `/* */`) only: `\b(WP-[A-Z]+-[0-9]+|ARCH-MNT-[0-9]+|API-[0-9]+|SEC-[A-Z]+-[0-9]+|REQ-[A-Z]+-[0-9]+|REV-[0-9]+|AST-X[0-9]+|PLAN-[A-Z]+-[0-9]+)\b`. GitHub issue references (`#123`) do not match. The developer re-counts with this regex in PR 1 and records the number as the initial baseline.
- Example: `src/Autheris.Api/Endpoints/ODataEndpoints.cs:91` (`// API-15: ...`).

## 5. Implementation steps
1. `ProgramRatchetTests.Plan_id_comments_do_not_grow` in `tests/Autheris.Tests.Architecture` using the section-4 regex, with baseline file `Baselines/plan-id-comment-counts.txt` (per project) = the current count in PR 1 (green); every cleanup PR lowers its project's count in the same PR; the last PR sets every count to 0.
2. Script `tools/strip-plan-ids.py`: Roslyn-free, uses the section-4 regex over comment trivia only (`//`, `///`, `/* */`): remove `ID:` / `(ID)` / `[ID]` prefixes and suffixes; drop the line if only an id remains; never touch string literals. Flags `--project <path> --dry-run --stats`.
3. Verifier `tools/strip-plan-ids/VerifyTrivia` (small console app or test helper): parses before/after with `CSharpSyntaxTree` and compares `ToString()` of trees without trivia; any difference fails.
4. Run per project; review by diff statistics (comment lines only); fix wording where a sentence becomes ungrammatical.
5. Last PR: all baseline counts 0.

## 6. Tests
- `ProgramRatchetTests.Plan_id_comments_do_not_grow` (Architecture).
- `ProgramRatchetTests.Plan_id_regex_allows_github_issue_refs` (`// see #123` passes).
- `StripPlanIdsScriptTests` not required (script verified by trivia comparison).

## 7. Verification commands
- `python3 tools/strip-plan-ids.py --project src/Autheris.Api --dry-run --stats`
- Trivia verifier on each changed file set.
- `dotnet build Autheris.sln -c Release -warnaserror`; `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build`.

## 8. Acceptance criteria
- [ ] Plan-id regex count in `src/` and `modules/` comments = 0.
- [ ] Trivia-free syntax trees identical before/after.
- [ ] Governance repository LOC <= 6,000.

## 9. PR slicing
First PR adds script, verifier and the ratchet with the current counts as baseline (green). Then one PR per project (Api, Application, Infrastructure, Domain, GraphQL, modules, TrinoSqlEngine), each lowering its baseline; the last PR reaches 0 everywhere.

## 10. Risks, rollback, must NOT
- Must NOT change code tokens, string literals or test names; revert per PR if verifier fails.

## 11. Estimate
+~200 tooling / -~1,900 comment lines. 2 person-days.
