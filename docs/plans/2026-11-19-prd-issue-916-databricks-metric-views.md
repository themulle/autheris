# PRD: Databricks metric views, governed read access (Issue #916)

Phase 1 (product manager). Status: PRD DRAFT, Phase 2 and Phase 3 pending. Decision: owner chose **option A** (queryable object, read only). Options B and C are out of scope for this PRD.

## 1. Qualification

1. Request: a Databricks Unity Catalog metric view cannot be queried with its measures through Autheris today.
2. Verified on `main` (2026-11-19): no metric view support in `src/`; `MEASURE(` appears nowhere; `SqlFunctionAllowlists` (used by `RowPolicyFactory`, `PolicyParserSetup`, `SemanticManifestValidator`) is allowlist based and fail closed, so `MEASURE` is rejected before the database.
3. Autheris reads no source metadata (WP-SIMPL-44): an object is cataloged only when dbt declares it. The governed SQL path is read only; there is no DDL path.
4. Existing separate mechanism: dbt semantic layer (`DbtMeasure` with `min_group_size`, `SemanticMetricQueryService`, `DbtSemanticManifestIngestionService`, core contract `ISemanticMetricImport`, WP-SIMPL-28). It compiles plain SQL against base tables and never emits `MEASURE()`.
5. Gate: new feature, so it starts after the 1.9.0 cut under `freeze-exception` or after the feature freeze (Stage 3 exit). No code before Phase 2 and Phase 3.

## 2. Benchmark: how Databricks metric views work

1. A metric view is a Unity Catalog object defined by YAML (`version`, `source`, optional `joins`, `filter`, `dimensions` with `name` and `expr`, `measures` with `name` and aggregate `expr`), created with `CREATE VIEW ... WITH METRICS LANGUAGE YAML AS $$ ... $$`.
2. Dimensions and measures are separate roles. Measures are not stored columns; they are aggregations evaluated at query time at the grain of the requested `GROUP BY`.
3. Query form: `SELECT dim, MEASURE(m) FROM mv GROUP BY dim` (also `GROUP BY ALL`). `MEASURE()` is valid only against a metric view. Plain `SELECT *` and a bare measure column reference are not valid semantics; measures must be wrapped in `MEASURE()`.
4. Measures can be composed (a measure referencing another measure), can use `FILTER (WHERE ...)`, and can be window measures. Joins and a view-level `filter` are applied inside the definition, invisible to the caller.
5. Unity Catalog permissions apply to the view as one object (SELECT on the view). Source-side row filters and column masks on the underlying tables are evaluated by Databricks according to its view-execution identity rules; this must be confirmed per workspace (Q1).
6. Best practice taken over: treat the metric view as a governed semantic object with explicit dimension and measure roles, query only through the aggregate form, never expose the YAML expressions to callers who lack the object grant.

## 3. Anti-patterns to reject

1. Global allowlisting of `MEASURE`: it must be enabled for the Databricks dialect only, every other dialect keeps rejecting it.
2. Allowlisting arbitrary Spark SQL expressions inside the metric view definition: Autheris does not parse or trust the YAML; the object stays opaque.
3. Treating a measure as an ordinary column: a measure over source columns Autheris cannot see is an oracle (like #813), so measures need explicit grants.
4. Applying a row policy or tenant predicate "after" the measure aggregates: it must hold before aggregation or the object is refused (see section 6).
5. Auto-discovering metric views from the Databricks catalog (conflicts with WP-SIMPL-44: dbt is the cataloging source).
6. Letting `SELECT *` or plain column reads silently return partial or misleading data.
7. Pulling definition (DDL, option B) or YAML translation (option C) into this scope.
8. Duplicating the dbt semantic path: two metric registries with diverging governance.

## 4. User stories

1. As a data consumer, I query a cataloged metric view with `SELECT dim, MEASURE(m) FROM mv GROUP BY dim` through governed SQL and get the Databricks-computed value.
2. As an AI agent (MCP) or REST client, I call `describe_dataset` and see the object kind `metric_view` and dimensions and measures listed separately.
3. As a data owner, I grant the object and each measure explicitly, and I can set per-column states for dimensions.
4. As a security owner, I know a caller without the object or measure grant learns neither a value nor a measure or column name.
5. As an operator, I know other dialects are unaffected.

## 5. Requirements

1. REQ-MV-01: A metric view is cataloged as an object through a dbt source declaration, with a new object kind `metric_view` (model: the object kind mechanism of #896/#904). No source introspection.
2. REQ-MV-02: Each exposed column carries a role, `dimension` or `measure`, declared in the dbt source metadata (default for undeclared columns: not exposed, fail closed).
3. REQ-MV-03: `MEASURE(<measure>)` is allowed only when the dialect is Databricks, the target object kind is `metric_view`, the argument is a bare, granted measure column of that object, and the statement is an aggregate query over granted dimensions.
4. REQ-MV-04: Rejected, with the existing fixed refusal message: `MEASURE` on any other dialect or object kind, `MEASURE` of a non-measure or denied or unknown name, a bare measure reference outside `MEASURE()`, `SELECT *` on a metric view, `MEASURE` nested in other expressions beyond the aggregate allowlist, joins of a metric view with other objects (first release).
5. REQ-MV-05: Measure grants are per measure and default deny. An object grant alone yields dimensions only per their column states; measures need their own grant.
6. REQ-MV-06: No measure value, name or column name is disclosed to a caller without the grant (same rule as #871), across REST, MCP, GraphQL and the Trino metadata surface.
7. REQ-MV-07: `describe_dataset`, REST catalog, MCP and the Trino metadata surface show kind `metric_view` and list dimensions and measures separately; denied columns are absent.
8. REQ-MV-08: Minimum group size (`min_group_size` semantics, as on `DbtMeasure`) applies to measure queries on a metric view when set on the object; enforcement point decided in Phase 2 (Q2).
9. REQ-MV-09: Audit records the object, measures and dimensions used; no YAML definition is stored or logged.
10. REQ-MV-10: Parity: every channel that exposes governed SQL gives the same allow or refuse result for the same statement.

## 6. Security and governance constraints (binding input to Phase 3)

1. Row policies and column masks: they must apply to dimension columns exactly as on tables. A row policy or tenant predicate cannot be injected into the metric view's internal source scan, so the default is: the object is cataloged for tenant-scoped or row-policy-scoped callers only if the policy columns are exposed dimensions and the predicate is applied as a filter on dimensions before aggregation (Databricks allows `WHERE` on dimensions). If that cannot be guaranteed for a policy, the query is refused (fail closed). Recommended: refuse metric view queries for callers under a row policy or tenant predicate that does not reference an exposed dimension.
2. Masked dimensions: a dimension with a mask state is exposed masked or not at all; never as a measure grouping key in raw form.
3. Tenant predicate and #847: the expression guard (non-raising expressions only before the row and tenant predicates, #867) must keep holding. `MEASURE()` is classified as an aggregate allowlist entry only in the guarded position; dimension expressions in `GROUP BY` and `WHERE` pass the same guard; no new raising or oracle function enters. Phase 3 must add a case with `MEASURE()` combined with division, casts and time bucketing under a tenant predicate.
4. Opaque object: measures aggregate over columns Autheris cannot see. Therefore explicit measure grants (REQ-MV-05) and, if Q2 is answered yes, minimum group size to limit differencing.
5. Identity (Q1): if Databricks evaluates source row filters and masks as the service identity, row-level rules do not hold at the source; then the object must be treated as service-identity-scoped and only be grantable at the object level by an owner who accepts that.
6. Fail closed: unknown object kind, missing role declaration, parser ambiguity or a missing grant refuses the statement.
7. Mandatory `[Trait("Category","Security")]` tests are set in Phase 3 (denied measure, denied dimension, `SELECT *`, per-dialect allowlist, oracle probing of names, tenant plus `MEASURE`, cross-channel parity).

## 7. Fit with the semantic metric import

1. Option A does not use `ISemanticMetricImport` for query execution. Metric view objects are SQL objects queried through governed SQL; the dbt semantic path (`SemanticMetricQueryService`) is unchanged.
2. The cataloging reuses the dbt metadata ingestion (`DbtMetadataIngestionService`), not the semantic manifest path. The dbt source declares the object as `metric_view` with column roles.
3. No YAML import and no translation into `DbtMeasure` (option C stays deferred). A later option C could feed `ISemanticMetricImport`, which already is the core seam (WP-SIMPL-28); nothing in A blocks it.
4. Naming guard: do not name the new concepts `Metric` where `list_metrics`/`query_metrics` already mean dbt semantic metrics; use "metric view measure".

## 8. Acceptance criteria

1. A cataloged metric view can be queried with `MEASURE(...)` on Databricks; the result equals the direct Databricks result.
2. Every other dialect still rejects `MEASURE` (test per dialect).
3. Caller without object grant or without measure grant gets no value and no measure or column name.
4. `describe_dataset` and the REST catalog show kind `metric_view` with dimensions and measures separate.
5. `SELECT *` and bare measure reads are refused with the fixed message.
6. Under a tenant predicate or row policy the statement is either filtered before aggregation or refused; #847 guard tests still pass.
7. Cross-channel parity test is green; no schema change beyond the object kind and column role (migration policy: forward-only).

## 9. Non-goals

1. Defining or altering metric views (DDL, option B; WP-SIMPL-49 is a possible later home).
2. Translating YAML into dbt semantic models (option C).
3. Auto-discovery from the Databricks catalog; parsing of the YAML definition.
4. Joins of a metric view with other objects, window measure analysis, DML.
5. Other warehouses' semantic views (Snowflake, others).

## 10. Work packages (estimate: 4-6 PRs, 8-12 person-days)

1. WP-MV-1: object kind `metric_view` and column role in dbt source ingestion and catalog model.
2. WP-MV-2: Databricks-only `MEASURE()` handling in the SQL engine allowlist with the structural rules of REQ-MV-03/04.
3. WP-MV-3: measure grants and no-access disclosure rules across channels.
4. WP-MV-4: describe surfaces (REST, MCP, GraphQL, Trino metadata) with dimension and measure split.
5. WP-MV-5: tenant and row policy interplay, minimum group size, Security tests and parity test.

## 11. Open owner questions (recommended defaults)

1. Q1: Which identity evaluates source-side row filters and masks? Default: verify per workspace; assume service identity until proven otherwise and require an explicit owner acknowledgement at the object grant.
2. Q2: Is a minimum group size mandatory for measures? Default: yes, configurable per object, default off only if the owner explicitly accepts it; enforced after the Databricks result.
3. Q3: Is A enough for the first release, or is option C needed? Default: A only; revisit C after A is in use.
4. Q4: Row policy or tenant predicate callers: refuse unless the policy columns are exposed dimensions? Default: refuse (fail closed).
5. Q5: Allow joins of a metric view with other governed objects later? Default: no for the first release.
6. Q6: Timing: `freeze-exception` for the 1.9.x line or wait until the feature freeze ends? Default: wait; no code before the 1.9.0 cut.

## 12. Changelog

- 2026-11-19: PRD drafted (Phase 1), option A chosen by the owner.
