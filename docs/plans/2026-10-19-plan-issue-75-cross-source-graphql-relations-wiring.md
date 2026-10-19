# PLAN-DATA-75: Cross-Source Relation Resolver Wiring in Governed GraphQL Tree Execution

**Document ID:** `PLAN-DATA-75`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 3 Security Reviewed)  
**Author:** Solution Architect (`csharp-architect`)  
**Input PRD:** [`docs/plans/2026-10-19-req-issue-75-cross-source-graphql-relations-wiring.md`](2026-10-19-req-issue-75-cross-source-graphql-relations-wiring.md)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md)  
**Child Documents:** `REQ-FED-01`, `PLAN-BUG-75-GATING`, `F-GOV-19`, `SEC-FED-01`  
**GitHub Issue:** #75 Addendum  
**Priority:** Wave 3 (Feature Completion), Rank 3.2b  

---

## 1. Executive Summary & Problem Context

In earlier milestones, Autheris established two distinct subsystems for relations:
1. **Single-Source SQL Subtree Compilation (`TreeSqlCompiler` & `GovernedTreeQueryService`):** Compiles nested GraphQL relation selections into a single native SQL query with SQL-engine-level JSON aggregation (`FOR JSON PATH`, `json_group_array`, `json_agg`), requiring all participating tables to reside within the identical SQL data source and dialect.
2. **Federated Cross-Source Resolution (`CrossSourceRelationResolver` & `InMemoryCrossSourceRowReader`):** Provides gateway-level batched key lookup (DataLoader pattern) across heterogeneous data sources with full per-source governance (RLS, masking, tenant isolation, and audit logging), registered in DI via `DataApiServiceExtensions.cs`.

However, `GovernedTreeQueryService` was never connected to `ICrossSourceRelationResolver`. In `GovernedTreeQueryService.cs` (lines 277–280), cross-source selections are unconditionally aborted:
```csharp
if (!RelationExecutability.HasSameSourceName(rootAccess.Metadata, access.Metadata))
{
    throw new GatewayInvalidQueryException("Relations across data sources are not supported in one query.");
}
```
Concurrently, `CatalogSchemaModel.cs` uses `RelationExecutability.IsSameExecutionSource` to omit all cross-source relations from schema introspection, rendering cross-source traversal impossible even when data owners explicitly enable `AllowCrossDataSource`.

### Core Architectural Goals
1. **Tree Partitioning:** Partition incoming GraphQL selection trees into a primary same-source SQL subtree executed via `TreeSqlCompiler` and dependent cross-source relation subtrees executed via `ICrossSourceRelationResolver`.
2. **Four-Eyes Consent (SEC-FED-01/02):** Enforce mutual opt-in: `Gateway:WebSql:CrossSource:Enabled == true` (or `Gateway:CrossSource:Enabled == true`), and `TableRelation.AllowCrossDataSource == true` on the relation metadata.
3. **Clear-Key Invariant (SEC-FED-19):** Guarantee that join key columns on both parent and child tables have effective access level `ColumnAccessLevel.Clear`. Masked or HMAC join keys immediately fail closed with `CROSS_SOURCE_RELATION_KEY_NOT_CLEAR`.
4. **Parameter-Bounded Batched Execution:** Extract and deduplicate join keys from parent JSON rows; batch child queries within database parameter limits (max 2,000 keys, dialect-tuned).
5. **Zero-Allocation JSON Stitching:** Perform streaming or single-pass UTF-8 JSON merging to graft child row sets into parent objects without full-tree in-memory buffering.

---

## 2. Verified Code Facts & Delta Analysis

1. **`GovernedTreeQueryService.cs` (`src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs`):**
   - Constructor receives `ITableAccessResolver`, `ISqlConnectionFactory`, `IAuditLogRepository`, `IColumnMaskingProvider`, `IOptions<GatewayOptions>`, `ITableReadConcurrencyGate`, `ILogger`, `IDbSessionContextInitializer`, and `IAuditWriter`.
   - Requires injection of `ICrossSourceRelationResolver` (or optional resolution from scoped `IServiceProvider`).
   - Line 277 rejects non-matching sources:
     ```csharp
     if (!RelationExecutability.HasSameSourceName(rootAccess.Metadata, access.Metadata))
         throw new GatewayInvalidQueryException("Relations across data sources are not supported in one query.");
     ```
   - Must be replaced by query partitioning: partition relations into local (compiled into SQL) vs cross-source (resolved post-SQL via `ICrossSourceRelationResolver.ResolveRelationAsync`).
2. **`RelationExecutability.cs` (`src/Autheris.Application/Sql/Tree/Federation/RelationExecutability.cs`):**
   - `IsSameExecutionSource(TableMetadata parent, TableMetadata child)` evaluates identical data source type, source name, and dialect.
   - Schema generation in `CatalogSchemaModel.cs` (lines 327 & 382) calls this method to filter relations.
   - Must be updated to:
     ```csharp
     public static bool IsRelationSupported(
         TableMetadata parent,
         TableMetadata child,
         TableRelation relation,
         bool crossSourceEnabled)
     {
         if (IsSameExecutionSource(parent, child)) return true;
         return crossSourceEnabled && relation.AllowCrossDataSource;
     }
     ```
3. **`CrossSourceRelationResolver.cs` (`src/Autheris.Application/Data/Relations/CrossSourceRelationResolver.cs`):**
   - Implements `ResolveRelationAsync(CrossSourceRelationRequest request, CancellationToken ct)`.
   - Already provides:
     - `ValidateCrossSourceOptIn`: checks `AllowCrossDataSource`.
     - `ValidateJoinKeysClear`: verifies `parentDecision.GetEffectiveColumnAccess` and `childDecision.GetEffectiveColumnAccess` are `ColumnAccessLevel.Clear`.
     - `ValidateNoCrossSourceSort`: prohibits sorting parent by cross-source child column.
     - `ResolveEffectiveBatchSize`: dials batch size to dialect bounds (500 default, up to 2,000).
     - Full RLS and column masking on child rows.
     - Target audit recording (`AuditEventTypes.DataApiCrossSourceRelation` / `CrossSourceFederationQuery`).
4. **`TableRelation.cs` (`src/Autheris.Domain/Model/TableModels.cs`):**
   - Line 349: `public bool AllowCrossDataSource { get; init; }` already exists.
5. **`GatewayOptions.cs` & `RestApiOptions.cs`:**
   - `options.WebSql.CrossSource.Enabled` is available.
   - Add top-level convenience flag / configuration binding support for `Gateway:CrossSource:Enabled` mapping to `CrossSourceOptions.Enabled`.

---

## 3. Architectural Design

```
+----------------------------------------------------------------------------------------------------+
|                                    GraphQL Execution Pipeline                                      |
|                                                                                                    |
|  GraphQL Query: { orders { id, order_date, customer { id, name, email } } }                        |
|  - orders: Postgres (orders_db)                                                                    |
|  - customer: SQL Server (crm_db) [Cross-Source Relation: order_customer]                           |
+----------------------------------------------------------------------------------------------------+
                                                |
                                                v
+----------------------------------------------------------------------------------------------------+
|                            GovernedTreeQueryService.ExecuteAsync                                   |
|                                                                                                    |
|  1. Memoize Table Access & Evaluate PDP Decisions (orders: Allowed, customers: Allowed)           |
|  2. Tree Partitioning:                                                                             |
|     - Local SQL Tree: orders (root) columns: [id, order_date, customer_id]                         |
|     - Cross-Source Plan: relation 'order_customer' -> target: customers (crm_db)                   |
|  3. Pre-Execution Governance Checks:                                                               |
|     - Check Gateway:CrossSource:Enabled == true                                                    |
|     - Check relation.AllowCrossDataSource == true                                                  |
|     - Check Clear Keys: orders.customer_id is Clear, customers.id is Clear                         |
|  4. Execute Local SQL Query:                                                                       |
|     - Run compiled query against orders_db (PostgreSQL) -> JSON Array of orders                    |
+----------------------------------------------------------------------------------------------------+
                                                |
                                                v
+----------------------------------------------------------------------------------------------------+
|                                 Batched Cross-Source Resolution                                    |
|                                                                                                    |
|  5. Extract distinct customer_id values from parent JSON rows: [101, 102, 103, ...]                |
|  6. Invoke ICrossSourceRelationResolver.ResolveRelationAsync:                                      |
|     - Chunk keys into parameter-bounded batches (e.g. 500 keys)                                    |
|     - Read candidate rows from SQL Server via ICrossSourceRowReader                                |
|     - Apply Customer Table Governance (RLS, tenant boundary, column masking)                       |
|     - Emit Audit Log: AuditEventTypes.CrossSourceFederationQuery                                   |
+----------------------------------------------------------------------------------------------------+
                                                |
                                                v
+----------------------------------------------------------------------------------------------------+
|                                 Zero-Allocation JSON Stitching                                     |
|                                                                                                    |
|  7. Merge child objects/arrays into parent JSON document under relation response key               |
|  8. Apply HMAC pseudonymization for any root HMAC columns                                          |
|  9. Return unified JsonDocument to GraphQL execution pipeline                                      |
+----------------------------------------------------------------------------------------------------+
```

### Architectural Decisions

- **Decision D-75.1: Two-Phase Tree Execution (Hybrid Tree Query)**
  - Local relations continue to execute via `TreeSqlCompiler` inside the primary database engine.
  - Cross-source relations are pruned from the node before calling `TreeSqlCompiler.Compile`, ensuring the native SQL dialect compiler only processes single-source nodes.
  - The parent node's selected columns are augmented with the required parent join keys (`relation.JoinKeysParent`) if not already requested by the client, ensuring join key availability during extraction.
- **Decision D-75.2: Fail-Closed Security Opt-In & Clear-Key Guardrails**
  - If `Gateway:CrossSource:Enabled` is `false`, cross-source traversal throws `GatewayForbiddenException("Cross-source relations are disabled.")`.
  - If `relation.AllowCrossDataSource` is `false`, throws `GatewayInvalidQueryException("Relation across data sources requires AllowCrossDataSource opt-in.")`.
  - If either parent or child join key column is masked (`ColumnAccessLevel.Mask` or `Hmac`), throws `CrossSourceRelationException(CrossSourceRelationErrorCodes.CrossSourceRelationKeyNotClear)`.
- **Decision D-75.3: Memory-Efficient JSON Stitching**
  - Use `System.Text.Json.Nodes.JsonArray` or pooled `Utf8JsonWriter` to splice the resolved child dictionaries into the parent rows indexed by join key.
  - If parent results contain 0 rows, bypass cross-source resolution entirely (zero queries to child database).
- **Decision D-75.4: Recursive Nested Traversal**
  - If a cross-source child node itself has nested relations, child relations belonging to the child's data source are resolved by recursive tree compilation or secondary resolvers, preserving clean hierarchical composition.

---

## 4. Work Packages & Component Specifications

### WP-75-1: Query Tree Partitioning & Join Key Augmentation
- In `GovernedTreeQueryService.cs`:
  - Implement `PartitionTree(TreeQueryNode node, TableMetadata rootMeta, out TreeQueryNode localTree, out List<CrossSourceSubtreePlan> crossSourcePlans)`.
  - For each cross-source relation:
    - Verify that parent join keys are present in `node.Columns`. If missing, create a transient synthetic node with join keys included.
    - Record response key, join keys, and target selection node.

### WP-75-2: Execution Pipeline Integration
- In `GovernedTreeQueryService.ExecuteAsync`:
  - Validate all table access decisions up-front via `ResolveOnceAsync`.
  - Check that non-matching sources comply with cross-source opt-in (`AllowCrossDataSource` and `CrossSource.Enabled`).
  - Execute `TreeSqlCompiler.Compile` against the `localTree`.
  - Execute native query on primary database connection.
  - Parse root rows into a structured intermediate representation (`JsonArray` or dictionary list).
  - For each `CrossSourceSubtreePlan`:
    - Call `ICrossSourceRelationResolver.ResolveRelationAsync` with parent row dictionaries and target table specifications.
    - Stitch resolved child objects/lists into the corresponding parent items using `ResponseKey`.
  - Re-serialize to `JsonDocument`.

### WP-75-3: Schema Exposure Alignment (`CatalogSchemaModel`)
- In `RelationExecutability.cs`:
  - Add method `IsRelationSupported(TableMetadata parent, TableMetadata child, TableRelation relation, bool crossSourceEnabled)`.
- In `CatalogSchemaModel.BuildAsync`:
  - Pass `crossSourceEnabled` (from `options.WebSql.CrossSource.Enabled`).
  - In forward relation loop (line 327) and reverse relation loop (line 382):
    - Replace `RelationExecutability.IsSameExecutionSource(...)` with `RelationExecutability.IsRelationSupported(...)`.
  - Emit cross-source relation fields with clear documentation annotations.

### WP-75-4: Audit & Diagnostic Telemetry
- Ensure execution of cross-source relations logs:
  - `AuditEventTypes.CrossSourceFederationQuery` or `DataApiCrossSourceRelation`.
  - Records parent table, child table, parent source name, child source name, join keys, distinct key count, batches executed, and rows stitched.

---

## 5. Non-Functional Requirements & Performance

- **Latency Overhead:** Key extraction and memory merging must add $< 5$ ms for up to 1,000 parent rows.
- **Batch Clamping:** Target queries use `WHERE child_key IN (@k0, @k1, ...)` clamped to dialect limits:
  - SQLite: 500 keys
  - SQL Server: 1,000 keys
  - PostgreSQL: 2,000 keys
- **Zero-Allocation Hot Path:** Use `ReadOnlySpan<char>` and stack allocations for single-column string keys. Avoid string interpolation in key hashing.
- **Result Row Clamping:** Number of materialized child rows cannot exceed `CrossSourceOptions.MaxStagedRowsPerTable` (default 50,000) or `MaxNestedRows` (default 5,000).

---

## 6. Verification Checklist

- [ ] Single-source queries continue to compile and execute as a single SQL statement without regression.
- [ ] Cross-source GraphQL queries between SQLite and PostgreSQL/SQL Server resolve nested data correctly.
- [ ] Schema omits cross-source relations when `CrossSource.Enabled == false` or `AllowCrossDataSource == false`.
- [ ] Schema exposes cross-source relations when both switches are active.
- [ ] Runtime fails closed if client requests cross-source relation with disabled master switch or missing table consent.
- [ ] Queries fail closed with `CROSS_SOURCE_RELATION_KEY_NOT_CLEAR` if either join key is masked.
- [ ] Sorting parent rows by a cross-source column is rejected with `CROSS_SOURCE_SORT_NOT_SUPPORTED`.
- [ ] Dedicated audit log records are written with parent/child table metadata and batch counts.

---

## 7. Phase 3 Security Review & Binding Conditions (`csharp-security-expert`)

**Review Date:** 2026-10-19  
**Verdict:** APPROVED WITH CONDITIONS  
**Reviewer:** Application Security Expert (`csharp-security-expert`)

### Binding Security Conditions

1. **`SEC-DATA75-01`: Zero-Trust Join Key Exposure (SEC-FED-19 & Oracle Prevention)**
   - Before executing child batch lookups, `GovernedTreeQueryService` and `ICrossSourceRelationResolver` MUST verify that both `JoinKeysParent` and `JoinKeysChild` have effective access `ColumnAccessLevel.Clear` for the caller's active consent/profile.
   - If either parent or child key column is Masked, Hash-Masked (HMAC), Nullified, or Denied, execution MUST fail closed with HTTP 403 / `RELATION_KEY_MASKED` (`CROSS_SOURCE_RELATION_KEY_NOT_CLEAR`). Joining across sources on masked or pseudonymous keys creates a cross-database side-channel oracle.
2. **`SEC-DATA75-02`: Four-Eyes Consent Gate & Dual Opt-In**
   - Both parent table metadata and target table metadata/relation MUST have `AllowCrossDataSource == true`, and gateway configuration `Gateway:CrossSource:Enabled` (and `WebSql:CrossSource:Enabled`) MUST be explicitly `true`.
   - If either switch is disabled, cross-source queries MUST fail closed immediately with HTTP 403 `FORBIDDEN` / `CROSS_SOURCE_RELATIONS_DISABLED`.
3. **`SEC-DATA75-03`: Parameter Injection Defense & Bounded Batching**
   - Batch lookup queries into child data sources MUST strictly use parameterized queries (`DbParameter` collections, e.g. `@k0, @k1, ...`). String interpolation or raw SQL concatenation of extracted key values is strictly forbidden.
   - Distinct parent key batches MUST be clamped by dialect limits (SQLite: 500, SQL Server: 1,000, PostgreSQL: 2,000) and capped at a maximum of 2,000 keys per GraphQL operation. Operations exceeding batch or row caps (`MaxNestedRows` = 5,000) MUST terminate with `CROSS_SOURCE_BATCH_LIMIT_EXCEEDED`.
4. **`SEC-DATA75-04`: Independent Child Row-Level Security (RLS) & PDP Evaluation**
   - Child table lookups MUST independently enforce the child table's own RLS predicates, tenant boundary isolation (`TenantColumnName == tenantId`), and Casbin ABAC policies.
   - Parent table authorization or consent MUST NEVER implicitly grant access to unconsented child rows. Unmatched child rows under child RLS evaluate to empty/null.
5. **`SEC-DATA75-05`: Non-Destructive Structural Audit Logging**
   - Every cross-source resolution event MUST emit an immutable audit event (`CROSS_SOURCE_RELATION_RESOLVED` / `AuditEventTypes.CrossSourceFederationQuery`) recording parent source ID, child source ID, table names, join key column names, distinct key count, child row count, and execution duration.
   - Audit logs MUST NEVER persist cleartext or decrypted key values.

### Mandatory Security Test Suite (`[Trait("Category", "Security")]`)
- `[Trait("Category", "Security")] T_SEC_DATA75_01_MaskedJoinKeyFailsClosed`: Verifies that masking either parent or child join key column aborts cross-source execution with HTTP 403 / `RELATION_KEY_MASKED`.
- `[Trait("Category", "Security")] T_SEC_DATA75_02_FourEyesConsentRequired`: Verifies that disabling `CrossSource:Enabled` or omitting `AllowCrossDataSource` fails closed with 403 Forbidden.
- `[Trait("Category", "Security")] T_SEC_DATA75_03_ParameterInjectionAndBatchLimitClamping`: Verifies that all child lookups utilize strictly typed parameters and requests exceeding 2,000 keys abort with `CROSS_SOURCE_BATCH_LIMIT_EXCEEDED`.
- `[Trait("Category", "Security")] T_SEC_DATA75_04_IndependentChildRlsEnforcement`: Verifies that child table lookups enforce child tenant isolation and RLS filter predicates regardless of parent row consent.
- `[Trait("Category", "Security")] T_SEC_DATA75_05_StructuralAuditNoKeyLeakage`: Verifies that `CROSS_SOURCE_RELATION_RESOLVED` audit events contain table/source identifiers and metrics without cleartext key values.
