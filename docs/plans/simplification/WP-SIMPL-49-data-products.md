# WP-SIMPL-49 Data products: views (schedule, on demand, live) and stored procedures with their own product owner
Status: Ready for implementation (starts after the feature freeze, i.e. after Stage 3 exit; PRD D8). One phase: views of every load mode and procedures share the same governance.
Parent: [PRD](../2026-11-05-req-project-simplification.md) section 2.0 OD-44 (revised 2026-11-06) and [ADR-020](../../adr/ADR-020-unified-authorization-model.md) (amendment 2026-11-06, data products) | Issues: none (owner decision) | Owner decisions: OD-44 (binding), OD-15 (DuckDB as the place for central views), OD-18 (procedures core), OD-19, OD-22, OD-25, OD-26, OD-29, OD-30, OD-33, OD-35, OD-38..OD-43 | Security conditions: SEC-PROD-01..14 (sec plan section 11.7), SEC-AZ-01..19

## 1. Goal and outcome
A data product is an object defined in Autheris, never in the source database: a **view** (an SQL statement over catalog ids, executed in two stages through DuckDB; load mode `schedule`, `onDemand(ttl)` or `live`) or a **stored procedure** (a source procedure or table-valued function called with parameters, declared `read` or `write`). Every data product has its own **product owner**, who decides all grants on it through the normal consent path (WP-SIMPL-47) and maintains its row policies, masking, hidden columns, description and load mode. Base owners and product owners are completely decoupled: base owners approve nothing and cannot veto; they are notified and see where their objects are used. A grant on the product is enough; base-table grants, denies and hard row policies of the caller do not apply to it. Products are catalog objects in the reserved data source `products` and readable (or executable) on every transport (OData/Power BI, GraphQL, REST, WebSQL, Trino protocol, MCP).

**View execution (two stages, one code path for every load mode):** stage 1 (definer) runs the product SQL with the product identity through the existing DuckDB federation staging (no `ATTACH`, no DDL, no transaction on the source) and yields a result set in DuckDB; its lifetime follows the load mode (`schedule`: refreshed by cron; `onDemand(ttl)`: loaded by the first access, valid for `ttl` minutes, reloaded by the next access after expiry, dropped after an idle period; `live`: loaded per query). Stage 2 runs the caller's query on that result with the product's grants, row policies and masking. The product SQL addresses objects only by catalog id (`domain.schema.table`); the catalog resolves each id to its data source.

Who may create a product and change its definition (OD-44):
- a data owner or a delegate (`DataOwnerDelegation`) over objects they own; the product owner is the creator;
- the owner of a data source over objects of that data source, including procedures; the product owner is the creator;
- a `DataProductAdmin` (new role) over any objects; the admin designates the product owner and never themselves;
- the Git synchronisation identity `DataProductSync` (new role), which counts like a `DataProductAdmin`; the product owner comes from the file and is never the sync identity.

Measurable: (a) a view over two data sources is readable with identical rows on every transport for a caller who has a product grant and no base grant (parity rows P1..P13, section 6.3); (b) no product changes its SQL or base objects except by a caller with creation authority for all of them; (c) no admin- or sync-created product has its creator or the sync identity as product owner; (d) `git grep` finds no DDL issued to a data source by product code; (e) a procedure declared `read` needs an `Execute` grant and a procedure declared `write` an `ExecuteWrite` grant (an execute grant never suffices); the declaration, not Autheris, determines the permission class.

## 2. Preconditions and dependencies
- Must be merged first: WP-SIMPL-46 (decision point, all transports re-routed), WP-SIMPL-47 (decision pipeline, notifications, grant records), WP-SIMPL-48 (row policies incl. object kind `ProcedureResult`, effective-access report), WP-SIMPL-39 (procedures clean core), WP-SIMPL-41 (discovery), WP-SIMPL-42 (usage), WP-SIMPL-44 (no source-metadata reads, reserved source names), WP-SIMPL-45 (write grants and shared write pipeline), WP-SIMPL-21 (AST security visitor and dialect parity), WP-SIMPL-20 (unified governance repository).
- Starts after the feature freeze (Stage 3 exit, PRD D8); not on the `freeze-exception` list.
- One phase: all load modes and procedures ship together; there is no separate live-view expansion in the rewriter.

## 3. Scope
### In scope (Phase A)
- `DataProduct` model and store, kinds `View` and `Procedure`; admin API; catalog registration (`products.<schema>.<name>`).
- Roles `DataProductAdmin` and `DataProductSync` and the functional right `DataProduct.Admin`; data-source ownership (`DATA_SOURCE_OWNERS`); creation-authority checks; product-owner rules.
- Base-owner notification and "used by products" in the effective-access report and discovery.
- PII classification of product columns (lineage default, audited declassification by the creator).
- Views on the table-cache engine in two stages (load modes, limits, staleness, GDPR rules).
- Procedures as products: execute grant (`read`) or write-execute grant (`write`) on the product; the declaration decides the permission class; no transaction or connection enforcement by Autheris.
- Git synchronisation (plan, apply, webhook, drift detection), Git-managed definitions read-only in the API.
- Discovery, usage, audit, explain; parity rows.
### Load modes
- `schedule` (cron), `onDemand(ttl)` and `live` on one code path; a product owner may change the load mode without changing grants.
### Out of scope
- Incremental refresh (later; the table-cache CDC consumer is the candidate).
- Base-owner approval or veto of products (OD-44: decoupled).
- Procedures on Oracle or Databricks (WP-SIMPL-39 D3 stays).

## 4. Current state (verified on `d74f69aa`; re-verify before editing)
- Table cache: `src/Autheris.Application/State/Caching/TableCache/` (about 1,100 lines): `PeriodicTableCacheRefresher` (scheduled extraction, shadow swap), `TableCacheQueryRouter` (routes AST queries to the cache), `Governance/GovernedTableCacheExecutionService`, `TableCacheCatalog`, `TableCacheInvalidator`; admin endpoints `TableCacheAdminEndpoints.cs`, worker `TableCacheBackgroundWorker.cs`. It caches source tables, not query results.
- Procedures: file declarations (`*.proc.yaml` after WP-SIMPL-39), `ProcedureMode { Read, Write }` with write rejected at load ("write procedures are not supported yet (phase 2)", `ProcedureDefinitionLoader.cs:219`) and at execution (guard 1a, `GovernedProcedureExecutionService.cs:127-134`); access through consent on the governed table set (`EvaluateTableAsync`) and a result row scope (`RowScopeKey`, `ApplyRowScopeAsync`); no owner. WP-SIMPL-39 adds the never-committed (SQL Server) and read-only (PostgreSQL) transaction.
- Ownership: per table (`DataOwner`, `DataOwnerDelegation` in `Domain/Model/GovernanceModels.cs`, `IDataOwnershipRepository`); no data-source ownership.
- Roles: `GatewayRole` (`Domain/Security/GatewayRole.cs`): ClusterAdmin implies all; TenantAdmin and GovernanceAdmin imply DataOwner, DataSteward, SchemaPublisher, SecurityAuditor, Consumer; `FilterAdmin`, `FilterSync`, `CallerAttributeApprover`, `CallerAttributeInspector` are implied only by ClusterAdmin. Functional rights (`Domain/Governance/FunctionalRight.cs`) with four-eyes approval for privileged rights (`FunctionalRightMapping.IsApproved`).
- Git synchronisation pattern: virtual filters (`ManagedBy`, `/api/v1/governance/virtual-filters/sync/plan|apply|webhook`, definition hash).
- No object kind for Autheris-defined views or products exists; semantic virtual tables (`Catalog/Semantic/SemanticVirtualTables.cs`) come only from dbt manifests.

## 5. Implementation steps (TDD order; write the section-6 tests of each step first)
### Phase A
1. **Model, store, API (PR A1)** (`src/Autheris.Domain/Model/DataProductModels.cs`, new):
   ```csharp
   public enum DataProductKind { View, Procedure }
   public enum LoadMode { Schedule, OnDemand, Live }   // OnDemand carries ttl minutes; Live = ttl 0
   public enum DataProductStatus { Active, Stale, Invalid, Retired }
   public enum ProductCreatorAuthority { BaseOwner, DataSourceOwner, DataProductAdmin, GitSync }
   public enum ProductColumnClass { PiiMasked, PiiClearEligible, NonPii }
   public sealed record DataProductColumn(string Name, string DataType, ProductColumnClass Class, IReadOnlyList<ColumnLineage> Lineage,
       string? DeclassifiedBy, string? DeclassificationReason);
   public sealed record DataProduct(Guid Id, TenantId Tenant, string Schema, string Name, DataProductKind Kind, string Definition,
       string DefinitionHash, IReadOnlyList<TableIdentifier> BaseObjects, IReadOnlyList<DataProductColumn> Columns,
       Sid CreatedBy, ProductCreatorAuthority Authority, DataProductStatus Status, string? ManagedBy,
       LoadSettings Load, ProductLimits Limits, string? Description);
   ```
   Governance tables `DATA_PRODUCTS`, `DATA_PRODUCT_VERSIONS` (hash, definition, author, authority, created at) and `DATA_SOURCE_OWNERS` (tenant, data source, owner SID; delegation as for table owners), next free forward-only migration step at merge time. API `/api/v1/products` (`GET`, `PUT {schema}/{name}`, `POST {schema}/{name}/owner` (transfer), `POST {schema}/{name}/retire`, `POST {schema}/{name}/refresh`), `.WithAudit`. The product owner is stored as the data owner of the product catalog object through `IDataOwnershipRepository`, so consent approval, delegation, explain and owner grants (OD-34) work unchanged.
2. **Roles and authority (PR A1)**:
   - `GatewayRole.DataProductAdmin = 34`, `GatewayRole.DataProductSync = 35`; tenant-scoped only (strict parser); **not** implied by ClusterAdmin, TenantAdmin or GovernanceAdmin (an explicit exception in `Implies`, because the role can expose data). Functional right `DataProduct.Admin` is privileged (four-eyes mapping). `DataProductSync` may only call the sync endpoints.
   - Creation and definition change (`PUT`): allowed when the caller is, for every base object, its data owner or active delegate (`BaseOwner`); or owner of the data source of every base object (`DataSourceOwner`, also for procedures); or `DataProductAdmin`; or `DataProductSync` through the sync path. Otherwise 403 `PRODUCT_CREATION_NOT_AUTHORIZED`. Procedures can only be created by `DataSourceOwner`, `DataProductAdmin` or `GitSync`, because the objects a procedure reads or writes cannot be verified.
   - Product owner: `BaseOwner` and `DataSourceOwner` creations make the creator the product owner. `DataProductAdmin` and `GitSync` creations must name a product owner who is not the creator and not the sync identity (`PRODUCT_OWNER_MUST_DIFFER`). Transfers (`POST .../owner`) only by `DataProductAdmin`, never to themselves. The product owner cannot change the definition, base objects or column classification (only the creation-authority holders can).
   - Data-source owners are assigned by GovernanceAdmin or TenantAdmin of the tenant (control plane); assignments are audited.
3. **Base-owner notification and transparency (PR A1)**: creating a product, changing its definition or retiring it sends an in-app notification and a `product.*` event (WP-SIMPL-47 outbox) to the owners of every base object; no approval and no veto. The WP-SIMPL-48 effective-access report and the explain API list, per base object, the products that use it; discovery shows a product's base objects only by names the caller may see.
4. **Column classification (PR A1)**: product columns are derived from the AST and catalog metadata (never from the source, OD-25); a column derived from any PII column, or with unresolved lineage, is `PiiMasked` by default. The creator (a creation-authority holder) may set `PiiClearEligible` or `NonPii` per column with a mandatory reason (audited, `DeclassifiedBy`). A `PiiMasked` column is always Masked; a `PiiClearEligible` column is Clear only with a Clear rule on the caller's product grant.
5. **Views and load modes (PR A2)**:
   - Stage 1 (`ProductLoadService`, on the table-cache engine): runs the current definition (`DefinitionHash` checked before every load) through the governed federation staging under the product identity: no caller and no decision on base grants (definer), but connection policies, source admission, timeouts and cost limits apply. The result is stored in the table cache with shadow swap. Load modes: `schedule` (cron per product, cluster-wide lease, manual load by the product owner), `onDemand(ttl)` (first access loads, one load at a time per product via single-flight and cluster lease, other callers wait; the result is valid for `ttl` minutes; optional serve-stale-while-reloading within a tolerance; idle products are dropped after `IdleDropMinutes`), `live` (loaded per query; the caller's filters are not pushed into the sources).
   - Limits per product (`ProductLimits`: max rows, max bytes, max runtime) capped by global options; exceeding a limit fails the load and keeps the previous result (not for `live`: the query fails).
   - Staleness: `loaded_at` is exposed; a failed load sets `Stale` and keeps the last result; an optional `MaxStaleness` per product (maximum age of the last successful load, relevant when loads keep failing) denies reads with `PRODUCT_STALE` when exceeded; without it the last result is served marked stale.
   - GDPR: one snapshot is retained; erasures in a source reach the product with the next refresh (the refresh interval is the documented maximum lag); the GDPR disclosure report includes products through base-object lineage.
6. **Exposure and decision (PR A2)**: `Active` (and `Stale` within `MaxStaleness`) products are registered as object kind `DataProduct` in the reserved data source `products`; the table-cache router serves stage 2. The decision point decides the product object with the caller's grants, denies and hard row policies on the product only (definer semantics); base-table grants, denies and hard policies of the caller are neither required nor applied. Row policies, per-cell column state and masking (WP-SIMPL-48) apply to product columns. Products may read other products; creation authority for those is the ownership of the inner product.
7. **Procedures as products (PR A3, PR A4)**:
   - Procedures declared `mode: read`: the execute decision (`AccessOperation.Execute` on the product object) replaces the consent lookup on the governed table set and the `RowScopeKey` semi-join; row policies and masking apply to the result through WP-SIMPL-48 bindings of object kind `ProcedureResult`. Autheris adds no transaction, read-only flag or connection restriction (OD-44): the declaration only decides the permission class; the database rights of the configured connection are the only technical limit and are the operator's responsibility.
   - Procedures declared `mode: write` (PR A4): allowed (OD-44). Separate grant operation `ExecuteWrite` (independent of `Execute`, like OD-33); wired on the procedure REST route and MCP `call_procedure` (MCP read-only tokens excluded); other transports reject (OD-35); intent and outcome audit (parameter hash, affected-row count, duration) via `RecordRequiredAsync`; a per-product call rate limit. What a procedure does is bounded only by the database rights of its connection; the product states this to the product owner.
   - Definitions move from the procedure directory into products: `*.proc.yaml` gains `product_owner`, `schema` and `mode`; the directory loader is replaced by the Git sync of step 8 (one source of truth).
8. **Git synchronisation (PR A5)**: `/api/v1/products/sync/plan|apply|webhook` following the virtual-filter sync (signed webhook; the plan lists creates, changes and retirements; apply runs in one transaction), callable only by `DataProductSync`; files carry definition, kind, refresh, limits, column classification with reasons and the product owner. Synced products have `ManagedBy = Git`; their definition is read-only through the API, while the product-owner functions (grants, row policies, masking, hidden columns) stay available; drift (hash differs) is reported and never silently overwritten.
9. **Discovery, usage, audit, explain (PR A6)**: discovery lists products (kind, description, product owner, refresh time, columns with class and state); usage records reads per product and refresh runs per product with the source statistics of each source; the data-access audit entry carries product id and definition hash; explain shows the product grant and that base rules do not apply.

## 6. Tests
### 6.1 Unit (`tests/Autheris.Tests.Unit/Products/`), all `[Trait("Category","Security")]`
- `ProductAuthorityTests` (SEC-PROD-01..04): `Owner_creates_over_own_objects_and_becomes_product_owner`, `Delegate_creates_over_owners_objects`, `Owner_cannot_include_foreign_object`, `DataSourceOwner_creates_views_and_procedures_in_own_source`, `Only_datasource_owner_admin_or_sync_create_procedures`, `Admin_must_designate_other_product_owner`, `Sync_identity_never_product_owner`, `Transfer_only_by_admin_never_to_self`, `Product_owner_cannot_change_definition_or_classification`.
- `ProductRoleTests` (SEC-PROD-05): `DataProductAdmin_not_implied_by_cluster_tenant_or_governance_admin`, `DataProductAdmin_tenant_scoped_only`, `DataProduct_admin_right_requires_four_eyes_mapping`, `Sync_role_limited_to_sync_endpoints`.
- `ProductDecisionTests` (SEC-PROD-06, 07): `Product_grant_suffices_without_base_grants`, `No_product_grant_is_default_deny`, `Base_deny_and_hard_policy_do_not_affect_product`, `Deny_on_product_wins`, `Self_approval_of_product_grant_rejected`.
- `ProductColumnTests` (SEC-PROD-08): `Pii_lineage_defaults_to_masked`, `Unresolved_lineage_is_pii`, `Declassification_requires_reason_and_is_audited`, `Clear_eligible_needs_clear_rule_on_grant`.
- `ViewLoadTests` (SEC-PROD-09, 10): `Load_runs_only_current_hash`, `Load_failure_keeps_result_and_marks_stale`, `Max_staleness_exceeded_denies`, `Limits_exceeded_fail_load`, `Load_lease_single_runner`, `OnDemand_first_access_loads_and_ttl_expiry_reloads`, `OnDemand_concurrent_callers_share_one_load`, `OnDemand_idle_product_dropped`, `Live_loads_per_query`, `Load_mode_change_keeps_grants`, `Result_shared_across_callers_policies_applied_at_read`, `No_ddl_sent_to_source`.
- `ProcedureProductTests` (SEC-PROD-11, 12): `Execute_grant_required_and_base_consent_not_used`, `Result_row_policy_applies`, `Declared_read_needs_execute_grant`, `Declared_write_requires_execute_write_grant`, `Execute_grant_never_allows_write_procedure`, `Write_procedure_rejected_on_unwired_transport_and_readonly_mcp_token`, `Write_procedure_intent_and_outcome_audited`, `No_transaction_or_readonly_flag_added_by_autheris`.
- `ProductSyncTests` (SEC-PROD-13): `Sync_requires_sync_role_and_signed_webhook`, `Git_managed_definition_read_only_in_api`, `Drift_reported_not_overwritten`, `Product_owner_functions_remain_on_git_products`.
- `ProductTransparencyTests` (SEC-PROD-14): `Base_owners_notified_on_create_change_retire`, `Report_lists_products_using_base_object`, `Discovery_hides_base_objects_invisible_to_caller`, `Audit_carries_product_id_and_hash`.
### 6.2 Architecture
- `ProductArchitectureTests.No_ddl_statement_built_in_product_code`, `.Products_decided_only_by_decision_point`, `.Load_reads_definition_by_hash_only`.
### 6.3 Parity rows (`CrossTransportAuthorizationParityTests`)
P1 view, `schedule`, single source; P2 view over two sources; P3 no product grant; P4 caller denied on a base object but granted on the product (allowed); P5 PII class masked; P6 clear-eligible with Clear rule; P7 row policy on the product; P8 stale beyond `MaxStaleness`; P9 product over a product; P10 procedure declared read with execute grant; P11 procedure declared write with write-execute grant (REST, MCP; other transports reject); P12 retired product; P13 `onDemand` expiry and reload.

## 7. Verification commands (one build at a time)
```
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "Category=Security&FullyQualifiedName~Products"
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build --filter "FullyQualifiedName~ProductArchitectureTests|FullyQualifiedName~AuthorizationArchitectureTests"
dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --no-build --filter "FullyQualifiedName~CrossTransportAuthorizationParityTests|FullyQualifiedName~ProcedureProduct"
```

## 8. Acceptance criteria
- [ ] Owners (incl. delegates), data-source owners, `DataProductAdmin` and the Git sync create products under the authority rules; base owners approve nothing and are notified.
- [ ] Product owners decide all grants on their products; admin- and sync-created products never have the creator or the sync identity as product owner; transfers only by `DataProductAdmin`.
- [ ] `DataProductAdmin` is tenant-scoped, four-eyes mapped and implied by no other role.
- [ ] Product grants suffice; base grants, denies and hard policies of the caller do not affect products; a deny on the product wins.
- [ ] Views load by the current hash only, in the modes `schedule`, `onDemand(ttl)` and `live`, with limits, lease, single-flight, staleness handling and a shared result across callers.
- [ ] Procedures are products: execute grants for `read`, write-execute grants for `write`, result row policies, no transaction or connection enforcement by Autheris, write procedures only on wired transports.
- [ ] Git-managed definitions are read-only in the API, product-owner functions stay; drift is reported.
- [ ] Discovery, usage, audit, explain and the effective-access report cover products; parity rows P1..P13 green.

## 9. PR slicing (<= 400 net added src lines each)
A1 "Model, roles, authority, notification, classification" (steps 1-4, ~400 src); A2 "Views, load modes and exposure" (steps 5-6, ~400 src); A3 "Read procedures as products" (step 7 read part, ~300 src, net negative with the removed consent and row-scope paths); A4 "Write procedures" (step 7 write part, ~300 src); A5 "Git synchronisation" (step 8, ~300 src); A6 "Discovery, usage, audit, explain, parity rows" (step 9 and section 6.3, ~250 src).

## 10. Risks, rollback, and things the developer must NOT do
- Risk: a `DataProductAdmin` exposes data that base owners would not release. Mitigation: dedicated tenant-scoped role implied by no other role, four-eyes role mapping, never product owner of own creations, self-approval forbidden for grants, full audit, base-owner notification and the "used by products" report.
- Risk: a write procedure changes more than intended. Mitigation: creation only by data-source owners or `DataProductAdmin`, minimal database rights of the configured connection (operator responsibility), write-execute grant, intent and outcome audit, rate limit.
- Risk: cached copies of personal data. Mitigation: one result per product, documented erasure lag (the load interval or ttl), GDPR report coverage, `PiiMasked` default.
- Rollback: each PR reverts cleanly; A1 with its migration step (no production data at plan time).
- Must NOT: send DDL to a data source; read source metadata for product columns; let the product owner change the definition, base objects or column classification; let an admin or the sync identity become product owner of its own creation; imply `DataProductAdmin` from any role; apply or require base-table grants, denies or hard policies for a product decision; run a refresh for a definition whose hash is not the current version; run a procedure declared write without a write-execute grant; let a Git-managed definition be changed through the API.

## 11. Estimate
About +1,800 / -400 src (removed procedure consent and row-scope paths), tests +2,100; 10-11 person-days (A1 2, A2 3.5, A3 1.5, A4 1, A5 1, A6 1.5).

## 12. Required edits to other components (made by this WP, after the program)
- Decision point (WP-SIMPL-46 code): object kind `DataProduct`, operations `Execute`/`ExecuteWrite` for procedure products, definer semantics.
- Consent pipeline (WP-SIMPL-47 code): product grants are ordinary consent requests decided by the product owner; `product.*` events.
- Effective-access report (WP-SIMPL-48 code): "used by products" per base object.
- Procedures (WP-SIMPL-39 code): directory loader replaced by the product sync; consent lookup and `RowScopeKey` replaced by the execute decision and result row policies; `ProcedureMode` (kept by WP-SIMPL-39) gets write mode enabled here.
- Source admission (WP-SIMPL-44 code): `products` added to the reserved source names.

## Decisions taken in this spec
- D-49a (OD-44): base owners and product owners are decoupled; no approval, no veto, notification only.
- D-49b (OD-44): creation authority = ownership (incl. delegates) of every base object, data-source ownership, `DataProductAdmin` or the Git sync; the product owner is the creator for ownership-based creations and a different person for admin and sync creations.
- D-49c (OD-44): `DataProductAdmin` is a new tenant-scoped role implied by no other role (also not ClusterAdmin), because it can expose data; it is mapped with four-eyes approval.
- D-49d: the product owner is the data owner of the product catalog object, so existing ownership, delegation, consent and owner-grant mechanics apply unchanged.
- D-49e: the product owner manages grants, row policies, masking, hidden columns, description and refresh; definition, base objects and column classification belong to the creation-authority holders.
- D-49f (revised): one phase; views of every load mode run in two stages through DuckDB (stage 1 definer, stage 2 caller), so there is no trusted-subtree expansion in the rewriter; load modes differ only in the lifetime of the stage-1 result.
- D-49g (OD-44): procedures are not protected by Autheris beyond the permission class: `read` needs `Execute`, `write` needs `ExecuteWrite`; no transaction or connection enforcement; database rights bound what a procedure does.
- D-49h (OD-44): Git is the admin channel for definitions (`DataProductSync`); repository review is the control; Git-managed definitions are read-only in the API.
- D-49i: PII lineage defaults to `PiiMasked`; only the creator can declassify, with a reason.
