# F-DIALECT-02: Governed AST SQL Compiler (DQL and DML)

Supersedes [F-DIALECT-01](f-dialect-01-ast-target-dialect-pushdown.md). Implementation: [`src/TrinoSqlEngine`](../../src/TrinoSqlEngine), entry point `ISqlEngine.Compile`.

## 1. Executive Summary and Problem Statement

WebSQL users write SQL in Trino syntax. The gateway must run that text on SQL Server, PostgreSQL, DuckDB, Oracle and (experimentally) Databricks and must guarantee that every statement only touches the rows and columns the caller may see or change.

The legacy path rewrites the token stream of the user's SQL (`RlsListener`, selected with `WebSql:SqlRewriterEngine = LegacyTokenStream`). It inserts predicates and masks as text, inlines literals, relies on deny-lists for dialect-specific lexer differences and cannot prove its result.

The compiler replaces that with a pipeline over a typed syntax tree:

| Aspect | Legacy token rewriting | AST compiler (`ISqlEngine.Compile`) |
|---|---|---|
| Representation | Token stream plus string splicing | Immutable typed AST |
| Values | Literals inlined, policy fragments spliced as text | Every value is a bind parameter (user literals, tenant, policy and mask values) |
| Policies and masks | Text fragments | Typed predicate and mask nodes, parsed once, fail-closed |
| Proof of coverage | None | `SecurityCoverageVerifier` re-proves every secured scope on each compile, independent of the injector |
| Output check | None | Dialect-aware text check of the emitted SQL |
| Dialect limits | Per-feature code | One capability table (bind count, IN-list size, identifier length) |
| Statements | SELECT (DML guarded by token rules) | SELECT, INSERT, UPDATE, DELETE, MERGE with check-option semantics |
| Result | SQL string | `CompiledSql` (SQL text plus typed `BoundParameter` list) |

The compiler is an opt-in library entry point at this time; see section 4 for how engines are selected.

## 2. Architecture

### 2.1 Pipeline

`ISqlEngine.Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken)` runs these passes (implemented in `GovernedSqlCompiler`):

```
token guards -> parse -> AST build -> validate -> simplify (user tree only) -> secure (inject typed predicates and masks)
  -> coverage verify -> capability check -> emit -> emitted-text check -> bind (at execution)
```

1. **Gates.** The dialect must have a capability entry and be `Production` (or the request sets `AllowExperimentalDialect`). The statement classes must be listed in `CompileRequest.Statements` and in the dialect's `DmlStatements`. `CompileRequest.Policy.Dml` must equal `DmlGuardOptions.Strict`, otherwise `SqlCompileConfigurationException`. `CompileLimits.Normalize` bounds `CompileTimeout` (default 2 s, maximum 30 s) and `MaxExpansionFactor` (default 64, maximum 256) and forces the variable-substitution token guard for Databricks.
2. **Token guards** (`SqlTokenSecurityOptions`): comments, backslashes and escaped literals in strings, dollar quoting, bracket lexer differentials, non-ASCII identifiers, dots in quoted identifiers, time-travel clauses and `${...}` variable substitution sequences.
3. **Parse** (ANTLR, Trino grammar) with limits: query length 65,536 characters, nesting depth 100, parse-tree depth 3,000.
4. **AST build and validate.** Function calls are checked against the dialect function map; a function name with a delimited part (`"lower"(x)`, `"pg_catalog".lower(x)`) is rejected. CAST targets come from a closed type set (`boolean`, `tinyint`, `smallint`, `integer`, `bigint`, `real`, `double`, `decimal`/`numeric`, `varchar`, `char`, `text`, `varbinary`, `date`, `time`, `timestamp` with optional time zone); `regclass`, `regtype`, `xml`, `json`, `oid` and similar are rejected.
5. **Simplify** runs on the user tree before injection, so security predicates never exist while the simplifier runs.
6. **Secure.** Tenant, row-policy and mask nodes are injected as opaque `SecurityPredicateExpression` / `MaskExpression` nodes into the `WHERE` of a secured derived table around every physical table reference, in every scope (subqueries, CTE bodies, set operations, `MERGE` source and target).
7. **Coverage verify** (`SecurityCoverageVerifier`) walks the final tree and proves the same coverage independently. A failure throws `SecurityCoverageException` and nothing is cached.
8. **Capability check** (`DialectCapabilityValidator`): constructs, functions, IN-list size, identifier length, bind count.
9. **Emit** through the dialect generator. Only validated structural integers (row limits, ordinals, frame offsets, type parameters) and keywords are emitted inline. `EmittedSqlInvariantChecker` then scans the text with a dialect-aware lexer and rejects stray quotes, comments, semicolons (except the registered trailing `;` of a SQL Server `MERGE`), dollar quotes and unregistered numeric tokens. The output must not exceed `MaxExpansionFactor` times the input length (minimum basis 64 characters) and 1 MiB.
10. **Bind** is performed by an `ICompiledSqlBinder` (section 2.4).

Every pass observes the compile budget and the caller's cancellation token. A timeout raises `SqlLimitExceededException(CompileTime)`.

### 2.2 Value-free plan cache

`CompiledSqlTemplateCache` stores verified compile results as templates that contain no tenant, policy or mask values. The key is the SHA-256 of the full canonical request (compiler version, capability table version `cap-5`, dialect, policy shape, token guards, limits, engine query-length limit) plus the SQL text; a hit additionally compares the full key material. On a hit the compiler re-validates every table dependency by catalog fingerprint and rebinds the current values (`Rebind`). Only verified output is cached; a failed verification never inserts an entry. The default capacity is 512 entries (first-in-first-out eviction).

### 2.3 Typed policy IR and masks

`ITableCatalog` supplies the physical tables and columns (`CatalogColumn.Name`, `DataType`, `Collation`). `IPolicyPredicateProvider` returns a `PolicyPredicate`: an expression tree with named parameter nodes, the values by name, a SHA-256 fingerprint of the shape and the referenced columns. A table without access gets the deny-all predicate (`1 = 0`), never null. `IColumnMaskProvider` returns a `MaskSpec` of kind `Nullify`, `Redact`, `PartialMask`, `Hmac`, `GeoJitter` or `Constant`; all mask arguments except the rounding scale are bound parameters. Keyed HMAC is computed inside the database on SQL Server and PostgreSQL; on DuckDB, Oracle and Databricks (`InDbHmac = false`) it degrades to `Redact`.

### 2.4 Binders and checked execution

`CompiledSql` carries the text, the ordered `BoundParameter` list (marker, name, ordinal, value, `SqlParameterType`, `ParameterOrigin`, catalog column type), the dialect, the statement class, the applied predicate ids and the compiler version. `BoundParameter.ToString()` never prints the value.

`DbCommandCompiledSqlBinder` is the ADO.NET base class of the provider binders. It refuses a statement that needs a row-count check, before it touches the command, with `CheckedExecutionRequiredException` (`DML_CHECKED_EXECUTION_REQUIRED`). The refusal triggers on `CompiledSql.RequiresRowCountCheck` and, as defense in depth, on the reserved alias `autheris_ins` anywhere in the SQL text. `ExpectedAffectedRows` has an `internal init`, so a `with` copy outside the assembly cannot clear it.

`CheckedDmlExecutor` (`ICheckedDmlExecutor`) is the only caller of the internal checked bind. It opens the connection if needed, begins a transaction, binds, executes, compares the affected row count through `DmlCheckOption.Enforce` before the commit, and rolls back on any exception, any count difference or an unreported count (`-1`). It also runs statements without a check.

## 3. Supported Dialects and Tiers

| Dialect | Tier | Compiler capability entry | Notes |
|---|---|---|---|
| SQL Server | Production | yes | Verified on SQL Server containers |
| PostgreSQL | Production | yes | PostgreSQL 16 |
| DuckDB | Production | yes | In-process |
| Oracle | Production (compiler tier) | yes | Oracle Free 23ai; the driver license must be reviewed before release (section 7) |
| Databricks | Experimental | yes | Compiles only with `CompileRequest.AllowExperimentalDialect = true`; verified on a Spark/Delta proxy, no Databricks SQL Warehouse run; no runtime connector |
| Snowflake | Experimental | no | A generator exists for the legacy string path only; `Compile` rejects it with `SqlCompileNotSupportedException(Dialect)` |
| SQLite, ANSI | n/a | no | Generators exist for the legacy string path; `Compile` rejects them with `SqlCompileNotSupportedException(Dialect)` |

`AllowExperimentalDialect` is a per-request flag. An architecture test (IL scan) forbids setting it in production assemblies.

### 3.1 Statement matrix

| Dialect | SELECT | INSERT | UPDATE | DELETE | MERGE |
|---|---|---|---|---|---|
| SQL Server | yes | yes | yes | yes | yes (one trailing `;`) |
| PostgreSQL | yes | yes | yes | yes | yes |
| DuckDB | yes | yes | yes | yes | yes |
| Oracle | yes | yes | yes | yes | at most one `UPDATE` and one `INSERT` clause; a `DELETE` clause or a repeated clause kind is rejected |
| Databricks (Delta) | yes | yes, without the INSERT check option | yes, no subquery in the condition | yes, no subquery in the condition | yes |

DML statements need the matching bit in `CompileRequest.Statements`; the default is `ReadOnly`. Not supported on any dialect and rejected at parse time: `RETURNING`/`OUTPUT`, `UPDATE ... FROM`, `DELETE ... USING`, joins in `UPDATE`/`DELETE`, `MERGE ... BY SOURCE` and `BY TARGET`.

Oracle's `MERGE` form is a parenthesized `ON`, the clause condition as a clause `WHERE`, and no stand-alone delete, because Oracle deletes only rows it also updated, which has different semantics from the other dialects.

Delta limits: open-source Delta refuses subqueries in `UPDATE`/`DELETE` conditions (`DELTA_UNSUPPORTED_SUBQUERY`); the compiler rejects them first (`SupportsSubqueryInDmlCondition = false`). Delta `INSERT` returns no row count, so the INSERT check option cannot be enforced and an `INSERT` into a table with a row policy is rejected (`ReportsInsertRowCount = false`).

## 4. Configuration

### 4.1 Engine selection

The default engine is the legacy token-stream rewriter: `FastSqlEngine.SqlRewriterEngine` defaults to `LegacyTokenStream` and `WebSql:SqlRewriterEngine` is unset (`GatewayOptions.WebSql.SqlRewriterEngine`, `string?`). Accepted values: unset or `LegacyTokenStream` (legacy), `AstCompiler`, `ShadowDualRun` (runs both, legacy result is authoritative).

`WebSql:SqlRewriterEngine = AstCompiler` routes `RewriteRls` to `FastSqlEngine.GenerateGovernedSql`, the string-returning AST path driven by `RlsOptions`. It does not call `ISqlEngine.Compile`; it does not use the typed policy IR, the bind-everything contract, the coverage verifier or the plan cache described here. The virtual-filter compiler (`SqlFilterCompiler`) uses this string path.

`ISqlEngine.Compile` is the typed entry point described in this document. It is registered through `ISqlEngine` (`FastSqlEngine`) and is called with an explicit `CompileRequest`; no gateway service calls it at this time.

```csharp
var compiled = engine.Compile(sql.AsMemory(), new CompileRequest
{
    TargetDialect = TargetSqlDialect.PostgreSql,
    Policy = new GovernancePolicy { RowFilters = rows, Masks = masks, Catalog = catalog, Tenant = tenantBinding },
    TokenGuards = SqlTokenSecurityOptions.Strict,
    Statements = StatementPermissions.Insert | StatementPermissions.Update,
    EnforcedMaxRows = 1000
}, ct);
// compiled.Sql + compiled.Parameters; bind with a provider binder; run DML with CheckedDmlExecutor.
```

`CompileRequest` members: `TargetDialect`, `Policy` (`RowFilters`, `Masks`, `Catalog`, `Tenant`, `Dml`, `TablesWithConsentRowFilter`, `TablesWithMaskedColumns`), `TokenGuards`, `Statements`, `EnforcedMaxRows` (never applied to DML), `TranslateTrinoDateFunctions`, `EnforceCatalogProjection` (default true), `AllowedFunctions` (can only narrow the dialect map), `AllowedTableFunctions`, `AllowExperimentalDialect`, `CompileTimeout`, `MaxExpansionFactor`.

### 4.2 Capability limits (capability table `cap-5`)

| Dialect | Bind parameters | IN-list items | Identifier length | Marker | Pagination |
|---|---|---|---|---|---|
| SQL Server | 2,100 | none (bind limit) | 128 characters | `@pN` | `OFFSET ... FETCH` |
| PostgreSQL | 65,535 | none (bind limit) | 63 bytes (longer names are rejected, never truncated) | `$n` | `LIMIT/OFFSET` |
| DuckDB | 65,535 | none (bind limit) | unbounded | `$n` | `LIMIT/OFFSET` |
| Oracle | 32,767 | 1,000 (ORA-01795) | 128 bytes | `:pN` | `OFFSET ... FETCH` |
| Databricks | 1,000 (conservative budget) | none | 255 characters | `:pN` | `LIMIT/OFFSET` |

A statement over a limit is rejected with `SqlLimitExceededException`; it is never split, truncated or widened.

### 4.3 Tenant isolation and startup

`Gateway:TenantIsolation:StrictCollisionStartup` (default `false`): tenant ids that differ only in case are always denied per request (section 5.4). With `true` the gateway additionally refuses to start outside Development when the configured tenant ids collide. In Development a collision only logs a warning. Tenant ids are never rewritten.

### 4.4 Oracle connection policy

Applies to data sources whose provider is Oracle (`DataSources:Connections:<name>`):

- `OracleConnectionStringPolicy` rejects `DBA Privilege` (SYSDBA, SYSOPER, SYSASM), OS authentication (empty user or `/`), the accounts `SYS` and `SYSTEM` (also when quoted), and proxy authentication.
- Outside Development the data source must use TCPS (`tcps://` or `(PROTOCOL=TCPS)` without `(PROTOCOL=TCP)`), the connection string must not contain a plaintext `Password`, and `DataSources:Connections:<name>:PasswordKeyVaultRef` must name the Key Vault secret. The connection factory resolves the secret when it opens the connection. In Development a plaintext password only logs a warning.
- `SqlConnectionFactory` wraps the driver connection so every command has `BindByName = true`; clearing it throws.
- On every pool rental a constant PL/SQL block pins and verifies `NLS_COMP`, `NLS_SORT`, `NLS_LANGUAGE`, `NLS_TERRITORY`, `NLS_NUMERIC_CHARACTERS`, the date and timestamp formats and `TIME_ZONE`, and clears the client identifier. A deviation raises and the connection is discarded. The session initializer then sets an opaque client identifier and the module name with bound values.

### 4.5 Other session settings

PostgreSQL sessions set `standard_conforming_strings = on` and `TimeZone = UTC` with the tenant context. SQL Server sessions set the tenant, user and purpose through `sp_set_session_context` (read-only keys). `DataSources:Connections:<name>:ReadUncommitted` (SQL Server, default `false`) selects `READ UNCOMMITTED`.

Databricks has no runtime connector: `SqlConnectionFactory` has no Databricks driver, and the compiler only produces text and parameters for it. There are no Databricks HTTP data source settings.

## 5. Security, Zero-Trust and Fail-Closed Behavior

### 5.1 Invariants

| ID | Invariant |
|---|---|
| INV-1 | Unknown rules, nodes, functions, table functions, dialects, capabilities and providers fail closed; there is no default dialect branch on the governed path. |
| INV-2 | Every physical table reference in every scope (including policy subqueries and `MERGE` ON) is covered; the predicate sits in the `WHERE` of the secured derived table, never in a join `ON` or an outer `WHERE`. |
| INV-3 | Injected predicates survive the simplifier (the simplifier runs before injection, and the base rewriter never descends into `SecurityPredicateExpression`). |
| INV-4 | All values are bound; identifiers are delimited and resolved to catalog canonical names; bind types of tenant and policy values follow the catalog column type. |
| INV-5 | No raw trusted SQL fragments on the typed path. |
| INV-6 | No source comments or whitespace in the output; the text checker runs in production. |
| INV-7 | Bounded resources: query length, nesting, parse-tree and AST depth, compile time, secured table references, emitted length, expansion factor, bind count, IN-list size, identifier length. Each has a typed `SqlLimitKind`. |
| INV-10 | DML check option (section 5.5). |
| INV-11 | Resolution equivalence: schema-qualified canonical physical names and symbol-based CTE references, so the database binds the same object the gateway secured. |
| INV-12 | Cache integrity (section 2.2): no values in cached entries, full key comparison, dependency fingerprints re-validated. |
| INV-13 | Each marker maps 1:1 to one `BoundParameter` of the expected origin and type; Oracle binds by name. |
| INV-14 | Session settings that change comparison, escaping or name resolution are pinned per connection (section 4.4 and 4.5). |
| INV-15 | Tenant equality is byte-exact on every dialect (section 5.3). |
| INV-16 | Bound values never appear in logs, spans, metrics, cache keys or exception messages; backend errors reach the caller as typed generic codes. |

### 5.2 Function and CAST allowlists, CTE identity

- **Functions.** `DialectCapabilities.Functions` is a declarative per-dialect map built from the WebSQL allowlist (`SqlFunctionAllowlists`) plus the Trino functions the compiler rewrites. A function without a rule is rejected. `CompileRequest.AllowedFunctions` can only narrow the map. A built-in deny list (for example `reflect`, `java_method`, `secret`, `IS_ROLEMEMBER`, `DBURITYPE`, `XMLTYPE`) is never permitted. Table functions are rejected unless listed in `AllowedTableFunctions`.
- **CAST.** Closed target-type set (section 2.1 step 4). The emitter spells types through the dialect's map; request text is never emitted as a type.
- **CTEs.** The CTE definition is emitted as a delimited identifier and every reference to it is rewritten to the same identifier. A recursive CTE name that equals a catalog table is rejected. CTE names that differ only by case within one visible scope are rejected. A non-recursive CTE that shadows a physical table has its body secured. A self-reference without `WITH RECURSIVE` is rejected.
- **Quoted names and catalogs.** Three-part names select the Databricks catalog; one- and two-part names that are ambiguous across catalogs are rejected.

### 5.3 Byte-exact tenant comparison

The tenant predicate is `col = value AND <byte-exact equality>`; the plain equality keeps index use and the second conjunct removes case, accent and padding insensitivity:

| Dialect | Byte-exact conjunct |
|---|---|
| SQL Server | `CAST(CAST(col AS nvarchar(256)) AS varbinary(512))` equals the same cast of the value, and `DATALENGTH` matches |
| PostgreSQL | `textsend(CAST(col AS text))` equals `textsend(CAST(value AS text))` |
| DuckDB | `encode(CAST(col AS varchar))` equals `encode(CAST(value AS varchar))` |
| Oracle | `"SYS"."UTL_RAW"."CAST_TO_RAW"` of both sides, over `VARCHAR2(4000)` casts; an empty tenant string is rejected because Oracle treats it as NULL |
| Databricks | `CastBinary` comparison; plain equality on `UTF8_BINARY` columns, binary comparison for other or unknown collations |

### 5.4 B-1: request-time denial of colliding tenants

`SecurityContextResolutionMiddleware` checks the resolved tenant against `TenantCollisionGuard`, built from the configured tenant ids (default and allowed forward-auth tenants, WebSQL data source allowlist, OpenMetadata and ITSM tenant maps). A tenant whose id equals another configured id case-insensitively is denied with HTTP 403 and the stable code `TENANT_ID_COLLISION`, an audit entry (`TENANT_COLLISION_DENIED`, decision `DENY`) is written, and the request never reaches the next middleware. A failing audit sink does not turn the denial into an allow. If the guard cannot be resolved the request is also denied. Other tenants are unaffected.

### 5.5 DML

- **Forced tenant.** The tenant value of every written row must be a literal equal to the caller's tenant and is replaced by the bound tenant parameter (INSERT VALUES, INSERT SELECT including set operations, MERGE INSERT). A missing tenant column in an INSERT is rejected.
- **Predicates.** Tenant and row-policy predicates are opaque conjuncts of the `WHERE` (UPDATE, DELETE) or the `ON` (MERGE). The user part is parenthesized so an `OR` cannot absorb them. An unfiltered or tautological user `WHERE`/`ON` is rejected (best-effort net; the injected conjuncts already bound the row set).
- **Policy-column protection.** Assigning the tenant column or any column the applicable row policy references is rejected, so a row cannot move across a policy boundary. Correlated target policies are rejected.
- **Masked-column protection.** A masked column cannot be written, and cannot be read in `SET`, `WHERE`, `ON`, `WHEN` conditions or inserted values.
- **MERGE.** The clause set is closed (`MergeUpdateClause`, `MergeDeleteClause`, `MergeInsertClause`). The source is secured like a query, the target predicates are in `ON`. A source whose qualifier equals the target alias, the target table name or its unqualified name is rejected (`MergeAliasGuard`). The target is a catalog table, never a CTE.
- **Strict guards only.** Any `DmlGuardOptions` value other than `Strict` is rejected with `SqlCompileConfigurationException` before the cache is consulted.
- **INSERT check option.** An `INSERT ... VALUES` into a table with a plain admin row policy compiles to `INSERT INTO t (cols) SELECT v.cols FROM (SELECT row1 UNION ALL SELECT row2 ...) v WHERE <policy over v>`, with the reserved alias `autheris_ins`. Every written value except the tenant is cast to the provider-native catalog type, so the policy is evaluated on the value the database stores. String policy columns accept only equality and `IN` with a bound value, compared byte-exact (like the tenant, section 5.3); range, `LIKE`, `BETWEEN`, functions, column-to-column, `<>`, `NOT` and `NOT IN` on string columns are rejected. Every policy column must appear in the column list. The statement carries `ExpectedAffectedRows` (the number of rows) and must run through `CheckedDmlExecutor`, which rolls back when the affected count differs (`DML_CHECK_OPTION_VIOLATION`). The row set is a balanced `UNION ALL` tree, so 1,000 rows compile within the depth limits. Still rejected: consent-based and correlated policies, `INSERT ... SELECT` and `MERGE ... INSERT` into a policy table, and Databricks.
- **Catalog type map.** `CatalogTypeMap` resolves a catalog `DataType` to a closed list of native types per dialect, including precision, scale, length and fractional seconds. A type outside the list (for example `xml`, `geography`, `citext`, `CLOB`, Oracle `VARCHAR2(n CHAR)`) rejects the INSERT into a policy table. On SQL Server a `CHAR(n)`/`NCHAR(n)` policy column is rejected for the check option.

### 5.6 Typed errors and codes

| Type | Code / kind | Meaning | HTTP status |
|---|---|---|---|
| `SqlCompileNotSupportedException` | reason `Dialect`, `StatementClass`, `Construct` | Dialect, statement class or construct without a governed path | none |
| `SqlCompileConfigurationException` | n/a | A security guard was relaxed per request | none |
| `SqlLimitExceededException` | `SqlLimitKind`: `BindParameters`, `InListItems`, `IdentifierLength`, `QueryLength`, `NestingDepth`, `AstDepth`, `SecuredTableReferences`, `EmittedSqlLength`, `PolicyExpansionFactor`, `CompileTime` | A limit was exceeded; carries dialect, requested and maximum | none |
| `SecurityCoverageException` | n/a | The coverage proof failed | none |
| `EmittedSqlInvariantViolationException` | n/a | The emitted-text check failed | none |
| `UnfilteredDmlException` | n/a | Unfiltered DML | none |
| `PolicyParseException`, `PolicyConflictException` | n/a | A policy expression could not be parsed or has no value of its declared type | none |
| `AstBuildException`, `SecurityException` | n/a | Unsupported or forbidden syntax | none |
| `MissingClientParameterException` | n/a | No value for a client parameter at bind time | none |
| `DmlConstraintViolationException` | `DML_CONSTRAINT_VIOLATION`; kinds `Unique`, `ForeignKey`, `NotNull`, `Check`, `MergeMultipleMatches` | A data constraint failed (sanitized) | none |
| `GovernedSqlException` | `SQL_DATA_ERROR` | A value could not be stored or converted (truncation, conversion, overflow) | none |
| `GovernedSqlException` | `SQL_PROVIDER_ERROR` | Any other provider error | none |
| `DmlCheckOptionViolationException` | `DML_CHECK_OPTION_VIOLATION` | A written row violates the row policy | none |
| `CheckedExecutionRequiredException` | `DML_CHECKED_EXECUTION_REQUIRED` | A check-option statement was bound outside `CheckedDmlExecutor` | none |
| Middleware | `TENANT_ID_COLLISION` | Colliding tenant id | 403 |

The compiler's exceptions have no HTTP mapping in the gateway at this time; the only mapped compiler-adjacent response is `TENANT_ID_COLLISION`. For reference, `SecurityContextResolutionMiddleware` also returns 403 `CROSS_TENANT_ACCESS_FORBIDDEN` and 401 `UNAUTHORIZED_NO_SID`.

### 5.7 Error sanitization

`DmlErrorSanitizer.Map(dialect, exception)` is total and walks the inner exceptions. The result has a fixed message, the dialect and no inner exception; no value, column or table name survives.

| Result | SQL Server | PostgreSQL | Oracle | DuckDB | Delta |
|---|---|---|---|---|---|
| `DML_CONSTRAINT_VIOLATION` | 2627, 2601 (unique); 547 (check or foreign key, by message class; an unknown class is not guessed); 515 (not null); 8672 (merge) | SQLSTATE 23505, 23503, 23502, 23514, 21000 | 1, 2291, 2292, 1400, 1407, 2290, 30926 | message patterns | `DELTA_*` constraint and merge classes |
| `SQL_DATA_ERROR` | 2628, 8152, 245, 8114, 8115, 242, 241, 220, 232, 295, 9803 | SQLSTATE class 22 | 12899, 1722, 1858, 1861, 1840, 1843, 1438, 1401, 1476, 6502, 1830 | `Conversion Error`, `Out of Range`, `Could not convert` | `CAST_INVALID_INPUT`, `CAST_OVERFLOW`, `ARITHMETIC_OVERFLOW`, `NUMERIC_VALUE_OUT_OF_RANGE`, `INVALID_ARRAY_INDEX`, `DELTA_EXCEED_CHAR_VARCHAR_LIMIT` |
| `SQL_PROVIDER_ERROR` | every other error | every other error | every other error | every other error | every other error |

## 6. Performance

Measured with the benchmark runner (`dotnet run -c Release -- compile` in `benchmarks/Autheris.Benchmarks`, `CompilePathBenchmark`), .NET 10 on Linux (WSL2), single thread. Query: an aggregating join with a `WHERE`, `GROUP BY` and `ORDER BY`, two secured tables.

| Path | Result | Regression limit of the runner |
|---|---|---|
| Cache hit (template rebind and dependency validation) | 16.1 µs per compile | 250 µs |
| Cache miss (parse, build, simplify, inject, verify, emit) | 0.73 ms per compile | 10 ms |

These are reference values from a development machine; the limits in the runner are regression guards, not service-level targets. Throughput of the legacy rewriter in [`performance-benchmarks.md`](../comparisons/performance-benchmarks.md) is a separate measurement and not comparable to this table.

## 7. Limitations

- Literal bind types follow the literal's type. Comparing a literal with a column of another type (for example a numeric literal against `VARCHAR2` on Oracle) can raise a provider conversion error (`ORA-01722`), which is a one-bit side channel; on SQL Server `nvarchar` against `varchar` forces implicit conversions. Tenant and policy binds follow the catalog column type.
- `CHAR`/`NCHAR` policy columns are rejected for the INSERT check option on SQL Server (fixed-length padding makes the byte-exact comparison always fail).
- Triggers that change policy columns after an insert are not covered by the check option. Governed tables must not have such triggers.
- SQL Server: the gateway does not set `ANSI_WARNINGS`; the driver default (`ON`) makes over-long values raise. A session with `ANSI_WARNINGS OFF` would truncate silently, so the session must keep the default.
- The tenant collision guard knows the configured tenant ids only. Two tenant ids that collide and exist only in the identity provider are not detected. Data isolation still holds through the byte-exact comparison.
- The catalog `DataType` and `Collation` must match the database. The INSERT check option, the bind types and the Databricks collation shortcut trust the catalog; a mismatch (for example `decimal(18,3)` in the catalog against `decimal(18,2)` in the database) weakens the check. No catalog loader fills these fields from database metadata at this time.
- The GraphQL tree compiler (`TreeSqlCompiler`, `SqlDataSourceExecutor`) and the other text consumers (lakehouse executors, streaming enforcers, procedures, in-memory row filters) still use the legacy text path. The byte-exact tenant comparison and the typed policy IR do not apply there.
- The runtime executors do not call `DmlErrorSanitizer` or `CheckedDmlExecutor` yet; no gateway service calls `ISqlEngine.Compile`. A statement that needs the checked path fails closed if bound with `Bind`.
- Oracle uses `Oracle.ManagedDataAccess.Core` under the Oracle Free Use Terms and Conditions; the license must be reviewed before release, and the package is not signature-pinned (`trustedSigners` for `Oracle.*` is not configured).
- Oracle `MERGE` supports at most one `UPDATE` and one `INSERT` clause and no `DELETE` clause.
- Databricks is Experimental: bind budget 1,000 is provisional, there is no runtime connector, no Databricks SQL Warehouse run and no catalog loader for collation or Unity Catalog metadata. Delta has no INSERT check option and no subqueries in `UPDATE`/`DELETE` conditions.
- Snowflake, SQLite and ANSI have no compiler capability entry.
- PostgreSQL `search_path` and Oracle `CURRENT_SCHEMA` are not pinned; resolution equivalence relies on schema-qualified physical names.
- The compile result is cached per canonical request; a catalog change is detected through table fingerprints, not through events.
