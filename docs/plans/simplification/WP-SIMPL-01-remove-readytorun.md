# WP-SIMPL-01 Remove ReadyToRun
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-01-remove-readytorun-509) | Issues: #509 | Owner decisions: none specific (REQ-SIMPL-09.1) | Security conditions: SEC-SIMPL-01, SEC-SIMPL-03, SEC-SIMPL-04, SEC-SIMPL-65

## 1. Goal and outcome
Disable ReadyToRun for the framework-dependent API publish (the R2R spike was a NO-GO: 441 MB vs 373 MB, cold P50 5.58 s vs 4.52 s). After merge, no `PublishReadyToRun` element exists in any `*.csproj` or `Directory.Build.*`, the R2R benchmark is deleted, the locked restore graph (`packages.lock.json`) is unchanged, and the Security test count does not decrease.

## 2. Preconditions and dependencies
- None. Can run in parallel with WP-SIMPL-02..09.
- WP-SIMPL-10 runs its "before" measurement before this merges and its "after" measurement after it.

## 3. Scope
### In scope
- `src/Autheris.Api/Autheris.Api.csproj`: delete line 39 (`PublishReadyToRun`) and rewrite the comment at lines 34-36.
- Delete `benchmarks/Autheris.Benchmarks/ReadyToRunBenchmark.cs` (273 lines) and its `r2r` switch in `benchmarks/Autheris.Benchmarks/Program.cs:54-58`.
- Update `tests/Autheris.Tests.Unit/ReadyToRunPublishSecurityTests.cs` (assertions that require R2R).
- New architecture test `NoReadyToRunArchitectureTests`.
### Out of scope
- `RuntimeIdentifiers`, the conditional `RuntimeIdentifier` and `SelfContained` lines (csproj:38, 40, 41) stay, so the lock file keeps `net10.0/linux-x64` (see Decisions).
- Workflows (`docker-publish.yml`, `release.yml`): no R2R reference; no edit.
- `benchmarks/results/r2r-spike-report.md` and `benchmarks/load/scripts/measure-r2r-impact.sh` stay as evidence; `benchmarks/load/docker/docker-compose.yml:158` (`DOTNET_ReadyToRun: 1`, runtime default) stays.
- TieredPGO, ServerGC, OptimizationPreference unchanged.

## 4. Current state (verified on 63cbb893)
- `src/Autheris.Api/Autheris.Api.csproj:34-36` comment "PLAN-OPS-150 WP-D: ReadyToRun ..."; `:38` `<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>`; `:39` `<PublishReadyToRun Condition="'$(SelfContained)' != 'true'">true</PublishReadyToRun>`; `:40` conditional `RuntimeIdentifier` linux-x64 when publishing; `:41` `SelfContained=false` for linux-x64.
- `src/Autheris.Api/packages.lock.json` targets: `net10.0`, `net10.0/linux-x64`.
- `Directory.Build.props:14-15` `RestorePackagesWithLockFile`, `RestoreLockedMode` in CI.
- `benchmarks/Autheris.Benchmarks/Program.cs:54-58` dispatches `r2r` to `ReadyToRunBenchmark.Run(args[1..])`.
- `tests/Autheris.Tests.Unit/ReadyToRunPublishSecurityTests.cs` (132 lines): Security tests `DockerPublish_ReadyToRun_KeepsLockedRestoreAuditPinsAndTestAuthExclusion` (line 21; asserts `PublishReadyToRun` single item at 39-40), `ApiCsproj_ReadyToRun_NoPackVersionOverrideNoFeedOrAuditChange` (50), `ApiLockFile_HasLinuxX64Target_RestoreLockedModeCondition_Unchanged` (74); non-security `ApiCsproj_KeepsTieredPgo` (87), `ApiCsproj_ReadyToRun_NotSelfContained_NotForSelfContainedRelease` (~98; asserts `PublishReadyToRun` single item).
- `tests/Autheris.Tests.Architecture/RepositoryScan.cs` provides `RepoRoot`, `EnumerateFiles(relativeDirectory, pattern)`, `ReadText`.

## 5. Implementation steps (TDD)
1. Red: add `tests/Autheris.Tests.Architecture/NoReadyToRunArchitectureTests.cs` (sealed class, uses `RepositoryScan.EnumerateFiles("", "*.csproj")` plus `Directory.Build.props`/`Directory.Build.targets` if present; skips bin/obj via RepositoryScan). Assert no file contains the element `PublishReadyToRun` with value `true` (XML parse with `XDocument`, element name match, not substring, so comments do not trigger). Fails now on csproj:39.
2. Red: in `ReadyToRunPublishSecurityTests.cs` change line 39-40 to `project.Descendants("PublishReadyToRun").ShouldBeEmpty();` and in the non-security test replace the `PublishReadyToRun` assertion with `ShouldBeEmpty()`; rename that non-security method to `ApiCsproj_NoReadyToRun_NotSelfContained_NotForSelfContainedRelease`. Keep the three Security method names and all other assertions (lock file, audit pins, test-auth exclusion, forbidden pack overrides) unchanged.
3. Green: delete csproj line 39; replace comment 34-36 with: `<!-- #509: ReadyToRun removed (spike NO-GO, benchmarks/results/r2r-spike-report.md). RuntimeIdentifiers keeps the linux-x64 graph in the locked restore used by the framework-dependent publish. -->`.
4. Delete `ReadyToRunBenchmark.cs` and the `r2r` block in `Program.cs`. If `benchmarks/Autheris.Benchmarks/packages.lock.json` changes on restore, stop and report (it must not).
5. Update the class summary of `ReadyToRunPublishSecurityTests` to say the tests guard the publish settings after #509.

## 6. Tests
- New `Autheris.Tests.Architecture`: `NoReadyToRunArchitectureTests.No_project_or_props_file_enables_PublishReadyToRun` (`[Trait("Category","Security")]` not required; plain architecture test).
- Changed `Autheris.Tests.Unit`: `ReadyToRunPublishSecurityTests.DockerPublish_ReadyToRun_KeepsLockedRestoreAuditPinsAndTestAuthExclusion` (Security, now asserts absence), `ApiCsproj_NoReadyToRun_NotSelfContained_NotForSelfContainedRelease` (renamed, non-security).
- Unchanged and must pass: `ApiCsproj_ReadyToRun_NoPackVersionOverrideNoFeedOrAuditChange`, `ApiLockFile_HasLinuxX64Target_RestoreLockedModeCondition_Unchanged`, `ApiCsproj_KeepsTieredPgo`, `ReleaseBuildExcludesTestAuthTests`, `BenchmarkStackHardeningTests`.
- No test deleted; Security count unchanged (SEC-SIMPL-03).

## 7. Verification commands
- `dotnet restore Autheris.sln --locked-mode` (must succeed with no lock file diff: `git diff --exit-code -- '*packages.lock.json'`).
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build --filter "FullyQualifiedName~NoReadyToRunArchitectureTests"`
- `dotnet test tests/Autheris.Tests.Unit -c Release --no-build --filter "FullyQualifiedName~ReadyToRunPublishSecurityTests|FullyQualifiedName~ReleaseBuildExcludesTestAuthTests"`
- `dotnet publish src/Autheris.Api/Autheris.Api.csproj -c Release --no-restore -p:AutherisIncludeTestAuth=false -o /tmp/pub && ! ls /tmp/pub/*.r2r* 2>/dev/null`
- `git grep -n 'PublishReadyToRun' -- '*.csproj' '*.props' '*.targets'` returns nothing.

## 8. Acceptance criteria
- [ ] No `PublishReadyToRun` element in any csproj/props/targets.
- [ ] `ReadyToRunBenchmark.cs` and the `r2r` dispatch deleted; benchmarks project builds.
- [ ] All `packages.lock.json` files unchanged; locked restore succeeds.
- [ ] Security test count (filter `Category=Security`) equal to base.
- [ ] Dockerfile non-root user and read-only root filesystem settings and the Trivy step untouched (SEC-SIMPL-65; `git diff --stat` shows no Dockerfile/workflow change).

## 9. PR slicing
One PR (about +40 / -285 lines): csproj, benchmark deletion, test changes, new architecture test.

## 10. Risks, rollback, must NOT do
- Risk: lock graph change if `RuntimeIdentifiers` were removed; therefore it stays. Rollback: revert the PR.
- Must NOT: remove `RuntimeIdentifiers`/`RuntimeIdentifier`/`SelfContained`, touch workflows or Dockerfiles, delete or rename any Security test, regenerate lock files, enable trimming or AOT.

## 11. Estimate
+40 / -285 LOC; 0.5 person-days.

## Decisions taken in this spec
- D-01-1: `RuntimeIdentifiers` and the publish-time `RuntimeIdentifier`/`SelfContained` stay (plan: "keep only if the locked restore needs it"; it does: `--no-restore` publish resolves linux-x64 from the lock file, and the Security test asserts the `net10.0/linux-x64` target).
- D-01-2: Security test method names stay to avoid a SEC-SIMPL-03 coverage-map row; only the non-security method is renamed.
