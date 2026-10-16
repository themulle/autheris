# F-DIALECT-02: Governed AST SQL Compiler (DQL and DML)

Supersedes [F-DIALECT-01](f-dialect-01-ast-target-dialect-pushdown.md) and the SQL plan cache of [F-PERF-11](f-perf-11-query-plan-cache.md). Implementation: [`src/TrinoSqlEngine`](../../src/TrinoSqlEngine) (entry point `ISqlEngine.Compile`) and the governed pipeline in [`src/Autheris.Application/Sql`](../../src/Autheris.Application/Sql). Decision record: [ADR-017 amendments](../adr/ADR-017-distributed-state-ast-generator-and-rbac.md).

## 1. Executive Summary

WebSQL users write SQL in Trino syntax. The gateway runs that text on PostgreSQL, SQL Server, SQLite and Oracle and guarantees that every statement only touches the rows and columns the caller may see or change.

Every governed SQL statement takes **one code path**: the AST compiler. There is no second engine, no runtime switch and no shadow mode. The compiler parses the statement into an immutable typed syntax tree, injects typed tenant, row-policy and mask nodes, proves the coverage, emits dialect text and returns a `CompiledSql`: the SQL text plus a typed list of bound parameters. Every value (user literals, tenant, policy and mask values) reaches the database as a bind parameter.

| Aspect | Behavior |
|---|---|
| Representation | Immutable typed AST |
| Values | Every value is a bind parameter |
| Policies and masks | Typed predicate and mask nodes (`IRowPolicy`, `MaskSpec`), parsed once, fail-closed |
| Proof of coverage | `SecurityCoverageVerifier` re-proves every secured scope on each compile, independent of the injector |
| Output check | Dialect-aware text check of the emitted SQL |
| Dialect limits | One capability table (bind count, IN-list size, identifier length) |
| Statements | SELECT, INSERT, UPDATE, DELETE, MERGE with check-option semantics |
| Result | `CompiledSql` (SQL text plus typed `BoundParameter` list), bound by a dialect binder |
| Errors | Stable codes and fixed messages through one mapper (`SqlCompilerErrorMapper`) |

## 2. Architecture

### 2.1 Governed pipeline

Every SQL-text entry point converges on `GovernedSqlRewriter` and `GovernedSqlExecutor` (`src/Autheris.Application/Sql/Services/`):

```
GovernedSqlRewriter
  Stage 1  AnalyseAndStatementPolicy   parse, statement class, function security
  Stage 2  IdentityResolution          user, tenant, roles, data source and dialect admission
  Stage 3  GovernanceResolution        TableAccessDecision per table (typed IRowPolicy, MaskSpec), keyed by TableIdentity
  Stage 4  RewriteOptions              GovernancePolicyFactory -> GovernedPolicyInput -> CompileRequestFactory.Create -> CompileRequest
  Stage 5  SecureRewrite               ISqlEngine.Compile(sql, request, ct) -> CompiledSql
  -> GovernedRewrite (Compiled, accessed tables, data source, delivered row limit, virtual filters)
GovernedSqlExecutor
  CompiledSqlAuditDigester.EnsureKey()                    (DML: before any lease, connection or command)
  ISqlConnectionFactory.CreateOpenConnectionAsync(...)    (session pins, Oracle BindByName wrapper)
  DbSessionContextInitializer.InitializeSessionAsync(...) (tenant context)
  binder dialect == CompiledSql.Dialect, else fail closed before any connection or command is created
  IProviderExecutionGuard wraps read and DML              (SQLite: statement interrupt at the timeout)
  SELECT: CompiledSqlBinders.For(dialect).Bind(command, compiled, clientValues); ExecuteReader
  DML:    CompiledSqlBinders.CheckedExecutorFor(dialect).ExecuteInTransactionAsync(tx, compiled, clientValues, limits, ct)
  DbException: timeout/unavailable classification first, then DmlErrorSanitizer.Map(dialect, ex)
```

- **Stage 3 only decides.** Row policies are typed (`IRowPolicy`), masks are `MaskSpec` values; no policy is rendered to SQL text on this path.
- **Stage 4** builds the typed `GovernancePolicy` (row filters, masks, catalog from `IGovernedCatalogProvider`, tenant binding, the function policy) and the `CompileRequest`. A missing policy factory fails with `SqlCompileConfigurationException`.
- **Stage 5** calls `ISqlEngine.Compile`. Engine exceptions propagate unwrapped to the error mapper.
- `DbCommand.CommandText` on the governed path is assigned only inside `DbCommandCompiledSqlBinder`.
- Validation-only callers (Arrow Flight SQL in `ArrowFlightSqlServer`, `ISqlRewritePipeline`, `IFederatedQueryExecutionService`) use `ValidateAsync`, which runs the same stages and returns no SQL text.

**Governed entry points.** WebSQL (JSON, NDJSON, Parquet), WebSQL async statements (`/v1/statement`), SQL endpoints, async query jobs, Arrow export, Arrow Flight SQL, the MCP SQL tool, the MCP dataset tools, REST datasets and the SQL table reads of OData, MCP, GraphQL flat reads, connectors and batch relations (`SqlTableReadExecutor`). The architecture test `GovernedEntryPoints_ReachCompileAndBinder` proves that each reaches `Compile` and the binder.

**Governed table reads.** The reads of SQL tables that are not written as SQL text (OData, MCP dataset tools, GraphQL flat reads, `SqlConnector`, batch relations) are described by a `TableReadIntent` (table, columns, neutral filter, order, limit, offset). `IGovernedTableReadPlanner` authorizes the columns against the `TableAccessDecision`, rejects filter and order columns that are masked or denied (oracle-inference guard), and formulates Trino text with named client parameters; a paged read without an explicit order uses the primary key when all its columns are clear, otherwise all clear-text columns of an orderable type in catalog order. `SqlTableReadExecutor` (the `IDataSourceExecutor` of `DataSourceType.Sql`, scoped) hands that text to `IGovernedSqlExecutionService.ExecuteQueryBufferedAsync`, so tenant, row policy, masks, limits, dialect admission (`SQL_DIALECT_NOT_SUPPORTED` for Experimental dialects such as Databricks) and the compiled-SQL audit digest come from the single compiler path. The executor reports `RlsPushdownExecuted` and `InDbColumnMaskingExecuted` so that callers neither filter nor mask twice, refuses a non-canonical ClusterAdmin claim before the read (`GovernedQueryStages`), fails closed with `UNSUPPORTED_COLUMN_TYPE` for spatial columns and with `ROW_LIMIT_EXCEEDED` when the row cap is below the requested page. The architecture test `PlannerOutput_NeverReachesACommand` guarantees that planned text is never assigned to a `DbCommand`.

Behavior of this path: a table read counts against the WebSQL row cap (`WebSql.MaxAllowedRows`) and compile limits; errors use the `SqlCompilerErrorMapper` codes; each read writes the governed `WEBSQL_QUERY` audit entry in addition to the table-query entry of the calling service; there is no synthetic-data fallback. `SqlConnector` requires an `IColumnMaskingProvider` and resolves the executor in its own DI scope for every read.

**Outside `Compile`.** The GraphQL tree compiler (`TreeSqlCompiler`) is the single documented exception to `ISqlEngine.Compile` for governed SQL reads: it builds one JSON-aggregating statement per selection tree (SQL Server `FOR JSON PATH`, PostgreSQL `jsonb_agg`, SQLite `json_group_array`), a shape the AST compiler does not produce. It renders its predicates and masks from the same typed policy model through `PolicyPredicateRenderer`, uses no legacy API and must not reach the binder. `TreeSqlCompiler.SupportsTreeCompile(dialect)` names the dialects it supports (SQL Server, PostgreSQL, SQLite); tables of other dialects (Oracle) are not exposed in the GraphQL schema (every root query runs through the tree compiler) and stay readable through OData, REST and MCP. The test `TreeVsCompilePolicyParityTests` guards the exception: for the same tenant, row filter and mask, the tree compiler and `Compile` expose the same rows and the same masked values. The other listed exceptions are the OLAP table reads and the federated staging reads. Federated (cross-source) plans run on DuckDB with bound parameters (`CrossSourcePlanner` produces a `CompiledSql`).

### 2.2 Compiler passes

`ISqlEngine.Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken)` runs these passes (implemented in `GovernedSqlCompiler`):

```
token guards -> parse -> AST build -> validate -> simplify (user tree only) -> secure (inject typed predicates and masks)
  -> coverage verify -> capability check -> emit -> emitted-text check -> bind (at execution)
```

1. **Gates.** The dialect must have a capability entry and be `Production` (or the request sets `AllowExperimentalDialect`, which an architecture test forbids in production assemblies). The statement classes must be listed in `CompileRequest.Statements` and in the dialect's `DmlStatements`. `CompileRequest.Policy.Dml` must equal `DmlGuardOptions.Strict`, otherwise `SqlCompileConfigurationException`. `CompileLimits.Normalize` bounds `CompileTimeout` (default 2 s, maximum 30 s) and `MaxExpansionFactor` (default 64, maximum 256) and forces the variable-substitution token guard for Databricks.
2. **Token guards** (`SqlTokenSecurityOptions`, set by `CompileRequestFactory`): comments, backslashes and escaped literals in strings, dollar quoting (every dialect), bracket lexer differentials (`[`, `]` and bracket or comment sequences inside strings and quoted identifiers; SQL Server and SQLite), non-ASCII identifiers, dots in quoted identifiers, time-travel clauses and reserved `autheris_` identifiers. `CompileRequest.Normalize` forces the `${...}` variable-substitution guard for Databricks. `[` is rejected on SQL Server and SQLite only; on PostgreSQL it is array syntax (`tags[1]`, `ARRAY[...]`). Oracle has no array type: array constructors and subscripts are rejected with a typed not-supported error (422 `ConstructNotSupported`), as on SQL Server.
3. **Parse** (ANTLR, Trino grammar) with limits: query length 65,536 characters, nesting depth 100, parse-tree depth 3,000.
4. **AST build and validate.** Function calls are checked against the dialect function map; a qualified or delimited function name (`"lower"(x)`, `"pg_catalog".lower(x)`) is rejected. CAST targets come from a closed type set (`boolean`, `tinyint`, `smallint`, `integer`, `bigint`, `real`, `double`, `decimal`/`numeric`, `varchar`, `char`, `text`, `varbinary`, `date`, `time`, `timestamp` with optional time zone). Identifiers starting with `autheris_` are reserved (`SQL_RESERVED_IDENTIFIER`).
5. **Simplify** runs on the user tree before injection, so security predicates never exist while the simplifier runs.
6. **Secure.** Tenant, row-policy and mask nodes are injected as opaque `SecurityPredicateExpression` / `MaskExpression` nodes into the `WHERE` of a secured derived table around every physical table reference, in every scope (subqueries, CTE bodies, set operations, `MERGE` source and target, policy subqueries).
7. **Coverage verify** (`SecurityCoverageVerifier`) walks the final tree and proves the same coverage independently. A failure throws `SecurityCoverageException` and nothing is cached.
8. **Capability check** (`DialectCapabilityValidator`): constructs, functions, IN-list size, identifier length, bind count.
9. **Emit** through the dialect generator. Only validated structural integers (row limits, ordinals, frame offsets, type parameters) and keywords are emitted inline. `EmittedSqlInvariantChecker` then scans the text with a dialect-aware lexer and rejects stray quotes, comments, semicolons (except the registered trailing `;` of a SQL Server `MERGE`), dollar quotes and unregistered numeric tokens. The output must not exceed `MaxExpansionFactor` times the input length (minimum basis 64 characters) and 1 MiB.
10. **Bind** is performed by a dialect binder at execution (section 2.5).

Every pass observes the compile budget and the caller's cancellation token. A timeout raises `SqlLimitExceededException(CompileTime)`.

### 2.3 Value-free template cache

`CompiledSqlTemplateCache` (inside the engine, `FastSqlEngine.CompileCache`) stores verified compile results as templates that contain no tenant, policy or mask values. It is the only cache of compiled SQL; the gateway has no separate plan cache.

- **Key.** `CompileCacheKey.Material` serializes the whole `CompileRequest` canonically (every property takes part; an unknown property type throws) plus the compiler version (`ast-compiler/1.0`), the capability table version (`cap-6`), the engine query-length limit and the SQL text. The cache key is the SHA-256 of that material; a hit additionally compares the full material with ordinal equality. Row filters, masks and the catalog are not serialized into the key: they are validated per table on a hit (policy shape and catalog fingerprints). The tenant value is never part of the key.
- **Values.** A template holds the SQL text and slot descriptors. On a hit the compiler re-validates every table dependency and rebinds the current tenant, policy and mask values (`Rebind`).
- **Insertion.** Only verified output is cached; a failed coverage proof or text check never inserts an entry.
- **Capacity.** 512 entries, first-in-first-out eviction. The capacity is not configurable.

Policy expressions are parsed once and cached by `PolicyExpressionParser` (`PolicyParseCacheOptions`).

### 2.4 Typed policy IR and masks

`ITableCatalog` supplies the physical tables and columns (`CatalogColumn.Name`, `DataType`, `Collation`). The gateway fills it from database metadata through one `IColumnMetadataReader` per dialect (`GovernedCatalogProvider`). A row policy is a `PolicyPredicate`: an expression tree with named parameter nodes, the values by name, a SHA-256 fingerprint of the shape and the referenced columns. A table without access gets the deny-all predicate (`1 = 0`), never null.

Tables read by a policy subquery (dependent tables) are governed like primary tables: the governance record must belong to the statement's data source, be active and satisfy `DataSources:RequireTenantColumn` (with `TenantColumnExemptTables`); otherwise the policy's own table is denied.

`MaskSpec` kinds: `Nullify`, `Redact`, `PartialMask`, `Hmac`, `GeoJitter`, `Constant`. All mask arguments except the rounding scale are bound parameters. Keyed HMAC is computed inside the database on SQL Server, PostgreSQL and SQLite (the `gateway_hmac_sha256` UDF registered by `SqlConnectionFactory`); on DuckDB, Oracle and Databricks (`InDbHmac = false`) it degrades to `Redact`.

### 2.5 Binders and checked execution

`CompiledSql` carries the text, the ordered `BoundParameter` list (marker, name, ordinal, value, `SqlParameterType`, `ParameterOrigin`, catalog column type), the dialect, the statement class, the applied predicate ids and the compiler version. `BoundParameter.ToString()` never prints the value. Only the engine assembly constructs a `CompiledSql` (enforced by an IL scan).

`CompiledSqlBinders` is a closed set of stateless binders, not resolved through DI. `For(dialect)` returns the provider binder; `CheckedExecutorFor(dialect)` returns the checked executor. Both throw `SqlCompileNotSupportedException(Dialect)` for a dialect without a binder (Snowflake, ANSI).

`DbCommandCompiledSqlBinder` is the ADO.NET base class of the binders. It refuses a statement that needs a row-count check, before it touches the command, with `CheckedExecutionRequiredException` (`DML_CHECKED_EXECUTION_REQUIRED`). The refusal triggers on `CompiledSql.RequiresRowCountCheck` and, as defense in depth, on the reserved alias `autheris_ins` anywhere in the SQL text.

`CheckedDmlExecutor.ExecuteInTransactionAsync` runs every governed DML statement inside the session transaction: it binds, executes, enforces the check option (`DmlCheckOption.Enforce`) and then `WebSql:MaxAffectedRows`, commits on success and rolls back on any exception, any count difference or an unreported count (`-1`). A transaction of another connection is rejected. Exceeding `MaxAffectedRows` raises `DmlAffectedRowsLimitException` (`DML_AFFECTED_ROWS_LIMIT`, no counts in the message).

## 3. Dialects

### 3.1 Compiler tiers and WebSQL admission

| Dialect | Compiler tier | Admitted to the governed path | Notes |
|---|---|---|---|
| PostgreSQL | Production | yes | Session pin `search_path` |
| SQL Server | Production | yes | Session options pinned |
| SQLite | Production | yes | Statement interrupt at the timeout; HMAC via UDF |
| Oracle | Production | yes | `BindByName`, TCPS, NLS pins; driver license (section 9) |
| DuckDB | Production | no (not a data source dialect) | Engine of federated (cross-source) plans |
| Databricks | Experimental | no | Compiles only with `AllowExperimentalDialect`; not executable through WebSQL |
| Snowflake, ANSI | none | no | No capability entry: `Compile` throws `SqlCompileNotSupportedException(Dialect)` (501 `SQL_DIALECT_NOT_SUPPORTED`); the generators are reachable only from tests |

Admission (`SqlDialectMapper.IsExecutable`) is derived, not listed: a dialect is executable when it maps to an AST dialect, `SqlConnectionFactory` can open a connection for it and its capability entry has the tier `Production`. The admitted set is exactly {PostgreSQL, SQL Server, SQLite, Oracle} (pinned by the test `Admission_ExactSet`). Because a tier change admits a dialect, `DialectCapabilityTable.cs` is under security CODEOWNERS. `ToTargetDialect` throws for an unmapped dialect (never a default). Rejection messages list the admitted dialects from the table. Tables of a data source whose dialect is not admitted appear in the startup report with the code `DIALECT_NOT_ADMITTED` (section 6.3).

For Databricks, `CheckedExecutorFor(Databricks)` is non-transactional: it refuses `RequiresRowCountCheck` and refuses any DML while `WebSql:MaxAffectedRows > 0`.

### 3.2 Statement matrix

| Dialect | SELECT | INSERT | UPDATE | DELETE | MERGE |
|---|---|---|---|---|---|
| SQL Server | yes | yes | yes | yes | yes (one trailing `;`) |
| PostgreSQL | yes | yes | yes | yes | yes |
| SQLite | yes | yes | yes | yes | no |
| Oracle | yes | yes | yes | yes | at most one `UPDATE` and one `INSERT` clause; a `DELETE` clause or a repeated clause kind is rejected |
| DuckDB | yes | yes | yes | yes | yes |
| Databricks (Delta) | yes | yes, without the INSERT check option | yes, no subquery in the condition | yes, no subquery in the condition | yes |

DML needs `WebSql:AllowDml`, a role from `WebSql:DmlWriterRoles` and the matching bit in `CompileRequest.Statements` (default `ReadOnly`). Not supported on any dialect and rejected at parse time: `RETURNING`/`OUTPUT`, `UPDATE ... FROM`, `DELETE ... USING`, joins in `UPDATE`/`DELETE`, `MERGE ... BY SOURCE` and `BY TARGET`.

### 3.3 Capability limits (capability table `cap-6`)

| Dialect | Bind parameters | IN-list items | Identifier length | Marker | Pagination |
|---|---|---|---|---|---|
| SQL Server | 2,100 | none (bind limit) | 128 characters | `@pN` | `OFFSET ... FETCH` |
| PostgreSQL | 65,535 | none (bind limit) | 63 bytes (longer names are rejected, never truncated) | `$n` | `LIMIT/OFFSET` |
| SQLite | 32,766 | none (bind limit) | unbounded | `?N` | `LIMIT/OFFSET` |
| Oracle | 32,767 | 1,000 (ORA-01795) | 128 bytes | `:pN` | `OFFSET ... FETCH` |
| DuckDB | 65,535 | none (bind limit) | unbounded | `$n` | `LIMIT/OFFSET` |
| Databricks | 1,000 (provisional budget) | none | 255 characters | `:pN` | `LIMIT/OFFSET` |

A statement over a limit is rejected with `SqlLimitExceededException`; it is never split, truncated or widened. Under an enforced row cap a root `FETCH FIRST n ROWS WITH TIES` is downgraded to `FETCH FIRST n ROWS ONLY`, so ties cannot exceed the cap.

## 4. Session Pinning and Providers

### 4.1 Default schema

`DataSources:Connections:<name>:DefaultSchema` is pinned on every connection rental: PostgreSQL `search_path = pg_catalog, "<schema>", pg_temp`, Oracle `CURRENT_SCHEMA`. The value must match `^[A-Za-z_][A-Za-z0-9_$#]{0,127}$`, is passed only as a bound value and is quoted by the database (`format('%I')`, `DBMS_ASSERT.ENQUOTE_NAME`). Each pin is verified inside the database; a mismatch disposes the connection and the request fails with 503 `SQL_SOURCE_UNAVAILABLE`.

`DataSources:Connections:<name>:RequireDefaultSchema` (default `true`) applies to PostgreSQL and Oracle sources:

- With `true` and no `DefaultSchema` the gateway refuses to start (the connection factory would refuse the source at request time).
- A WebSQL-reachable PostgreSQL or Oracle source (default data source, `AllowedDataSources`, `DataSourceMappings`, `TenantDataSourceAllowlist`) always needs `DefaultSchema`, regardless of the flag.
- `false` is only for a source that no WebSQL path reaches. SQL Server and SQLite ignore both settings.

### 4.2 Other session settings

- PostgreSQL sessions set `standard_conforming_strings = on` and `TimeZone = UTC` with the tenant context.
- SQL Server sessions pin `ANSI_WARNINGS`, `ANSI_NULLS`, `ANSI_PADDING`, `CONCAT_NULL_YIELDS_NULL`, `ARITHABORT` and `QUOTED_IDENTIFIER` on and `NUMERIC_ROUNDABORT` off on every rental (verified through `@@OPTIONS`), and set the tenant, user and purpose through `sp_set_session_context` (read-only keys). `DataSources:Connections:<name>:ReadUncommitted` (default `false`) selects `READ UNCOMMITTED`.
- SQLite connections read back `trusted_schema` and register the `gateway_hmac_sha256` UDF. `SqliteProviderExecutionGuard` interrupts a statement at `WebSql:ExecutionTimeoutSeconds` (504 `SQL_TIMEOUT`) and on cancellation. `IProviderExecutionGuard` is a required dependency of the executors.
- Oracle: section 4.3.

### 4.3 Oracle connection policy

Applies to data sources whose provider is Oracle (`DataSources:Connections:<name>`):

- `OracleConnectionStringPolicy` rejects `DBA Privilege` (SYSDBA, SYSOPER, SYSASM), OS authentication (empty user or `/`), the accounts `SYS` and `SYSTEM` (also when quoted), and proxy authentication.
- Outside Development the data source must use TCPS (`tcps://` or `(PROTOCOL=TCPS)` without `(PROTOCOL=TCP)`), the connection string must not contain a plaintext `Password`, and `DataSources:Connections:<name>:PasswordKeyVaultRef` must name the Key Vault secret. In Development a plaintext password only logs a warning.
- `SqlConnectionFactory` wraps the driver connection so every command has `BindByName = true`; clearing it throws. `IsolationLevel.Unspecified` is mapped to `ReadCommitted` (the driver rejects `Unspecified`).
- On every pool rental a constant PL/SQL block pins and verifies `NLS_COMP`, `NLS_SORT`, `NLS_LANGUAGE`, `NLS_TERRITORY`, `NLS_NUMERIC_CHARACTERS`, the date and timestamp formats and `TIME_ZONE`, and clears the client identifier. A deviation raises and the connection is discarded. The session initializer then sets an opaque client identifier and the module name with bound values.

## 5. Security, Zero-Trust and Fail-Closed Behavior

### 5.1 Invariants

| ID | Invariant |
|---|---|
| INV-1 | Unknown rules, nodes, functions, table functions, dialects, capabilities and providers fail closed; there is no default dialect branch on the governed path. |
| INV-2 | Every physical table reference in every scope (including policy subqueries and `MERGE` ON) is covered; the predicate sits in the `WHERE` of the secured derived table, never in a join `ON` or an outer `WHERE`. |
| INV-3 | Injected predicates survive the simplifier (the simplifier runs before injection, and the base rewriter never descends into `SecurityPredicateExpression`). |
| INV-4 | All values are bound; identifiers are delimited and resolved to catalog canonical names; bind types of tenant and policy values follow the catalog column type. |
| INV-5 | No raw SQL fragments on the governed path. Expression nodes have no free-text members; the only marker node, `NamedBindMarkerExpression`, holds a validated identifier and is constructed only by `PolicyPredicateRenderer`. |
| INV-6 | No source comments or whitespace in the output; the text checker runs in production. |
| INV-7 | Bounded resources: query length, nesting, parse-tree and AST depth, compile time, secured table references, emitted length, expansion factor, bind count, IN-list size, identifier length. Each has a typed `SqlLimitKind`. |
| INV-10 | DML check option (section 5.5). |
| INV-11 | Resolution equivalence: schema-qualified canonical physical names, symbol-based CTE references and the pinned default schema, so the database binds the same object the gateway secured. |
| INV-12 | Cache integrity (section 2.3): no values in cached entries, full key comparison, dependency fingerprints re-validated. |
| INV-13 | Each marker maps 1:1 to one `BoundParameter` of the expected origin and type; Oracle binds by name. |
| INV-14 | Session settings that change comparison, escaping or name resolution are pinned per connection (section 4). |
| INV-15 | Tenant equality is byte-exact on every dialect (section 5.3). |
| INV-16 | Bound values never appear in logs, spans, metrics, cache keys, audit records or exception messages; backend errors reach the caller as typed generic codes. |

### 5.2 Function and CAST allowlists, CTE identity

- **Functions.** `DialectCapabilities.Functions` is a declarative per-dialect map built from the WebSQL allowlist (`SqlFunctionAllowlists`) plus the Trino functions the compiler rewrites. A function without a rule is rejected. `WebSql:AdditionalAllowedFunctions` and `CompileRequest.AllowedFunctions` can only narrow the map; qualified function names are never accepted from the caller list. A built-in deny list (for example `reflect`, `java_method`, `secret`, `IS_ROLEMEMBER`, `DBURITYPE`, `XMLTYPE`; on SQLite `load_extension`, `pragma_*`, `sqlite_*` and `gateway_hmac_sha256` from user SQL) is never permitted. Table functions are rejected.
- **CAST.** Closed target-type set (section 2.2 step 4). The emitter spells types through the dialect's map; request text is never emitted as a type.
- **CTEs.** The CTE definition is emitted as a delimited identifier and every reference to it is rewritten to the same identifier. A recursive CTE name that equals a catalog table is rejected; recursive CTEs are not usable in WebSQL. CTE names that differ only by case within one visible scope are rejected. A non-recursive CTE that shadows a physical table has its body secured.
- **Names.** Three-part names select the catalog and are checked against the data source; one- and two-part names that are ambiguous across catalogs are rejected. Unqualified names resolve through the pinned default schema.

### 5.3 Byte-exact tenant comparison

The tenant predicate is `col = value AND <byte-exact equality>`; the plain equality keeps index use and the second conjunct removes case, accent and padding insensitivity:

| Dialect | Byte-exact conjunct |
|---|---|
| SQL Server | `CAST(CAST(col AS nvarchar(256)) AS varbinary(512))` equals the same cast of the value, and `DATALENGTH` matches |
| PostgreSQL | `textsend(CAST(col AS text))` equals `textsend(CAST(value AS text))` |
| SQLite | blob cast of both sides |
| DuckDB | `encode(CAST(col AS varchar))` equals `encode(CAST(value AS varchar))` |
| Oracle | `"SYS"."UTL_RAW"."CAST_TO_RAW"` of both sides, over `VARCHAR2(4000)` casts; an empty tenant string is rejected because Oracle treats it as NULL |
| Databricks | plain equality on `UTF8_BINARY` columns, binary comparison for other or unknown collations |

### 5.4 Tenant isolation and the tenant registry

**Configured tenants.** `SecurityContextResolutionMiddleware` checks the resolved tenant against `TenantCollisionGuard`, built from the configured tenant ids (default and allowed forward-auth tenants, WebSQL data source allowlist, OpenMetadata and ITSM tenant maps). A tenant id that equals another configured id case-insensitively is denied with 403 `TENANT_ID_COLLISION` and the audit event `TENANT_COLLISION_DENIED`. `Gateway:TenantIsolation:StrictCollisionStartup` (default `false`) additionally refuses the start outside Development when configured ids collide. Tenant ids are never rewritten.

**Tenant registry** (`ITenantRegistry`, `Gateway:TenantIsolation:Registry`, on by default). It detects collisions between tenants that only the identity provider knows:

- Every authenticated request registers its tenant id. A spelling that collides case-insensitively with a registered one marks the group as collided; every spelling of a collided group is denied (403 `TENANT_ID_COLLISION`).
- A ClusterAdmin that selects a tenant by header only looks it up (`TryGetAsync`): it never registers a tenant and never marks a collision. An unknown header tenant is denied with 403 `TENANT_UNKNOWN`. A header-selected tenant must therefore already be registered (by an IdP login or the `Tenants` configuration).
- New registrations are budgeted per issuer (`MaxNewTenantsPerIssuerPerWindow`, default 20 per `WindowSeconds` = 60; per authentication scheme for issuer-less tokens). The existence check precedes the budget. An exhausted budget answers 429 `TENANT_REGISTRATION_RATE_LIMITED`.
- A registry that cannot decide denies the request: 503 `TENANT_REGISTRY_UNAVAILABLE`.
- `Enabled = false` is the explicit opt-out; the configured-tenant guard stays active either way.

**Resolving a collision.** `POST /api/admin/tenant-registry/{tenantId}/resolve-collision` (canonical ClusterAdmin) sets the spelling to keep for a collided group. It changes only a group that carries the collision mark.

| Request / outcome | Response |
|---|---|
| Body (optional) | `{ "expectedCurrent": "<spelling>" }`, a compare-and-swap on the group's current spelling |
| Resolved | 200 `{ resolved, previous, note }`; audit `TENANT_COLLISION_RESOLVED` with `keptSpelling`, `previousSpelling`, `expectedCurrent` |
| No collided group for this id | 404 |
| The group holds another spelling than `expectedCurrent` | 409 `{ error, current }`, nothing changed |
| Invalid tenant id | 400 |
| Caller is not a canonical ClusterAdmin | 403 |

Runbook:

1. Find the colliding spellings in the audit records `TENANT_COLLISION_DENIED`.
2. Fix the identity provider so that it issues only one spelling. An IdP that keeps issuing the other spelling marks the group again.
3. Call the endpoint with the exact spelling to keep and pass the current spelling of the group (from the audit record or a 409 response) as `expectedCurrent`, so that two operators cannot overwrite each other's choice.
4. Every other spelling stays denied. Keep `previousSpelling` of the `TENANT_COLLISION_RESOLVED` record in case the wrong spelling was kept.

### 5.5 DML

- **Forced tenant.** The tenant value of every written row must be a literal equal to the caller's tenant and is replaced by the bound tenant parameter (INSERT VALUES, INSERT SELECT including set operations, MERGE INSERT). A missing tenant column in an INSERT is rejected. An INSERT that names a foreign tenant is a security rejection (403).
- **Predicates.** Tenant and row-policy predicates are opaque conjuncts of the `WHERE` (UPDATE, DELETE) or the `ON` (MERGE). The user part is parenthesized so an `OR` cannot absorb them. An unfiltered, tautological or constant-only user `WHERE`/`ON` (for example `WHERE 1 = 0` or `id = 3 AND 1 = 0`) is rejected as unfiltered DML.
- **Policy-column protection.** Assigning the tenant column or any column the applicable row policy references is rejected, so a row cannot move across a policy boundary. Correlated target policies are rejected.
- **Masked-column protection.** A masked column cannot be written, and cannot be read in `SET`, `WHERE`, `ON`, `WHEN` conditions or inserted values. HMAC-masked columns are rejected in predicates and joins. A DML subquery reads a masked table through the masked projection.
- **MERGE.** The clause set is closed (`MergeUpdateClause`, `MergeDeleteClause`, `MergeInsertClause`). The source is secured like a query, the target predicates are in `ON`. A source whose qualifier equals the target alias, the target table name or its unqualified name is rejected (`MergeAliasGuard`). The target is a catalog table, never a CTE.
- **Strict guards only.** `DmlGuardOptions.Strict` is the only accepted value; anything else is rejected with `SqlCompileConfigurationException` before the cache is consulted.
- **INSERT check option.** An `INSERT ... VALUES` into a table with a plain admin row policy compiles to `INSERT INTO t (cols) SELECT v.cols FROM (SELECT row1 UNION ALL SELECT row2 ...) v WHERE <policy over v>`, with the reserved alias `autheris_ins`. Every written value except the tenant is cast to the provider-native catalog type, so the policy is evaluated on the value the database stores. String policy columns accept only equality and `IN` with a bound value, compared byte-exact; range, `LIKE`, `BETWEEN`, functions, column-to-column, `<>`, `NOT` and `NOT IN` on string columns are rejected. Every policy column must appear in the column list. The statement carries `ExpectedAffectedRows` and runs through `CheckedDmlExecutor`, which rolls back when the affected count differs (`DML_CHECK_OPTION_VIOLATION`). Still rejected: consent-based and correlated policies, `INSERT ... SELECT` and `MERGE ... INSERT` into a policy table, and Databricks.
- **Catalog type map.** `CatalogTypeMap` resolves a catalog `DataType` to a closed list of native types per dialect. A type outside the list (for example `xml`, `geography`, `citext`, `CLOB`, Oracle `VARCHAR2(n CHAR)`) rejects the INSERT into a policy table. On SQL Server a `CHAR(n)`/`NCHAR(n)` policy column is rejected for the check option.

### 5.6 Error codes and HTTP mapping

`SqlCompilerErrorMapper.TryMap` (`src/Autheris.Application/Sql/Errors/`) is the one mapping of compiler and execution errors to a status, a stable code and a fixed message. It walks the inner-exception chain to a depth of 8; the first matching rule in table order wins, and a security exception is never downgraded to a 4xx data code. It serves every surface that returns or persists an error of a governed entry point: `GatewayExceptionHandler` (ProblemDetails extension `code`), WebSQL and the dataset endpoints (`SqlErrorResponse`: `{ error, code, message, limit?, traceId }`), Arrow Flight SQL (ProblemDetails with `code`), the MCP error results, and the persisted error of async statements and async query jobs.

The client message is always the fixed message of the code; it never contains `Exception.Message`, driver text, values, object names or counts. `limit` carries only the `SqlLimitKind` name. The original exception is logged server-side with the trace id. A 503 response sets `Retry-After: 5`.

| Exception (condition) | HTTP | Stable code |
|---|---|---|
| Parser time budget or parser-slot exhaustion (`ParseCanceledException` with inner `TimeoutException`) | 422 | `SQL_LIMIT_EXCEEDED` (`limit = CompileTime`) |
| ANTLR `RecognitionException` / `ParseCanceledException` | 400 | `SQL_SYNTAX_ERROR` |
| `MissingClientParameterException` | 400 | `SQL_PARAMETER_MISSING` |
| `ReservedIdentifierException` (identifier starting with `autheris_`) | 400 | `SQL_RESERVED_IDENTIFIER` |
| `SqlCompileNotSupportedException` (reason `Construct`), `AstBuildException` | 422 | `SQL_CONSTRUCT_NOT_SUPPORTED` |
| `SqlCompileNotSupportedException` (reason `StatementClass`) | 403 | `SQL_STATEMENT_NOT_PERMITTED` |
| `SqlCompileNotSupportedException` (reason `Dialect`) | 501 | `SQL_DIALECT_NOT_SUPPORTED` |
| `SqlLimitExceededException` (any `SqlLimitKind`), compile budget (`OperationCanceledException` with inner `TimeoutException`) | 422 | `SQL_LIMIT_EXCEEDED` (+ `limit`) |
| `UnfilteredDmlException` | 403 | `SQL_UNFILTERED_DML` |
| `MaskedAggregationException` (masked column as aggregate argument or `GROUP BY` expression) | 403 | `AGGREGATION_ON_MASKED_COLUMN` |
| `PolicyParseException`, `PolicyConflictException`, `WebSqlTableDeniedException` (table not usable for any reason) | 403 | `SQL_POLICY_DENIED` |
| Tenant id collision | 403 | `TENANT_ID_COLLISION` |
| `SecurityCoverageException`, `EmittedSqlInvariantViolationException`, `SqlCompileConfigurationException` (gateway defect) | 500 | `SQL_COMPILER_INVARIANT` |
| `WebSqlPolicyException` and any other `SecurityException` | 403 | `SQL_POLICY_VIOLATION` |
| `DmlCheckOptionViolationException` | 403 | `DML_CHECK_OPTION_VIOLATION` |
| `DmlConstraintViolationException` | 409 | `DML_CONSTRAINT_VIOLATION` |
| `DmlAffectedRowsLimitException` | 422 | `DML_AFFECTED_ROWS_LIMIT` |
| `GovernedSqlException` `SQL_DATA_ERROR` | 422 | `SQL_DATA_ERROR` |
| `GovernedSqlException` `SQL_PROVIDER_ERROR` | 502 | `SQL_PROVIDER_ERROR` |
| `CheckedExecutionRequiredException` (gateway defect) | 500 | `DML_CHECKED_EXECUTION_REQUIRED` |
| `GovernedSqlException` with an unknown code (gateway defect) | 500 | `SQL_COMPILER_INVARIANT` |
| `SqlSourceUnavailableException` (default schema missing or pin not verified) | 503 | `SQL_SOURCE_UNAVAILABLE` |
| Execution timeout / source unavailable (`DataAccessErrorClassifier`, after all rules) | 504 / 503 | `SQL_TIMEOUT` / `SQL_SOURCE_UNAVAILABLE` |

- `SQL_POLICY_DENIED` is the one code for "table not usable": not registered, inactive, no columns, case-spelling conflict, governance column missing in the database, catalog load failure, unparsable or conflicting policy, access-decision deny. A denied (hidden) column and a column that does not exist produce the same code and message, so a response never tells whether a table or column exists.
- `SQL_POLICY_VIOLATION` is reserved for statement-level rules that do not depend on a table: DDL, a function that is not permitted, a read-only token, a missing writer role, a table-less statement.
- `SQL_COMPILER_INVARIANT` and `DML_CHECKED_EXECUTION_REQUIRED` are counted by `autheris_sql_compiler_invariant_failures_total`.
- Other codes of the tenant middleware: 403 `TENANT_UNKNOWN`, 429 `TENANT_REGISTRATION_RATE_LIMITED`, 503 `TENANT_REGISTRY_UNAVAILABLE`, 403 `CROSS_TENANT_ACCESS_FORBIDDEN`, 401 `UNAUTHORIZED_NO_SID`.
- GitOps declaration endpoints (for example `/consents/apply`) answer a curated 400 without the JSON parser message.

### 5.7 Error sanitization

`DmlErrorSanitizer.Map(dialect, exception)` runs after the timeout/unavailable classification. It is total and walks the inner exceptions. The result has a fixed message, the dialect and no inner exception; no value, column or table name survives.

| Result | SQL Server | PostgreSQL | Oracle | Delta |
|---|---|---|---|---|
| `DML_CONSTRAINT_VIOLATION` | 2627, 2601 (unique); 547 (check or foreign key, by message class); 515 (not null); 8672 (merge) | SQLSTATE 23505, 23503, 23502, 23514, 21000 | 1, 2291, 2292, 1400, 1407, 2290, 30926 | `DELTA_*` constraint and merge classes |
| `SQL_DATA_ERROR` | 2628, 8152, 245, 8114, 8115, 242, 241, 220, 232, 295, 9803 | SQLSTATE class 22 | 12899, 1722, 1858, 1861, 1840, 1843, 1438, 1401, 1476, 6502, 1830 | `CAST_INVALID_INPUT`, `CAST_OVERFLOW`, `ARITHMETIC_OVERFLOW`, `NUMERIC_VALUE_OUT_OF_RANGE`, `INVALID_ARRAY_INDEX`, `DELTA_EXCEED_CHAR_VARCHAR_LIMIT` |
| `SQL_PROVIDER_ERROR` | every other error | every other error | every other error | every other error |

SQLite and DuckDB errors are classified by their provider error classes and message patterns into the same three results.

## 6. Audit and Startup

### 6.1 Audit fields

Every governed statement writes an audit record with:

| Field | Content |
|---|---|
| `compiler_version` | `CompiledSql.CompilerVersion` (for example `ast-compiler/1.0`) |
| `dialect` | The target dialect of the compiled statement |
| `bind_count` | Number of bound parameters (never values) |
| `sql.compiled_digest` | `HMAC-SHA256(k_digest, CompilerVersion ‖ Dialect ‖ CompiledSql.Sql ‖ parameter shape)`; the shape is (ordinal, type, origin) only; 64 hex characters |
| `originalSqlHash` | `HMAC-SHA256(k_original, redacted statement)`: the client statement with every literal replaced by `@p_redacted`; statements that differ only in values share the hash |
| `compiledSql` | The compiled text (markers only, no values by construction), only when `Gateway:Audit:StoreCompiledSql = true` (default `false`) |

- `k_digest = HKDF(auditKey, "Autheris:SqlCompiledDigest:v1")`, `k_original = HKDF(auditKey, "Autheris:SqlOriginalHash:v1")`.
- The audit text of a statement is the literal-redacted text (strings, numbers including `1.5E3`, binary literals `X'..'`). A statement that does not lex is redacted completely to `@p_redacted_statement`. There is no hash over the raw text, and the secured SQL text is not stored.
- Rejections are audited with the stable code and the exception type, never the message: unsupported construct, reserved identifier, syntax error, limits, any security exception and any `ArgumentException` (code `SQL_BAD_REQUEST` when no mapper rule owns it). A client cancellation is not audited as a rejection.
- DML audit records are written with `CancellationToken.None`. An audit failure after the commit is logged as Critical and counted by `autheris_dml_audit_failed_total`; the response stays a success because the change is committed. The failure audit of a DML that failed before the commit never masks the original exception.
- The federated (cross-source) executor uses the same digester and fields.

### 6.2 Audit key requirement

The audit key is `GovernanceDb:AuditHmacKeyVaultRef`, otherwise the `Autheris:AuditChain:v1` HKDF key of `DataMasking:HmacSecretKeyVaultRef` (the same key as the audit hash chain). It is read once (singleton `CompiledSqlAuditDigester`) and must be at least 32 bytes outside Development.

- `AuditKeyStartupCheck` (hosted service, registered when `WebSql:Enabled = true`) resolves the key at startup. Outside Development and Test a missing, unreadable or too short key stops the host.
- In Development and Test an ephemeral key is used.
- A DML statement resolves the key before any lease, connection or command, so a key that cannot be read fails the statement before anything is executed.

### 6.3 Denied-tables startup report

`DeniedTablesStartupReporter` (background service, registered with `WebSql:Enabled`) lists, after the start, every table that the governed pipeline denies: stored row predicates that the production parser rejects (access profiles of the configured tenants and the default tenant, virtual filters) and tables of a dialect that WebSQL does not admit (`DIALECT_NOT_ADMITTED`). It logs one warning per table with table id, policy id and a stable code, never a predicate or a value. A store that cannot be read is a warning, not a failed start; the request path stays fail closed on its own.

Correlated consent filters (Casbin) are covered by the pre-deploy lint `tools/PolicyPredicateLint`, which reads the whole governance store and must report 0 unparsable production predicates before a deployment.

### 6.4 Other startup rules

- `Gateway:WebSql:SqlRewriterEngine` present in the configuration (any value, also empty, in every environment) fails the start (`GatewayStartupValidator.ValidateSqlRewriterEngine`). The value is never echoed.
- `Gateway:Insecure:danger_bypass_websql_governance` has no effect: every statement is governed. It remains a DANGER switch, so it fails the start outside Development; in Development it logs one warning at startup.
- PostgreSQL and Oracle sources without `DefaultSchema` fail the start (section 4.1).

## 7. Configuration Reference

| Key | Default | Effect |
|---|---|---|
| `Gateway:WebSql:Enabled` | `false` | Enables WebSQL and the startup checks of section 6 |
| `Gateway:WebSql:AllowDml` | `false` | Permits DML (with `DmlWriterRoles`) |
| `Gateway:WebSql:DmlWriterRoles` | empty | Roles allowed to run DML |
| `Gateway:WebSql:MaxAffectedRows` | `1000` | Affected-row limit per DML statement, enforced in the transaction; `0` = unlimited (listed as a DANGER bypass when DML is enabled) |
| `Gateway:WebSql:ExecutionTimeoutSeconds` | `30` | Command timeout; SQLite statements are interrupted at this time (504) |
| `Gateway:WebSql:AdditionalAllowedFunctions` | empty | Narrows the dialect function map; never widens it |
| `Gateway:WebSql:DefaultMaxRows`, `MaxAllowedRows` | `1000`, `10000` | Row caps, enforced in the compiled statement |
| `Gateway:DataSources:Connections:<name>:DefaultSchema` | unset | Default schema pinned per rental (PostgreSQL, Oracle) |
| `Gateway:DataSources:Connections:<name>:RequireDefaultSchema` | `true` | PostgreSQL/Oracle: refuse the source without `DefaultSchema` |
| `Gateway:DataSources:Connections:<name>:PasswordKeyVaultRef` | unset | Oracle password secret (required outside Development) |
| `Gateway:DataSources:Connections:<name>:ReadUncommitted` | `false` | SQL Server isolation level |
| `Gateway:Audit:StoreCompiledSql` | `false` | Store the compiled text in the audit record |
| `Gateway:GovernanceDb:AuditHmacKeyVaultRef` | unset | Audit key (else derived from `DataMasking:HmacSecretKeyVaultRef`) |
| `Gateway:TenantIsolation:StrictCollisionStartup` | `false` | Refuse the start on colliding configured tenant ids (outside Development) |
| `Gateway:TenantIsolation:Registry:Enabled` | `true` | Tenant registry |
| `Gateway:TenantIsolation:Registry:MaxNewTenantsPerIssuerPerWindow` | `20` | Registration budget per issuer |
| `Gateway:TenantIsolation:Registry:WindowSeconds` | `60` | Budget window |
| `Gateway:TenantIsolation:Registry:CacheTtlSeconds` | `60` | Positive cache TTL; a collision recorded by another replica takes effect within this time |
| `Gateway:TenantIsolation:Registry:MaxCacheEntries`, `MaxTrackedIssuers` | `10000`, `1024` | Cache and issuer bounds |

Removed or inert settings:

| Key | Behavior |
|---|---|
| `Gateway:WebSql:SqlRewriterEngine` | Removed; configuring it fails the start |
| `Gateway:WebSql:PlanCache` | Removed; configuring the section fails the start. The engine template cache has a fixed size of 512 (section 2.3) |
| `Gateway:Insecure:danger_bypass_websql_governance` | No effect; fails the start outside Development (DANGER switch) |
| `RowFilters:SubqueryStrategy` | Ignored on the governed path; policy subqueries are always correlated. A non-default value is reported as a startup warning; the setting still applies to row filters built as SQL text (REST, staging, aggregations, procedures) |

## 8. Verification

### 8.1 CI gate `sql-compiler-gate`

The `sql-compiler-gate` job in `.github/workflows/ci.yml` runs on every pull request (`SQLGATE_PROFILE=pr`, no retries). `.github/workflows/sql-compiler-gate-nightly.yml` runs the `nightly` profile, the manual `cutover` profile and the secret-gated live Databricks job (G9); it has no `pull_request` trigger. Making the job a required check is a branch-protection setting of the repository owner.

On a pull request the `sql-compiler-gate` job starts in parallel with the other jobs and runs the gate tests in Debug (security mutant hooks) and the database differential (G4, G6). The Release run of the gate tests and the architecture guards are part of the `build-and-test` job, which runs every test of the solution in Release (`WorkflowPolicyTests` asserts that its filter does not exclude them). The G8 benchmark and the G7 mutation score (Stryker.NET) run in the nightly workflow.

| Gate | Content |
|---|---|
| G1 | Build and test suites |
| G2 | Conformance matrix: Trino input -> expected SQL and parameter list per dialect |
| G3 | Round-trip identity of generated typed ASTs |
| G4 | Differential corpus: the live AST outcome of every corpus statement, executed on SQLite, PostgreSQL, SQL Server and Oracle, against the frozen legacy results; `looser` outcomes fail unless approved in `tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json` |
| G5 | Property and fuzz tests (fail-closed typing, partition oracles) |
| G6 | Real execution (Testcontainers PostgreSQL, SQL Server, Oracle; in-process SQLite and DuckDB; Spark/Delta as Databricks proxy) |
| G7 | Security regression suites (`Category=Security`); gate tests run in Debug and Release with the security mutant hooks (`ICompilerHooks`, implemented in the test assembly only; production uses the no-op hooks and all controls are always enabled) |
| G8 | Performance: allocation and Gen2 count compared with `benchmarks/baselines/sql-compiler-legacy.json` (`tools/SqlGate.BenchCheck`) |
| G9 | Live Databricks SQL warehouse (nightly, secret-gated, skipped without secrets) |
| G10 | Consumer parity of the IR renderers |

**Frozen legacy results.** The legacy engine no longer exists. Its outcomes were recorded once from the last tree that contained it and are committed as `tests/Autheris.Tests.SqlCompilerGate/golden-results/legacy-<Dialect>.json` (SQLite, PostgreSQL, SQL Server, Oracle; every corpus statement in both scenarios). The files are never regenerated. The differential classifies the live AST outcome against them with unchanged rules (rows, mask and write direction). A corpus statement without a frozen result fails the gate; `FrozenLegacyTests` pins the codec, the coverage and the classification.

**Accepted differences.** Every differential outcome that deviates from the frozen legacy result is recorded in `tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json`. `entries` group statements per dialect, class (`ast-stricter`) and reason (for example `SecurityException`, `MaskedAggregationException`, `UnfilteredDmlException`) and carry a concrete `justification`, an `issue`, a `reviewer` and an `approval` of `pending` or `approved`. `legacyDefects` record statements where the legacy result itself was wrong (for example an unexpanded `WITH RECURSIVE` member). `AcceptedDifferencesWriter` proposes new entries as `pending` with a placeholder justification. Under `SQLGATE_PROFILE=cutover`, `CutoverPrecondition_EveryAcceptedDifferenceIsApproved` fails while any entry is `pending`.

### 8.2 Architecture guards

`tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs` scans every project under `src` at IL level. Each scan has a positive control.

| Test | Guarantee |
|---|---|
| `NoLegacyRewriterTypes`, `DeletedPathsNotReintroduced` | The legacy rewriter types, members and files stay deleted |
| `NoTrustedSqlExpression` | No raw-SQL expression node exists |
| `NamedBindMarkerExpression_IsOnlyConstructedByThePolicyPredicateRenderer` | The only marker node is built by the renderer |
| `CompiledSqlIsOnlyConstructedByTheEngine` | No `CompiledSql` construction, `with` copy or setter call outside the engine |
| `NoCommandTextAssignmentOutsideTheAllowlist`, `TheGovernedPath_NeverAssignsCommandText` | Command text (setter, command constructors, `CreateCommand(string)`) only in allowlisted types; never on the governed path outside the binder |
| `NoProductionAssembly_ReferencesTheTokenStreamRewriter` | No production code uses ANTLR's `TokenStreamRewriter` |
| `GovernedEntryPoints_ReachCompileAndBinder` | Every governed entry point reaches `Compile` and the binder; the GraphQL tree compiler and the text consumers are listed exceptions that must not reach the binder |

## 9. Limitations

- Literal bind types follow the literal's type. Comparing a literal with a column of another type (for example a numeric literal against a text tenant column) is rejected or can raise a provider conversion error.
- Triggers that change policy columns after an insert are not covered by the check option. Governed tables must not have such triggers.
- The catalog `DataType` and `Collation` come from database metadata; the INSERT check option, the bind types and the Databricks collation shortcut trust them.
- Not usable on the governed path: recursive CTEs, table functions, qualified function names, and on SQL Server and SQLite any `[` token (bracket lexer differential guard).
- Client-named parameters are not supported in federated (cross-source) queries; such a statement fails closed.
- The GraphQL tree compiler and the OLAP/staging reads (section 2.1) do not run through `Compile`; they render the typed policy model.
- Oracle uses `Oracle.ManagedDataAccess.Core` under the Oracle Free Use Terms and Conditions; the license review is a release gate for announcing Oracle as supported.
- Databricks is Experimental and not admitted to WebSQL: the bind budget of 1,000 is provisional, Delta has no INSERT check option and no subqueries in `UPDATE`/`DELETE` conditions, and Databricks DML is refused while `MaxAffectedRows > 0`.
- Snowflake and ANSI have no capability entry.
- The compile result is cached per canonical request; a catalog change is detected through table fingerprints, not through events.

## 10. Upgrading to the Single-Path Compiler

Reference for operators and API clients moving from a release with the legacy token-stream rewriter. No schema or data migration is involved.

**Configuration and startup**

| Change | Action |
|---|---|
| `WebSql:SqlRewriterEngine` fails the start | Remove the key from every configuration source |
| `WebSql:PlanCache` fails the start | Remove the section from every configuration source |
| `danger_bypass_websql_governance` has no effect and fails the start outside Development | Remove it |
| `AuditKeyStartupCheck` stops the host outside Development and Test without a usable audit key | Configure `GovernanceDb:AuditHmacKeyVaultRef` or `DataMasking:HmacSecretKeyVaultRef` (at least 32 bytes) |
| `RequireDefaultSchema` defaults to `true` | Set `DefaultSchema` for every PostgreSQL and Oracle source, or `RequireDefaultSchema = false` for a source that no WebSQL path reaches |
| The tenant registry is on by default | A tenant selected by header must already be registered (IdP login or `Tenants` configuration), otherwise 403 `TENANT_UNKNOWN`; resolve collisions with the endpoint of section 5.4; `Registry:Enabled = false` opts out |
| Admitted dialects are exactly PostgreSQL, SQL Server, SQLite and Oracle | Databricks sources are not executable through WebSQL; Snowflake is never executable |
| SQLite statements are interrupted at `WebSql:ExecutionTimeoutSeconds` | Clients that relied on unbounded SQLite statements receive 504 |
| `RowFilters.SubqueryStrategy` is ignored | Policy subqueries are always correlated |
| Pre-deploy | Run `tools/PolicyPredicateLint` against the production governance store (0 unparsable predicates) and review the denied-tables startup report |

**API behavior**

- Error statuses and codes follow section 5.6 on every governed surface, including the dataset endpoints. Compiler and DML errors moved from 403/500 to 400/409/422/501/502/503 with stable codes; unsupported constructs answer 422.
- Identifiers starting with `autheris_` are reserved: 400 `SQL_RESERVED_IDENTIFIER`.
- GitOps declaration endpoints answer a curated 400 without the JSON parser message.

**Audit**

- Records carry `compiler_version`, `dialect`, `bind_count` and `sql.compiled_digest` instead of the secured SQL text; the compiled text only with `Audit:StoreCompiledSql`.
- `originalSqlHash` is a keyed HMAC over the literal-redacted text (key derived from the audit key), not a SHA-256 over the raw text; statements that differ only in literal values share it.
- The compiled digest key derives from the audit key, not from the masking master secret.

**Stricter statement rules**

- Constant-only DML predicates count as unfiltered DML.
- Qualified functions are not accepted from the caller list; `AdditionalAllowedFunctions` only narrows the dialect function map; an allowlisted table function is still rejected.
- `FETCH FIRST ... WITH TIES` is downgraded under an enforced row cap.
- Recursive CTEs are not usable.
- HMAC-masked columns are rejected in predicates and joins.
- A masked column cannot be an aggregate argument (including `FILTER` and the ordered-aggregate `ORDER BY`) or a grouping expression in any SELECT (`AGGREGATION_ON_MASKED_COLUMN`); a bare whole-row reference (`SELECT t FROM t`, `to_json(t)`) to a table with masked columns is rejected in a SELECT list, enforced by the visitor and again by the coverage verifier.
- An INSERT that names a foreign tenant is a security rejection.

**Public API (embedders and extensions)**

- `RewriteSqlAsync` of `IGovernedSqlExecutionService`, `ISqlRewritePipeline` and `IFederatedQueryExecutionService` is `ValidateAsync` and returns no SQL text.
- `ITenantRegistry` has `TryGetAsync` and `ResolveCollisionAsync`; implementers must add them.
- `SqlTokenSecurityOptions.FromRlsOptions` is `ForDialect`; `AstBuilderOptions.FromRlsOptions`, `RlsOptions`, the string policy and mask provider interfaces and the legacy `ISqlEngine` members are removed.
- The governed services require `IProviderExecutionGuard`.
- `GovernedSqlResult.RewrittenSql` and the `secretProvider` parameter of `GovernedSqlRewriter` are removed.

**Rollback** is a revert of the cutover commit; there is no runtime switch (see [ADR-017](../adr/ADR-017-distributed-state-ast-generator-and-rbac.md)).

## 11. Performance

Measured with the benchmark runner (`dotnet run -c Release -- compile` in `benchmarks/Autheris.Benchmarks`, `CompilePathBenchmark`), .NET 10 on Linux (WSL2), single thread. Query: an aggregating join with a `WHERE`, `GROUP BY` and `ORDER BY`, two secured tables.

| Path | Result | Regression limit of the runner |
|---|---|---|
| Cache hit (template rebind and dependency validation) | 16.1 µs per compile | 250 µs |
| Cache miss (parse, build, simplify, inject, verify, emit) | 0.73 ms per compile | 10 ms |

These are reference values from a development machine; the limits are regression guards, not service-level targets. The CI regression check is G8 (section 8.1).
