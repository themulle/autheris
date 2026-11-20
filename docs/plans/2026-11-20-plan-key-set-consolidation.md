# Plan: one key-set model (row policies), remove the consent row filter key-set model

Status: Phase 2 plan, owner decision taken 2026-11-20, not implemented. Parents: [WP-SIMPL-48](simplification/WP-SIMPL-48-row-policies-dynamic-coverage.md) (row policies as the single row model), [WP-SIMPL-52](simplification/WP-SIMPL-52-governance-data-model.md) (open follow-up 3: fate of `consent_row_filters.key_set_*`).

## 1. Decision (owner, 2026-11-20)
1. Add the one missing setting to the row policy (virtual filter) model: an optional per-policy maximum number of keys.
2. Then discard the old model and remove it completely: the key-set strategy, duration and maximum on consent row filters, their admin REST API and MCP tool, their options, and the key-set stage, cache and loader for correlated consent filters.

No production deployment exists (owner rule 2026-10-13), so the REST, MCP and configuration changes are breaking and need no compatibility layer.

## 2. Evidence (main at `ef876ad15`, re-verify line numbers before editing)
- Old model: `CONSENT_ROW_FILTERS.key_set_strategy`, `key_set_duration_seconds`, `key_set_max_keys` (both baselines). Settings are read and written by `RowFilterEvaluationStore.cs` (`:183`, `:238`), resolved over three levels Global, Object and Rule (`RowFilterEvaluationSource`, `RowFilterEvaluationResolver`), administered through `GovernanceEndpoints.RowFilterEvaluation.cs`, `CatalogApiEndpoints.cs:216` (object level) and the MCP admin tools (`McpAdminTools`), with `RowFilterEvaluationAdminService`, `IAccessPlanStore` / `AccessPlanningService`, options `Gateway:RowFilters:KeySetCache` (`RlsOptions.KeySetCache`, `RowFilterKeySetCacheOptionsValidator`), audit event types, and the stage `Application/Sql/Policy/RowFilterKeySets/` (16 files: stage, cache, loader, correlated filter site registry, substitution, startup reporter).
- The definition of a correlated consent filter is gone: step `0024_drop_superseded_governance_columns` dropped `dependent_table`, `foreign_key_column`, `subquery_predicate_json`, `additional_hops_json` and the related columns. The key-set settings of a consent row filter therefore have no filter shape left to act on. To be confirmed in WP-KS-0 before anything is deleted.
- New model already has the concept: `VirtualFilter.EvaluationMode` (`Live` or `KeySet`, column `evaluation_mode`), `KeySetMaxAge` (column `key_set_max_age_seconds`), a global cap `Gateway:VirtualFilters:Federation:MaxAuthorizedKeys` (default 50,000), `KeyCacheTtlSeconds` and `KeyQueryTimeoutSeconds`, and its own key-set provider and cache (`Application/VirtualFilters/Federation/SqlVirtualFilterKeySetProvider.cs`, `VirtualFilterKeySetCache.cs`). It does not use the types of the old stage.
- Mapping old to new: strategy `Exists` / `KeySetCache` is `Live` / `KeySet`; duration is `key_set_max_age_seconds`; maximum keys has no per-policy equivalent (only the global cap). The object-level override has no equivalent and is not carried over (see D-KS-1).

## 3. Decisions to record
- **D-KS-1:** No object-level (per table) override in the new model. A policy carries its own mode, age and maximum; the global options are the default and the ceiling. Reason: one place to look, and the object level only existed to configure correlated consent filters.
- **D-KS-2:** The per-policy maximum is optional, must be at least 1, and is clamped to `MaxAuthorizedKeys`. Exceeding the effective maximum at load time keeps today's fail-closed behaviour (`ROW_FILTER_KEY_SET_UNAVAILABLE` denial, never a truncated key set).
- **D-KS-3:** Removed configuration keys fail startup with a removed-key message (the pattern used for `Gateway:CatalogSearch:Enabled`), so a stale `Gateway:RowFilters:KeySetCache:*` is never silently ignored.

## 4. Work packages (order matters)
| Package | Size | Content |
|---|---|---|
| **WP-KS-0 (verify)** | XS | Prove that no consent row filter can still reach the key-set stage: list the callers of `RowFilterKeySetStage` and `CorrelatedFilterSiteRegistry`, run the existing tests that use them, and record the result in this plan. If a live path exists, stop and report to the owner before WP-KS-2. |
| **WP-KS-1 (add)** | S | Add `key_set_max_keys INT NULL` to `virtual_filters` (governance step, see section 5), `VirtualFilter.KeySetMaxKeys` with validation (positive, only meaningful in `KeySet` mode; reject a value above `MaxAuthorizedKeys` at definition time), use it in `SqlVirtualFilterKeySetProvider` (effective maximum = min of policy and global), expose it on the virtual filter REST body and the file sync format, include it in the definition hash, and document it. Tests: validation, clamp, exceeding the limit fails closed with the existing error code, hash changes, REST round trip, both engines. |
| **WP-KS-2 (remove code)** | M | Delete the old model end to end: the stage folder `Application/Sql/Policy/RowFilterKeySets/`, `RowFilterEvaluationStore` key-set parts, `RowFilterEvaluationResolver`, `RowFilterEvaluationAdminService` and its interface, the REST routes (`GovernanceEndpoints.RowFilterEvaluation.cs`, the object-level route in `CatalogApiEndpoints.cs`), the MCP admin tool and its annotations and group entries, `RowFilterKeySetCacheOptions` and its validator plus the configuration schema entries (with removed-key startup failure), the access planning fields, the audit event types and the diagnostics. Update `ErrorSanitizingFilter` and `GatewayDenialStatusMapper` only where they name removed codes (keep `ROW_FILTER_KEY_SET_UNAVAILABLE` if the virtual filter provider still raises it). Update every test and baseline (endpoint count, ingress and read-only route lists, MCP tool list, configuration reference, options ratchet, security inventory). Ratchet: `git grep -n -E "RowFilterKeySet|RowFilterEvaluation|CorrelatedFilterSite" -- src` returns nothing except a migration reader if one is needed. |
| **WP-KS-3 (DDL)** | S | Forward-only governance step dropping `consent_row_filters.key_set_strategy`, `key_set_duration_seconds` and `key_set_max_keys` on both engines (shape deltas, checksum goldens, `StepSqlLint`, step tests). Same caution as step 0023: all nodes must run the build of WP-KS-2 before the step applies, because older code still selects the columns. This step may be combined with the add column step of WP-KS-1 into one step if both PRs merge together; otherwise two steps. |

Estimate (not measured): 3 PRs, about 4 to 6 person-days including review. WP-KS-1 can merge first and alone.

## 5. Governance step numbers
Last step on `main`: `0031_access_profile_items`. Reserved: `0032` (#922, PR #939), `0033` (#916 metric views, PR #920, renumbered from 0032). This plan takes the next free numbers at merge time, expected `0034` (add column) and `0035` (drop columns), or one step `0034` for both. The runner needs a gap-free catalogue: rename the file pair, both checksum goldens, `StepSqlLintTests` and the step tests when the order changes.

## 6. Security and review
- Phase 3 is required (short): the change removes a cache stage in the row-filter enforcement path and adds a limit that must fail closed. Binding conditions to be written by Phase 3; do not invent SEC identifiers here. Mandatory `[Trait("Category", "Security")]` cases at least for: exceeding the per-policy maximum denies (no truncated key set), a removed configuration key fails startup, no route remains that changes row filter evaluation settings, and the existing cross-channel row policy parity tests still pass.
- Integration impact: tests that call the removed REST routes or the MCP admin tool, the configuration reference test, the endpoint count baseline, and the audit coverage tests (`AuditEndpointCoverageTests`, `IngressTransportCoverageTests` lose routes). Compile-check the Integration project locally; run integration and parity on GitHub Actions.

## 7. Owner questions (defaults)
1. No object-level override (D-KS-1): yes.
2. One combined governance step for add and drop, or two: two steps if the PRs merge separately, one if together.
3. Remove the MCP admin tool without a replacement: yes (policy settings are changed through the virtual filter API).

## 8. Changelog
- 2026-11-20: plan created after the owner decision to extend the row policy model and remove the consent row filter key-set model completely.
