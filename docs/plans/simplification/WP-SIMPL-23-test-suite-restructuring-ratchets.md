# WP-SIMPL-23 Test-suite restructuring and ratchets
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-23-test-suite-restructuring-and-ratchets-req-simpl-08) | Issues: #519 (shard weights after moves), #523 | Owner decisions: OD-6, OD-17(6) | Security conditions: SEC-SIMPL-03, 50, 52, 60, 70

## 1. Goal and outcome
Unit tests live in domain folders mirroring the consolidated Application folders, are named after behaviour, are <= 800 lines per file, and are guarded by ratchets. Measurable: 0 `.cs` test files in the `Autheris.Tests.Unit` root (155 today); no test file > 800 lines; `Substitute.For` <= 3,000, `Received` <= 700, sleeps <= 40 (Stage 2 column); coverage map proves no Security test lost.

## 2. Preconditions and dependencies
- Stage 1 deletions of a domain merged before that domain is restructured (do not move tests that will be deleted).
- PR 23-1 (coverage map + ratchets) first; it is a prerequisite for test-moving PRs of WP-SIMPL-22 and Stage 1 deletions where possible.
- Parallel with WP-SIMPL-22 only on disjoint folders; WP-SIMPL-24 folder names define target folders (use current Application folders if 24 has not merged; rename with 24).

## 3. Scope
### In scope
- Move 155 root files into domain folders; fold review/finding-named files (`SecurityFindingsRemediationTests.cs` 1,378 lines, `BasicAuthDxTests`, `*Ext15Tests`, `SecurityReviewG5Tests`, `Csharp12MoatsTests`, `ComprehensiveSecurityAttackVectorTests`) into behaviour-named classes; split files > 800 lines; merge duplicates.
- Ratchets `TestSuiteRatchetTests`; baseline `mock-counts.txt`; lower `test-sleep-counts.txt`.
- Coverage map (SEC-SIMPL-60) and `TestCoverageMapTests`.
### Out of scope
- DB/host extraction (WP-SIMPL-22); deleting tests of deleted features (owned by the deleting WP); src changes.

## 4. Current state (verified on 63cbb893)
- `ls tests/Autheris.Tests.Unit/*.cs | wc -l` = 155 root files (includes `DefaultTenantCaller.cs` helper -> move to `Common/`).
- Baselines dir `tests/Autheris.Tests.Architecture/Baselines/` has `test-sleep-counts.txt`, `source-size-ceilings.txt` and others; no `mock-counts.txt`.
- Root examples: `BackstageIntegrationTests.cs`, `CasbinHotReloadTests.cs`, `DifferentialPrivacyTests.cs` (deleted by WP-SIMPL-12/14/18, skip), `BasicAuthDxTests.cs`, `ColumnMaskingTests.cs`, `ConsentResolutionTests.cs`.
- Plan baseline: Substitute.For 4,159, Received 986, sleeps 92, test LOC 382,125.

## 5. Implementation steps
1. Coverage map tooling (Red first): `scripts/test-coverage-map.py` lists test ids with categories (`dotnet test --list-tests` plus trait scan) at the program base commit `fe0e131d`; writes `docs/plans/2026-11-05-test-coverage-map.csv` (`old_id,new_id_or_reason,category`). Base Security list checked in at `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt`.
2. `TestCoverageMapTests` (Architecture project) reads the base list, the map and the current Security tests (reflection over test assemblies for `[Trait("Category","Security")]`); fails on any base id missing from current without a map row.
3. `TestSuiteRatchetTests` (Architecture): root-file baseline `unit-root-files.txt` (155 entries, shrink only), line ceiling 800 with baseline `test-file-line-ceilings.txt` for current offenders (shrink only), `mock-counts.txt` per project (`Substitute.For`, `.Received(`), folder allow-list `unit-test-folders.txt`. Each test fails when a count grows or a file appears that is not in the baseline; stale baseline entries also fail (forces lowering).
4. Per domain PR: `git mv` root files into `tests/Autheris.Tests.Unit/<Domain>/`, adjust namespace to `Autheris.Tests.Unit.<Domain>`, rename classes after behaviour, split > 800-line files by behaviour, delete exact duplicates; update baselines downward and the coverage map rows in the same PR.
5. Fold `SecurityFindingsRemediationTests.cs` last: distribute tests into domain classes, keep method names (SEC-SIMPL-52), map each in the CSV.
6. Replace `Thread.Sleep`/`Task.Delay` waits with `FakeTimeProvider` or awaitable signals; lower `test-sleep-counts.txt`. Replace `Received` call-count assertions with state assertions where possible.
7. Rerun #519 shard-weight script after the last move.

## 6. Tests (all in `Autheris.Tests.Architecture`)
- `TestCoverageMapTests.No_security_test_lost_without_mapping` [Security]; `.Map_rows_reference_existing_tests_or_deleting_wp` [Security].
- `TestSuiteRatchetTests.No_test_files_in_unit_root`, `.Test_file_line_ceiling`, `.Substitute_for_count_does_not_grow`, `.Received_count_does_not_grow`, `.Test_folders_follow_domains`, existing sleep ratchet lowered.
- `TestSuiteRatchetTests.Security_category_count_does_not_decrease` [Security] (SEC-SIMPL-03).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Unit -c Release`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --filter "FullyQualifiedName~TestSuiteRatchetTests|FullyQualifiedName~TestCoverageMapTests"`
- `ls tests/Autheris.Tests.Unit/*.cs | wc -l` = 0; `find tests -name '*Tests.cs' -exec wc -l {} + | awk '$1>800' | grep -v total` empty.
- `git grep -c 'Substitute.For' tests | awk -F: '{s+=$2} END {print s}'` <= 3,000.

## 8. Acceptance criteria
- [ ] Root files 0; no file > 800 lines; baselines for root files/line ceilings deleted at the end.
- [ ] Substitute.For <= 3,000, Received <= 700, sleeps <= 40, test LOC <= 307,000 (Stage 2).
- [ ] Coverage map complete; Security count >= base minus mapped "feature deleted" rows.
- [ ] No finding/review id in test class names (`git grep -lE 'class .*(Finding|Review|Ext[0-9]+|G[0-9]+)Tests' tests` = 0).

## 9. PR slicing
23-1 tooling + ratchets + map (<= 400 lines); then one PR per domain (moves are renames, net additions small); 23-last: SecurityFindingsRemediationTests fold, sleep/mock lowering. Security PRs are exempt from these ratchets via label only (SEC-SIMPL-70).

## 10. Risks, rollback, must NOT
- Risk: merge conflicts with L-PER/L-DEL; mitigated by per-domain PRs after that domain's deletions.
- Rollback: revert per domain PR; baselines revert with it.
- Must NOT: raise any baseline; drop a Security trait; rename a Security test without a map row; move tests a later WP deletes.

## 11. Estimate
Tests -10,000 to -20,000 (duplicate merges); tooling +600. 6 person-days.

## Decisions taken in this spec
- Coverage map stored as CSV in `docs/plans/` (SEC-SIMPL-60), base commit `fe0e131d` (plan verification commit).
- Ratchets use shrink-only baseline files in `Baselines/` (existing pattern) and fail on stale entries.
