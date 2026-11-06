# WP-SIMPL-48 Row policies, dynamic object coverage and per-cell column state
Status: Ready for implementation
Parent: [ADR-020](../../adr/ADR-020-unified-authorization-model.md) (amendment 2026-11-06) and [PRD](../2026-11-05-req-project-simplification.md) section 2.0 | Issues: none (owner decision) | Owner decisions: OD-38, OD-39, OD-40, OD-41, OD-42, OD-43 (binding), OD-22, OD-30, OD-32 (coverage revised by OD-40), OD-33, OD-34, OD-19 | Security conditions: SEC-AZ-15..19 (sec plan section 11.1), SEC-AZ-02, 04, 06, 07, 09

## 1. Goal and outcome
Row restrictions become one model: named, approved row policies (the existing virtual-filter definitions) with typed parameters, used at two levels. Grant records reference policies with parameter values (ORed across the subject's grants); hard row policies are assigned to users or AD groups (ANDed after the grant union, not bypassable). Grant records may name an object pattern that is evaluated at decision time, so new objects are covered; PII columns not explicitly approved as Clear stay Masked. When overlapping grants differ in columns or masking, the column state is decided per cell. Admins, owners and approvers see the effective access with its provenance.

Measurable: (a) `git grep -n -E "RowFilterPredicate|SubqueryJoinHop|AdditionalHops|SubqueryCorrelated|RowFilterSubqueryStrategy" -- src` returns only the migration step reader; (b) parity scenarios S20..S27 (section 6.3) pass on every transport and source; (c) the randomized per-cell test finds no Clear value outside the scope of a grant with Clear for that column; (d) the effective-access report and the approval preview return the row-restriction composition with provenance and no foreign predicate or parameter values.

## 2. Preconditions and dependencies
- Must be merged first: WP-SIMPL-46 PR 1 (decision point, grant step, decision record with `Grants`) for PR 1, 2 and 5 here; WP-SIMPL-46 PR 2 (all call sites re-routed through the decision point) for PR 3 and 4 here; WP-SIMPL-43 PR 3b (canonical effective groups).
- Governance SQL location: if WP-SIMPL-20 is merged, SQL goes into the unified repository; otherwise into the PostgreSQL and SQL Server partials.
- WP-SIMPL-21: the row-scope forms of step 3 and the per-cell projection of step 4 are added as cells of the dialect parity harness (SqlServer, PostgreSql, Oracle, Databricks, DuckDb, Ansi).
- Feeds: WP-SIMPL-47 step 9a (pattern items, internal-mode ownership rule, approval-time hint) needs PR 5 and PR 6 here; WP-SIMPL-45 `OwnRows` uses the row-scope compiler of PR 3; WP-SIMPL-41 maps the per-cell state of PR 4; WP-SIMPL-42 counts every contributing grant (OD-42, from the WP-SIMPL-46 decision record).
- Parity matrix: if WP-SIMPL-46 PR 6 merges after PR 4 here it includes S20..S27; otherwise PR 7 here adds them.
- Not parallel-safe: WP-SIMPL-46 PR 3-5 (`TableAccessPolicy`, decision cache), WP-SIMPL-45 PR 4 (`GovernedSqlRewriter`). Whichever merges second rebases.

## 3. Scope
### In scope
- Row-policy model: typed named parameters, evaluation mode `Live|KeySet` per policy, cross-source rule, SQL approval by the data owners of every table read (OD-38).
- Grant row scope: `GrantRowScopeEntry` on grant records; simple column filters kept; free SQL and correlated-subquery consent filters removed.
- Hard row policies: the existing `VirtualFilterAccessProfile` bindings, restricted to user and AD-group grantees, subject-bound (OD-39).
- Row-scope compiler: OR across grants, AND within a grant, parameter merge, hard policies, uncovered rule, composite keys pair-wise, `NULL` never matches.
- Per-cell column state for differing column signatures (OD-41).
- Object patterns on grant records, catalog epoch in the decision cache key, new-PII masking, internal-mode ownership rule (OD-40).
- Effective-access report and approval preview service (OD-43).
- Governance migration and conversion of existing filters.
### Out of scope
- Approval routes, UI and request payloads (WP-SIMPL-47 consumes the preview service), usage facts (WP-SIMPL-42), write-rule semantics (WP-SIMPL-45).
- Renaming the `VirtualFilter*` types or the `/api/v1/governance/virtual-filters` routes (user-facing term "row policy"; D-48a).
- A `UNION ALL` rewrite (OD-41) and semantic SQL containment checks (D-48b).

## 4. Current state (verified on `11985fd8`; re-verify line numbers before editing)
- Definitions: `VirtualFilter` in `src/Autheris.Domain/Model/VirtualFilterModels.cs` (structured `From`/`Joins`/`Where` or Trino SQL up to `MaxSqlLength` 4,000; `KeyColumns` may be composite; `ValidFromColumn`/`ValidToColumn`; approval status Active/PendingApproval/Rejected with `CreatedBy`/`ApprovedBy`; `ManagedBy` file sync; definition hash). No typed parameters: only caller placeholders (`src/Autheris.Application/VirtualFilters/CallerPlaceholders.cs`). `FilterBinding` (glob or regex target pattern, object kinds, key-to-column mapping, strategy `Adaptive|ShortCircuitOnly|DuckDbHashJoin|PushdownOnly`, `MaxPushdownKeys`) and `VirtualFilterAccessProfile` (grantee, scope, bindings, `Uncovered` Deny|Skip).
- Resolution: `MandatoryRowFilterResolver` (`src/Autheris.Application/VirtualFilters/MandatoryRowFilterResolver.cs`) is called at `src/Autheris.Application/Policy/TableAccessPolicy.cs:340` and ANDed after the consent decision. Composite keys compile to `EXISTS`, a single key to `IN (SELECT ...)` (`StructuredFilterSqlBuilder.cs:22,116-123`). Federation and key sets: `Application/VirtualFilters/Federation/` (planner, key-set cache and provider, pushdown selector). Admin API `/api/v1/governance/virtual-filters`, `/access-profiles`, `/effective-filters` (`src/Autheris.Api/Endpoints/VirtualFilterEndpoints.cs:42-62`), inspector `EffectiveFilterInspector.cs` (151 lines). About 8,300 src lines in virtual-filter files; 130 test files named after virtual filters, row filters or key sets.
- Consent row filters (`src/Autheris.Domain/Model/ConsentModels.cs`): `ConsentRowFilter` simple (column, operator, literal, `IN`, caller attribute) or `SubqueryCorrelated` with `SubqueryJoinHop` (one column pair per hop, so no composite keys) and `RowFilterSubqueryStrategy` Exists/InCorrelated/In; key-set cache in `src/Autheris.Application/Sql/Policy/RowFilterKeySets/` (17 files, `RowFilterEvaluationStrategy` Exists|KeySetCache, absolute expiry, failure or timeout deny, overflow sentinel). Builders: `AdvancedRlsPredicateBuilder`, `TypedRowFilterBuilder`, `RowFilterSqlBuilder`, `AdvancedRlsFilterGenerator`, `RowFilterEvaluationResolver`; persistence `ConsentSqlCore`. `RowFilterAstValidator` rejects free SQL in consent filters. Row filters are ORed across consents (`ConsentResolutionService`).
- Profile predicate: `AccessProfile.RowFilterPredicate` (free SQL) parsed by `RowPolicyFactory.FromAccessProfile` and ANDed in `TableAccessPolicy.Consent.cs:210` (`ApplyAccessProfiles`; the SR-M-07 modifier block is removed by WP-SIMPL-46 step 3). `PolicyPredicateLinter` allows caller placeholders only in virtual filters.
- Grants are per table (`Consent.TableId`); no pattern. Column level is per table and column (`TableAccessDecision`, `DecisionColumnMaskProvider`), so overlapping grants with different masking cannot be expressed per row. The decision cache key has no catalog epoch.

## 5. Implementation steps (TDD order; write the section-6 tests of each step first)
1. **Row-policy parameters, evaluation mode and approval (PR 1)** in `VirtualFilterModels.cs` and `VirtualFilterAdministrationService`:
   ```csharp
   public enum RowPolicyParameterType { String, Int32, Int64, Decimal, Date, Timestamp, Guid, Boolean }
   public sealed record RowPolicyParameter(string Name, RowPolicyParameterType Type, bool IsList, int MaxValues = 1_000);
   public enum RowPolicyEvaluationMode { Live, KeySet }
   // VirtualFilter gains: IReadOnlyList<RowPolicyParameter> Parameters, RowPolicyEvaluationMode EvaluationMode = Live, TimeSpan? KeySetMaxAge
   ```
   - Parameters are referenced as `:name` in structured `Where` values and in SQL definitions; values are always bound as typed parameters, never inlined. Unknown, missing, mistyped or over-long parameter lists are rejected at grant creation and deny at decision time (`ROW_POLICY_PARAMETER_INVALID`).
   - `KeySet` reuses the `RowFilterKeySets` cache (failure, timeout and overflow deny, nothing cached on failure); `KeySetMaxAge` is capped by the existing global key-set maximum. A policy whose `Source` differs from the target object's data source must use `KeySet` (validated on save and on binding; at decision time otherwise deny `ROW_POLICY_CROSS_SOURCE_REQUIRES_KEYSET`).
   - Approval: a structured policy is approved by a GovernanceAdmin of the tenant or a data owner/delegate of the tables it reads; a SQL policy may be authored only by a GovernanceAdmin and becomes `Active` only when a data owner/delegate of every table the SQL reads (extracted with the existing AST parser) has approved; the author never approves (four-eyes). Any definition change (hash) returns the policy to `PendingApproval`; the previous active version keeps applying until the new one is approved. File sync (`ManagedBy`) stays optional and follows the same approval.
2. **Grant row-scope entries (PR 2)**:
   ```csharp
   public sealed record GrantRowScopeEntry(string PolicyName, IReadOnlyDictionary<string, JsonElement> Parameters,
       IReadOnlyDictionary<string, string>? ColumnMap, UncoveredPolicy Uncovered = UncoveredPolicy.Deny);
   // Consent gains: IReadOnlyList<GrantRowScopeEntry> RowScope
   ```
   - Persisted in `CONSENT_ROW_SCOPES` (next free forward-only migration step at merge time, per the frozen v1.5.0 base policy), signed into the WP-SIMPL-47 grant record payload.
   - Simple `ConsentRowFilter` entries stay as the built-in parameterized form. Delete `RowFilterType.SubqueryCorrelated`, `SubqueryJoinHop`, `AdditionalHops`, `RowFilterSubqueryStrategy`, the consent-level key-set fields and their builder branches (`AdvancedRlsPredicateBuilder`, `AdvancedRlsFilterGenerator`, `TypedRowFilterBuilder`, `RowFilterSqlBuilder`, `RowFilterEvaluationResolver`), and `AccessProfile.RowFilterPredicate` with `RowPolicyFactory.FromAccessProfile` and its linter branch. Access-profile items carry `GrantRowScopeEntry` lists instead.
   - Validation on grant write: the policy exists and is `Active`, parameters match, every `ColumnMap` target exists on a single-object grant; for patterns the mapping is checked per object at decision time.
3. **Row-scope compiler (PR 3)** (`src/Autheris.Application/Policy/RowScopes/RowScopeCompiler.cs`, new; called only by the decision point; produces an AST, never SQL text):
   - Per grant: entries are ANDed. An entry whose key columns (after `ColumnMap`) are missing on the object: `Deny` -> the grant does not contribute to this object; `Skip` -> the entry is dropped.
   - Across the contributing grants: ORed; a grant without entries contributes TRUE. Entries of different grants that reference the same policy with the same column map and are each the only entry of their grant are merged by uniting their parameter values; if the merged list exceeds `MaxValues`, the compiler keeps separate ORed predicates.
   - Hard row policies: every `VirtualFilterAccessProfile` binding whose grantee is the user or one of the effective groups and whose target pattern matches the object is ANDed after the union; an uncovered object with `Deny` is an explicit deny (`HARD_ROW_POLICY_UNCOVERED`, `ExplicitDeny` step). Role grantees are rejected (OD-30). Hard policies also apply to owner grants (OD-34) and to the `OwnRows` predicate of writes (WP-SIMPL-45).
   - Narrowing inputs (WP-SIMPL-46 step 2) are ANDed last.
   - Composite keys: `Live` -> `EXISTS (... WHERE s.k1 = t.c1 AND s.k2 = t.c2)`; `KeySet` -> row-value `IN` where the dialect supports it (PostgreSql, Oracle, Databricks, DuckDb), otherwise `EXISTS (SELECT 1 FROM (VALUES (@a1,@b1), ...) v(k1,k2) WHERE v.k1 = t.c1 AND v.k2 = t.c2)` (SqlServer, Ansi). Never independent per-column lists. A `NULL` key column never matches.
4. **Per-cell column state (PR 4)** in the shared governed query builder (the single SQL composition path behind every SQL transport after WP-SIMPL-46 PR 2) and in `StreamingRowFilterAstEvaluator` for streaming:
   - Group the contributing grants of the object by column signature (column set and level per column). One signature: plain `WHERE <union>`, no `CASE`.
   - Several signatures: compute each signature's scope once as a flag. For `Live` scopes, join a derived key set `LEFT JOIN (SELECT DISTINCT <keys> FROM ...) f_i ON <pair-wise keys>` with flag `f_i.k1 IS NOT NULL`; for `KeySet` and simple scopes, the flag is the predicate on bound parameters. Projection per column: `CASE WHEN <flags of signatures granting Clear> THEN col WHEN <flags of signatures granting Masked> THEN mask(col) ELSE NULL END`; `WHERE` = OR of all flags (omitted when one signature is unfiltered).
   - Safety rules: the derived key set is `DISTINCT` so the join never duplicates rows; a column that is Masked or absent in any signature counts as Masked for predicate, join, `GROUP BY`, `ORDER BY` and aggregate checks (`RejectMaskedReads`, aggregation privacy); `NULL` keys never set a flag.
   - The decision exposes `ColumnAccessLevel.RowDependent` with the per-signature detail for consumers; discovery (WP-SIMPL-41) shows such a column as `Masked` with `clearInSomeRows: true`; Envoy treats any row restriction as restricted (unchanged).
5. **Dynamic object coverage (PR 5)**:
   - `Consent.ObjectPattern` (`source.schema.object`, glob `*` only in the schema and object segments; the data-source segment is literal; no regex), `TableId` null for pattern grants; `ColumnSetMode { AllNonHidden, Explicit }`. Clear only for explicitly named columns. A PII-classified column without an explicit Clear rule is Masked, including columns added or classified after the approval. Hidden objects and columns are never covered; explicit deny wins.
   - Lookup: grants by table id plus pattern grants of the tenant and data source, matched at decision time through a per-epoch compiled matcher.
   - Catalog epoch: incremented on object or column create, delete or rename, PII classification change, hidden-flag change and owner change; part of the decision cache key next to the policy and attribute epochs (SEC-AZ-07).
   - Internal-mode ownership rule (OD-40 default): a pattern grant item records the approving owner SIDs and the workflow mode; in `Internal` mode it covers an object only while the object's current owner or delegate is among them. A matching object of another owner is not covered; `PatternCoverageMonitor` (hosted service, on catalog epoch change, lease-guarded) submits a pending item for that owner through the WP-SIMPL-47 decision pipeline. In `External` mode the decision covers the whole pattern.
6. **Effective-access report and approval preview (PR 6)** (`src/Autheris.Application/Policy/Pdp/EffectiveAccessService.cs`, new; built on `IAccessDecisionPoint.ExplainAsync`):
   - `GET /api/v1/authz/subjects/{subject}/effective-access?object=<pattern>&cursor=&pageSize=` (default 50, max 200) -> per object: allowed, column states (Clear, Masked, RowDependent, NoAccess), contributing grants `{ grantId, via: user|groupSid, rowScope: [{ policy, parameterCount, uncovered }] }`, hard policies `{ policy, via }`, flags `REDUNDANT_GRANT { grantId, coveredBy }` and `EMPTY_HARD_INTERSECTION`.
   - Redundancy and empty intersections are detected structurally (D-48b): a grant is redundant when another contributing grant has no row scope (or the same policy, column map and a superset of parameter values) and the same or broader columns and levels; two hard policies on the same key columns with disjoint literal or parameter sets give an empty intersection.
   - `IGrantEffectPreview.PreviewAsync(subject, requestedItems, ct)` returns the same flags for a pending request; WP-SIMPL-47 shows it to internal approvers and exposes it as `effectPreview` on the request resource. Non-blocking; every preview call is audited (`AUTHZ_EFFECT_PREVIEW`).
   - Authorization and redaction as the WP-SIMPL-46 explain API (SEC-AZ-06): GovernanceAdmin or SecurityAuditor of the tenant, the data owner/delegate for own objects, any user for self; policy names and parameter counts only, never predicate or parameter values of other subjects; uniform 404 cross-tenant. `GET /api/v1/governance/effective-filters` stays as the hard-policy view.
7. **Migration (PR 2 and PR 5)** in the next free forward-only governance migration step: `CONSENT_ROW_SCOPES`; `consents.object_pattern`, `column_set_mode`, approving owner SIDs and workflow mode per item; policy parameters and evaluation mode on virtual filters. Existing `SubqueryCorrelated` consent filters are converted into generated policies (`migrated-<consentId>-<n>`, structured joins, status `Active`, `ManagedBy = Migration`) when every hop is expressible; otherwise the consent is revoked with reason `OD38_ROW_FILTER_NOT_CONVERTIBLE`. Non-empty `AccessProfile.RowFilterPredicate` values are dropped. Both lists go into the WP-SIMPL-46 migration report (no production data; not deployed).

## 6. Tests
### 6.1 Unit (`tests/Autheris.Tests.Unit/Policy/RowScopes/`), all `[Trait("Category","Security")]`
- `RowPolicyParameterTests`: `Parameter_values_always_bound_never_inlined`, `Parameter_type_and_count_validated`, `Unknown_parameter_rejected`, `Invalid_parameter_at_decision_denies`.
- `RowPolicyApprovalTests` (SEC-AZ-15): `Sql_policy_inactive_until_owner_of_every_read_table_approves`, `Author_cannot_approve_own_sql_policy`, `Only_governance_admin_authors_sql_policy`, `Definition_change_requires_reapproval_and_old_version_keeps_applying`.
- `RowPolicyEvaluationTests` (SEC-AZ-15): `Composite_key_compared_pairwise_not_per_column`, `Null_key_never_matches`, `Cross_source_policy_requires_keyset`, `Keyset_failure_timeout_overflow_deny`, `Keyset_max_age_capped_by_global_maximum`.
- `RowScopeCompilerTests` (SEC-AZ-16): `Scopes_ored_across_grants_and_anded_within_grant`, `Unfiltered_grant_yields_all_rows`, `Same_policy_parameters_merged_into_one_predicate`, `Merged_values_over_cap_fall_back_to_or`, `Hard_policy_anded_after_union_and_not_bypassed_by_other_group_grant`, `Hard_policy_applies_to_owner_grant_and_own_rows_writes`, `Uncovered_deny_grant_does_not_apply`, `Uncovered_skip_grant_applies_unfiltered`, `Hard_policy_uncovered_deny_is_explicit_deny`, `Role_grantee_hard_policy_rejected`.
- `PerCellColumnStateTests` (SEC-AZ-18): `Single_signature_compiles_without_case`, `Clear_only_in_rows_of_clear_grant`, `Column_outside_covering_grants_is_null`, `Flag_join_never_duplicates_rows`, `Masked_in_any_signature_counts_as_masked_for_predicates_and_aggregates`, `Randomized_no_clear_value_outside_clear_scope` (seeded, 1,000 cases: random grants, scopes, masking and rows; the oracle evaluates each cell independently).
- `DynamicCoverageTests` (SEC-AZ-17): `Pattern_grant_covers_object_created_after_approval`, `Pattern_must_fix_data_source`, `New_pii_column_masked_without_explicit_clear`, `Column_classified_pii_later_is_masked`, `Hidden_object_never_covered_by_pattern`, `Internal_mode_pattern_does_not_cover_object_of_non_approving_owner`, `External_mode_pattern_covers_all_matching_objects`, `Catalog_epoch_in_decision_cache_key`, `Explicit_deny_beats_pattern_grant`.
- `EffectiveAccessReportTests` (SEC-AZ-19): `Report_lists_provenance_group_grant_policy`, `Redundant_grant_flagged`, `Empty_hard_intersection_flagged`, `Report_never_returns_predicate_or_parameter_values_of_other_subjects`, `Report_authorization_matches_explain`, `Preview_is_non_blocking_and_audited`.
- `RowPolicyMigrationTests`: `Subquery_consent_filter_converted_to_generated_policy`, `Unconvertible_filter_revokes_consent_with_reason`, `Profile_predicate_dropped_and_reported`.
### 6.2 Architecture and dialects
- `AuthorizationArchitectureTests` (extend): `Row_scopes_built_only_by_RowScopeCompiler` (no row-predicate string building outside `Policy/RowScopes` and the dialect renderers), `No_free_sql_predicate_on_grant_models` (no `string` predicate property on `Consent`, `AccessProfile` or profile items).
- WP-SIMPL-21 dialect parity cells `RowScopeDialectTests`: composite `KeySet` (row-value `IN` vs `VALUES` join), `Live` `EXISTS`, per-cell `CASE` with flag join, on SqlServer, PostgreSql, Oracle, Databricks, DuckDb and Ansi.
### 6.3 Parity matrix rows (`CrossTransportAuthorizationParityTests`, WP-SIMPL-46 section 6.3)
S20 filtered grant via group A plus unfiltered grant via group B -> all rows, both grant ids in `Grants`; S21 overlapping grants with different masking -> per-cell state; S22 hard policy via group A plus unfiltered grant via group B -> filtered; S23 pattern grant and an object created after approval; S24 uncovered `Deny` vs `Skip`; S25 composite-key policy; S26 cross-source `KeySet` policy; S27 same policy with parameters from two grants merged. The matrix becomes 27 x 15; each cell asserts allowed, per-column (or per-cell) state, row-set fingerprint and the set of contributing grant ids.

## 7. Verification commands (one build at a time)
```
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "Category=Security&FullyQualifiedName~Policy.RowScopes"
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build --filter "FullyQualifiedName~AuthorizationArchitectureTests"
dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --no-build --filter "FullyQualifiedName~CrossTransportAuthorizationParityTests|FullyQualifiedName~RowScopeDialectTests"
git grep -n -E "RowFilterPredicate|SubqueryJoinHop|AdditionalHops|SubqueryCorrelated|RowFilterSubqueryStrategy" -- src   # only the migration step reader
```

## 8. Acceptance criteria
- [ ] Row policies have typed parameters bound as parameters, `Live|KeySet` per policy, the cross-source `KeySet` rule and the SQL four-eyes owner approval.
- [ ] Grant records carry only row-scope entries and simple column filters; no free SQL in any grant or profile model.
- [ ] Scopes are ORed across grants and ANDed within a grant; same-policy parameters merge; hard policies are ANDed after the union, subject-bound and not bypassable (also for owner grants and `OwnRows` writes); uncovered `Deny` by default.
- [ ] Composite keys are compared pair-wise on every dialect; `NULL` keys never match.
- [ ] Per-cell column state: no `CASE` for one signature; the randomized test is green; masked-in-any-signature columns are rejected in predicates and aggregates.
- [ ] Pattern grants cover new objects; the data source is fixed; new PII is Masked without explicit Clear; catalog epoch in the cache key; internal-mode ownership rule enforced and pending items created.
- [ ] Effective-access report and preview with provenance and flags, authorized and redacted like explain, preview audited and non-blocking.
- [ ] Migration converts or revokes subquery consent filters and drops profile predicates, both reported.
- [ ] Parity rows S20..S27 green on all transports and sources.

## 9. PR slicing (<= 400 net added src lines each)
1. PR 1 "Row-policy parameters, evaluation mode, approval": step 1 (~350 src).
2. PR 2 "Grant row-scope entries and removal of free and subquery filters": step 2, migration part of step 7 (+380 / about -1,300 src).
3. PR 3 "Row-scope compiler": step 3 (~400 src).
4. PR 4 "Per-cell column state": step 4 (~380 src).
5. PR 5 "Dynamic object coverage": step 5, migration part of step 7 (~380 src).
6. PR 6 "Effective-access report and approval preview": step 6 (~350 src).
7. PR 7 "Parity rows and dialect cells": sections 6.2 and 6.3 (tests only; skipped if WP-SIMPL-46 PR 6 already contains them).

## 10. Risks, rollback, and things the developer must NOT do
- Risk: performance of `Live` policies with several signatures. Mitigation: one flag per signature, `KeySet` for expensive or cross-source policies, benchmark rows in `AccessDecisionPointBenchmark` (not a gate).
- Risk: dynamic coverage exposes new objects without an approver noticing. Mitigation: data source fixed in the pattern, new PII Masked, internal-mode ownership rule, approval-time hint (WP-SIMPL-47) states that future objects are included, effective-access report.
- Risk: masking drift while moving filter logic. Mitigation: existing virtual-filter and consent filter tests stay green except the deleted subquery-filter and profile-predicate tests; parity rows S20..S27.
- Rollback: PR 1, 3, 4 and 6 revert cleanly; PR 2 and PR 5 revert together with their migration step (no production data; not deployed).
- Must NOT: inline parameter values; compare composite keys per column; let a hard policy be bypassed by any grant; let a policy, binding or preview grant access; use `UNION ALL` for per-cell state; reveal a Clear value in a row outside a Clear grant's scope; allow a pattern without a literal data source or with regex; cache a decision without the catalog epoch; return predicate or parameter values of other subjects; treat a `NULL` key as a match.
- Binding Phase 3 conditions: SEC-AZ-15..19 (sec plan section 11.1) and SEC-AZ-02, 04, 06, 07, 09.

## 11. Estimate
+2,240 / -1,300 src (step 2 deletions about -1,300); tests +2,400 / -900. 12 person-days (parameters and approval 1.5, row-scope entries and removal 2, compiler 2, per-cell state 2.5, dynamic coverage 2, report and preview 1.5, parity and dialect cells 0.5).

## 12. Required edits to other work packages
- **WP-SIMPL-46:** the decision record lists all contributing grants (`Grants`), no `AuthorizingGrant` (OD-42); the grant step calls `RowScopeCompiler` once PR 3 here is merged; until PR 4 here, overlapping grants whose column signatures differ use the strictest column level (fail-closed interim); the cache key gains the catalog epoch (PR 5 here); D-46b, D-46c and D-46i are amended.
- **WP-SIMPL-47:** step 9a materializes one grant record per profile item (object or pattern, OD-40) with approving owners and mode; the approval-time hint uses `IGrantEffectPreview`; `PatternCoverageMonitor` submits pending items through `IConsentDecisionPipeline`.
- **WP-SIMPL-42:** every contributing grant is counted in full; totals count the access once (OD-42).
- **WP-SIMPL-45:** `OwnRows` uses the row-scope compiler (grant union, then hard policies); write-rule row scopes use `GrantRowScopeEntry`.
- **WP-SIMPL-41:** a `RowDependent` column is shown as `Masked` with `clearInSomeRows: true`; pattern grants make new objects `Granted` without a new request.
- **WP-SIMPL-21:** add the dialect cells of section 6.2.

## Decisions taken in this spec
- D-48a: The code keeps the `VirtualFilter*` type names and routes; "row policy" is the user-facing term. A rename would touch about 8,300 lines without behaviour change.
- D-48b: Redundancy and empty intersections are detected structurally (unfiltered or superset parameters; disjoint literal sets), not by SQL containment, so the report never runs data queries.
- D-48c: When merged parameter values exceed `MaxValues`, the compiler keeps separate ORed predicates instead of truncating.
- D-48d: A column that is Masked or absent in any signature is treated as Masked for predicates, joins, grouping, ordering and aggregates.
- D-48e (OD-40 default): In internal mode, an owner change ends pattern coverage of that object until the new owner approves; in external mode the external decision covers the whole pattern.
- D-48f: `GET /api/v1/governance/effective-filters` stays as the hard-policy view; the new report links to it.
- D-48g: Discovery shows a `RowDependent` column as `Masked` with `clearInSomeRows: true`, so discovery never claims Clear for rows the caller cannot see clear.
