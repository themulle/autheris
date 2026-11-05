# WP-SIMPL-33 Plan-id comment cleanup
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-33-plan-id-comment-cleanup-req-simpl-02d) | Issues: #515 | Owner decisions: none specific | Security conditions: SEC-SIMPL-03 (no code change, no test loss)

## 1. Goal and outcome
Remove plan/ticket ids (e.g. `WP-*`, `ARCH-MNT-*`, `API-*`, `SEC-*`, `REQ-*`, `REV-*`, `AST-X1`) from code comments in `src/` and `modules/`; only GitHub issue references (`#123`) for known limitations remain. A ratchet test pins the count at 0. Governance repository LOC <= 6,000 afterwards.

## 2. Preconditions and dependencies
- Merged: WP-SIMPL-26..32 (do not clean code twice). Precedes WP-SIMPL-34.

## 3. Scope
### In scope
- `tools/strip-plan-ids.py` (new), per-project cleanup PRs, `ProgramRatchetTests.Plan_id_comments_do_not_grow` baseline 0, residual governance-repository comment cleanup.
### Out of scope
- `docs/`, tests' display names, string literals, error codes.

## 4. Current state (verified on 63cbb893)
- Regex `//.*\b(WP-[A-Z]+-[0-9]+|ARCH-MNT-[0-9]+|API-[0-9]+|SEC-[A-Z]+-[0-9]+|REQ-[A-Z]+-[0-9]+)` matches 684 lines in `src/*.cs`; PRD estimate ~1,860 lines / >= 521 ids with the full id set (incl. XML doc comments).
- Example: `src/Autheris.Api/Endpoints/ODataEndpoints.cs:91` (`// API-15: ...`).

## 5. Implementation steps
1. Red: `ProgramRatchetTests.Plan_id_comments_do_not_grow` in `tests/Autheris.Tests.Architecture` with baseline file `Baselines/plan-id-comment-counts.txt` = 0; fails today.
2. Script `tools/strip-plan-ids.py`: Roslyn-free regex over comment trivia only (`//`, `///`, `/* */`): remove `ID:` / `(ID)` / `[ID]` prefixes and suffixes; drop the line if only an id remains; never touch string literals. Flags `--project <path> --dry-run --stats`.
3. Verifier `tools/strip-plan-ids/VerifyTrivia` (small console app or test helper): parses before/after with `CSharpSyntaxTree` and compares `ToString()` of trees without trivia; any difference fails.
4. Run per project; review by diff statistics (comment lines only); fix wording where a sentence becomes ungrammatical.
5. Set baseline 0.

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
One PR per project (Api, Application, Infrastructure, Domain, GraphQL, Extensions/modules, TrinoSqlEngine); first PR adds script + ratchet (ratchet enabled in the last PR).

## 10. Risks, rollback, must NOT
- Must NOT change code tokens, string literals or test names; revert per PR if verifier fails.

## 11. Estimate
+~200 tooling / -~1,900 comment lines. 2 person-days.
