# Audit of the not-yet-implemented plans (2026-11-19)

This audit is planning and documentation only. It changes no code.

Checked against `origin/main` at 37578c014. That commit includes #899, #904, #905, #913 and the #917 interim budgets. The audit also read the local, unpushed implementation branches and the new-issue plans on the local branch `docs/plans-new-issues-2026-11-19`. Plans whose work has merged are skipped unless something is still open.

Some plan files are also changed by local implementation branches:
- WP-20 addendum
- WP-23
- WP-28
- WP-29
- WP-38
- WP-52
- the handover document

Those files are not edited here, so later merges do not conflict. Their findings are listed below and must be applied by the branch that owns the file. Plans without local changes were fixed in place, each with a `2026-11-19 audit` changelog line:
- WP-10, WP-25, WP-32, WP-35, WP-36, WP-48.

## Verdicts

| Plan | Verdict | Edited here |
|---|---|---|
| WP-20 PR A + #915 | Implemented locally; small text update | no (local branch) |
| WP-23 last-a/b/c | Partly implemented locally; plan stale | no (local branch) |
| WP-28 PR 3a/3b/3c | Implemented locally | no (local branch) |
| WP-28 PR 4 (OpenMetadata) | Needs update (4a in progress, halted as WIP) | no (local branch) |
| WP-29 PR 2a/2b/3 | Implemented locally (PR 3 in review) | no (local branch) |
| WP-29 PR 4 / 4b / 5 | Needs update | no (local branch) |
| WP-32 | Needs update (targets, order, work packages) | yes |
| WP-35 | Needs update (#917 interim budgets) | yes |
| WP-10 | Needs minor update | yes |
| WP-36 | Needs update (6c closed; three private advisory residuals) | yes |
| WP-25 follow-ups | Implemented except two Lows | yes |
| WP-48 PR 7 | Done (#882); two Lows | yes |
| WP-38 PR 5 (#908) | Implemented locally; section 6 stale | no (local branch) |
| WP-52b profile items | Implemented locally (step 0031); plan body stale | no (local branch) |
| New-issue plans (rows 30-39) | Mixed, see below | no (other PM's branch) |

## Findings: module extractions (WP-28, WP-29)

### `ModuleHttpClientGuard`
`ModuleHttpClientGuard` exists only on the WP-29 PR 3 branch. SEC-28-47 has WP-28 PR 4a port a second copy. Instead, land WP-29 PR 3 first and have 4a use that guard. The test `Module_httpclient_without_ssrf_handler_fails_startup` stays in one place.

### `ModuleSecurityGuards`
Both lines change `ModuleSecurityGuards`:
- WP-28 adds the table-driven owned-port check.
- WP-29 adds seven protected types and the hosted-service allowance.

The second branch to land merges these by hand. The OpenMetadata ports belong in the owned-port table, not in `ProtectedServiceTypes`.

### `EnforcedModuleSections`
WP-29 PR 2a lands first. After that, WP-28 3b adds the dbt row and 4b adds the openmetadata row. Both rows are in the scope of those PRs; they are not rebase leftovers.

### Early key pass
The early key pass in `Program.cs` (from WP-29 PR 3) defers the sections of enabled modules. WP-28 4b therefore needs two tests:
- openmetadata enabled: its keys are accepted;
- openmetadata not enabled: startup fails.

### WP-28 4b scope gap
`CatalogHttpContent` (`Extensions/DataCatalog`) is used only by `OpenMetadataCatalogAdapter`. It must move with the adapter or be deleted.

### WP-29 PR 4 (lineage)
Lineage is independent of WP-28 PR 4 and needs only WP-29 PR 3. The work package is not Hydrogen-ready yet. Add:
- the moves: `Application/Lineage/IOpenLineageClient.cs` and `Extensions/Lineage/{OpenLineageClient,LineageExportServiceCollectionExtensions}`;
- the REST routes at `GovernanceEndpoints.Audit.cs:302-307` plus `SyncOpenLineageAsync` at :388; no GraphQL field exists;
- `DataCatalogOptions.cs:39-40`, with a removed-key message;
- `AddLineageExportIntegration`;
- the baselines: ingress exemptions, read-only routes, project edges, snapshots, CI shard weights;
- the SEC-29-07 redaction keys;
- the test `Lineage_routes_return_404_without_module`.

### Owner-review CI PR
WP-28 PR 4c and WP-29 PR 4b both edit `docker-publish.yml` and `module-e2e.sh`. Merge them into one owner-review CI PR that signs and checks events, lineage and openmetadata together.

### WP-29 PR 5
Precondition 2 still names WP-12 "DataCatalog deleted". That is stale, because the DataCatalog sync stays core.

These items remain for PR 5:
- `DataCatalogSyncService`, `DataCatalogSyncBackgroundService` and `DataCatalogServiceCollectionExtensions`;
- `CatalogHttpContent`, unless 4b moves it;
- `IGovernanceWorkflowHook`, which is an alias;
- `BoundedHttpContent`, to be folded into `BoundedHttpContentReader`;
- `ExtensionRegistration` and `ExtensionsServiceCollectionExtensions`.

The Api usings are in `GatewayExecutionServiceExtensions.cs:25` and `GatewayServiceCollectionExtensions{,.GraphQL,.Options}.cs`. Also check `Autheris.Extensions.Tests` and `InternalsVisibleTo`.

### Merge order after the v1.9.0 tag
1. WP-28 3a.
2. WP-29 2a, then 2b.
3. WP-29 PR 3, rebased onto main.
4. WP-28 3b. It reconciles the guards and adds the dbt section row.
5. WP-28 3c.
6. WP-28 4a (without the guard port), then 4b.
7. WP-29 PR 4.
8. The single owner-review CI PR.
9. WP-29 PR 5.

## Findings: WP-20, WP-23, WP-52b, WP-38 PR 5

### WP-20 PR A and #915
- The plan calls `ConsentGrantSqlCore` and `ConsentSqlCore` "stale". They are live top-level types: `ConsentSqlCore.BindIdInClause` is used in `CatalogBatchUpsertHelper:432` and in `ClassificationAccess:98`.
- As a result, the acceptance item "no *SqlCore cutover row other than AuditSqlCore" cannot be met. Restate it to accept the two rows.
- `MaxAllowedLoc` is 15_842 on main. The local stack ends at 15_842 (PR A -1, #915 +1). Merge PR A and #915 together, or #915 needs `freeze-exception`.

### WP-23
- Locally, Received (497) and sleeps (30) already meet their targets. Substitute.For is 3760 against a target of 3000.
- The test-LOC target of 307,000 cannot be reached: tests are about 491,700 lines today.
- The plan's "155 root files" is stale; the count is 0.
- `unit-root-files.txt` and `test-file-line-ceilings.txt` (28 entries) still exist.
- The changelog has no lines for last-a, c1 or c2.
- c3 is a halted local WIP commit (6223e8995). It removes about 188 substitutes, but it is unbuilt and needs a build and test pass before it can be kept.
- Remaining work:
  - c4: about -600 substitutes, in slices of about 150, each using the c2 fakes.
  - c5: split the 28 files that exceed 800 lines.
  - c6: delete `unit-root-files.txt`, recheck the class-name rule, and rerun the shard weights.

### WP-52b
- The plan body still says step 0030, `AccessProfileItems0030Tests` and a PostgreSQL-only integration test. Change these to step 0031 with tests on both engines.
- The parent WP-52 status still reads "Draft (Phase 2)".
- Add to the upgrade notes: PostgreSQL 16 is the minimum, and a table entry with a surrogate pair (for example an emoji) aborts the PostgreSQL step. This is fail-closed.

### WP-38 PR 5
Section 6 still describes containerised clients and Security-tagged interop tests. The branch actually runs the clients on the runner against HTTPS loopback, tagged `Category=TrinoInterop` only.

A local run needs Java 17+, a Python venv and a container runtime.

Owner verification steps:
1. Set `AUTHERIS_TRINO_JDBC_JAR` (484, SHA-256 checked) and `AUTHERIS_TRINO_PYTHON`, and install with `pip install --require-hashes`.
2. Run the Integration filter `Category=TrinoInterop` (10 tests).
3. Merge, then dispatch the nightly.
4. If the nightly fails, file an issue.

## Findings: plans fixed in place

- **WP-32.** Counts on main:
  - 165 options classes (target 90);
  - 948 properties (target 600);
  - 70 `*Enabled` flags (target 40);
  - 462 undocumented properties (target 0).

  "Module options live in modules" is wrong today: only `CdcWebhooksOptions` moves (WP-29 PR 3). The 40-flag and 600-property targets look unreachable under R13 and SEC-SIMPL-32. The estimated floors are about 43 to 47 flags and about 650 properties.

  Positional records could game the class and property budget, so a rule against that was added. The work packages are now PR 0 to PR 7, run serially:
  - PR 0: scaffold;
  - PR 1: Governance;
  - PR 2: Catalog;
  - PR 3a/3b: Security;
  - PR 4a/4b: Query;
  - PR 5a/5b: Runtime;
  - PR 6: module-owned options;
  - PR 7: baseline.

  The ratchet and test names are corrected to what PR 1 actually created.
- **WP-35.** Main already holds the owner's interim budgets from #917 (430 MB, 5.0 s cold start, P99 3.47 ms, CI median 120 min). The plan body and the `FootprintBudgetTests` spec still say 325 MB / 3.0 s.

  The core flavor without modules still measures about 410 MB. Reaching 325 MB therefore needs a new WP-SIMPL-35b, built on a per-layer measurement:
  - the base image;
  - the Kerberos apt layer;
  - the RID, native and satellite assets;
  - the duplicate `chown -R` layers.
- **WP-10.** The result file `footprint-2026-11-18-feeab35.json` records the better of two cold-start samples. Results are append-only, so a stable baseline needs a new file.
- **WP-36.** All sub-packages are merged; 6c needs no PR. The 5c test names differ from section 6. The WP stays open only for three private advisories (x8g9, rxr4, 66xj), which are tracked privately.
- **WP-25.** Two Lows remain:
  - `ModuleLoader.cs:192` passes no `ownerUidProbe`, so runtime root ownership is enforced only at image level;
  - there is no negative CI job for unsigned modules.
- **WP-48 PR 7.** Done (#882). Lows: S30 to S37 are outside the 19-surface theory, and the Deny-only S34 variant is missing.

## Findings: new-issue plans (rows 30-39, other PM's branch)

- **#849:** OK, minor. Name the logger injection path. Hard order: WP-L-2 (`SqlFailureInfo`), then #849.
- **#889:** needs update.
  - `ReadOnlyScopes` hits the shrink-only `OptionsBudgetTests`; state an offset.
  - Add `GatewayEmptyListConfigurationValidator.cs:29`.
  - Add snapshot and config-reference regeneration.
  - Add an Entra/AD FS `scope` regression test.
  - D-889-3 (a missing scope claim means not read-only) needs explicit Phase 3 SEC conditions.
- **#891:** OK. Add the test folder `tests/Autheris.Tests.Unit/Products/`.
- **#909:** needs update. The 600-character `[Theory]` lookback also hides tests. Either add a lookback work package with the test `Theory_WithLongInlineDataList_IsFound`, or make option B (Roslyn) the default. Land it after WP-52b and WP-23, which touch the same inventory files.
- **#915:** OK, implemented locally, stacked on WP-20 PR A. Re-measure `MaxAllowedLoc` on rebase.
- **#916** (step 0032): needs update.
  - Recheck the `ExternalCatalogReader` path and line.
  - `DbtObjectKindResolver` moves to `modules/Autheris.Modules.Dbt/` with WP-28 3b.
  - `RepositoryScan` does not cover `modules/`, so the ratchet needs a module scan.
  - Module metadata must go through the module port.
  - SEC conditions are missing for D-MV-5/7/8.
  - Q1 and Q3 need defaults.
- **#910/#911/#912:** needs update. They cite 325 MB / 3.0 s, but main has the #917 interim budgets. #911 is covered by #917; rerun the nightly. Check #912 against #919. The Q1 default contradicts #917.
- **PR #887 follow-ups:** OK, minor.
  - The line references are shifted.
  - Phase 3 SEC conditions for F-887-1/3/4 are missing.
  - Q1 should say "file the hardening issue", per the auto-file rule.
  - Link to the WP-38 plan.
- **PR #899/#904/#913 Lows:** OK, minor. L-899-1 and L-899-2 are fixed in the local WP-20 PR A. WP-L-3 depends on PR A.
- **#894/#896/#898:** needs update. #896 is done (#904, step 0030), but decision 11 still says 0029. The status still reads "Phase 3 next" although Phase 3 conditions exist. Verify that #880 has merged.
- **#918:** duplicates WP-28 PR 4a. SEC-28-44, the test `Cluster_dedup_unavailable_rejects_webhook`, and 4a moving the same code already cover it, so fold #918 into 4a. Keep only a dbt check (path `modules/Autheris.Modules.Dbt/`), and only if SEC-28-06 does not already cover it.

## Other open items from the handover (section 4)

Handover section 4 names "small leftovers in WP-12, 39, 40, 42", but no plan specifies them. Each needs either a short plan or an explicit closure note.

WP-19 PR-C, WP-27, WP-31, WP-44/45, WP-46 and WP-50 are done on main.

## DDL step queue

| Step | Owner | State |
|---|---|---|
| 0029 | role grantee | done |
| 0030 | #904 table object kind | done |
| 0031 | WP-52b profile items | local, approved |
| 0032 | #916 | planned |

The next free step after these is 0033.

## Owner questions (recommended default)

1. CODEOWNERS for the events, lineage and openmetadata modules. Default: put the explicit module-folder lines, with the Lakehouse owner, into each module-creating code PR, as was done for dbt. No glob.
2. SEC-29-09 parity depth. Default: the harness case is SQLite or in-memory. Real PostgreSQL logical-replication parity is a follow-up.
3. WP-32 targets. Default:
   - keep classes at 90 or fewer, and undocumented at 0;
   - set flags to 48 or fewer;
   - set properties to the floor measured after PR 4, never above 650.
4. WP-32 module options. Default: move the CDC, dbt, OpenMetadata and Lakehouse options into their modules (PR 6).
5. WP-23. Default: drop the test-LOC target and keep Substitute.For at 3,000 or fewer, reached in c4 slices.
6. WP-20. Default: accept the `ConsentSqlCore` / `ConsentGrantSqlCore` cutover rows and restate the criterion.
7. WP-35 footprint. Default: keep the #917 interim budgets for 1.9.0, then open WP-SIMPL-35b after the tag. It tightens to 420 MB / 3.5 s first, then 325 MB / 3.0 s by 2027-01-15. Also: accept a Kerberos opt-in image flavor, if the SQL Server Windows-auth tests stay green.
8. WP-25 Lows. Default: one small owner-review PR after the tag.
9. #918. Default: fold it into WP-28 PR 4a.

## Changelog
- 2026-11-19: audit created by the second project manager. No code changes.
