# Implementation Plan: REQ-GOV-107 - Key-Set Cache Strategy for Correlated Row Filters

**Document ID:** `PLAN-GOV-107` (revision 1, 2026-10-15)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-GOV-107](2026-10-15-req-issue-107-row-filter-key-set-cache.md) (decisions D-1..D-16, FR-1..FR-9, SR-1..SR-10, AC-1..AC-15)
**Branch:** `feat/issue-107-rowfilter-keyset-cache` (base `main` `21dfd399`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Status:** SECURITY REVIEWED (Phases 2 and 3 complete; verdict approved with binding conditions, section 12); next: Phase 4 TDD implementation

**Binding project rules applied:** no upgrade/repair migrations (the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server is extended in place, the `0001` checksum constants stay unchanged, the shape snapshots under `src/Autheris.Infrastructure/Persistence/Migrations/Shape/` and their tests are updated); least privilege on sources (SELECT only); fail closed; CI stays fast (no sleeps, `TimeProvider`/`FakeTimeProvider`, shared fixtures); Phase-4 TDD developers are Sonnet; the SqlGate/AST security suites stay green; `src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.cs` is **not** modified (section 2, A-4).

---

## 1. Verification of the PRD Against the Code (`7bfda713`)

| PRD item | Result |
|---|---|
| `AdvancedRlsPredicateBuilder.CorrelatedExpression` always emits `EXISTS` | **Confirmed** (`src/Autheris.Application/Sql/Policy/AdvancedRlsPredicateBuilder.cs:33-108`). The EXISTS body is `SELECT 1 FROM dep [JOIN hops] WHERE dep.pk = autheris_target.fk AND <predicate> [AND temporal]`. Subquery predicate values are literals from `SubqueryFilterPredicateJson` bound through `PolicyParameterNamer`; no user attributes can enter the subquery today. |
| Callers | **Confirmed.** `TypedRowFilterBuilder.Condition` (consents; ALLOW and DENY share it, DENY is wrapped in `NOT (...)` by `BuildCombined`) and `CasbinEnforcementService.BuildCorrelatedRowFilterPredicate` (`CasbinEnforcementService.Masking.cs:88`, rule-ordinal parameter prefix). |
| "String and typed forms agree" check | **Located precisely.** Two checks run in `GovernancePolicyFactory.TryAdmit`: `TableAccessDecision.RequireTypedRowPolicy()` (`Domain/Interfaces/TableAccessDecision.cs:108`: `CombinedRowFilterSql` must equal `RowPolicy.Project().Sql`) and `MarkersAgree` (Virtual Filter markers). Both compare the **decision**, not the compiler input. Substituting after `TryAdmit` on the compiler input only (`rowPredicates`) cannot interfere with them (resolves PRD R-4, see A-2). |
| Decision cache keeps no typed objects | **Confirmed** (`ConsentRowFilterRecipe` XML doc): after a hit the typed policy is rebuilt from the recipe (`ConsentRowFilter` list, JSON). New `ConsentRowFilter` properties therefore travel with the recipe automatically; the recipe JSON must round-trip them (WP-A test). |
| `VirtualFilterKeySetCache` / `SqlVirtualFilterKeySetProvider` reusable | **Partly.** The cache mechanism (dedicated `MemoryCache`, single-flight per key, failures not cached, shared load detached from caller cancellation, own timeout) is reusable but typed to `VirtualFilterKeySet`, has **no epoch in its key and no age re-check on hit**. The provider builds its key query as **string SQL** (`StructuredFilterKeyQueryBuilder`) outside the compiler and supports SQL Server/PostgreSQL/SQLite only. D-9 requires the compiler path, so only the cache mechanism and the connection/session/read-only pattern are reused (A-3, A-5). |
| Policy subquery governance | **Confirmed and made precise.** Tables in a policy subquery are rewritten by `PolicySubqueryTenantRewriter` (`TrinoSqlEngine/Ast/Security/PolicySubqueryTenantRewriter.cs`): each becomes a derived table projecting the catalog columns (the "governed projection") with the tenant predicate (`SecurityScope.PolicySubquery`); no masks, no consent filters; nested policy subqueries rejected. It is called from `AstSecurityVisitor.Tables.cs:76` and `AstSecurityVisitor.Expressions.cs:252`. M-A1 (`FindUnusableDependentsAsync`) denies the owning table when a dependent is inactive, unrecorded or violates `RequireTenantColumn`. |
| Parameter typing | **Confirmed.** `PolicyColumnTypeAnnotator` annotates `autheris_target.col IN (:p, ...)` items with the target column's catalog type (varchar vs nvarchar, NUMBER vs VARCHAR2). An IN list on the target FK is therefore typed from the FK column for free (D-8). `SqlParameterType` has no GUID member (uuid keys need a decision, A-9). |
| Policy epochs | **Confirmed with a gap.** `IEpochValidationService.GetCurrentEpochsAsync` silently returns the **local, non-authoritative** epoch when Redis is configured but unreachable (`EpochValidationService.GetCurrentEpochCoreAsync`, `Authoritative = false` is not surfaced). "Epoch unavailable -> bypass" (D-6, SR-4) needs a new authoritative read (contract C-6). Repositories bump epochs in-repo (`IncrementTableEpochAsync` + `InvalidateEpochAsync`). |
| `CONSENT_ROW_FILTERS` persistence | **Confirmed** on all three engines (`*BaselineSchema.cs`, `*GovernanceRepository.Consent.cs`); written only in `CreateConsentAsync`; no tenant column (tenant comes from `CONSENTS`). |
| Object-level storage separate from `TABLES` | **Confirmed necessary**: ingestion upserts rewrite `TABLES`; F-GOV-21 used separate tables for the same reason. |
| Admin API / MCP plan-apply | **Partly inaccurate in the PRD.** MCP two-phase HitL exists only for access grants (`admin_plan_access` / confirm with TOTP via `AccessPlanningService.ConfirmPlanAsync` / `admin_apply_access`); `StoredAccessPlan` is typed to access grants. A new plan kind is required (A-8). |
| `LogGeneratedSql` / audit | **Confirmed.** `GovernedSqlRewriter.RewriteCoreAsync:642` logs `compiled.Sql` at Debug; statement audit details are built in `GovernedSqlExecutor.cs:340` (`virtual_filters`), fed from `GovernedRewrite`. |
| Dialect limits | **Confirmed** (`DialectCapabilityTable.cs`): SQL Server 2,100 binds; PostgreSQL 65,535; Oracle 32,767 binds and 1,000 IN items (`AdvancedRlsPredicateBuilder.InLists` chunks with `OR`); SQLite 32,766; DuckDB 65,535; Databricks experimental 1,000. |
| Channels (D-13) | **Verified.** Every SQL channel of `RowFilterChannelParityTests` (WebSQL, Trino, SQL endpoints, OData, GraphQL, GraphQL tree, MCP sample rows / query, Arrow export, Flight SQL, OLAP) reaches `GovernancePolicyFactory` only through `GovernedSqlRewriter.RewriteCoreAsync`. In-memory sources (`InMemoryRowFilterEvaluator`) do not compile SQL and are unaffected. Substitution in the factory therefore covers all compiled channels; channels whose dialect has no key loader (DuckDB federation, Databricks, Trino) fall back to `EXISTS` (reason `DialectUnsupported`). |
| Legacy `Gateway:RowFilters:SubqueryStrategy` | **Confirmed** string-path only (`RlsOptions.cs:104`); unchanged. |
| Template cache | **Confirmed** value-free shape key; a padded IN list is a distinct shape only per bucket (D-7). |

No PRD decision has to be reverted. Two PRD statements are refined: D-4's "SHA-256 of the canonical filter definition" becomes a hash over the **compiled key query and its binds** (A-6, complete by construction), and D-11(b) needs a new plan kind (A-8).

---

## 2. Architecture Decisions

### A-1 Site identification: registry of correlated EXISTS nodes (no AST or decision change)
`AdvancedRlsPredicateBuilder.CorrelatedExpression` registers every `ExistsExpression` it creates in `CorrelatedFilterSiteRegistry` (a `ConditionalWeakTable<ExistsExpression, CorrelatedFilterSite>`, reference identity). The site carries the rule id, the rule-level settings, the source (`Consent`/`Casbin`) and a structural eligibility flag (no temporal columns). `PolicyPredicate.And/Or` and `PolicyPredicate.Create` keep child references, so the registered instance survives combination with Virtual Filter and Casbin parts; a decision-cache hit rebuilds the predicate through the same builder and re-registers. Any node not created by the builder (a user AST, a node re-created by some rewriter) is simply not found and stays `EXISTS` (exact, fail-safe). Rejected alternatives: a marker AST node (engine change touching the security visitor), carrying sites on `PolicyPredicateRowPolicy` (needs merge logic in four `With*` helpers of `TableAccessDecision`).

### A-2 Substitution point: inside `GovernancePolicyFactory`, after admission, on the compiler input only
`CreateAsync` keeps its admission loop unchanged (`TryAdmit`: `RequireTypedRowPolicy`, `MarkersAgree`, M-A1 dependents). After the loop, for every admitted table with a `PolicyPredicate`, the new stage `IRowFilterKeySetStage` rewrites `rowPredicates[identity]` (and only that dictionary). The decision, its text projection and the decision cache are never touched (D-5). The `dependents` set is computed from the **final** predicates, so a fully substituted policy no longer makes its dependent tables resolvable for the compiler. The factory gets a new method `CreateAssemblyAsync` returning `GovernedPolicyAssembly(Policy, Report)`; `CreateAsync` delegates and returns `.Policy` (existing callers and tests unchanged). With no stage registered (null) the factory behaves exactly as today (AC-8).

### A-3 Key query: derived from the registered EXISTS and compiled by the engine with the same policy-subquery rewriter
New engine entry point `IPolicyKeyQueryCompiler` (implemented next to `GovernedSqlCompiler` in `TrinoSqlEngine`, new files only). Input: the registered `ExistsExpression`, the predicate's parameter values, the statement's verified `ITableCatalog`, `TenantBinding`, the target `TableCatalogEntry`, dialect and `RowLimit = MaxKeys + 1`. Derivation (fail -> `KeyQueryNotDerivableException` -> fallback `NotDerivable`):
1. Body must be `SelectStatement(QuerySpecification)` without GROUP BY/HAVING/ORDER/LIMIT; WHERE is a conjunction (AND chain, parentheses allowed).
2. Exactly one conjunct is `dep.pk = autheris_target.fk` (both bare column references, one side qualified with `RowFilterAliases.Target`). It is removed.
3. No remaining node references `autheris_target` (so the predicate is independent of the outer row; this is what makes `EXISTS(... dep.pk = t.fk AND P) <=> t.fk IN (SELECT dep.pk ... WHERE P AND dep.pk IS NOT NULL)` hold).
4. Result: `SELECT DISTINCT dep.pk FROM <same FROM> WHERE dep.pk IS NOT NULL AND <remaining conjuncts>` + dialect row limit.
5. The **same** `PolicySubqueryTenantRewriter` is applied (one level, canonical names, governed projection, tenant predicate per table), then `DialectCapabilityValidator`, the dialect generator and `EmittedSqlInvariantChecker`. A key-query coverage check proves that every physical table is wrapped with its tenant predicate (reuse of the `SecurityCoverageVerifier` policy-subquery scope; if that is not callable standalone, a dedicated `KeyQueryCoverageVerifier` with the same rule).
Output `CompiledKeyQuery(Sql, Parameters, KeyColumnType, ForeignKeyColumnType, CompilerVersion)`. No template cache for key queries (cold path). `AstSecurityVisitor.cs` is **not** modified; the new entry point is nevertheless a new compile path in the engine and is flagged for owner review (section 9).

### A-4 Why not compile the key query as a user statement
Dependent tables are deliberately not "admitted" (a user reference to them must stay governed by stage 3), user statements would apply masks to `dep.pk` (wrong keys), and rendering policy literals into SQL text would re-introduce text handling. The engine entry point reuses exactly the rewriter that governs the `EXISTS` body, so SR-2 parity holds by construction.

### A-5 Generic single-flight cache, dedicated instance
`SingleFlightKeySetCache<TValue>` (Application, extracted from `VirtualFilterKeySetCache`): dedicated size-limited `MemoryCache`, single-flight per key, failures never cached, shared load detached from caller cancellation and bounded by its own timeout, absolute expiration only. `VirtualFilterKeySetCache` becomes a thin wrapper (behaviour preserved, its tests unchanged). The row-filter instance `RowFilterKeySetCache` has its own `SizeLimitBytes` (D-4) and stores `RowFilterKeySetEntry` (keys **or** overflow sentinel, `LoadedAt` from `TimeProvider`, tenant and data source for hit verification).

### A-6 Cache key = SHA-256 over everything that determines the result
`rfks/v1` | data source name | dialect | tenant | compiled key-query SQL | every bind (name, `SqlParameterType`, canonical invariant value; the tenant bind included) | FK column catalog type | effective `MaxKeys` | authoritative epochs of the target table and of every governance identifier the dependents resolve to (sorted `identifier=epoch`) | `CompilerVersion`. The full 64-hex SHA-256 is the `MemoryCache` key (no truncation). The compiled SQL contains canonical table names, the projected catalog columns and every predicate shape, so catalog and definition changes are covered without a hand-written canonicalizer. Principal identity is **not** in the key: the key query is principal-independent (tenant-only session), which is the point of the feature (A-12, R-3).

### A-7 Settings resolution
`RowFilterEvaluationResolver` (pure): for each of strategy, duration, maxKeys independently: rule value ?? object value ?? global default; then duration clamped to `MaxDuration`, maxKeys to `MaxKeys`. Source of the strategy reported (`Rule`/`Object`/`Global`). Casbin sites have no rule level (D-14). A stored invalid value (direct load) is read as "not set" for that setting and the strategy as `Exists`, with one Warning per table/rule id (no values) (D-11, AC-14).

### A-8 Admin surfaces
REST (GovernanceAdmin, `WithAudit(AuditLevel.Full, <new event types>)`):
- `GET|PUT /api/v1/governance/row-filters/{id}/evaluation` (rule; tenant of the consent must equal the caller tenant, else `404` fixed body).
- `GET|PUT /api/v1/catalog/datasets/{id}/row-filter-strategy` (object; dataset id resolution as for `/keys`), `DELETE` clears (all inherit).
MCP: `admin_plan_row_filter_evaluation` and `admin_apply_row_filter_evaluation`, confirmation through the existing `ConfirmPlanAsync` (TOTP). `StoredAccessPlan` gets `PlanKind` (`Access` default, `RowFilterEvaluation`) and an optional `RowFilterEvaluationPlan`; each apply tool rejects a plan of the other kind (`SecurityException`). Both paths call the same application service `RowFilterEvaluationAdminService` (validation, repository, audit entry with before/after, epoch bump by the repository).

### A-9 Typing and key value conversion (D-8)
Supported key type pairs (catalog types of `dep.pk` and `target.fk`, normalized by `CatalogTypeMap`): integer -> integer of equal or wider range; exact numeric with scale 0 -> same; bounded character -> bounded character of the same Unicode class (`varchar->varchar`, `nvarchar->nvarchar`; `varchar->nvarchar` allowed, `nvarchar->varchar` **not**); uuid/uniqueidentifier -> same, bound as `String` (relies on the column-type annotation). Everything else (LOB/MAX types, floating point, dates, binary, mixed classes) -> fallback `TypeNotBindable`. Each loaded value is converted with checked conversions to the FK type; a value that does not fit (overflow, string longer than the FK length) makes the **whole filter** fall back to `EXISTS` (`ValueNotBindable`); values are never truncated or dropped.

### A-10 IN-list shape, padding, empty set, parameter budget (D-6, D-7, FR-6, FR-7)
- Leaf: `autheris_target.<fk> IN (:__rls_ks<n>_<i>, ...)` built by `AdvancedRlsPredicateBuilder.InLists` (Oracle chunks of 1,000 joined with `OR`, parenthesized).
- Padding: list length = `min(nextPow2(count), effectiveMaxKeys)`, filled by repeating the last key.
- Empty key set: the leaf becomes `1 = 0`; the surrounding AND/OR structure is preserved.
- Polarity: only sites reached from the predicate root through `AND`, `OR` and parentheses are substituted. A site under `NOT`, `CASE`, a function or a comparison stays `EXISTS` (`NegatedContext`) - this also guarantees DENY consents are never substituted, independent of the DENY validation on save.
- Budget per statement: `budget = caps.MaxBindParameters - existingPolicyBinds - Gateway:RowFilters:KeySetCache:BindParameterReserve (default 256)`. If the sum of padded lengths exceeds it, the largest lists fall back first (`ParameterBudget`) until it fits. If `Compile` still throws `SqlLimitExceededException` (bind parameters) the rewriter re-assembles **once** with the stage disabled for this statement (`BindLimitRetry`), so the feature never turns a working statement into an error.
- Substitution yields a new `PolicyPredicate` via `PolicyPredicate.Create` (parameter/type check, new fingerprint).

### A-11 Execution of the key query
`RowFilterKeySetLoader` (Application): governed read connection `DataSources:Connections[<data source>]` (provider must match the catalog dialect), dialects SQL Server, PostgreSQL, Oracle, SQLite; read-only transaction on PostgreSQL and Oracle (`SET TRANSACTION READ ONLY`, rolled back), tenant-only session context (`IDbSessionContextInitializer`, `userSid: null`), `CommandTimeout = KeyQueryTimeout`, reads at most `MaxKeys + 1` rows; more -> overflow sentinel cached for the duration. Any exception -> `GatewaySecurityException(RowFilterKeySetErrorCodes.KeySetUnavailable = "ROW_FILTER_KEY_SET_UNAVAILABLE")` with the database exception as inner only, Warning with exception type only; nothing cached; the statement is denied (SR-1, AC-11). HTTP mapping identical to `VIRTUAL_FILTER_SOURCE_UNAVAILABLE`.

### A-12 Source-native per-user RLS (R-3)
The key query is principal-independent by design. A new per-connection flag `DataSources:Connections:<name>:KeySetCacheAllowed` (default `false`) is the operator's attestation that the source does not filter the dependent tables by the session's user. Without it every site of that data source falls back (`SourceNotEligible`). (Binding detail in Phase 3.)

### A-13 Epochs
New `IEpochValidationService.TryGetAuthoritativeEpochsAsync` (C-6). Non-authoritative (Redis configured but unreachable or read failed) -> no cache lookup, no key query, fallback `EpochUnavailable`. Without Redis (single node) the local epoch is authoritative (existing semantics).

### A-14 Diagnostics (D-16)
`RowFilterKeySetReport` per statement: per site `{table, ruleId?, source, strategySource, outcome (Hit|Miss|Fallback), reason?, keyCount?, paddedCount?, ageSeconds?}`. Into: `GovernedRewrite.KeySets` -> audit details `row_filter_key_sets` (`GovernedSqlExecutor`), and one Debug line after the compiled-SQL line when `LogGeneratedSql` is on. Never key values, never a hash of key values. Metrics on `GatewayDiagnostics.Meter`: `autheris.rowfilter.keyset.hits`, `.misses`, `.fallbacks{reason}`, `.key_query.duration` (histogram), `.cache.size_bytes` (observable gauge); tags limited to `dialect`, `outcome`, `reason` (no tenant, table or rule id).

---

## 3. Contracts (fixed up front)

### C-1 Domain (WP-A, first commit "contracts")
```csharp
// src/Autheris.Domain/Model/RowFilterEvaluation.cs
public enum RowFilterEvaluationStrategy { Exists = 0, KeySetCache = 1 }
public enum RowFilterEvaluationSource { Global = 0, Object = 1, Rule = 2 }

/// Stored settings of one level; null = inherit.
public sealed record RowFilterEvaluationSettings(
    RowFilterEvaluationStrategy? Strategy, int? DurationSeconds, int? MaxKeys)
{ public static RowFilterEvaluationSettings Inherit { get; } = new(null, null, null); }

public sealed record EffectiveRowFilterEvaluation(
    RowFilterEvaluationStrategy Strategy, RowFilterEvaluationSource StrategySource,
    TimeSpan Duration, int MaxKeys);

public sealed record RowFilterEvaluationChange(
    TableIdentifier TargetTable, TenantId? Tenant, Guid? RowFilterId,
    RowFilterEvaluationSettings Before, RowFilterEvaluationSettings After, long NewEpoch);

// ConsentRowFilter (ConsentModels.cs) gains, serialized in ConsentRowFilterRecipe:
public RowFilterEvaluationStrategy? KeySetStrategy { get; init; }
public int? KeySetDurationSeconds { get; init; }
public int? KeySetMaxKeys { get; init; }

// TableMetadata gains (loaded, never written by UpsertTableMetadataAsync):
public RowFilterEvaluationSettings? RowFilterEvaluation { get; init; }
```

### C-2 Options (WP-A, first commit)
```csharp
// RowFilterOptions (Domain/Options/Security/RlsOptions.cs) gains:
public RowFilterKeySetCacheOptions KeySetCache { get; init; } = new();

public sealed class RowFilterKeySetCacheOptions
{
    public RowFilterEvaluationStrategy DefaultStrategy { get; init; } = RowFilterEvaluationStrategy.Exists;
    public TimeSpan DefaultDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(1);
    public int MaxKeys { get; init; } = 2000;              // 1..10,000
    public long SizeLimitBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan KeyQueryTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int BindParameterReserve { get; init; } = 256;  // 0..1,000
    public int MaxConcurrentKeyQueries { get; init; } = 4; // 1..64 per node (SEC-RF107-13)
}
// DataSourceConnectionOptions gains: public bool KeySetCacheAllowed { get; init; }   // default false (A-12)
// RowFilterKeySetCacheOptionsValidator : IValidateOptions<GatewayOptions> (D-15 rules, ValidateOnStart)
```

### C-3 Application settings and admin (WP-A)
```csharp
public static class RowFilterEvaluationResolver
{
    public static EffectiveRowFilterEvaluation Resolve(
        RowFilterEvaluationSettings? rule, RowFilterEvaluationSettings? obj, RowFilterKeySetCacheOptions global);
    /// 400 reasons; null when valid. Checks caps, positivity, enum range and (rule level) correlated + ALLOW + no temporal.
    public static string? Validate(RowFilterEvaluationSettings s, RowFilterKeySetCacheOptions global, ConsentRowFilter? rule, ConsentEffect? effect);
}
public interface IRowFilterEvaluationAdminService
{
    Task<RowFilterEvaluationView?> GetRuleAsync(Guid rowFilterId, TenantId tenant, CancellationToken ct);
    Task<RowFilterEvaluationChange?> SetRuleAsync(Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings s, Sid actor, CancellationToken ct);
    Task<RowFilterEvaluationView?> GetObjectAsync(TableIdentifier table, CancellationToken ct);
    Task<RowFilterEvaluationChange?> SetObjectAsync(TableIdentifier table, RowFilterEvaluationSettings? s, Sid actor, CancellationToken ct);
}
public sealed record RowFilterEvaluationView(RowFilterEvaluationSettings Stored, EffectiveRowFilterEvaluation Effective);
```

### C-4 Repositories (WP-A)
```csharp
// IConsentRepository (default interface impl throws NotSupportedException)
Task<(ConsentRowFilter Filter, ConsentEffect Effect, TenantId Tenant, TableIdentifier Table)?> GetRowFilterAsync(Guid rowFilterId, CancellationToken ct = default);
/// One transaction: read before, update the three columns, bump POLICY_EPOCHS of the consent's target table; then InvalidateEpochAsync.
/// Null when the filter does not exist or its consent belongs to another tenant.
Task<RowFilterEvaluationChange?> UpdateRowFilterEvaluationAsync(Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, CancellationToken ct = default);
// ITableMetadataRepository
/// Upsert (null = delete row) in TABLE_ROW_FILTER_STRATEGIES + epoch bump of the table, one transaction; null when the table is unknown.
Task<RowFilterEvaluationChange?> SetRowFilterEvaluationAsync(TableIdentifier table, RowFilterEvaluationSettings? settings, Sid actor, CancellationToken ct = default);
```

### C-5 Governance DDL (WP-A; in place in `0001`, checksums unchanged)
`CONSENT_ROW_FILTERS` gains three nullable columns; new table `TABLE_ROW_FILTER_STRATEGIES`:

| Engine | `CONSENT_ROW_FILTERS` additions | `TABLE_ROW_FILTER_STRATEGIES` |
|---|---|---|
| SQL Server | `key_set_strategy INT NULL, key_set_duration_seconds INT NULL, key_set_max_keys INT NULL` | `table_id NVARCHAR(128) NOT NULL PRIMARY KEY, strategy INT NULL, duration_seconds INT NULL, max_keys INT NULL, updated_by NVARCHAR(256) NOT NULL, updated_at DATETIMEOFFSET NOT NULL` |
| PostgreSQL | `key_set_strategy INTEGER NULL, key_set_duration_seconds INTEGER NULL, key_set_max_keys INTEGER NULL` | `table_id TEXT PRIMARY KEY, strategy INTEGER NULL, duration_seconds INTEGER NULL, max_keys INTEGER NULL, updated_by TEXT NOT NULL, updated_at TIMESTAMPTZ NOT NULL` |
| SQLite | `key_set_strategy INTEGER NULL, key_set_duration_seconds INTEGER NULL, key_set_max_keys INTEGER NULL` | `table_id TEXT PRIMARY KEY, strategy INTEGER NULL, duration_seconds INTEGER NULL, max_keys INTEGER NULL, updated_by TEXT NOT NULL, updated_at TEXT NOT NULL` |

No CHECK constraints on value ranges (caps are configuration, validated on write and on read, A-7). Shape snapshots `governance-shape.{sqlite,postgresql,sqlserver}.json` regenerated; `GovernanceSchemaShapeSnapshotTests` extended (`EmbeddedSnapshot_CoversTheRowFilterStrategyTable`), PostgreSQL/SQL Server shape integration tests stay green. Least-privilege grant lists (runtime login: SELECT/INSERT/UPDATE/DELETE on the new table) updated in the existing least-privilege tests.

### C-6 Epochs (WP-C)
```csharp
// IEpochValidationService (default impl: returns null = not authoritative -> fallback)
ValueTask<IReadOnlyDictionary<TableIdentifier, long>?> TryGetAuthoritativeEpochsAsync(IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
    => ValueTask.FromResult<IReadOnlyDictionary<TableIdentifier, long>?>(null);
```

### C-7 Engine (WP-B; new files in `src/TrinoSqlEngine/Governance/`)
```csharp
public interface IPolicyKeyQueryCompiler
{
    /// <exception cref="KeyQueryNotDerivableException">The EXISTS cannot be turned into an equivalent key query.</exception>
    CompiledKeyQuery CompileKeyQuery(PolicyKeyQueryRequest request, CancellationToken ct);
}
public sealed record PolicyKeyQueryRequest(
    ExistsExpression Correlated, IReadOnlyDictionary<string, PolicyValue> Parameters,
    ITableCatalog Catalog, TableCatalogEntry Target, TenantBinding Tenant,
    TargetSqlDialect Dialect, int RowLimit);
public sealed record CompiledKeyQuery(
    string Sql, ImmutableArray<BoundParameter> Parameters,     // existing bind record of CompiledSql (INV-16 ToString)
    string? KeyColumnType, string? ForeignKeyColumnType, string ForeignKeyColumn, string CompilerVersion);
public sealed class KeyQueryNotDerivableException(string reasonCode) : Exception(reasonCode) { public string ReasonCode { get; } = reasonCode; }
```
`FastSqlEngine` implements `IPolicyKeyQueryCompiler`; DI registers it from the same singleton.

### C-8 Application runtime (WP-B: registry + substitution; WP-C: stage, cache, loader)
```csharp
// WP-B: src/Autheris.Application/Sql/Policy/RowFilterKeySets/
public sealed record CorrelatedFilterSite(Guid? RuleId, CorrelatedFilterSource Source,
    RowFilterEvaluationSettings? RuleSettings, bool StructurallyEligible);   // false when temporal columns are set
public enum CorrelatedFilterSource { Consent, Casbin }
internal static class CorrelatedFilterSiteRegistry
{
    public static void Register(ExistsExpression node, CorrelatedFilterSite site);
    public static bool TryGet(ExistsExpression node, out CorrelatedFilterSite site);
}
internal static class CorrelatedKeySetSubstitution
{
    /// Positive-polarity registered sites of the predicate, in a stable pre-order.
    public static IReadOnlyList<(ExistsExpression Node, CorrelatedFilterSite Site)> FindSites(PolicyPredicate predicate);
    /// Replaces each given node (reference identity) by its IN list / 1 = 0; returns a new PolicyPredicate.
    public static PolicyPredicate Substitute(PolicyPredicate predicate,
        IReadOnlyDictionary<ExistsExpression, KeySetReplacement> replacements, TargetSqlDialect dialect);
}
public sealed record KeySetReplacement(string ForeignKeyColumn, IReadOnlyList<PolicyValue> Keys, int PaddedCount, int SiteOrdinal);
public static class KeySetPadding { public static int Bucket(int count, int maxKeys); }

// WP-C: src/Autheris.Application/Sql/Policy/RowFilterKeySets/
public interface IRowFilterKeySetStage
{
    ValueTask<IReadOnlyDictionary<TableIdentity, PolicyPredicate?>> ApplyAsync(
        RowFilterKeySetContext context, IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates,
        RowFilterKeySetReport report, CancellationToken ct);
}
public sealed record RowFilterKeySetContext(string DataSourceName, TenantId Tenant, TargetSqlDialect Dialect,
    ITableCatalog Catalog, TenantBinding TenantBinding,
    IReadOnlyDictionary<TableIdentity, TableMetadata?> Metadata,
    IReadOnlyDictionary<TableIdentity, IReadOnlyList<TableIdentifier>> GovernanceIdentifiers,  // target + dependents -> epoch keys
    bool Disabled);                                                                             // BindLimitRetry
public sealed class RowFilterKeySetReport { public IReadOnlyList<RowFilterKeySetOutcome> Outcomes { get; } public void Add(RowFilterKeySetOutcome o); }
public sealed record RowFilterKeySetOutcome(string Table, Guid? RuleId, CorrelatedFilterSource Source,
    RowFilterEvaluationSource StrategySource, KeySetOutcomeKind Outcome, KeySetFallbackReason? Reason,
    int? KeyCount, int? PaddedCount, int? AgeSeconds);
public enum KeySetOutcomeKind { Exists, Hit, Miss, Fallback }
public enum KeySetFallbackReason { Deny_Negated, Temporal, NotDerivable, Overflow, ParameterBudget, BindLimitRetry,
    EpochUnavailable, DialectUnsupported, SourceNotEligible, TypeNotBindable, ValueNotBindable, InvalidStoredSetting, LoadThrottled }
public sealed record GovernedPolicyAssembly(GovernancePolicy Policy, RowFilterKeySetReport KeySets);
public sealed class SingleFlightKeySetCache<TValue> : IDisposable where TValue : class { /* A-5 */ }
public sealed record RowFilterKeySetEntry(IReadOnlyList<object> Keys, bool Overflow, DateTimeOffset LoadedAt,
    string Tenant, string DataSource) { public long EstimateBytes(); }
public static class RowFilterKeySetErrorCodes { public const string KeySetUnavailable = "ROW_FILTER_KEY_SET_UNAVAILABLE"; }
```
`KeySetFallbackReason.Deny_Negated` is the reason name for a site in a negated context (`NegatedContext` in prose).

---

## 4. Files per Layer and Work Package

Ownership is exclusive; a file listed under one WP is edited only by that developer. Shared files are listed with their single owner and the requested change.

### WP-A - Settings, persistence, admin surface (developer 1)
- Domain: `Model/RowFilterEvaluation.cs` (new), `Model/ConsentModels.cs` (3 properties), `Model/ConsentRowFilterRecipe.cs` (round-trip test only, likely no change), `Model/TableModels.cs` / `TableMetadata` (property), `Options/Security/RlsOptions.cs` (C-2), `Options/Storage/DatabaseOptions.cs` (`KeySetCacheAllowed`), `Audit/AuditEventTypes.cs` (`ROW_FILTER_EVALUATION_CHANGED`, `TABLE_ROW_FILTER_STRATEGY_CHANGED`).
- Application: `Policy/RowFilterEvaluationResolver.cs` (new), `Policy/RowFilterKeySetCacheOptionsValidator.cs` (new), `Governance/Services/RowFilterEvaluationAdminService.cs` (new), `Governance/Interfaces/IRowFilterEvaluationAdminService.cs` (new), `Interfaces/IGovernanceRepository.cs` (C-4), `Governance/Interfaces/IAccessPlanStore.cs` (`PlanKind`), `Governance/Services/AccessPlanningService.cs` (row-filter plan/apply, kind check), `Mcp/Tools/McpAdminTools.cs` (2 tools), `Domain/Model/AccessPlanningModels.cs` (request/result records).
- Infrastructure: `Persistence/Migrations/{Sqlite,PostgreSql,SqlServer}BaselineSchema.cs`, `Persistence/Migrations/Shape/governance-shape.*.json`, `Persistence/{Sqlite,PostgreSql,SqlServer}GovernanceRepository.Consent.cs` (read/write of 3 columns, new methods), `.Catalog.cs` (load `RowFilterEvaluation`, `SetRowFilterEvaluationAsync`), `SqliteGovernanceLintSource.cs` if it enumerates `CONSENT_ROW_FILTERS` columns, Redis/in-memory `IAccessPlanStore` implementations (`PlanKind` round trip).
- Api: `Endpoints/GovernanceEndpoints.RowFilterEvaluation.cs` (new partial + one `Map...` call in `GovernanceEndpoints.cs`), `Endpoints/CatalogApiEndpoints.cs` (row-filter-strategy routes); admin service DI registration in the endpoint/governance registration file **owned by WP-C** (WP-A hands over the lines).
- Tests: `Tests.Unit/Governance/RowFilterEvaluationResolverTests.cs`, `...ValidatorTests.cs`, `...AdminServiceTests.cs`, `GovernanceSchemaShapeSnapshotTests.cs`, recipe round-trip, MCP plan/apply tests; `Tests.Integration/RowFilterEvaluationRepositoryContractTestsBase.cs` + SQLite/PostgreSQL/SQL Server subclasses (shared container fixtures), endpoint auth/400/404 tests, least-privilege tests (new table grants).

### WP-B - Engine key-query compiler, site registry, substitution (developer 2)
- TrinoSqlEngine (new files only + one interface implementation line): `Governance/IPolicyKeyQueryCompiler.cs`, `Governance/PolicyKeyQueryCompiler.cs`, `Governance/KeyQueryNotDerivableException.cs`, `FastSqlEngine.cs` (implements the interface by delegating). **Not touched:** `Ast/Visitors/AstSecurityVisitor*.cs`, `PolicySubqueryTenantRewriter.cs` (called, not changed; if its constructor needs a `TypedPolicyContext`, build one from the request).
- Application: `Sql/Policy/AdvancedRlsPredicateBuilder.cs` (registration in `CorrelatedExpression`; the `CorrelatedFilterSite` comes from the `ConsentRowFilter` plus a source argument), `Governance/CasbinEnforcementService.Masking.cs` (source `Casbin`, no rule settings), `Sql/Policy/TypedRowFilterBuilder.cs` (source `Consent`), new `Sql/Policy/RowFilterKeySets/{CorrelatedFilterSite,CorrelatedFilterSiteRegistry,CorrelatedKeySetSubstitution,KeySetPadding,KeySetReplacement}.cs`.
- Tests: `TrinoSqlEngine.Tests/Governance/PolicyKeyQueryCompilerTests.cs` (golden key SQL per dialect for issue examples 1 and 2, tenant wrapper per table incl. hops, row limit syntax per dialect, `NotDerivable` cases, coverage mutation), `Tests.Unit/Sql/Policy/CorrelatedKeySetSubstitutionTests.cs` (polarity, AND/OR groups, empty set, padding, Oracle chunking, reference identity, parameter names), golden statement tests `Tests.Unit/Sql/Policy/RowFilterKeySetGeneratedSqlTests.cs` (SQL Server, PostgreSQL, Oracle; AC-2, AC-3, AC-8).

### WP-C - Runtime: cache, loader, stage, factory/rewriter integration, epochs, diagnostics (developer 3, or developer 2 after WP-B)
- Application: new `Caching/SingleFlightKeySetCache.cs`; `VirtualFilters/Federation/VirtualFilterKeySetCache.cs` (wrapper); new `Sql/Policy/RowFilterKeySets/{RowFilterKeySetStage,RowFilterKeySetCache,RowFilterKeySetLoader,RowFilterKeySetEntry,RowFilterKeySetReport,RowFilterKeySetCacheKey,KeySetValueConverter,RowFilterKeySetErrorCodes}.cs`; `Sql/Policy/GovernancePolicyFactory.cs` (A-2); `Sql/Services/GovernedSqlRewriter.cs` (assembly, BindLimitRetry, Debug line); `Sql/Internal/GovernedRewrite.cs` (`KeySets`); `Sql/Services/GovernedSqlExecutor.cs` (audit details); `Interfaces/IEpochValidationService.cs` (C-6); `Domain/Diagnostics/GatewayDiagnostics.cs` (instruments); error mapping file that maps `VIRTUAL_FILTER_SOURCE_UNAVAILABLE` (add the new code).
- Infrastructure: `Cache/EpochValidationService.cs` (C-6 implementation over `GetCurrentEpochCoreAsync`).
- Api: `Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs` (all DI of WP-A/B/C, options validator `ValidateOnStart`).
- Tests: `Tests.Unit/Caching/SingleFlightKeySetCacheTests.cs` (single flight with N parallel cold callers using a gated `TaskCompletionSource`, failure not cached, caller cancellation does not cancel the shared load, timeout), `Tests.Unit/Sql/Policy/RowFilterKeySetStageTests.cs` (resolution matrix applied, every fallback reason, age re-check with `FakeTimeProvider`, epoch in key, tenant separation, overflow sentinel, budget), `RowFilterKeySetCacheKeyTests.cs` (sensitivity to every input), `KeySetValueConverterTests.cs`, `EpochValidationServiceAuthoritativeTests.cs`, existing `VirtualFilterKeySetCache` tests unchanged; `Tests.Integration/RowFilterChannelParityTests.cs` (KeySetCache variant, same fixture), `RowFilterKeySetSqlServerTests.cs` / `...PostgreSqlTests.cs` (shared Testcontainers fixtures: governance parity toggles, fail-closed, SELECT-only login), `Tests.Performance` benchmark (category `Performance`, not gating).

---

## 5. Sequencing and Parallelism

| Step | WP-A | WP-B | WP-C |
|---|---|---|---|
| 0 (day 1, first hours) | **Contracts commit**: C-1, C-2, `KeySetCacheAllowed`; push immediately | C-7 interface + records, C-8 WP-B records; push | C-6 interface; C-8 WP-C records/interfaces; push |
| 1 | DDL x3 + shape snapshots + shape tests | `PolicyKeyQueryCompiler` (TDD on golden key SQL) | `SingleFlightKeySetCache<T>` + VF wrapper; epoch implementation |
| 2 | Repositories x3 (contract test base) | Registry + builder registration (consent, Casbin) | `RowFilterKeySetCacheKey`, `KeySetValueConverter`, loader (SQLite unit fixture) |
| 3 | Resolver/validator/admin service | Substitution + padding + polarity | Stage against fakes of C-7/C-8 (WP-B) and C-3 resolver (WP-A) |
| 4 | REST endpoints, MCP plan/apply | Golden statement tests (needs WP-C factory for the full pipeline, else factory-level fakes) | Factory/rewriter/executor integration, diagnostics, DI |
| 5 | - | - | Parity + Testcontainers integration tests, benchmark |

Dependencies: WP-B and WP-C depend only on the contracts of step 0. WP-C step 4 consumes the real WP-A resolver and WP-B substitution (rebase on both). If only two developers are available: developer 1 = WP-A, developer 2 = WP-B then WP-C. Merge order: WP-A, WP-B, WP-C (each rebased, full unit suite + SqlGate suite green before push).

---

## 6. Test Strategy (AC mapping)

| AC | Tests (owner) |
|---|---|
| AC-1 | Resolver matrix (27 combinations of set/unset per level x 3 settings + clamping), validator 400 cases incl. caps, non-correlated, DENY, temporal; endpoint 400 tests (A) |
| AC-2 | Golden key SQL (B, `PolicyKeyQueryCompilerTests`) and golden statement SQL with padded binds for issue examples 1 (`fms.air1` via `conf.client` + hop `md.crane`) and 2 (`md.engine` via `md.crane`) on SQL Server, PostgreSQL, Oracle (B, `RowFilterKeySetGeneratedSqlTests`) |
| AC-3 | Two tables, different strategies, one statement -> IN for one, EXISTS for the other (B) |
| AC-4 | Admin service + repository contract tests: epoch bump per change; stage test: new epoch -> loader called again (A, C) |
| AC-5 | `SingleFlightKeySetCacheTests` and stage test with 32 parallel cold calls -> loader invoked once (C) |
| AC-6 | Stage test: bump of target, then of a dependent epoch -> reload each time (C) |
| AC-7 | Stage fallback tests: overflow (sentinel cached: second call no loader), budget, DENY (negated), temporal (C, B) |
| AC-8 | No setting anywhere -> byte-identical SQL; existing golden/SqlGate suites unchanged (B, C) |
| AC-9 | `RowFilterChannelParityTests` KeySetCache variant over all 11 channels, plus empty key set, NULL FK rows and multi-hop data (C) |
| AC-10 | Non-gating SQL Server benchmark + manual reference-environment record in the Phase-4 hand-off (C) |
| AC-11 | Loader failure and timeout (fake connection factory, no sleeps: blocked command + cancellation) -> `ROW_FILTER_KEY_SET_UNAVAILABLE`, nothing cached, next call retries (C) |
| AC-12 | `FakeTimeProvider`: entry loaded with 300 s, duration lowered to 60 s, advance 61 s -> reload (C) |
| AC-13 | Log capture + audit details: strategy, source, outcome, reason, count; assertion that no key value string occurs (C) |
| AC-14 | DDL/shape tests per engine, checksum constant tests unchanged, repository round trip, invalid stored values -> `Exists` + Warning (A) |
| AC-15 | Two tenants, identical filter -> two loads, own keys (unit with fake loader; integration on SQLite parity fixture) (C) |

Security regression ids: see section 12 (`SEC-RF107-nn`); tests carry `[Trait("Category", "Security")]`.

---

## 7. Least-Privilege Notes

- Sources: the key query is a plain `SELECT DISTINCT` with a row limit on the governed read connection; no DDL, temp tables, TVPs or catalog queries (the FK/PK types come from the governance catalog). The SELECT-only login integration test (SQL Server, PostgreSQL) runs the key query.
- Governance store: the runtime login needs SELECT/INSERT/UPDATE/DELETE on `TABLE_ROW_FILTER_STRATEGIES` and UPDATE on `CONSENT_ROW_FILTERS` (new). Least-privilege tests updated (WP-A).

---

## 8. Risks

| ID | Risk | Mitigation |
|---|---|---|
| P-1 | Reference identity of registered EXISTS nodes is lost by an unknown rewrite before the factory -> silent `EXISTS` (performance only) | Stage test asserts the site is found for consent, Casbin and VF-combined decisions; metric `fallbacks{reason}` exposes it. |
| P-2 | Key-query derivation diverges from the EXISTS semantics | Derivation rules A-3 (single correlation conjunct, no target reference, `IS NOT NULL`), golden tests, parity tests with NULL FKs and multi-hop data. |
| P-3 | Per-statement overhead (epoch read, key-query compile, hashing) on hot paths | Only for sites with effective `KeySetCache`; compile of the key query is AST-only (no parse); measured in the benchmark. A per-assembly memo of compiled key queries keyed by the site's `PolicyPredicate.Fingerprint` + values hash is allowed if the benchmark shows need. |
| P-4 | Bind-limit retry doubles assembly cost | Only on `SqlLimitExceededException`; counted as fallback. |
| P-5 | Engine entry point is new security-relevant code | Flagged for owner review; mutation-style tests (drop tenant wrapper, keep correlation conjunct) must fail. |
| P-6 | Generalizing the VF cache regresses F-GOV-14 | Wrapper keeps the public surface; existing VF tests are the regression net. |

---

## 9. Owner Flags

1. **New engine compile entry point** (`IPolicyKeyQueryCompiler`, A-3). `AstSecurityVisitor.cs` is not modified; the entry point reuses `PolicySubqueryTenantRewriter`. Security-sensitive; owner review requested at Phase 5.
2. **`KeySetCacheAllowed` per data source defaults to `false`** (A-12): enabling the feature needs an operator attestation per source in addition to the rule/object setting.

---

## 10. Phase-3 Review Focus

Cache-key completeness (A-6), principal independence (A-12), polarity rule (A-10), epoch authority (A-13), value conversion (A-9), engine entry point (A-3), MCP plan-kind separation (A-8), diagnostics confidentiality (A-14).

---

## 11. Changelog

- 2026-10-15: Revision 1 (Phase 2).
- 2026-10-15: Phase 3 security review (section 12); C-2 gains `MaxConcurrentKeyQueries`, C-8 gains `LoadThrottled`.
- 2026-10-17: Phase 4 WP-A drift notes (G-PLAN-REBASE). (a) `RowFilterEvaluationResolver` and `RowFilterKeySetCacheOptionsValidator` live in `src/Autheris.Application/Policy/` (namespace `Autheris.Application.Policy`), not under `Sql/Policy`; the validator takes an optional `ILogger` and is registered next to the other `IValidateOptions<GatewayOptions>` validators. (b) The repository contract test base is `Tests.Unit/Governance/RowFilterEvaluationRepositoryContractTestsBase.cs` (SQLite subclass in Tests.Unit) and is linked into Tests.Integration (PostgreSQL/SQL Server subclasses), the pattern of `AccessProfileRepositoryContractBase`, because the Integration project has no SQLite factory. (c) The PostgreSQL and SQL Server consent hydration read a fresh `Guid.NewGuid()` as `ConsentRowFilter.Id` instead of the stored `id` column, so a rule id was not addressable; the read now selects the stored id (SQLite already did). (d) Persistence is shared in `Persistence/RowFilterEvaluationStore.cs` (dialect-parameterised like `CatalogKeyDefinitionStore`); `SqliteGovernanceLintSource` does not read the key-set columns (lint does not depend on the strategy). (e) `Resolve` treats a level holding an invalid stored value (undefined enum, non-positive number) as strategy `Exists` for that level and ignores the invalid number; `HasInvalidStoredValue` is public so the runtime stage (WP-C) can log the one Warning per table/rule. (f) C-3 contract additions: `IRowFilterEvaluationAdminService.SetObjectAsync` takes the caller `TenantId` (audit entry tenant; the object level itself is not tenant scoped) and the interface gains `ValidateRuleAsync`/`ValidateObjectAsync` (validate against the current caps and return the stored "before" without writing; used by plan and apply) plus `RowFilterEvaluationValidationException` (mapped to 400). (g) The shared dataset resolution of `PUT /datasets/{id}/keys` moved unchanged into `Application/Governance/Services/DatasetIdResolver` and is used by both routes. (h) The REST PUT bodies are bound manually (4 KiB cap, curated 400) because the framework binder turns a malformed body into a 500 in this host; unknown properties, numeric or non-exact strategy strings, wrong number types and overflow are all 400. (i) `StoredAccessPlan` gains `AccessPlanKind PlanKind` (the property is called `PlanKind`, the enum `AccessPlanKind`) and `RowFilterEvaluationPlan`; both MCP tools are admin tools, hence cluster-admin sessions only. (j) The WP-A DI registrations are in `GatewayGovernanceServiceExtensions` (admin service and options validator; the validator runs with the existing `GatewayOptions` ValidateOnStart).
- 2026-10-17: Phase 4 WP-B drift notes and contract additions (G-PLAN-REBASE). (a) `SecurityCoverageVerifier.Verify` only accepts a root statement (policy scope false), so the key-query coverage proof wraps the governed key statement as `SELECT 1 WHERE <SecurityPredicateExpression(EXISTS (key statement))>` and verifies that (the same policy-scope rules the `EXISTS` body is proven with); no dedicated `KeyQueryCoverageVerifier` is needed. The row limit is stripped from the verified copy only (the verifier rejects raw literals inside a predicate). (b) The key statement carries `ORDER BY dep.pk` in addition to the limit: SQL Server emits `OFFSET .. FETCH` which needs an `ORDER BY`, and `ORDER BY (SELECT NULL)` is invalid with `SELECT DISTINCT`. (c) C-7 `KeyQueryNotDerivableException` gains a second property `Rule` (diagnostic rule name, never shown to users) and the reason codes `NotDerivable`, `TypeNotBindable` and `DialectUnsupported` (`KeyQueryNotDerivableReasons`); the A-9 pair allowlist and the collation check (SEC-RF107-07 a, c) run inside the compiler and surface as reason `TypeNotBindable`, an unsupported dialect as `DialectUnsupported`, so the WP-C stage maps `ReasonCode` one to one to `KeySetFallbackReason`. The allowlist is public as `KeySetKeyTypes` (engine) so the WP-C value converter classifies the FK type the same way. (d) The compiler re-checks its own result after the `InterceptInjected` hook (no remaining `autheris_target` reference, `dep.pk IS NOT NULL` conjunct, row limit, single `SELECT`) and throws `SecurityException` (never `KeyQueryNotDerivableException`) on a violation, so a mutated pipeline cannot degrade into a silent fallback. (e) C-8: `CorrelatedKeySetSubstitution` gains `Scan` (eligible sites plus the registered sites that were excluded as negated or shared, so WP-C can report `Deny_Negated`); `FindSites` returns `Scan(...).Eligible`. `Substitute` throws `InvalidOperationException` for a replacement key that is not an eligible site (fail closed). (f) `IRlsFilterGenerator` gains a default interface overload `BuildCorrelatedPredicate(filter, prefix, CorrelatedFilterSource)` used by the Casbin path; the one-argument-less existing overload keeps the `Consent` source. (g) `InternalParameterPrefix` (`__gql_`) does not textually cover the reserved `__rls_ks` prefix. Phase 5 fix round F1: the rejection is implemented (SEC-RF107-07 e): `GovernedSqlRewriter` rejects raw SQL text containing `__rls_ks` and `GovernedSqlExecutor` rejects request parameter names starting with it, next to the `__gql_`, `p_rls_` and `rls_` checks (same `WebSqlPolicyException` messages). (h) Site ordinals must be unique per statement across all tables (WP-C assigns them); the parameter names are `__rls_ks<ordinal>_<index>`. (i) Finding for WP-C: `GovernedSqlCompiler` budgets the emitted text at `MaxExpansionFactor` (default 64, ceiling 256) times the user SQL (floor 64 characters, so 4,096 characters by default); a padded list of 2,000 keys needs roughly 12,000 to 15,000 characters and a short statement throws `SqlLimitExceededException(PolicyExpansionFactor)`. The A-10 retry (`Disabled = true`) must therefore also trigger on `SqlLimitKind.PolicyExpansionFactor` (not only on the bind limit), or the stage must account for the text budget (characterized by `RowFilterKeySetGeneratedSqlTests.LargePaddedKeyList_ExceedsTheDefaultExpansionBudgetOfAShortStatement`). (j) Observation, unchanged by WP-B: on Oracle the target alias is emitted unquoted (folded to `AUTHERIS_TARGET`) while the typed builders reference it quoted in lower case (`"autheris_target"."fk"`); the `EXISTS` and the substituted `IN` list share that spelling by design (parity), so both are affected alike if this does not resolve on Oracle. WP-C's Oracle check should confirm it.
- 2026-10-17: Phase 5 fix round for WP-B. F1 (Medium): the user-SQL and request-parameter rejection of the reserved `__rls_ks` prefix (SEC-RF107-07 e) is implemented in `GovernedSqlRewriter` and `GovernedSqlExecutor`. F2: `CorrelatedKeySetSubstitution.Substitute` refuses a site that is not structurally eligible (temporal) with `InvalidOperationException`. F3: `PolicyKeyQueryCompiler.CheckBody` returns `NotDerivable` (rule `UnqualifiedColumn`) for single-part column references. F6: `PolicyPredicate.Create` already prunes parameters that no tree node references, so the substituted predicate carries no stale names; a test now pins this. F4: the Casbin sites are two shared static instances (the consent site is still created per build because it carries per-rule settings). The nested-subquery test is renamed to the plan's mandatory name `NestedSubquery_Rejected`.
- 2026-10-17: Phase 4 WP-A fix round. (a) The stored-id fix wrongly also touched the column-rule mapping of the PostgreSQL/SQL Server consent hydration (ordinal out of range); reverted there, only row filters read the stored id. (b) `RowFilterEvaluationResolver.ResolveForRule` and `IsEligibleRule` added: a stored DENY, non-correlated or temporal rule with a strategy resolves to `Exists` (SEC-RF107-06e read side); WP-C logs the Warning. (c) The REST handlers refuse (403) a caller without a user SID instead of writing a placeholder actor. (d) Residual admin-vs-admin race: the MCP apply checks the stored `Before` before the write but the repository write is not conditional; after the write the returned `Before` is compared with the plan and a Warning is logged on mismatch (accepted, both changes are audited).
- 2026-10-17: Pre-WP-C addendum (section 13, Phase 2): decisions D-WPC-1..D-WPC-9 close the WP-B review gaps F5 (text budget `ExpansionBudget` + retry extended to `PolicyExpansionFactor`, proposed SEC-RF107-15 amendment pending Phase 3), F7 (`Substitute` takes a list and builds its own reference-equality map), F8 (key sets for `SELECT` statements only, reason `WriteStatement`) and the WP-B hand-off items; the WP-A fix-round bullet that had been appended after section 12.6 was moved here unchanged.
- 2026-10-17: Phase 3 review of the WP-C addendum (section 13.4): APPROVED WITH CONDITIONS; SEC-RF107-15 final wording (retry for `PolicyExpansionFactor`), new SEC-RF107-17..20.
- 2026-10-17: Phase 4 WP-C part 1 drift note (G-PLAN-REBASE, written before implementing). The PR #108 stub of `SingleFlightKeySetCache<T>` lives in `Sql/Policy/RowFilterKeySets/` (namespace `Autheris.Application.Sql.Policy.RowFilterKeySets`); the plan (section 4) places it in `Caching/`. It moves to `src/Autheris.Application/Caching/SingleFlightKeySetCache.cs` (namespace `Autheris.Application.Caching`, next to `BoundedCache`), because it is a generic mechanism shared with `VirtualFilterKeySetCache`; nothing referenced the stub yet.
- 2026-10-17: Phase 4 WP-C part 1 built (runtime building blocks, tested in isolation, not wired into statement execution; stage, factory, rewriter, executor, retry, instruments and DI follow in part 2). Built: `SingleFlightKeySetCache<T>` (with an optional `accept` predicate so a joiner or a raced entry never serves a stale value, SEC-RF107-04 d) and `VirtualFilterKeySetCache` on top of it (tests unchanged); `RowFilterKeySetCacheKey` (SEC-RF107-03, input record `RowFilterKeySetCacheKeyInput`, no text budget in the input, SEC-RF107-18); `KeySetValueConverter` (SEC-RF107-07 a-c, instance per FK type, output `long`/`decimal`/`string`); `RowFilterKeySetEntry`, `RowFilterKeySetCache`, `RowFilterKeySetReport`, `RowFilterKeySetErrorCodes`, the three appended fallback reasons (D-WPC-5); `RowFilterKeySetLoader` (A-11, SEC-RF107-01 b, -08, -12, -20); `IEpochValidationService.TryGetAuthoritativeEpochsAsync` in `EpochValidationService` (SEC-RF107-05); `RowFilterKeySetTextBudget` and public `CompileLimits.MinimumInputForExpansion` (D-WPC-3). Drift and additions beyond the plan text: (a) `RowFilterKeySetEntry` gains `bool NotBindable = false` (a loaded set with a value that does not fit the FK type is cached as a sentinel for the duration, like `Overflow`, so `ValueNotBindable` does not re-run the key query on every statement; the stage reports `ValueNotBindable` for it); the entry, report and error codes moved from `RowFilterKeySetContracts.cs` into the files named in section 4. (b) `RowFilterKeySetCache.GetOrLoadAsync` returns a `RowFilterKeySetCacheResult` (`Hit`/`Miss`/`Throttled`, entry, age) instead of throwing for throttling, and converts a load timeout into the same `ROW_FILTER_KEY_SET_UNAVAILABLE` denial as a failed key query, so the stage has one exception to map; a load that returns an entry of another tenant or data source fails closed. (c) Engine: `CompiledSqlBinders.BindKeyQuery(DbCommand, CompiledKeyQuery, TargetSqlDialect)` (new public method, the only new engine API; `CompileLimits.MinimumInputForExpansion` is the other) binds a key query through the production binder. Without it the loader would have had to construct a `CompiledSql` and call `CompiledSqlBinders.For`, which `CompiledSqlIsOnlyConstructedByTheEngine` and `TheCallGraphScanner_FindsTheKnownCompileAndBinderCallers` (SEC-X1-07) forbid. The loader's fixed `SET TRANSACTION` / `PRAGMA query_only` statements need a `CommandTextAllowlist` entry in `CutoverArchitectureTests` (with reason, like `SqlVirtualFilterKeySetProvider`); both changes are flagged for owner/Phase 5 review. (d) `RowFilterKeySetReport` gains `HasSubstitution` and `MarkSubstitutedSitesAsRetried(reason)` for the D-WPC-4 retry. (e) D-WPC-6 (`Substitute` taking a list) is not part of part 1 and is still open for part 2. (f) The authoritative epoch read reads Redis per table with single-key GETs through the existing `GetCurrentEpochCoreAsync` (no micro cache, no staleness budget) and treats any non-authoritative table as a failure of the whole batch.

---

## 12. Security review (Phase 3)

**Reviewer role:** `csharp-security-expert` (Phase 3), 2026-10-15. **Scope:** PRD SR-1..SR-10, this plan (A-1..A-14, C-1..C-8), verified against `7bfda713`. Ids use the form `SEC-RF107-nn` (the TrinoSqlEngine traceability test treats `SEC-<digits>` tokens in tests as legacy ids; `SEC-RF107-nn` does not match that pattern and needs no legacy catalog row).

### 12.1 Threat summary

The feature caches an **authorization artefact** (the set of FK values a correlated ALLOW filter admits) across statements and principals. The accepted trade-off is bounded staleness of the *dependent data* for opt-in ALLOW filters. Everything else must be exact: no sharing across tenants, filters or data sources; no use beyond the duration; no use when epochs are not authoritative; never for negated (DENY) or temporal filters; no keys in any output; the key query governed exactly as the `EXISTS` body. The plan's structure (substitution after admission on the compiler input only, key query derived from the registered `EXISTS` and governed by the same `PolicySubqueryTenantRewriter`, cache key over the compiled key query) is sound. The findings below close the remaining gaps; they are binding for Phase 4.

### 12.2 Binding security conditions

| Id | Severity | Threat / finding | Binding condition (owner WP) |
|---|---|---|---|
| SEC-RF107-01 | High | **Cross-principal reuse.** The cache key deliberately has no principal (A-6). That is only correct while the key query result is principal-independent. Today the subquery predicate holds consent literals only, but a future input (user attribute, session value, `PolicyParameterExpression` of another origin) would make one user's key set reusable by another. Source-native per-user RLS (R-3) is the same threat at the source: the `EXISTS` runs with the caller's `user_sid` session context, the key query with tenant only. | (a) A site is eligible only if every `PolicyParameterExpression` in its `EXISTS` body has `Origin == ParameterOrigin.Policy` and its value comes from the filter definition; otherwise fallback `NotDerivable` (exact). (b) The key query session is initialized with `userSid: null, purpose: null`; an integration test on SQL Server and PostgreSQL reads the session context inside the key query and asserts that no user identity is present, also on a pooled connection previously used by a user statement. (c) `DataSources:Connections:<name>:KeySetCacheAllowed` defaults to `false` (A-12); without it every site of the source falls back (`SourceNotEligible`). Startup logs one Information line listing the data sources with the flag on. Phase 6 documents the flag as the operator's attestation that the dependent tables are not filtered per user at the source. (B, C) |
| SEC-RF107-02 | High | **Cross-tenant reuse / key confusion.** Tenants must never share an entry (SR-3, AC-15). | Tenant value and data source are cache-key components **and** stored in `RowFilterKeySetEntry`; on every hit the stored tenant and data source are compared ordinally with the request; a mismatch is treated as a miss, the entry is removed, and a Warning (no values) is logged. The tenant bind of the compiled key query is part of the hashed binds. Test with two tenants and identical filters plus a forced-collision test (test hook inserts an entry of tenant B under tenant A's key -> miss, reload). (C) |
| SEC-RF107-03 | High | **Cache-key completeness and collisions.** Delimiter-joined strings (as in the VF key `string.Join(':', ...)`) are ambiguous when a component contains the delimiter (tenant ids, data source names); hash truncation raises collision probability. | `RowFilterKeySetCacheKey` hashes a **length-prefixed** encoding (`<len>:<bytes>` per component, UTF-8) with SHA-256 and uses the full 64 hex digits; no truncation anywhere (also not in the `MemoryCache` key). Bind values are encoded with a type tag and an invariant canonical form (`long`/`int` decimal, `decimal` invariant `G29`, strings ordinal UTF-8, `byte[]` hex, date/time ISO 8601 `O`, null as a distinct tag). Additional components beyond A-6: provider name and SHA-256 of the data source connection string (a reconfigured connection never reuses entries), and the dialect. Tests: sensitivity (changing any single component changes the key), ambiguity (`("a:b","c")` vs `("a","b:c")` differ), stability across runs and cultures (`tr-TR`). (C) |
| SEC-RF107-04 | High | **Stale keys beyond the bound; stale after revocation.** Revocation of a consent and changes of dependent governance must take effect immediately; only dependent *data* staleness is accepted. | (a) The stage runs per statement **after** `TryAdmit` (M-A1, `RequireTypedRowPolicy`, `MarkersAgree`); no key set is ever stored in a decision, the decision cache, the consent cache or the compiled-template cache (A-2). (b) `LoadedAt` is taken **before** the key query starts (conservative). The `MemoryCache` entry uses `AbsoluteExpiration = LoadedAt + effectiveDuration` (absolute, never sliding, never relative to load completion). (c) On every hit: `now - LoadedAt < currentEffectiveDuration` (strict, `TimeProvider`), else miss. (d) No stale-on-error, no refresh-ahead, no serve-stale while a reload is in flight: a waiter of an in-flight load receives the **new** result or the failure. (e) Target and dependent epochs are read once before the lookup and the same values go into the key. Tests: AC-4, AC-6, AC-12, revocation test (revoke the consent while its key set is cached -> next statement is denied/filtered by the new decision without touching the cache). (C) |
| SEC-RF107-05 | High | **Epoch unavailability.** `GetCurrentEpochsAsync` returns non-authoritative local epochs silently (section 1). | `TryGetAuthoritativeEpochsAsync` is all-or-nothing: it returns `null` when Redis is configured but not connected, when any read fails or when the pipelined MGET path cannot confirm every table; it never throws (exception -> `null` + Warning without values). `null` -> no lookup, no key query, fallback `EpochUnavailable` (exact `EXISTS`). The default interface implementation returns `null`. Tests: disconnected multiplexer, one failing key in a batch, exception path. (C) |
| SEC-RF107-06 | High | **Semantic widening by substitution.** `NOT (fk IN ...)` and `NOT EXISTS` differ for NULL FKs; a reused node instance can sit in both a positive and a negated position; a derivation error turns a correlated filter into an uncorrelated one. | (a) Polarity rule A-10: only sites reachable from the root through `AND`/`OR`/parentheses; any other ancestor -> `EXISTS`. (b) If a registered instance occurs **more than once** in the predicate tree, it is not substituted anywhere (`Deny_Negated`). (c) Derivation rules A-3 are mandatory and each has a negative test: zero or two correlation conjuncts, correlation through an expression (`dep.pk = t.fk + 0`), any remaining reference to `autheris_target` (incl. temporal), OR at the top level of WHERE, GROUP BY/HAVING/LIMIT in the body, non-column correlation operands -> `NotDerivable`. (d) The key query always carries `dep.pk IS NOT NULL`. (e) DENY, non-correlated and temporal rules are rejected with 400 on save; a directly loaded invalid combination is ignored on read (Warning). (f) Parity tests include NULL FKs, an empty key set, multi-hop filters, a DENY consent on the same table and an ALLOW consent OR-combined with a simple filter. (B, A) |
| SEC-RF107-07 | High | **Parameter type confusion and truncation.** Binding a key with the FK column type can truncate (`SqlParameter.Size`) or convert lossy (`nvarchar` -> `varchar` yields `?` and can match unrelated FK values), widening access. | (a) Pair allowlist A-9; everything else `TypeNotBindable`. (b) `KeySetValueConverter` uses checked conversions; any key that overflows, exceeds the declared length (`string.Length` > declared length for character types) or is not a canonical uuid makes the **whole site** fall back (`ValueNotBindable`); keys are never truncated, rounded or dropped. (c) If the catalog exposes collations for PK and FK and they differ, `TypeNotBindable`. (d) Keys are bound only as `PolicyParameterExpression`s (Origin `Policy`), never as literals; a test with a key value containing quotes, `--`, `;` and a NUL character asserts the compiled text contains placeholders only and `EmittedSqlInvariantChecker` passes. (e) Parameter names use the reserved prefix `__rls_ks<n>_<i>`, which user SQL cannot contain (`InternalParameterPrefix` check), and `PolicyPredicate.Create` rejects conflicts. (B, C) |
| SEC-RF107-08 | Medium | **Fail closed on key-query failure and on governance errors.** A broad `catch` that maps every exception to a fallback would turn governance violations into silent `EXISTS` (acceptable) but connection errors into fallbacks (violates SR-1); conversely, mapping a `KeyQueryNotDerivableException` to deny would be an availability bug. | Only `KeyQueryNotDerivableException` maps to fallback. A `SecurityException`/`SqlLimitExceededException` from the key-query compiler propagates unchanged (the compiler error mapper handles it, same as for the `EXISTS` form). Any exception of the loader (connection, command, read, timeout, conversion of a row) -> `GatewaySecurityException(ROW_FILTER_KEY_SET_UNAVAILABLE)` with a fixed message that names neither the dependent tables nor the rule id; inner exception attached for logs only; Warning logs the exception type only; nothing cached; partially read rows discarded; the next request retries. Caller cancellation only stops that caller's wait (shared load continues, bounded by `KeyQueryTimeout`). (C) |
| SEC-RF107-09 | Medium | **Confidentiality of keys and key sets.** Auto-generated record `ToString()`/`PrintMembers` print collections; a hash of a small key set is brute-forceable. | (a) `RowFilterKeySetEntry`, `KeySetReplacement`, `CompiledKeyQuery` and any type holding key values override `ToString()` to print counts only (INV-16 pattern of `BoundParameter`). (b) Never log, audit or emit a hash of key values; the only hash allowed in diagnostics is the first 12 hex digits of the cache-key hash (which includes tenant and SQL, not derivable from keys alone). (c) Key counts appear only in the statement audit details and the `LogGeneratedSql` Debug line; never in HTTP responses, problem details, MCP tool results, GraphQL extensions or OpenTelemetry span tags. (d) Metric tags limited to `dialect`, `outcome`, `reason`. (e) The key-query SQL may be logged at Debug under `LogGeneratedSql` (placeholders only), binds never. Log-capture test over a full statement with known key values (incl. tenant and keys) asserts none occurs in any log line or audit field. (B, C) |
| SEC-RF107-10 | Medium | **Agreement-check bypass / substitution integrity.** The registry is process-global; a writer outside the builder could mark arbitrary nodes; the stage could write its result back into decisions. | (a) `CorrelatedFilterSiteRegistry` is `internal`; `Register` is called only from `AdvancedRlsPredicateBuilder.CorrelatedExpression` (architecture test: no other call site in `src/`). (b) The stage replaces exactly the registered nodes chosen for substitution; a test compares the substituted tree with the original and asserts that all other nodes are reference-equal. (c) The stage never mutates `TableAccessDecision`, `ConsentRowFilterRecipe` or any cache; the substituted `PolicyPredicate` lives only in the per-statement `DecisionRowPolicyProvider`. (d) The compiler's coverage proof and dialect validation run unchanged on the final statement; the template-cache key is computed from the substituted policy (shape per bucket). (B, C) |
| SEC-RF107-11 | Medium | **Engine entry point (A-3).** A second compile path could miss a tenant wrapper, accept nested subqueries or emit something other than a single read-only SELECT. | `PolicyKeyQueryCompiler` must: resolve tables only through the request catalog (the statement's admitted catalog incl. dependents); apply `PolicySubqueryTenantRewriter` unchanged (nested subqueries rejected); emit a single `SELECT` (statement class asserted); run `DialectCapabilityValidator` and `EmittedSqlInvariantChecker`; prove tenant coverage for every physical table (coverage verifier or `KeyQueryCoverageVerifier`); honour `CompilerHooks` so that mutation tests can disable each control. Mutation tests (drop tenant wrapper, keep the correlation conjunct, drop `IS NOT NULL`, drop the row limit) must fail. `AstSecurityVisitor*.cs` stays unmodified; the entry point is listed for owner review at Phase 5. (B) |
| SEC-RF107-12 | Medium | **Dirty or writable key reads.** `DataSourceConnectionOptions.ReadUncommitted` sets `READ UNCOMMITTED` on every SQL Server session of the source (`SqlConnectionFactory.cs:171`): a key query would cache rows of uncommitted, later rolled-back transactions for the whole duration (the per-statement `EXISTS` only sees them momentarily). | The SQL Server key query runs `SET TRANSACTION ISOLATION LEVEL READ COMMITTED` before the query, regardless of `ReadUncommitted`. PostgreSQL and Oracle run inside `SET TRANSACTION READ ONLY` and roll back; SQLite opens the connection read-only where the factory supports it. Test per engine (unit with a recording connection; SQL Server integration asserts `transaction_isolation_level = 2` inside the key query). (C) |
| SEC-RF107-13 | Medium | **Memory and load DoS.** `MemoryCache` silently refuses an entry larger than `SizeLimit`, which would re-run the key query on every request; concurrent cold keys each hold a source connection. | (a) `EstimateBytes` counts per key (`string`: 2 x length + 40; boxed numerics 24; array and entry overhead) and the 64-char cache key. (b) An entry larger than `SizeLimitBytes / 8` is not cached as keys: the site falls back for the duration with a small overflow sentinel (`Overflow`). (c) Only bounded key types (A-9) are read; at most `MaxKeys + 1` rows. (d) New option `MaxConcurrentKeyQueries` (default 4 per node, 1..64): a new load starts only if a slot is free immediately; otherwise the statement uses `EXISTS` (fallback `LoadThrottled`, not cached). Joining an in-flight load is not throttled. (e) Cache keys are admin-defined (filters, tenants, epochs); no request input other than the tenant enters the key, so users cannot inflate the key space. (C, A for the option) |
| SEC-RF107-14 | Medium | **Admin surface.** Unauthorized or cross-tenant edits, enum/number confusion, stale or cross-kind MCP plans, missing audit. | (a) REST: `GovernanceAdmin`; rule level requires the consent's tenant to equal the caller tenant; not-found and other-tenant both return the same fixed `404` body without echoing the id; object level uses the same authorization and dataset resolution as `PUT /datasets/{id}/keys`. (b) Strict input: strategy only as the strings `Exists`/`KeySetCache`/`null` (`Enum.IsDefined`, numeric enum values rejected), integers in range, unknown properties rejected; validation repeated at MCP apply time against the current caps. (c) MCP: `PlanKind` checked at apply in both directions; plan stores the `Before` values and apply rejects when the stored settings changed since planning (optimistic concurrency); TOTP confirmation, admin and tenant binding as for access plans. (d) Each change writes a domain audit entry through the HMAC-chained audit writer with actor, tenant, target table, rule id and before/after settings (no data values) and uses the new event types (not `ConsentApplied`). (e) Repository update and epoch bump in one transaction; `InvalidateEpochAsync` after commit. (A) |
| SEC-RF107-15 | Low | **Bind-limit retry misuse.** | The retry re-runs the whole assembly from the same decisions with `Disabled = true`; it happens at most once per statement and only for `SqlLimitExceededException` of kind bind parameters; any other compiler exception propagates. (C) |
| SEC-RF107-16 | Low | **Configuration drift.** | `ValidateOnStart` enforces D-15 plus `MaxConcurrentKeyQueries` and `BindParameterReserve` ranges; `KeyQueryTimeout` <= the smallest statement timeout of WebSQL and SQL endpoints; `DefaultStrategy = KeySetCache` logs a startup Warning. Options are read per statement (`IOptionsMonitor`), so lowered caps apply on the next request (with SEC-RF107-04 c). `SizeLimitBytes` changes need a restart (documented). (A, C) |

### 12.3 Mandatory security tests (Phase 4)

All carry `[Trait("Category", "Security")]` and name their id in the test name or a comment (`SEC-RF107-nn`).

| Id | Test (project / class) |
|---|---|
| SEC-RF107-01 | `Tests.Unit/Sql/Policy/RowFilterKeySetStageTests.NonPolicyParameterOrigin_FallsBack`; `...SourceWithoutKeySetCacheAllowed_FallsBack`; `Tests.Integration/RowFilterKeySetSqlServerTests.KeyQuerySession_HasNoUserIdentity_OnPooledConnection` (+ PostgreSQL) |
| SEC-RF107-02 | `RowFilterKeySetStageTests.TwoTenants_IdenticalFilter_NeverShareEntry`; `...ForgedEntryOfOtherTenant_IsTreatedAsMiss` |
| SEC-RF107-03 | `Tests.Unit/Sql/Policy/RowFilterKeySetCacheKeyTests.EveryComponent_ChangesKey`, `...DelimiterAmbiguity_ProducesDifferentKeys`, `...IsCultureInvariant`, `...UsesFullSha256` |
| SEC-RF107-04 | `RowFilterKeySetStageTests.AgeIsMeasuredFromQueryStart`, `...LoweredDuration_ForcesReload` (AC-12), `...RevokedConsent_TakesEffectWhileKeysCached`, `...NoStaleServeWhileReloadInFlight` |
| SEC-RF107-05 | `Tests.Unit/Cache/EpochValidationServiceAuthoritativeTests` (disconnected, partial batch failure, exception); `RowFilterKeySetStageTests.NonAuthoritativeEpochs_FallBackWithoutKeyQuery` |
| SEC-RF107-06 | `Tests.Unit/Sql/Policy/CorrelatedKeySetSubstitutionTests.NegatedSite_NotSubstituted`, `...SharedInstanceInTwoPositions_NotSubstituted`, `...SiteInsideCase_NotSubstituted`; `TrinoSqlEngine.Tests/Governance/PolicyKeyQueryCompilerTests.NotDerivable_*` (one per rule A-3); `RowFilterChannelParityTests.KeySetCache_*` incl. NULL FK, empty set, DENY on same table |
| SEC-RF107-07 | `Tests.Unit/Sql/Policy/KeySetValueConverterTests` (overflow, over-length, nvarchar->varchar refused, collation mismatch, uuid canonical); `RowFilterKeySetGeneratedSqlTests.HostileKeyValues_AreBindsOnly` |
| SEC-RF107-08 | `RowFilterKeySetStageTests.LoaderFailure_DeniesWithStableCode_CachesNothing_NextRetries` (AC-11), `...CompilerSecurityException_Propagates`, `...NotDerivable_FallsBack`, `...ErrorMessage_NamesNoDependentTableOrRule` |
| SEC-RF107-09 | `RowFilterKeySetDiagnosticsTests.NoKeyValueInLogsAuditOrMetrics` (log capture incl. `LogGeneratedSql`), `...KeyHoldingTypes_ToStringPrintsCountsOnly`, `...ProblemDetails_HaveNoKeyCount` |
| SEC-RF107-10 | `Tests.Architecture/RowFilterKeySetRegistryTests.RegisterIsCalledOnlyByAdvancedRlsPredicateBuilder`; `CorrelatedKeySetSubstitutionTests.UnreplacedNodes_AreReferenceEqual`; `GovernancePolicyFactoryKeySetTests.DecisionAndRecipe_AreNotMutated`, `...DriftedDecision_DeniedBeforeStage` |
| SEC-RF107-11 | `PolicyKeyQueryCompilerTests.MutationDropsTenantWrapper_IsDetected`, `...KeepsCorrelationConjunct_IsDetected`, `...NestedSubquery_Rejected`, `...EmitsSingleSelect` |
| SEC-RF107-12 | `RowFilterKeySetLoaderTests.SqlServer_ForcesReadCommitted_EvenWithReadUncommitted`, `...PostgreSqlAndOracle_ReadOnlyTransaction_RolledBack`; `RowFilterKeySetSqlServerTests.KeyQuery_RunsReadCommitted` |
| SEC-RF107-13 | `RowFilterKeySetStageTests.OversizedEntry_FallsBackWithSentinel`, `...ConcurrencyLimit_FallsBackWithoutLoad`, `...JoiningInFlightLoad_IsNotThrottled` |
| SEC-RF107-14 | `Tests.Integration/RowFilterEvaluationEndpointAuthTests` (401/403, cross-tenant 404 identical body, numeric enum rejected, caps 400); `Tests.Unit/Governance/AccessPlanningRowFilterPlanTests.PlanKindMismatch_Rejected`, `...ChangedSincePlan_Rejected`, `...RequiresTotp`; `RowFilterEvaluationAdminServiceTests.WritesHmacChainedAuditWithBeforeAfter` |
| SEC-RF107-15 | `GovernedSqlRewriterKeySetTests.BindLimit_RetriesOnceWithExists`, `...OtherCompilerErrors_DoNotRetry` |
| SEC-RF107-16 | `RowFilterKeySetCacheOptionsValidatorTests` (all ranges, timeout relation, startup warning) |

Existing suites that must stay green unchanged: SqlGate/AST security suites (`TrinoSqlEngine.Tests` categories `Security`, `SqlGate`), `SecurityRegressionTraceabilityTests`, `VirtualFilterChannelParityTests`, `RowFilterChannelParityTests` (EXISTS variant), the `VirtualFilterKeySetCache` tests, the governance shape and least-privilege tests.

### 12.4 Residual risks (accepted, documented in Phase 6)

- Bounded staleness of dependent data for ALLOW filters (R-1), by design and opt-in.
- Per-node caches: N nodes run N key queries per duration (R-5).
- Collation equality can only be checked where the catalog records collations; elsewhere the attestation of matching PK/FK types by the administrator applies.
- `KeySetCacheAllowed` is an operator attestation; Autheris cannot detect source-native per-user RLS.

### 12.5 Verdict

**APPROVED WITH CONDITIONS.** Phase 4 may start once the developers have read sections 3 and 12. SEC-RF107-01 to -07 are release-blocking; SEC-RF107-08 to -16 are required before Phase 5 sign-off. No blocking owner question; two owner flags remain (section 9: engine entry point review, `KeySetCacheAllowed` default `false`).

### 12.6 Gate 3 checklist

- [x] SR-1..SR-10 mapped to conditions (SR-1 -> 08; SR-2 -> 11; SR-3 -> 02/03; SR-4 -> 04/05; SR-5 -> 06/07; SR-6 -> 11/12; SR-7 -> 13; SR-8 -> 09; SR-9 -> 14; SR-10 -> 01).
- [x] Every condition has at least one mandatory test.
- [x] `AstSecurityVisitor.cs` not modified by the plan.
- [x] No upgrade migration; baseline extended in place.

---

## 13. Addendum before WP-C (2026-10-17)

**Author:** `csharp-architect` (Phase 2 addendum). **Input:** WP-B Phase 5 review findings F5, F7, F8 and the WP-B hand-off (changelog 2026-10-17, WP-B (h)-(j)). These decisions are binding for WP-C; where they touch section 12 they are marked.

### 13.1 Decisions

**D-WPC-1 Statement context for the stage (contract addition to C-8).** `GovernedPolicyInput` gains `RowFilterKeySetStatement? KeySetStatement { get; init; }` with `sealed record RowFilterKeySetStatement(bool IsRead, int UserSqlLength, int MaxExpansionFactor, bool Disabled)`; `RowFilterKeySetContext` gains the same three values next to `Disabled`. `GovernedSqlRewriter.RewriteCoreAsync` fills it (`IsRead = metadata.StatementType == SqlStatementType.Select`, `UserSqlLength = rawSql.Length`, `MaxExpansionFactor` = the value `CompileRequestFactory.Create` puts into the request after `CompileLimits.Normalize` clamping, read from one shared helper so both cannot drift). `KeySetStatement == null` (any other factory caller) means the stage is not invoked (AC-8 behaviour).

**D-WPC-2 Key sets apply to read statements only (F8).** If `IsRead` is false (INSERT, UPDATE, DELETE, and any future statement type), the factory does not call the stage; every registered site of the statement is reported `Fallback` with the new reason `WriteStatement` (no epoch read, no cache lookup, no key query). Rationale: a substituted IN list on a DML target passes through `InsertCheckPolicy.Rewrite` and becomes byte-exact equality, which can differ from the collation-aware `EXISTS`; skipping is simpler than proving parity and is the status quo. The positive test (`== Select`) is fail-safe for unknown statement types.

**D-WPC-3 Up-front text budget (F5 a).** New pure helper `RowFilterKeySetTextBudget` (Application, `RowFilterKeySets/`):
- `Limit(userSqlLength, factor) = factor * max(userSqlLength, CompileLimits.MinimumInputForExpansion)`; the private engine constant `GovernedSqlCompiler.MinimumInputForExpansion` (64) moves to `public const int CompileLimits.MinimumInputForExpansion` and the compiler uses it (one-line engine change, no behaviour change).
- `KeyListBudget = Limit / 2` characters for all IN lists of the statement together; the other half is the reserve for the rewritten user statement, tenant wrappers, masks and the remaining policy text. Fixed constant, no option (the expansion factor itself, SEC-ADG-05, is not relaxed).
- `EstimateListChars(dialect, siteOrdinal, paddedCount, fkColumnName)`: an **upper bound** of the emitted text of one IN leaf: per item `1 + len("__rls_ks") + digits(ordinal) + 1 + digits(index) + 2`, per IN chunk (Oracle: one per 1,000 items, joined with ` OR `) `2 * (len(alias) + len(fk) + 6) + 16`. A test pins the bound against the real emitter.
- Pre-load gate: before cache lookup or key query, a site whose 1-key list (`paddedCount = 1`) does not fit the remaining `KeyListBudget` falls back with reason `ExpansionBudget` (no load). Loads always use the configured `MaxKeys + 1` row limit; the budget never enters the cache key (entries stay statement-independent).
- Post-load fit: after all sites are resolved, the stage applies one combined loop over the bind budget (A-10) and `KeyListBudget`: while either is exceeded, the site with the largest padded list falls back, reason `ParameterBudget` if the bind budget is exceeded at that moment, else `ExpansionBudget`. A fallback for budget reasons is not cached and does not touch the cached entry. Ties are broken by site ordinal (deterministic).
- Consequence (documented in Phase 6): under the default factor 64 a short statement (`<= 64` characters, limit 4,096) carries at most about 100 keys (bucket 64); larger key sets of such statements stay `EXISTS`, visible as `fallbacks{reason="ExpansionBudget"}`.

**D-WPC-4 Retry extended to the expansion limit (F5 b, safety net).** The one-time retry with `Disabled = true` triggers on `SqlLimitExceededException` whose `Kind` is `BindParameters` **or** `PolicyExpansionFactor`, and only when the first assembly substituted at least one site (`report` holds a `Hit` or `Miss`); otherwise the exception propagates (the `EXISTS` form would fail identically). The retry re-runs `CreateAssemblyAsync` from the same decisions; with `Disabled` the stage reads no epochs, cache or loader and substitutes nothing, so the retried statement is byte-identical to the strategy-`Exists` statement and passes every compiler check unchanged (coverage, capability validation, invariant checker, both limits). A second exception of any kind propagates. The final report replaces each previously substituted site's outcome by `Fallback` with reason `BindLimitRetry` or the new `ExpansionLimitRetry`. `EmittedSqlLength` and `InListItems` are not retry triggers (absolute limits far above `MaxKeys` lists; Oracle chunking covers IN items).

**D-WPC-5 New fallback reasons.** `KeySetFallbackReason` gains `ExpansionBudget`, `ExpansionLimitRetry`, `WriteStatement` (appended, existing values unchanged). Metric tag `reason` and audit field `row_filter_key_sets[].reason` carry the enum name verbatim (`ExpansionBudget`, `ExpansionLimitRetry`, `WriteStatement`); same confidentiality rules as SEC-RF107-09.

**D-WPC-6 `Substitute` builds its own reference-equality map (F7).** Signature change: `Substitute(PolicyPredicate predicate, IReadOnlyList<(ExistsExpression Node, KeySetReplacement Replacement)> replacements, TargetSqlDialect dialect)`. `Substitute` constructs the `Dictionary` with `ReferenceEqualityComparer.Instance` itself and throws `InvalidOperationException` when the same node instance appears twice. Callers can therefore not collapse two structurally equal `ExistsExpression` records through a default-comparer dictionary. WP-B tests are adapted to the list form.

**D-WPC-7 Site ordinals and substitution errors.** The stage assigns ordinals `0..n-1` across the whole statement before any fallback decision: tables in ordinal order of their `TableIdentity` string, sites in `Scan(...).Eligible` pre-order. Ordinals of sites that later fall back are not reused, so parameter names (and the template-cache shape) do not depend on other sites' outcomes. `InvalidOperationException`/`ArgumentException`/`PolicyConflictException` from `Substitute` are programming errors: the stage has no catch for them (only `KeyQueryNotDerivableException` maps to fallback, SEC-RF107-08) and they propagate to the rewriter's error mapping (statement rejected, fail closed).

**D-WPC-8 Loader binds and the cache key.** `RowFilterKeySetLoader` binds **every** element of `CompiledKeyQuery.Parameters` by name in array order, regardless of `Origin` (the SQLite row limit is a compiler-generated `QueryLiteral` bind; SEC-RF107-01 a applies to the `EXISTS` body and is enforced by the compiler). `RowFilterKeySetCacheKey` hashes the same full array (name, type, origin, canonical value). The loader asserts that the command's parameter count equals `Parameters.Length` before executing (mismatch -> `ROW_FILTER_KEY_SET_UNAVAILABLE`).

**D-WPC-9 Operator diagnostics.** (a) Startup: a hosted `RowFilterKeySetStartupReporter` logs one Information line `Row-filter key-set cache allowed on data sources: {DataSources}` (names sorted ordinally, `none` when empty), SEC-RF107-01 c. (b) Read-side ineligible settings: the stage logs one Warning per rule id (no values, process-wide dedupe set capped at 4,096 ids) when a registered site that is excluded (negated/shared) or not structurally eligible (temporal) carries non-null `RuleSettings`, and the existing `InvalidStoredSetting` Warning when `RowFilterEvaluationResolver.HasInvalidStoredValue` is true for the rule or object level. Non-correlated rules create no site and therefore have no runtime effect; they are rejected on save (WP-A). (c) Oracle alias spelling (WP-B (j)) is verified by a WP-C test, see 13.3.

### 13.2 Condition changes

- **SEC-RF107-15, proposed amendment, needs Phase 3 confirmation:** "The retry re-runs the whole assembly from the same decisions with `Disabled = true`; it happens at most once per statement, only for `SqlLimitExceededException` of kind `BindParameters` or `PolicyExpansionFactor`, and only when the first assembly substituted at least one site. With `Disabled` the stage performs no epoch read, cache lookup or key query and substitutes nothing. Any other compiler exception, and any exception of the retried compile, propagates."
- A-10 budget paragraph is extended by D-WPC-3 (text budget next to the bind budget); C-8 contract additions per D-WPC-1, D-WPC-5, D-WPC-6. No other condition changes; D-WPC-2 and D-WPC-3 only add fallbacks to the status quo `EXISTS`.

### 13.3 Additional mandatory WP-C tests

| Decision | Test (project / class) |
|---|---|
| D-WPC-2 | `GovernancePolicyFactoryKeySetTests.WriteStatement_StageNotInvoked_ReportsWriteStatement` (INSERT, UPDATE, DELETE; loader and epoch fakes record zero calls) |
| D-WPC-3 | `RowFilterKeySetTextBudgetTests.Limit_MatchesCompilerFormula` (asserts against a real `PolicyExpansionFactor` exception's `Maximum`), `...EstimateIsUpperBound_PerDialect` (SQL Server, PostgreSQL, Oracle, SQLite; counts 1, 64, 1,000, 1,001, 2,000; ordinals 0 and 10); `RowFilterKeySetStageTests.ShortStatement_LargeKeySet_FallsBackExpansionBudget_NotCached`, `...PreLoadGate_SkipsKeyQuery`, `...CombinedBudget_DropsLargestFirst_ReasonPerConstraint`; the WP-B characterization `LargePaddedKeyList_ExceedsTheDefaultExpansionBudgetOfAShortStatement` stays and a pipeline twin `ShortStatement_2000Keys_CompilesWithExists` asserts no exception through the rewriter |
| D-WPC-4 | `GovernedSqlRewriterKeySetTests.ExpansionLimit_RetriesOnceWithExists` (retried SQL byte-identical to strategy `Exists`), `...RetryOnlyWhenSiteSubstituted`, `...SecondLimitException_Propagates`, `...OtherLimitKinds_DoNotRetry` (e.g. `EmittedSqlLength`), `...RetryReportsExpansionLimitRetry`; all `[Trait("Category", "Security")]` under SEC-RF107-15 |
| D-WPC-6 | `CorrelatedKeySetSubstitutionTests.TwoStructurallyEqualSites_BothSubstituted_DistinctOrdinals`, `...DuplicateNodeInList_Throws` |
| D-WPC-7 | `RowFilterKeySetStageTests.SiteOrdinals_UniqueAcrossTables_StableUnderFallback`, `...SubstitutionProgrammingError_Propagates` (internal seam throws `InvalidOperationException`) |
| D-WPC-8 | `RowFilterKeySetLoaderTests.Sqlite_BindsRowLimitParameter_AndAllCompiledParameters`, `RowFilterKeySetCacheKeyTests.RowLimitBind_IsHashed` |
| D-WPC-9 | `RowFilterKeySetStartupReporterTests.LogsAllowedSourcesOnce_SortedOrNone`; `RowFilterKeySetStageTests.IneligibleStoredSetting_WarnsOncePerRule_NoValues`, `...InvalidStoredSetting_Warns`; `Tests.Integration/RowFilterKeySetOracleTests.TargetAliasSpelling_InListAndExistsResolve` (Oracle container: the quoted `"autheris_target"` reference resolves against the emitted alias for both forms; if it does not, it is a pre-existing `EXISTS` defect fixed in the dialect generator for both forms, not by WP-C special-casing) |

### 13.4 Phase 3 review of the addendum

**Reviewer role:** `csharp-security-expert` (Phase 3), 2026-10-17. **Verified against the code:** `GovernedSqlCompiler.cs` step 10 (limit = `MaxExpansionFactor * max(rawSql.Length, 64)`, thrown before the template is cached, so a failed key-form compile caches nothing), `CompileLimits.Normalize` (clamp 256), `CompileRequestFactory.Create` (does not set the factor; the compiler default 64 applies, no option governs it), `GovernedSqlRewriter` (`isDml` and the non-`Select` rejection derive from the same `metadata.StatementType`; there is no retry yet), `RowFilterKeySetContext.Disabled`.

**Verdict: APPROVED WITH CONDITIONS.** No decision weakens a check. D-WPC-2/3/5/6/7/9 only add fallbacks to the status-quo `EXISTS` or fail closed. D-WPC-8 is required for correctness and strengthens the cache key.

**Findings**
- **Retry (D-WPC-4).** The retried statement is the strategy-`Exists` statement, so it runs every control the baseline runs (coverage, capability validation, invariant checker, both limits); nothing is weaker. It is confirmed with two additions. (a) The retry must start from the original decisions and the unsubstituted `PolicyPredicate`, never from the per-statement provider of the first assembly. (b) The first exception carries `Actual` (emitted length) and `Maximum`; `Actual` is a function of the padded key count and therefore key-set size. It is swallowed, and nothing may log or audit its values, only the kind (SEC-RF107-09).
- **DoS (SEC-RF107-13).** A key load is cache-key bound (admin-defined filters, tenant, epochs) and its result is cached independent of the statement; the budget never enters the cache key and never evicts. A budget fallback after a load therefore discards nothing, and an attacker cannot force repeated loads by varying statement length or text. Loads stay bounded by one per cache key and duration plus `MaxConcurrentKeyQueries`. The pre-load gate (1-key list) lets a load start for nearly every statement; that is acceptable, because the load is amortised across all later statements. The retry costs at most one extra compile (no extra load) and happens only after a substituted site, so the worst case is a 2x compile cost per statement, bounded by the compile timeout; a retry that fails identically (the `EXISTS` form is also too large) is the baseline error.
- **Statement/compiler gap.** `IsRead` (`== Select`) and `isDml` come from one classification; the only other admitted type is DML. A statement the stage treats as read but the compiler parses as write would be rejected by `StatementPermissions.ReadOnly`; a mismatch can only fail closed. Keep the stage input and `isDml` on the same variable. A text-length drift between stage and compiler fails safe via the retry.
- **Side channel.** `ExpansionBudget` versus a hit reveals coarse key-set size relative to statement length to anyone who can see reasons (audit, metrics) and, through latency, to the statement author. Reasons stay out of every client-facing surface (SEC-RF107-09 c); the timing difference is accepted (residual risk, size is already bucketed).
- **Raising the expansion factor.** With the default 64, a statement of at most 64 characters carries about 100 keys. No option governs the factor: `CompileRequestFactory` leaves the `CompileRequest` default (64), and `CompileLimits.MaxExpansionFactorLimit` clamps to 256. Raising it is safe only as a **new validated server option** (for example `WebSql.MaxExpansionFactor`, ceiling 256, read once per statement, never a request or user value); it relaxes SEC-ADG-05 for every statement, not only key-set statements, so it needs an owner decision and is not part of WP-C. Preferred remedies stay: leave the fallback, or raise `MaxKeys` only together with the factor.

**SEC-RF107-15, final binding wording (replaces the text in 12.2 and the amendment in 13.2):** "The retry re-runs the whole assembly from the original decisions and the unsubstituted policy predicate with `Disabled = true`; it happens at most once per statement, only for `SqlLimitExceededException` of kind `BindParameters` or `PolicyExpansionFactor`, and only when the first assembly substituted at least one site (`Hit` or `Miss`). With `Disabled` the stage performs no epoch read, cache lookup or key query and substitutes nothing, so the retried statement is the strategy-`Exists` statement and passes every compiler check unchanged. The first exception's values (`Actual`, `Maximum`) are never logged, audited or surfaced (kind only). Any other compiler exception, and any exception of the retried compile, propagates unchanged."

**New binding conditions**

| Id | Severity | Condition |
|---|---|---|
| SEC-RF107-17 | Medium | `IsRead` is `metadata.StatementType == Select`, computed from the same value as `isDml`; the stage is invoked only when `IsRead` is true and `request.Statements` is `ReadOnly`. A write, unknown type or `KeySetStatement == null` reports `WriteStatement` or no stage and never reads epochs, cache or loader. (C) |
| SEC-RF107-18 | Medium | The text budget is a pure function of `userSqlLength` and the normalized factor. It never enters the cache key, never changes, evicts or caches an entry, and a budget fallback is never cached. Its reasons (`ExpansionBudget`, `ParameterBudget`, `ExpansionLimitRetry`, `BindLimitRetry`, `WriteStatement`) and the key counts are limited to audit details, metric tags and Debug logs (SEC-RF107-09 c, d). `EstimateListChars` must be an upper bound per dialect (pinned by test), so the retry stays a safety net. (C) |
| SEC-RF107-19 | Low | The retried compile uses the same `CancellationToken` and statement deadline; the retry never loads keys, and a failure of the retried compile is not retried again. (C) |
| SEC-RF107-20 | Low | The loader binds every `CompiledKeyQuery.Parameter` (including the SQLite `QueryLiteral` row limit), checks the command parameter count, and the cache key hashes the same full array (D-WPC-8). A mismatch is `ROW_FILTER_KEY_SET_UNAVAILABLE`, not a fallback. (C) |

**Additional mandatory tests** (`[Trait("Category", "Security")]`, id in name): SEC-RF107-15 `GovernedSqlRewriterKeySetTests.RetryStartsFromUnsubstitutedPredicate`, `...FirstExceptionValues_NotInLogsOrAudit`; SEC-RF107-17 `GovernancePolicyFactoryKeySetTests.UnknownStatementType_StageNotInvoked` and the D-WPC-2 test of 13.3; SEC-RF107-18 `RowFilterKeySetStageTests.BudgetFallback_DoesNotEvictOrChangeCachedEntry`, `...VaryingStatementLength_LoadsOncePerCacheKey` (loader fake counts one call across statements of different lengths), `RowFilterKeySetDiagnosticsTests.BudgetReasons_NotInProblemDetails`; SEC-RF107-19 `GovernedSqlRewriterKeySetTests.Retry_UsesSameToken_NoKeyLoad`; SEC-RF107-20 the D-WPC-8 tests of 13.3 plus `RowFilterKeySetLoaderTests.ParameterCountMismatch_Unavailable`. Existing 13.3 tests stand.
