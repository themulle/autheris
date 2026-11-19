# Plan: Databricks metric views, governed read access (Issue #916), Phase 2

Phase 2 (solution architect), 2026-11-19. Input: the Phase 1 PRD [`2026-11-19-prd-issue-916-databricks-metric-views.md`](2026-11-19-prd-issue-916-databricks-metric-views.md) (option A, `REQ-MV-01..10`, security constraints in section 6). This plan does not repeat the PRD. It fixes the design decisions, the step number and the work packages. Code facts verified on `origin/main` `e3e32b401`. **Implementation starts after the v1.9.0 tag** (PRD Q6, default taken), and only after Phase 3.

## 1. Code facts that shape the design

- The object kind is binary today:
  - enum: `DiscoveryObjectKind { Table, View, Procedure }` (`src/Autheris.Domain/Model/DiscoveryModels.cs:3`);
  - model property: `TableModels.cs:154`;
  - persisted as `TABLES.object_kind VARCHAR(16)` with `CHECK (object_kind IN ('table','view'))` by step `0030_table_object_kind` (both engines).
- Several sites map the kind with `== View ? "view" : "table"`:
  - `CatalogTablePersistedProjection.cs:131`;
  - `CatalogGovernanceRatchet.cs:71`;
  - `CatalogBatchUpsertHelper.cs:509`;
  - `ExternalCatalogReader.cs:880`;
  - `TrinoJdbcMetadataShapes.cs:413`.
  A third kind needs an exhaustive switch at each of them.
- dbt cataloging: `src/Autheris.Extensions/Dbt/DbtObjectKindResolver.cs` (`MetaKey = "autheris.object_kind"`, line 15) sets the kind from `materialized` or source meta. The semantic manifest path (`DbtSemanticManifestIngestionService.cs:480`) stays unchanged.
- Columns are stored in `TABLE_COLUMNS` (`PostgreSqlBaselineSchema.cs:40`). There is no role column.
- Functions:
  - `src/TrinoSqlEngine/SqlFunctionAllowlists.cs` has a per-dialect list (`DatabricksFunctions`, line 63) and `CompilerNames(dialect)` (line 132), used by `Ast/Capabilities/DialectCapabilities.cs:73`;
  - `DatabricksDialectGenerator.cs` emits Databricks SQL.
  `MEASURE` is unknown everywhere today, so it is refused (fail closed).
- Column visibility rule: `src/Autheris.Application/Catalog/Discovery/DiscoveryColumnVisibility.cs` (Clear or Masked visible; #871 rule).

## 2. Design decisions (Phase 2)

1. D-MV-1, kind: add `DiscoveryObjectKind.MetricView`, persisted as `'metric_view'` (11 characters, fits `VARCHAR(16)`). Every kind mapping site becomes an exhaustive `switch` that throws on an unknown kind. A ratchet test forbids `== DiscoveryObjectKind.View ?` ternaries in `src/`.
2. D-MV-2, column role: `TABLE_COLUMNS.column_role VARCHAR(16) NULL` with `CHECK (column_role IS NULL OR column_role IN ('dimension','measure'))`. It is mapped to `MetricViewColumnRole? Role` on the column model.
   - On a metric view, a column with a `NULL` role is not exposed: it is not cataloged as visible and is refused in SQL (REQ-MV-02, fail closed).
   - On other kinds, the role must be `NULL`. Ingestion rejects anything else.
3. D-MV-3, ingestion: only a dbt **source** can declare `autheris.object_kind: metric_view`. A model with that kind is rejected at ingestion with a diagnostic. Column meta is `autheris.column_role: dimension|measure`. The YAML definition is never read or stored (REQ-MV-09, PRD section 3.2).
4. D-MV-4, engine: `measure` is added to the Databricks `CompilerNames` only. `DatabricksDialectGenerator` emits `MEASURE(<column>)` unchanged. Every other dialect keeps refusing it through the existing allowlist path.
5. D-MV-5, statement guard: a new `MetricViewStatementGuard` runs after table resolution and before the PDP decision is applied. It refuses the statement with the existing fixed refusal message unless every rule holds:
   - one metric view in `FROM` and no other relation: no join, subquery source or CTE over it;
   - every select item is a dimension column reference that also appears in `GROUP BY` (or `GROUP BY ALL`), or `MEASURE(<measure column>)`, optionally aliased;
   - no `SELECT *`, and no bare measure reference outside `MEASURE()`;
   - `MEASURE()` takes exactly one bare column of role `measure` of that metric view, and is not nested in other expressions except an outer `CAST` or arithmetic that is already allowlisted for aggregates;
   - `WHERE` and `HAVING` reference dimensions only (`HAVING` may use `MEASURE()`);
   - the dialect is Databricks; on any other dialect, a statement over a metric view is refused before compilation.
6. D-MV-6, grants (REQ-MV-05/06): in the PDP column decision for a `MetricView`, a `measure` column is `NoAccess` unless an explicit column grant allows it. An object grant alone yields dimensions according to their column states. Hidden measures are dropped by `DiscoveryColumnVisibility` on every channel, so names do not leak. No new grant table is needed: the existing column-level consent rules are reused.
7. D-MV-7, row policy and tenant predicate (PRD Q4, default taken: refuse):
   - The statement is allowed only if every column of the decision's row filter and tenant predicate is an exposed `dimension` of the metric view.
   - The predicate is then added to `WHERE`, which Databricks applies before measure aggregation.
   - Otherwise the statement is refused.
   - The #847/#867 expression guard runs on dimension expressions unchanged.
8. D-MV-8, minimum group size (PRD Q2, default taken: yes when set):
   - Enforcement needs a group count, so a metric view with a minimum group size must declare `autheris.count_measure: <measure>`, a `measure` column that counts rows.
   - The guard adds `MEASURE(<count_measure>)` as a hidden column and drops result groups below the minimum after Databricks returns the result.
   - If a minimum is set and no count measure is declared, every query on the object is refused.
9. D-MV-9, surfaces:
   - REST catalog and MCP `describe_dataset` show kind `metric_view` and list dimensions and measures separately (REQ-MV-07).
   - Trino `system.jdbc` and `information_schema` report `TABLE_TYPE = 'VIEW'`, because JDBC clients know only the standard types. Hidden measures are absent.
   - **GraphQL and OData do not expose metric views in the first release** (default taken): a generated list field would select bare columns, which D-MV-5 refuses.
10. D-MV-10, audit: the audit record lists the object and the dimension and measure columns used. This is the existing per-column audit, with no new fields and no YAML (REQ-MV-09).

## 3. DDL step number

- The next free governance step on `main` is `0031`. It is **reserved for WP-SIMPL-52b** (profile items migration, see the handover doc section 4).
- #916 therefore takes **`0032_metric_view_kind_and_column_role`** (PostgreSQL and SQL Server). If WP-SIMPL-52b has not merged when WP-MV-1 is ready, WP-MV-1 waits. The DDL queue runs strictly one at a time, and the step catalogue must be gap-free. Whichever branch merges second renumbers its step pair, both checksum goldens, `StepSqlLintTests` and its step tests.
- Step content:
  - drop and recreate the `object_kind` CHECK with `'metric_view'` added (`ck_tables_object_kind` on PostgreSQL, `CK_TABLES_object_kind` on SQL Server; take the exact names from the 0030 SQL);
  - add `TABLE_COLUMNS.column_role` with its CHECK.
  The step is forward-only and idempotent (`IF COL_LENGTH` / `IF NOT EXISTS` style, as in 0030).

## 4. Work packages

| WP | Size | Depends on | Content |
|---|---|---|---|
| WP-MV-1 | M | WP-SIMPL-52b merged (step 0031) | Kind, role, step 0032, persistence, dbt ingestion |
| WP-MV-2 | S | none (parallel to WP-MV-1) | Engine: Databricks-only `MEASURE` |
| WP-MV-3 | M | WP-MV-1, WP-MV-2 | `MetricViewStatementGuard` and pipeline hook |
| WP-MV-4 | M | WP-MV-1 | PDP measure default deny and no-disclosure on describe channels |
| WP-MV-5 | S | WP-MV-4 | Surfaces: REST, MCP, Trino metadata; GraphQL and OData exclusion |
| WP-MV-6 | M | WP-MV-3, WP-MV-4 | Row policy, tenant, minimum group size, Security and parity tests |

**WP-MV-1**
- Files:
  - `DiscoveryModels.cs` (enum);
  - `TableModels.cs` (`MetricViewColumnRole? Role` on the column model; verify the column record in `TableModels.cs`);
  - the five kind-mapping sites listed in section 1;
  - `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{postgresql,sqlserver}/0032_metric_view_kind_and_column_role.{sql,shape.json}`;
  - the `TABLE_COLUMNS` read and write in the catalog repository (find it with `git grep -n "TABLE_COLUMNS" src/Autheris.Infrastructure`);
  - `DbtObjectKindResolver.cs` (source-only `metric_view`, column role and `count_measure` meta).
- Tests:
  - `tests/Autheris.Tests.Unit/Persistence/Migrations/MetricViewMigration0032Tests.cs` (modelled on `TableObjectKindMigration0030Tests`);
  - `StepSqlLintTests` gets the row `(32, metric_view_kind_and_column_role)`;
  - goldens `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.{postgresql,sqlserver}.txt`;
  - new `DbtObjectKindResolverMetricViewTests`: `Source_MetricView_IsCataloged`, `Model_MetricView_IsRejected`, `MetricView_ColumnWithoutRole_NotExposed`, `NonMetricView_ColumnRole_IsRejected`;
  - architecture ratchet `ObjectKind_HasNoBinaryViewTernary`.

**WP-MV-2**
- Files: `SqlFunctionAllowlists.cs` (`measure` in the Databricks compiler names only) and `DatabricksDialectGenerator.cs`.
- Tests:
  - `tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs`: `Measure_IsEmittedVerbatim`;
  - one rejection test per other dialect: `Measure_IsRejected_On<Dialect>` (PostgreSQL, SQL Server, Oracle, DuckDb, Trino);
  - a conformance fixture entry in `tests/TrinoSqlEngine.Tests/Conformance/Fixtures/Databricks.json`.
- SQL Compiler Gate: the corpus counts may move, so update the G8 baseline only with this justification.

**WP-MV-3**
- New `src/Autheris.Application/Sql/Security/MetricViewStatementGuard.cs`, next to `TableQualificationValidator.cs`. It is called from `DefaultSqlSecurityValidator` (`src/Autheris.Application/Sql/Services/DefaultSqlSecurityValidator.cs`) once the resolved table metadata is available.
- Unit tests in `tests/Autheris.Tests.Unit/Sql/MetricViewStatementGuardTests.cs`, one per D-MV-5 rule. The `[Trait("Category","Security")]` cases are `SelectStar`, `BareMeasure`, `MeasureOfDimension`, `MeasureOfUnknownColumn`, `Join`, `Subquery`, `NonDatabricksDialect`, `WhereOnMeasure` and `NestedMeasure`. The allowed cases are `GroupByAll` and `HavingWithMeasure`.

**WP-MV-4**
- PDP column decision for `MetricView`: `src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs`, at the column-level derivation.
- `DiscoveryColumnVisibility.cs` needs no rule change: hidden measures are already `NoAccess`. Add tests only.
- Tests (Security): `ObjectGrantOnly_MeasureIsNoAccess`, `MeasureGrant_MeasureIsClear`, `DeniedMeasure_NameAbsentOnRestMcpTrino`.

**WP-MV-5**
- Files:
  - `ExternalCatalogReader.cs:880` (`"metric_view"` and a `role` per column in the REST and MCP payloads);
  - `TrinoJdbcMetadataShapes.cs:413` (`'VIEW'`);
  - the GraphQL schema builder and the OData model builder filter out `MetricView` (find the type module and the EDM builder with `git grep -n "DiscoveryObjectKind" src/Autheris.Api src/Autheris.Application/GraphQL`).
- Tests: extend `tests/Autheris.Tests.Unit/Catalog/Discovery/ObjectKindViewChannelTests.cs` with metric view cases. Add `GraphQl_MetricView_NotExposed` and `OData_MetricView_NotExposed`.

**WP-MV-6**
- Files: the guard (D-MV-7 predicate check and D-MV-8 hidden count column and group drop). Reuse the minimum group size post-filter from the dbt semantic path (`SemanticMetricQueryService`) as a shared helper; do not copy it.
- Tests (Security):
  - `RowPolicyOnNonDimension_IsRefused`;
  - `TenantPredicateOnDimension_IsAppliedBeforeAggregation`;
  - `Measure_WithDivisionCastTimeBucket_UnderTenantPredicate_PassesExpressionGuard` (PRD 6.3);
  - `MinGroupSize_WithoutCountMeasure_IsRefused`;
  - `MinGroupSize_DropsSmallGroups`;
  - a cross-channel parity test (REQ-MV-10).

## 5. Integration test impact

- `tests/Autheris.Tests.Integration/Persistence/TableObjectKindContractTests.cs`: add `metric_view` to the accepted kinds, add a rejected kind, and add the `column_role` CHECK (both engines).
- `tests/Autheris.Tests.Integration/TrinoJdbcMetadataParityTests.cs`: a metric view lists as `VIEW`, and hidden measures are absent.
- Parity: there is no Databricks container in CI. REQ-MV-10 parity runs on the WebSQL, Trino `/v1/statement` and MCP `query_sql` channels with a recording fake executor and asserts identical allow and refuse decisions and emitted SQL (`tests/Autheris.Tests.Parity`). Acceptance criterion 1 (equal to the direct Databricks result) is a manual check against a real workspace, recorded in the PR.
- dbt ingestion integration tests (`git grep -ln "autheris.object_kind" tests/Autheris.Tests.Integration`) get one source fixture with a metric view.

## 6. Phase 3

**Required** (PRD section 6 is the binding input). The points to review are D-MV-5 (guard completeness), D-MV-6 (default deny), D-MV-7 (predicate placement) and D-MV-8 (differencing limit). No SEC conditions are set here.

## 7. Owner questions

The PRD questions Q1-Q6 stand with their recommended defaults. Phase 2 takes Q2, Q4, Q5 and Q6 as defaults. Q1 and Q3 stay open. New questions:

1. Q7: Should metric views be kept out of GraphQL and OData in the first release (D-MV-9)? Default: **yes**.
2. Q8: Should minimum group size require a declared count measure (D-MV-8)? Default: **yes**, refused otherwise.
3. Q9: Should JDBC `TABLE_TYPE` be `VIEW` or `METRIC VIEW`? Default: **`VIEW`**, for client compatibility.
4. Q1 (from the PRD, still open): Which identity evaluates source-side row filters and masks? Default: assume the service identity. Granting the object then requires an explicit owner acknowledgement, stored as a grant note. No schema change.
