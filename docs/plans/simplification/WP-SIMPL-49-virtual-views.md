# WP-SIMPL-49 Virtual views (governed views defined in Autheris)
Status: Ready for implementation (starts after the feature freeze, i.e. after Stage 3 exit; PRD D8)
Parent: [PRD](../2026-11-05-req-project-simplification.md) section 2.0 OD-44 and [ADR-020](../../adr/ADR-020-unified-authorization-model.md) (amendment 2026-11-06, virtual views) | Issues: none (owner decision) | Owner decisions: OD-44 (binding), OD-15 (DuckDB as the place for central views), OD-19, OD-22, OD-25, OD-29, OD-30, OD-38..OD-43 | Security conditions: SEC-VIEW-01..10 (sec plan section 11.7), SEC-AZ-01..19

## 1. Goal and outcome
A data owner or GovernanceAdmin defines a read-only view as SQL in Autheris (for example a report or a data product such as a cube with dimensions and measures) without creating anything in the source database. The view names its own product owners, who may differ from the creator and from the base owners. The owners of every base object approve once, at creation: the exact definition, the designated product owners and the classification of the view's columns. From then on the view is an independent data product: the product owners decide all grants on it through the normal consent path (WP-SIMPL-47), so granting authority is delegated from the base owners to the product owners (OD-44). The view is a catalog object like a table: listed in discovery and readable on every transport (OData, GraphQL, REST, WebSQL, Trino protocol, MCP). A grant on the view is enough; base-table grants and base-table denies of the caller play no role (a user may be denied on a base table and still read the view). Row policies, masking, explicit deny on the view, audit and usage apply to the view as to any object.

Measurable: (a) a view over one source and a view over two sources return identical rows through every transport for the same grant (parity rows V1..V10, section 6.3); (b) no view is readable before every base-table owner has approved its current definition hash; (c) `git grep` finds no DDL issued to a data source by the view code; (d) a PII-derived view column is never Clear unless the base owners classified it as Clear-eligible or non-PII at approval and the caller's view grant has a Clear rule; (e) after activation, grants on the view need only the product owners' approval, and a change of product owners needs the base owners' approval.

## 2. Preconditions and dependencies
- Must be merged first: WP-SIMPL-46 (decision point, all transports re-routed), WP-SIMPL-48 (row policies, per-cell state, pattern grants), WP-SIMPL-47 (decision pipeline; definitions are approved as request kind `VIEW_DEFINITION`), WP-SIMPL-41 (discovery), WP-SIMPL-42 (usage), WP-SIMPL-44 (no source-metadata reads, reserved source names), WP-SIMPL-21 (AST security visitor and dialect parity harness), WP-SIMPL-20 (unified governance repository).
- Starts after the feature freeze (Stage 3 exit, PRD D8); it is not part of the `freeze-exception` list.
- Not parallel-safe with any WP touching `GovernedSqlRewriter`, `FederatedDuckDbExecutionService` or the decision point; none is planned after Stage 3.

## 3. Scope
### In scope
- `VirtualView` definition model, governance store, admin API, approval through the WP-SIMPL-47 pipeline (`VIEW_DEFINITION`), versioning by definition hash.
- Catalog registration as object kind `VirtualView` in the reserved data source `views` (namespace `views.<schema>.<name>`).
- Expansion into the governed query path: single-source views pushed down to the source dialect as a derived table; multi-source views executed through the DuckDB federation (OD-15).
- Definer semantics in the decision point: the view is an independent object; base-table grants, denies and hard row policies of the caller do not propagate (OD-44).
- Product owners: designated at creation, approved by the base owners together with the definition; transfer of product ownership needs the base owners' approval; granting on the view is delegated to the product owners.
- Column metadata from the definition and the catalog (never from the source, OD-25); PII lineage.
- Nesting (views over active views, depth <= 3, no cycles), invalidation and suspension.
- Discovery, usage, audit, explain integration; parity rows.
### Out of scope
- Writes to views (read-only; writes are rejected as for any non-writable object).
- Materialization or caching of view results (a later DuckDB cache needs its own decision).
- View parameters (callers filter with `WHERE`/`$filter` as on tables).
- Invoker-rights views (OD-44: definer only).

## 4. Current state (verified on `e8bd5942`; re-verify before editing)
- No user-defined view object exists. `TableMetadata` has no object kind for views; the catalog knows tables, views of sources and procedure results (OD-22) and the dbt-derived semantic virtual tables (`src/Autheris.Application/Catalog/Semantic/SemanticVirtualTables.cs`, domain `semantic`, metrics only).
- Ad-hoc SQL over one or several sources exists only per request: WebSQL and the Trino protocol (`GovernedSqlRewriter`), cross-source planning (`src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs`, `CrossSourceQueryRouter.cs`) and DuckDB federation (`FederatedDuckDbExecutionService`).
- SQL is parsed and checked by the Trino AST and `src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor*.cs` (DML guards, mask guards incl. aggregates and derived columns).
- PRD OD-15 names DuckDB as the future place for central views and measures.

## 5. Implementation steps (TDD order; write the section-6 tests of each step first)
1. **Model and store (PR 1)** (`src/Autheris.Domain/Model/VirtualViewModels.cs`, new):
   ```csharp
   public enum VirtualViewStatus { Draft, PendingApproval, Active, Invalid, Suspended, Retired }
   public sealed record VirtualViewColumn(string Name, string DataType, bool Pii, bool ClearApprovedByBaseOwners,
       IReadOnlyList<ColumnLineage> Lineage);   // lineage = base object + column(s), from the AST
   public sealed record VirtualView(Guid Id, TenantId Tenant, string Schema, string Name, string Sql, string DefinitionHash,
       IReadOnlyList<TableIdentifier> BaseObjects, IReadOnlyList<VirtualViewColumn> Columns, VirtualViewStatus Status,
       Sid CreatedBy, IReadOnlyList<Sid> ProductOwners, string? Description, int Depth);   // ProductOwners may differ from creator and base owners
   ```
   Governance tables `VIRTUAL_VIEWS`, `VIRTUAL_VIEW_VERSIONS` (definition hash, SQL, approvals per base owner, decided at), next free forward-only migration step at merge time. Admin API `/api/v1/governance/views` (`GET`, `PUT {schema}/{name}`, `POST {schema}/{name}/submit`, `POST {schema}/{name}/transfer`, `POST {schema}/{name}/retire`), authorization: GovernanceAdmin of the tenant, or a data owner/delegate of at least one base object of the definition; `.WithAudit`.
2. **Validation and approval (PR 1)**:
   - The SQL (Trino syntax, maximum 16,000 characters) must parse to one `SELECT` (CTEs allowed); the AST security visitor rejects DML, DDL, system catalogs and functions outside the allow-list; every referenced object must exist in the tenant catalog and be readable by the decision point as an object (not hidden for the author unless the author owns it).
   - Columns: names and types are derived from the AST and the catalog column metadata of the base objects (never from the source, OD-25); PII is propagated by lineage: a view column derived from any PII column (directly or in an expression) is PII; a column whose lineage cannot be resolved is PII.
   - Approval at creation: `submit` creates a `VIEW_DEFINITION` request in the WP-SIMPL-47 decision pipeline (internal or external mode as routed); in internal mode one approval item per distinct owner of the base objects. Each owner sees the SQL, the base objects and columns they own, the designated product owners, and the view columns derived from their PII columns; per such column the owner sets the classification: PII Masked-only, PII Clear-eligible (`ClearApprovedByBaseOwners`) or non-PII (for example an aggregate such as a count or sum that discloses no personal data). The author never approves their own item (four-eyes). The view becomes `Active` only when every item is approved for the current definition hash; the approval also delegates granting authority on the view to the designated product owners.
   - Changes: any SQL change creates a new version that must be approved again by the base owners while the previous active version keeps serving. Changing the product owners (`POST {schema}/{name}/transfer`) creates a `VIEW_OWNERSHIP` request that the owners of every base object must approve; until then the previous product owners stay in charge. Adding a delegate of a product owner (`DataOwnerDelegation`) needs no base-owner approval.
3. **Catalog registration (PR 2)**: an `Active` view is registered as object kind `VirtualView` under the reserved data source `views` (`views.<schema>.<name>`; WP-SIMPL-44 rejects `views` as a configured data-source name). The view's data owners are its `ProductOwners` (plus their delegates). Grants on the view are ordinary grant records (consent, profile items, patterns incl. `views.*`), decided by the product owners only; base owners are not consulted per grant (delegated granting authority, OD-44).
4. **Decision and expansion (PR 2, PR 3)**:
   - The decision point decides the view object with the caller's grants and denies on the view (definer semantics): base-table grants, base-table denies and base-table hard row policies of the caller are neither required nor applied (OD-44: a user may be denied on a base table and granted on the view). Hard row policies apply to the view only through bindings that match the view (target patterns may name views). The base owners see, in the approval preview, which base-table restrictions the view does not carry over, and can build the needed restriction into the definition.
   - Expansion: the rewriter replaces the view reference with the approved SQL as a derived table, then applies the caller's row scope, per-cell column state and masking on the view's columns (WP-SIMPL-48). A view whose base objects all live in one data source is compiled to that source's dialect and pushed down as one statement; otherwise the federation path executes it in DuckDB with per-source sub-queries (OD-15). No DDL is ever sent to a data source.
   - A view column classified PII is Masked unless it is Clear-eligible (`ClearApprovedByBaseOwners`) and the caller's view grant has a Clear rule for it; a column classified non-PII at approval follows the grant's column set like any non-PII column. Masked view columns follow the existing mask guards (no predicates, joins, grouping or aggregates over masked values).
   - Nesting: a view may reference other `Active` views up to depth 3; cycles are rejected at validation; a nested view is expanded with its own approved definition, and the outer view's approval covers the inner view only through the inner view's own approval.
5. **Invalidation and suspension (PR 4)**: on a catalog epoch change that touches a base object, the view is revalidated. A column removed or retyped or an object deleted sets `Invalid`. A base column newly classified as PII masks the derived view columns until the current base owner reclassifies them; the view stays `Active`. A change of the base owner does not require re-approval (the approval given at creation stands and stays audited), but the current owner of any base object can withdraw approval, which sets `Suspended` (default taken). Only `Active` views are readable; others deny with `VIEW_NOT_ACTIVE`. Base owners and product owners are notified (in-app, `view.*` consent events of WP-SIMPL-47).
6. **Discovery, usage, audit, explain (PR 5)**: discovery lists views like tables (kind `VirtualView`, description, owners, columns with PII and state; base objects only by names the caller may see); usage records one fact per view access with the source statistics of each underlying source (WP-SIMPL-42); the data-access audit entry carries view id and definition hash; explain shows the view grant and the propagation rules applied.

## 6. Tests
### 6.1 Unit (`tests/Autheris.Tests.Unit/Views/`), all `[Trait("Category","Security")]`
- `VirtualViewValidationTests` (SEC-VIEW-01, 02): `Only_single_select_accepted`, `Dml_ddl_and_system_catalog_rejected`, `Unknown_or_hidden_base_object_rejected_for_non_owner`, `Columns_derived_from_catalog_never_from_source`, `Pii_propagates_by_lineage_and_unresolved_lineage_is_pii`.
- `VirtualViewApprovalTests` (SEC-VIEW-03): `View_inactive_until_every_base_owner_approves_current_hash`, `Author_cannot_approve_own_item`, `Sql_change_requires_reapproval_previous_version_keeps_serving`, `Only_owner_or_governance_admin_creates_view`, `Product_owner_transfer_requires_base_owner_approval`, `Grants_on_active_view_decided_by_product_owners_only`, `Base_owner_change_keeps_view_active_and_new_owner_can_withdraw`, `Aggregate_column_declassified_by_base_owner_is_non_pii`.
- `VirtualViewDecisionTests` (SEC-VIEW-04, 05, 06): `Grant_on_view_suffices_without_base_grants`, `No_view_grant_is_default_deny`, `Base_object_deny_of_caller_does_not_affect_view`, `Explicit_deny_on_view_wins`, `Hard_policy_bound_to_view_applies`, `Pii_view_column_masked_without_base_owner_clear_approval`, `Pii_view_column_masked_without_clear_rule_on_view_grant`, `Masked_view_column_rejected_in_predicates_and_aggregates`.
- `VirtualViewExpansionTests` (SEC-VIEW-07): `Single_source_view_pushed_down_as_one_statement`, `Multi_source_view_runs_in_duckdb_federation`, `No_ddl_sent_to_source`, `Nesting_depth_over_three_rejected`, `Cycle_rejected`.
- `VirtualViewLifecycleTests` (SEC-VIEW-08): `Base_column_removed_sets_invalid_and_denies`, `New_pii_in_lineage_requires_reapproval`, `Owner_withdrawal_suspends_view`, `Inactive_view_denies_on_every_transport`.
- `VirtualViewObservabilityTests` (SEC-VIEW-09, 10): `Audit_entry_carries_view_id_and_definition_hash`, `Usage_fact_per_view_access_with_source_stats`, `Discovery_never_shows_base_objects_hidden_from_caller`, `Reserved_views_source_name_rejected_for_data_sources`.
### 6.2 Architecture
- `AuthorizationArchitectureTests.Views_decided_only_by_decision_point`; `VirtualViewArchitectureTests.No_ddl_statement_built_in_view_code`.
### 6.3 Parity rows (`CrossTransportAuthorizationParityTests`)
V1 single-source view with view grant, no base grants; V2 multi-source view; V3 no view grant; V4 caller denied on a base object but granted on the view (allowed); V5 PII column Clear-approved and Clear on the grant; V6 PII column without Clear approval; V7 row policy on the view; V8 nested view; V9 `Invalid` view; V10 `Suspended` view. Columns: all data transports and discovery (writes: rejected).

## 7. Verification commands (one build at a time)
```
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "Category=Security&FullyQualifiedName~Views"
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build --filter "FullyQualifiedName~VirtualViewArchitectureTests|FullyQualifiedName~AuthorizationArchitectureTests"
dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --no-build --filter "FullyQualifiedName~CrossTransportAuthorizationParityTests"
```

## 8. Acceptance criteria
- [ ] Data owners and GovernanceAdmins define views as SQL with designated product owners; nothing is created in a source.
- [ ] A view is readable only when every base owner approved its current definition hash and product owners; SQL changes and product-owner transfers require base-owner approval; grants on the view need the product owners only.
- [ ] A grant on the view suffices; no view grant means default deny; base-object grants, denies and hard policies of the caller do not affect the view; a deny on the view wins.
- [ ] PII lineage, masking defaults and mask guards apply to view columns.
- [ ] Single-source views are pushed down; multi-source views run through DuckDB; nesting depth <= 3, no cycles.
- [ ] Invalid, suspended and retired views deny on every transport.
- [ ] Discovery, usage, audit and explain cover views; parity rows V1..V10 green.

## 9. PR slicing (<= 400 net added src lines each)
1. PR 1 "Model, store, API, validation and approval": steps 1-2 (~400 src).
2. PR 2 "Catalog registration and single-source expansion": step 3 and the single-source part of step 4 (~380 src).
3. PR 3 "Federation and nesting": the multi-source and nesting parts of step 4 (~300 src).
4. PR 4 "Lifecycle": step 5 (~300 src).
5. PR 5 "Discovery, usage, audit, explain": step 6 (~300 src).
6. PR 6 "Parity rows and architecture tests": sections 6.2 and 6.3 (tests only).

## 10. Risks, rollback, and things the developer must NOT do
- Risk: a view discloses more than the base owners intended (joins, aggregates). Mitigation: approval per base owner on the exact definition hash, the designated product owners and the classification of every PII-derived column; PII masked by default; the approval preview lists base-table restrictions the view does not carry over; withdrawal by a current base owner suspends the view.
- Risk: expensive multi-source views. Mitigation: per-source timeouts and cost limits of the federation path; usage facts per view.
- Rollback: each PR reverts cleanly; PR 1 with its migration step (no production data at plan time).
- Must NOT: send DDL to a data source; read source metadata to derive view columns; let a view become readable before every base owner approved the current hash; let a view grant reveal a PII column as Clear without base-owner approval; skip the decision point for a view or for a nested view; evaluate the caller's base-table grants, denies or hard policies for a view (definer semantics only); let anyone but the product owners decide grants on an active view; change product owners without base-owner approval; allow writes through a view.

## 11. Estimate
About +1,900 src, +2,200 test lines. 10 person-days (model and approval 2.5, registration and single-source expansion 2, federation and nesting 1.5, lifecycle 1.5, discovery/usage/audit/explain 1.5, parity 1).

## 12. Required edits to other components (made by this WP, after the program)
- Decision point (WP-SIMPL-46 code): object kind `VirtualView`, definer semantics (no propagation of base-table grants, denies or hard policies), `VIEW_NOT_ACTIVE`.
- Consent pipeline (WP-SIMPL-47 code): request kind `VIEW_DEFINITION`, `view.*` events.
- Discovery (WP-SIMPL-41 code): kind `VirtualView`, base-object names filtered by visibility.
- Source admission (WP-SIMPL-44 code): `views` added to the reserved source names.

## Decisions taken in this spec
- D-49a (OD-44, owner 2026-11-06): base-object denies of the caller do not propagate; a user may be denied on a base table and granted on the view. The view is an independent data product.
- D-49b (OD-44 default): hard row policies apply to a view only through bindings that match the view; base-table hard policies do not propagate and are shown in the approval preview.
- D-49c: PII propagates by lineage; unresolved lineage counts as PII; at approval the base owners classify each PII-derived column as Masked-only, Clear-eligible or non-PII (aggregates); Clear needs Clear-eligibility and a Clear rule on the caller's view grant.
- D-49g (OD-44, owner 2026-11-06): granting authority is delegated: base owners approve once at creation (definition, product owners, column classification); afterwards only the product owners decide grants; a product-owner transfer needs base-owner approval.
- D-49h (default taken): a base-owner change keeps the view active; the current base owner may withdraw approval (suspends the view); a newly PII-classified base column masks the derived view columns until reclassified.
- D-49d: views live in the reserved data source `views`, so they never collide with source objects and pattern grants can target them (`views.*`).
- D-49e: nesting depth <= 3; cycles rejected.
- D-49f: no materialization, no parameters, read-only.
