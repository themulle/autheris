# PLAN-OI-CQ: Open issues, code-quality cluster (#504, #613)

- Date: 2026-11-13 · Base: `origin/main` @ `10400ec4` · Plan status: PLANNED (Phase 2 done, Phase 3 pending)
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) · Program specs: [`simplification/README.md`](simplification/README.md) · PRD [`2026-11-05-req-project-simplification.md`](2026-11-05-req-project-simplification.md) section 2.0 (OD-1..OD-45) is binding; a later OD wins.
- Freeze (PRD 5.1): both items are tests, analyzer configuration and behaviour-neutral refactors; no freeze exception needed. PR limit: <= 400 net added `src/` lines (tests do not count).
- IDs: work packages `WP-OI-CQ-<n>`, owner questions `Q-CQ-<n>`.

| Issue | Verdict | WP / spec | Open on main | Size | Phase 3 |
|---|---|---|---|:---:|:---:|
| #504 | PARTLY COVERED | [WP-SIMPL-36](simplification/WP-SIMPL-36-security-lane.md) sub-package 8 | Not started: no `SecurityTraitCoverageTests`; 4 of the 7 named classes untagged; 156 security test files untagged; several missing regression tests | M | yes (test inventory only) |
| #613 | NEW PLAN | WP-SIMPL-50 open question 2 follow-up | `CA1307`/`CA1310` suppressed in `Directory.Build.props` `NoWarn`; ~230 call sites | M | yes (comparison semantics of identifiers) |

## #504 Tag security regression tests from #495-#497 and add missing ones

- **Verdict:** PARTLY COVERED by WP-SIMPL-36 sub-package 8 (trait sweep, `SecurityTraitCoverageTests.Listed_regression_tests_carry_security_trait`). Gap: the spec covers only the listed classes; the issue's general ratchet and the per-item missing tests are not specified.
- **Verified state on main:** untagged: `tests/Autheris.Tests.Unit/Security/AccessPlanningTenantBindingTests.cs`, `.../Security/ApproveConsentRequestFourEyesMutationTests.cs`, `.../Identity/IdentityDeprovisioningServiceTests.cs`, `.../Jobs/AsyncJobWorkerConcurrencyTests.cs`; tagged: `CdcStreamAdminEndpointsTests` (1), `DbtCatalogMinGroupSizeIngestionTests` (1), `CdcSubscriptionPrincipalTests` (2). Across `tests/**/Security/*Tests.cs` 113 of 212 files carry no `[Trait("Category","Security")]`; 43 of 83 `*Security*Tests.cs` files elsewhere carry none. No architecture ratchet exists. Rule 4 of the assessment (#504 before the first deletion PR) was not met: deletion WPs already merged, so the inventory baseline now protects the remaining deletions (WP-SIMPL-18, 31) and later refactors.
- **Missing tests, code still present:** GraphQL batching `None` (`GatewayServiceCollectionExtensions.GraphQL.cs:238`); per-tenant scratch quota (`AsyncQueryJobManager.cs` ~315-351, `MultiTenancyOptions.MaxScratchBytes`); analytics 403 vs 404 (`AnalyticsEndpoints.cs`); webhook drops for locked/inactive/foreign-realm subjects (`CdcWebhookDeliveryService.cs`); Entra empty allowlist, `appid`-only token, WebSocket path and the Kerberos branch (`GatewayStartupValidator.cs`); dbt standing requires table ownership (`DbtSemanticManifestIngestionService.cs`); HitL enrolled secret without code denied when `RequireTotp2Fa=false` (`HitLStepUpApprovalService.cs`); CSV header escaping (`AsyncQueryJobBackgroundWorker.cs`). Developer verifies each item is still untested before adding it (several may have gained coverage in WP-36 sub-packages 2-3).
- **Design:** one tests-only PR. (1) Tag the listed classes at class level. (2) Add the missing regression tests next to their area's existing class. (3) Architecture ratchet `SecurityTraitCoverageTests.Security_folder_and_named_classes_carry_security_trait` (reflection over test assemblies: every class under a `Security` namespace segment or named `*Security*Tests` carries the trait at class or method level) with a committed baseline `tests/Autheris.Tests.Architecture/Baselines/security-trait-exemptions.txt` that may only shrink (Q-CQ-1). (4) Record the security-test inventory (`--list-tests --filter Category=Security`) as `docs/plans/simplification/security-test-inventory.txt` for later deletion WPs.
- **Work packages:** `WP-OI-CQ-1` (tests only, 0 src lines). Merge with WP-SIMPL-36 sub-package 8 (same PR; this section is its addendum).
- **Tests (TDD):** `SecurityTraitCoverageTests.Listed_regression_tests_carry_security_trait`; `..._Security_folder_and_named_classes_carry_security_trait`; `GraphQlBatchingSecurityTests.Batched_request_is_rejected`; `AsyncQueryJobScratchQuotaTests.Quota_is_counted_per_tenant_not_globally`; `AnalyticsEndpointsSecurityTests.Missing_table_returns_403_not_404`; `CdcWebhookDeliveryServiceTests.Drops_events_for_locked_inactive_and_foreign_realm_subjects`; `GatewayStartupValidatorTests.Rejects_empty_entra_client_allowlist`, `..._Rejects_appid_only_v1_token`, `..._Kerberos_branch_fails_without_keytab`; `DbtSemanticManifestIngestionServiceTests.Standing_requires_table_ownership_not_any_data_owner_role`; `HitLStepUpApprovalServiceTests.Enrolled_secret_without_code_is_denied_when_totp_not_required`; `CsvExportTests.Header_cells_are_escaped`. All tagged Security. Acceptance: `dotnet test --filter Category=Security` lists every new test; ratchet green.
- **Dependencies:** none blocking; before WP-SIMPL-18 and WP-SIMPL-31 deletion PRs.
- **Risks:** tagging changes the security-filtered CI subset (#522 smoke runs) and its duration; measure once after merge.
- **Size:** M (0 src; ~600 test lines; 1 PR).

## #613 Code quality: enable culture analyzers CA1307, CA1309 and CA1310 as errors in src

- **Verdict:** NEW PLAN (WP-SIMPL-50 open question 2 follow-up; no spec covers it).
- **Problem:** culture-sensitive string comparisons of identifiers, roles, SIDs, headers or SQL keywords are a bypass class (Turkish-I casing); the analyzers that find them are suppressed.
- **Verified state on main:** `Directory.Build.props:17` lists `CA1307`, `CA1310` (and `CA1304`, `CA1305`, `CA1311`) in `NoWarn`; `CA1309` is not enabled by `AnalysisLevel=latest-recommended`; `TreatWarningsAsErrors=true` already applies. Grep estimate of candidate sites in `src/` without a `StringComparison`/`StringComparer`: 28 `StartsWith/EndsWith("...")` + 6 with variables (culture-sensitive today: real CA1310 findings), 34 `CompareTo`, 1 `string.Compare`, 98 `Contains("...")` and 61 `Replace("...")` (ordinal by default; CA1307 flags them for explicitness). No `InvariantCulture`/`CurrentCulture` comparisons exist (CA1309 is a guard against regressions).
- **Root cause:** analyzers suppressed globally when `latest-recommended` was enabled; never revisited.
- **Design:** remove `CA1307`, `CA1310` from `NoWarn` and add `dotnet_diagnostic.CA1307/CA1309/CA1310.severity = error` scoped to `src/**` in `.editorconfig` (tests stay at the default, Q-CQ-2). Fix sites mechanically: `StringComparison.Ordinal` for identifiers, SQL text, paths, headers, JSON keys; `OrdinalIgnoreCase` only where the current code already compares case-insensitively (roles, claim types, header names, SQL keywords); `CompareTo` on strings becomes `string.CompareOrdinal`. Any site where `OrdinalIgnoreCase` changes accepted input (former culture-sensitive `StartsWith` on user-supplied text) gets a test. `CA1304/CA1305/CA1311` stay suppressed (formatting only; Q-CQ-3).
- **Work packages:** `WP-OI-CQ-2a` enable `CA1310` + fix the culture-sensitive `StartsWith/EndsWith/CompareTo` sites (security-relevant behaviour, ~70 sites, net src ~0). `WP-OI-CQ-2b` enable `CA1307` + `CA1309`, explicit comparison on `Contains/Replace/IndexOf` (~160 sites, behaviour-neutral). Each PR per project if the diff exceeds review size; net src lines ~0 (replacements), no size exception.
- **Tests (TDD):** `CultureInvariantComparisonSecurityTests` (Unit, `[Trait("Category","Security")]`, runs under `CultureInfo.CurrentCulture = tr-TR`): `Role_check_is_not_affected_by_turkish_i`, `Sid_prefix_check_is_ordinal`, `Header_name_match_is_culture_invariant`, `Sql_keyword_guard_is_culture_invariant` (e.g. `"INSERT"` vs `"ınsert"` must not slip through). Architecture: `AnalyzerConfigurationTests.Culture_analyzers_are_errors_in_src` (parses `Directory.Build.props`/`.editorconfig`; fails if CA1307/CA1309/CA1310 are suppressed for `src`). Acceptance: `dotnet build Autheris.sln` with 0 warnings.
- **Dependencies:** none; rebase-heavy (touches many files): schedule between lanes, not in parallel with WP-SIMPL-46 PR 3-5 or WP-SIMPL-45 PR 4.
- **Risks:** merge conflicts across lanes (mitigation: one PR per project, merged quickly); `OrdinalIgnoreCase` vs culture semantics may change matching of non-ASCII names (catalog search uses its own normalizer; leave search ranking code at its current comparer and document the exemption with a `#pragma` and reason).
- **Size:** M (net src ~0, ~230 sites, ~150 test lines, 2-4 PRs).

## Open owner questions

| ID | Question | Recommended default |
|---|---|---|
| Q-CQ-1 | Scope of the security-trait ratchet (#504) | All classes under a `Security` namespace segment or named `*Security*Tests`; tag all 156 now; exemption baseline only shrinks |
| Q-CQ-2 | Apply CA1307/CA1309/CA1310 to tests as well (#613) | No: `src` only (issue scope); tests keep the default |
| Q-CQ-3 | Also enable CA1304/CA1305/CA1311 (#613) | No: formatting only; separate low-priority issue if wanted |

## Phase 3 review scope

- `WP-OI-CQ-1`: confirm the regression-test list and the ratchet rule (no security test may be lost or untagged).
- `WP-OI-CQ-2a`: review every site where the comparison semantics change (role, SID, tenant, header, SQL keyword checks) and the `tr-TR` test set.

## Owner decisions (2026-11-13)

The owner confirmed the recommended default of every open question (`Q-*`) in this file. The defaults in the question tables are binding for the implementation; a later owner decision wins. Phase 3 (security review) still applies before implementation starts.
