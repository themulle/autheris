# F-GOV-14: Federated Virtual Filters on Web-APIs

## 1. Executive Summary & Problem Statement

Virtual Filters ([F-GOV-09](f-gov-09-virtual-filters.md)) restrict the rows of a protected object by a set of authorized keys that lives in a relational source, for example an authorization view of a SQL Server. For objects behind REST/Web-APIs (`DataSourceType.HttpDeclarative`) the filter cannot be pushed into the source as SQL, and an HTTP API cannot be trusted to filter. Without F-GOV-14 such a query is denied (HTTP 403).

F-GOV-14 lets a data steward bind a SQL-backed Virtual Filter to a Web-API table and still query it through the **WebSQL cross-source path**:

- The gateway loads the authorized key set from the filter's SQL key source (tenant-isolated, bounded, cached).
- Every row received from the upstream is checked against that key set in the gateway before it is staged into DuckDB (hash semi-join). This is the only mechanism that decides what is returned.
- Optimizations (Tier 0, Tier 1, Tier 2) only change **what is requested upstream**: no call at all for an empty key set or an unauthorized point lookup, or only the authorized keys when the API supports it. They never replace the semi-join.
- Everything that cannot be enforced exactly is denied with a stable `VF_*` code (fail closed).

Scope: the WebSQL cross-source path (`WebSql:CrossSource:Enabled = true`), structured Virtual Filters whose key source is a SQL data source, and target tables of type `HttpDeclarative`. REST, GraphQL, OData and MCP channels do not federate and answer 403 for a Web-API table that carries a Virtual Filter.

---

## 2. Architecture & Component Wiring

### 2.1 Data flow

```text
FederatedDuckDbExecutionService.ExecuteFederatedInternalAsync
  1. Transport gate, analyze, route (CrossSourceQueryRouter)
  2. AuthorizeAllTablesAsync        one TableAccessDecision per source-qualified table
       decision.AppliedVirtualFilterBindings   (filter, binding) pairs from MandatoryRowFilterResolver
  3. IVirtualFilterFederationPlanner.PlanAsync     per protected HTTP table:
       validate every pair -> load key sets -> choose the tier -> VirtualFilterTablePlan
  4. CrossSourcePlanner.Plan        attaches the plan to the staging request; the user query is not rewritten
  5. Start audit (virtualFilters[] + one key-source read record per loaded key set)
  6. FederatedStagingService.StageAsync
       skip upstream  -> stage an empty table, the connector is not called (Tier 0, Tier 1 miss)
       otherwise      -> merge the pushdown argument (a name clash is a denial)
                         GovernedConnectorReader.ReadAsync
                           raw upstream rows -> VirtualFilterEnforcement.Apply (semi-join)
                           residual row filters (consent, RLS, ABAC, tenant), masking, byte limit
  7. DuckDB runs the unmodified user query over the staged tables
```

| Component | Responsibility |
|---|---|
| `MandatoryRowFilterResolver` | Single policy decision point: decides which (filter, binding) pairs apply to a table (grantee, target pattern, `Supersedes`). The federation path never re-resolves profiles. |
| `VirtualFilterFederationPlanner` | Validates the pairs, loads the key sets, selects the tier and produces one `VirtualFilterTablePlan` per protected HTTP table. |
| `SqlVirtualFilterKeySetProvider` + `StructuredFilterKeyQueryBuilder` | Runs the filter's key query in the SQL key source and canonicalizes the keys. |
| `VirtualFilterKeySetCache` | Dedicated, size-limited, tenant-isolated, single-flight key set cache. |
| `VirtualFilterPointLookupExtractor` / `VirtualFilterPointLookupKeys` | Detects point lookups in the user query (Tier 1). |
| `HttpKeyPushdownCapability` / `VirtualFilterPushdownSelector` | Derives what the endpoint can take and which keys to push (Tier 1 hit, Tier 2). |
| `VirtualFilterEnforcement` | The gateway semi-join; created only by the planner. |
| `PathLookupConnector` | Per-key reader for path-template endpoints. |
| `GovernedConnectorReader` | Applies the enforcement to raw rows before the residual row filters; proves that every applied pair is covered. |
| `FederatedStagingService` | Executes the plan (skip, pushdown, path lookup) and writes the upstream read audit. |
| `GatewayDenialStatusMapper` | Shared HTTP mapping of gateway denials for the WebSQL endpoint and the global exception handler. |

### 2.2 Tiers

The tier states how the upstream is read. For every tier that reads the upstream, the gateway semi-join filters the result.

| Tier | Name | Condition | Upstream behavior |
|---|---|---|---|
| 0 | Empty key set | Any applied key set is empty, or the pushed key sets have no common member | Upstream is not called; an empty table is staged. All strategies. |
| 1 | Point lookup | The query is an eligible point lookup (`WHERE id = 47`, `id IN (...)`, composite equalities) | Miss (no queried key is authorized): upstream is not called, an empty table is staged. Hit: only the queried keys that are authorized are requested (when a pushdown capability exists), otherwise Tier 3. |
| 2 | Pushdown | Not a point lookup, the endpoint declares a key pushdown capability and the key count is within budget | The authorized keys are sent to the upstream (an optimization; the semi-join still runs). |
| 3 | Full fetch + semi-join | Everything else (no capability, over budget, `DuckDbHashJoin`, pushdown disabled) | The table is read completely and filtered by the gateway. |

The planner decides in this order for each protected table:

1. A pair that cannot be enforced exactly (Stage-2 SQL filter, validity window, key source that is not SQL, unsupported key type, unknown target column) denies the query.
2. Tier 0 if any key set is empty.
3. Tier 1 miss if the query is an eligible point lookup and, for some pair, the queried keys and the authorized keys are disjoint (only while `PushdownEnabled` is `true`).
4. The strategies of all applied bindings are combined, the most restrictive wins (section 3.2).
5. A read that would send a path template with an unfilled placeholder is denied (`VF_PATH_TEMPLATE_UNFILLABLE`).

A Tier 1 miss and Tier 0 stage an **empty relation** instead of returning early. The user query still runs, so a `LEFT JOIN`, an aggregate or another table in the query yields its true answer, and an unauthorized key looks exactly like a non-existent key.

### 2.3 Point lookup eligibility

A query is a point lookup for a protected table only if all of the following hold. Anything else is a scan, which is always safe.

- A single `SELECT` query specification without `WITH`, set operation, sub-query (`IN`, `EXISTS`, scalar), lateral or derived table.
- The protected table occurs exactly once (no self-join).
- Only the top-level `AND` chain of `WHERE` counts: `col = literal`, `literal = col`, `col IN (literal, ...)`. `NOT IN`, `OR` and expressions never count.
- A column counts when it is qualified with the table alias or name, or unqualified while the `FROM` clause has exactly one named table.
- Literals are canonicalized by the catalog type of the column; a literal that cannot be converted makes the query a scan.
- The cartesian product of `IN` lists is capped by `MaxPointLookupTuples`; more is a scan.

### 2.4 Hash semi-join enforcement

`VirtualFilterEnforcement` holds, per applied (filter, binding) pair, the target columns (after `ColumnMap`), their canonical key types and the authorized key set (a `FrozenSet` of typed keys). A raw upstream row is kept only if its projected key tuple is in the set of **every** pair (AND semantics, also across several filters).

- No key relation is staged into DuckDB and the user query is not modified, so there is no row multiplication, no key column in `SELECT *` and no query-shape special case.
- The enforcement is fixed before the connector runs; a connector cannot relax it. An HTTP connector can never attest that it already filtered or masked its rows, so the gateway always filters and masks them.
- Enforcement runs on raw rows, so a key column that is denied to the caller still filters.
- The residual decision proves coverage: `WithoutEnforcedVirtualFilters` denies with `VF_ENFORCEMENT_INCOMPLETE` when an applied pair is not covered or when some code path changed the combined row filter directly.
- A row whose key value cannot be converted or is `NULL` never matches.
- The single-column probe allocates nothing per row; the composite probe allocates its canonical key.

Key canonicalization uses the catalog type of the target column:

| Catalog type | Canonical type | Notes |
|---|---|---|
| Integer family | `long` | Integral numerics and strict invariant integer strings. |
| `decimal` / `numeric` | `decimal` | Invariant culture. |
| `uuid` / `uniqueidentifier` | `Guid` | |
| Text family | `string` | Ordinal, case-sensitive. |

Other types (date/time, boolean, floating point, binary) are denied with `VF_KEY_TYPE_MISMATCH`.

### 2.5 Key source and key set cache

`SqlVirtualFilterKeySetProvider` renders `SELECT DISTINCT <keys> FROM <from> [JOIN ...] WHERE <conditions> AND <key> IS NOT NULL` from the structured definition and runs it in the key source with bound parameters.

- Supported key source dialects: SQL Server, PostgreSQL and SQLite. All tables of the definition must be SQL tables of one data source.
- The key query is tenant-isolated: every table with a tenant column is restricted to the caller's tenant; a table without a tenant column is refused when `DataSources:RequireTenantColumn` is on and the table is not exempt. A filter of another tenant is refused.
- The key set does not depend on the principal (structured filters contain no principal reference).
- At most `MaxAuthorizedKeys` keys are read; one more denies with `VF_KEY_SET_TOO_LARGE`. A key set is never truncated. The key query has its own timeout (`KeyQueryTimeoutSeconds`).
- The cache key contains the tenant, the filter id, the definition hash, the key columns and the key types, so a changed definition never reuses an old set. The cache is separate from the shared memory cache (its own size limit `KeyCacheSizeLimitBytes`), so large key sets cannot evict other entries.
- Loads are **single-flight per cache key**: concurrent cold calls for one key share one in-flight load; a slow key source of one filter or tenant never delays the load of another cache key. A failed load is seen by its waiters and is not cached. The shared load is not tied to the first caller's cancellation and is bounded by `KeyQueryTimeoutSeconds`.
- Revocation latency: a key removed in the key source stays authorized for at most `KeyCacheTtlSeconds` (default 30 s, maximum 300 s).

### 2.6 Tier 2 pushdown through the endpoint capability

Pushdown capability is derived from the existing `HttpEndpointDescriptor` fields `BatchType`, `BatchParamName` and `PrimaryKeyField`. The pushed column is `PrimaryKeyField`; only single-key pairs whose target column equals it contribute, and the pushed values are the intersection of their key sets (narrowed to the queried keys for a point lookup). An unauthorized key is never sent.

| `BatchType` | Request | Notes |
|---|---|---|
| `QueryParameterList` | One `GET` with `?{BatchParamName}=v1,v2` | A value that contains `,` makes the pushdown unavailable. The estimated URL length must be at most `MaxPushdownUrlLength`. |
| `JsonBodyArray` | One `POST` with a JSON array body | |
| `ParallelSingleRequests` | One `GET` per key into the `{PrimaryKeyField}` placeholder of `PathTemplate` | Path lookup (section 2.7). At most `MaxPathLookupRequests` keys. |

No capability exists when `BatchType` is `None`, `BatchParamName` is empty, malformed or reserved (tenant or API key parameter names), `PrimaryKeyField` is empty, or the endpoint is paged (a pushdown would bypass pagination and miss rows). Values must be non-empty, at most 256 characters and free of control characters; path lookup values must not contain `/`, `\` or `..`.

The key count must be at most `min(binding.MaxPushdownKeys, VirtualFilters:Federation:MaxPushdownKeys)`. A pushdown argument whose name already exists in the session arguments is denied (`VF_PUSHDOWN_ARGUMENT_CONFLICT`). A binding that sets `PushdownFormat` must match the endpoint capability (`CommaSeparated` for `QueryParameterList`, `PostBatch` for `JsonBodyArray`); `RepeatedParam` and `ODataIn` are not supported formats, so such a binding reads Tier 3 under `Adaptive` and is denied under `PushdownOnly`. A non-2xx upstream answer (including 413 and 414) fails the query; a partial result is never returned.

### 2.7 Path-lookup reader semantics

For an endpoint whose path template contains the key placeholder (for example `/customers/{id}`), the gateway reads through `PathLookupConnector`:

- **One request per authorized (and queried) key**, throttled by the endpoint's `MaxConcurrentRequests`. Every request gets its own copy of the session items; the first failure cancels the requests still in flight.
- **Per-key restriction and de-duplication:** each answer is restricted to the rows whose key equals the requested key (canonical comparison), and the union is de-duplicated by that key (the key is the declared `PrimaryKeyField`). An upstream that ignores the key and answers every request with all rows therefore yields exactly the rows a full read would.
- **404 means "key not present":** that key contributes no row and the query continues; the count is audited as `upstreamKeysNotFound`. Every other failure (400, 401, 403, 5xx, a transport failure) fails the query. A wrong base path therefore shows as an empty result with a non-zero `upstreamKeysNotFound`.
- **Unfillable template:** a read of a path template that still contains a placeholder the gateway cannot fill is never sent with unexpanded braces. It is denied with `VF_PATH_TEMPLATE_UNFILLABLE` (HTTP 403) before any upstream call. This covers a full read (`DuckDbHashJoin`, `PushdownEnabled = false`), a key value that is unsafe for a path, a second placeholder, and `Adaptive` with more keys than `MaxPathLookupRequests`. `PushdownOnly` and `ShortCircuitOnly` keep `VF_PUSHDOWN_UNAVAILABLE` or `VF_PUSHDOWN_LIMIT_EXCEEDED`. Tier 0 and a Tier 1 miss skip the upstream and are not denied.

### 2.8 Domain model: `FilterBinding`

Federation is configured per binding of an access profile. The binding properties below are stored with the profile (`VirtualFilterStore`) and participate in the definition hash.

| Property | Type | Default | Description |
|---|---|---|---|
| `TargetPattern` | `string?` | profile scope | `source.schema.object` pattern, or a regex (prefix `^` or `regex:`). A regex is matched as a full match against `domain.schema.table` only, case-insensitively, with the non-backtracking engine and a 50 ms timeout. A pattern longer than 256 characters, or with backreferences or lookarounds, is rejected at write time. A pattern that cannot be evaluated at query time denies the query (`VF_PATTERN_EVALUATION_FAILED`); it is never treated as "no match". |
| `ColumnMap` | `IReadOnlyDictionary<string,string>?` | same name | Key column of the filter to column of the protected object. |
| `Strategy` | `VirtualFilterExecutionStrategy` | `Adaptive` | `Adaptive`, `ShortCircuitOnly`, `PushdownOnly`, `DuckDbHashJoin` (section 3.2). |
| `PushdownFormat` | `PushdownParameterFormat?` | `null` | `null` uses the endpoint capability. See section 2.6. |
| `MaxPushdownKeys` | `int` | `100` | Per-binding key budget, `1..1000`. The gateway-wide limit applies on top. |

The key columns come from the filter (`KeyColumns`); a binding has no key list of its own.

---

## 3. Configuration & Usage Examples

### 3.1 `appsettings.json`

All options live under the `Gateway` section. Out-of-range values of `VirtualFilters:Federation` and `DataSources:Http:MaxResponseBytes` are rejected (fail closed).

```json
{
  "Gateway": {
    "WebSql": {
      "CrossSource": {
        "Enabled": true,
        "AllowedTransports": [ "WebSql", "Trino" ],
        "MaxStagedRowsPerTable": 50000,
        "MaxStagedBytesPerTable": 33554432
      }
    },
    "VirtualFilters": {
      "Federation": {
        "KeyCacheTtlSeconds": 30,
        "MaxAuthorizedKeys": 50000,
        "KeyQueryTimeoutSeconds": 10,
        "KeyCacheSizeLimitBytes": 67108864,
        "PushdownEnabled": true,
        "MaxPointLookupTuples": 1000,
        "MaxPushdownKeys": 1000,
        "MaxPushdownUrlLength": 2048,
        "MaxPathLookupRequests": 10
      }
    },
    "DataSources": {
      "Http": {
        "MaxResponseBytes": 33554432
      }
    }
  }
}
```

| Option | Default | Valid range | Description |
|---|---|---|---|
| `WebSql:CrossSource:Enabled` | `false` | bool | Cross-source queries (and therefore federated Virtual Filters) are off until enabled. |
| `VirtualFilters:Federation:KeyCacheTtlSeconds` | `30` | `1..300` | Lifetime of a cached key set; also the revocation latency. |
| `VirtualFilters:Federation:MaxAuthorizedKeys` | `50000` | `1..1000000` | Largest authorized key set; more denies with `VF_KEY_SET_TOO_LARGE`. |
| `VirtualFilters:Federation:KeyQueryTimeoutSeconds` | `10` | `1..300` | Timeout of the key query. |
| `VirtualFilters:Federation:KeyCacheSizeLimitBytes` | `67108864` (64 MiB) | `1 MiB..4 GiB` | Size limit of the dedicated key set cache. |
| `VirtualFilters:Federation:PushdownEnabled` | `true` | bool | Allows narrowing what is fetched (Tier 1 skip, pushdown). Kill switch only: when `false`, every protected table is read completely (Tier 3) and filtered by the gateway, and enforcement is unaffected. It does not make more rows readable: `ShortCircuitOnly` and `PushdownOnly` bindings are denied (`VF_PUSHDOWN_UNAVAILABLE`) and a path-template endpoint is never read as a full scan (`VF_PATH_TEMPLATE_UNFILLABLE`). |
| `VirtualFilters:Federation:MaxPointLookupTuples` | `1000` | `1..10000` | Largest number of key tuples a point lookup may expand to; more is a scan. |
| `VirtualFilters:Federation:MaxPushdownKeys` | `1000` | `1..10000` | Largest number of keys pushed in one lookup. A binding can only lower it (its own range is `1..1000`). |
| `VirtualFilters:Federation:MaxPushdownUrlLength` | `2048` | `256..65536` | Largest estimated request URL (characters) of a query-parameter pushdown. |
| `VirtualFilters:Federation:MaxPathLookupRequests` | `10` | `1..100` | Largest number of single-key requests of a path lookup. |
| `DataSources:Http:MaxResponseBytes` | `33554432` (32 MiB) | `1 MiB..1 GiB` | Largest response body the `DeclarativeHttp` client accepts. **Global scope:** it applies to every user of that client (REST, OData, GraphQL, WebSQL cross-source reads, data source tests and plugin HTTP sources), not only to federated Virtual Filters. A larger body aborts while it is read with `ROW_LIMIT_EXCEEDED`, before anything is parsed. It is separate from the per-table staging budget `WebSql:CrossSource:MaxStagedBytesPerTable`. |

The upstream rows of a table count against `MaxStagedRowsPerTable` while they are read, **before** the semi-join removes unauthorized rows. A large API without a pushdown capability can therefore exceed the budget and be denied with `ROW_LIMIT_EXCEEDED`.

### 3.2 Strategies and strategy matrix

| Situation | `Adaptive` | `ShortCircuitOnly` | `PushdownOnly` | `DuckDbHashJoin` |
|---|---|---|---|---|
| Empty key set | Tier 0 | Tier 0 | Tier 0 | Tier 0 |
| Point lookup, no queried key authorized | Tier 1 miss, no call | Tier 1 miss, no call | Tier 1 miss, no call | Tier 1 miss, no call |
| Point lookup hit, pushdown possible | Tier 1, pushes the authorized queried keys | Tier 1, pushes them | Tier 1, pushes them | Tier 3 |
| Point lookup hit, no pushdown possible | Tier 3 | `VF_PUSHDOWN_UNAVAILABLE` or `VF_PUSHDOWN_LIMIT_EXCEEDED` | `VF_PUSHDOWN_UNAVAILABLE` or `VF_PUSHDOWN_LIMIT_EXCEEDED` | Tier 3 |
| Scan, pushdown possible within budget | Tier 2 | `VF_SCAN_NOT_PERMITTED` | Tier 2 | Tier 3 |
| Scan, no pushdown or over budget | Tier 3 | `VF_SCAN_NOT_PERMITTED` | `VF_PUSHDOWN_UNAVAILABLE` or `VF_PUSHDOWN_LIMIT_EXCEEDED` | Tier 3 |
| Pushdown switched off (`PushdownEnabled = false`) | Tier 3 | `VF_SCAN_NOT_PERMITTED` for a scan, otherwise `VF_PUSHDOWN_UNAVAILABLE` | `VF_PUSHDOWN_UNAVAILABLE` | Tier 3 |

The tier-skip rows (Tier 0, Tier 1 miss) apply first. The Tier 1 miss skip also requires `PushdownEnabled = true`. When several bindings apply to one table, the most restrictive wins: any `ShortCircuitOnly` denies scans and needs a pushdown for a hit, any `PushdownOnly` makes the pushdown mandatory, any `DuckDbHashJoin` forbids pushing. A contradiction denies instead of choosing one side. A Tier 3 read of a path-template endpoint is denied as described in section 2.7.

### 3.3 Virtual Filter definition and binding

A structured Virtual Filter whose key source is a SQL data source (`PUT /api/v1/governance/virtual-filters/{name}`):

```json
{
  "source": "mssql_corp",
  "key_columns": ["g.customer_id"],
  "from": { "table": "dbo.tenancy_grants", "alias": "g" },
  "where": [
    { "column": "g.status", "op": "eq", "value": "ACTIVE" }
  ]
}
```

An access profile binds it to the Web-API table (`PUT /api/v1/governance/access-profiles/{name}`). The `map` translates the filter key column to the column of the Web-API table:

```json
{
  "grantee": { "type": "role", "role": "SalesRepresentative" },
  "scope": "web_crm.*.*",
  "uncovered": "deny",
  "bindings": [
    {
      "filter": "vf_authorized_customers",
      "target": "^web_crm\\.public\\.customers$",
      "map": { "customer_id": "id" }
    }
  ]
}
```

The governance HTTP bodies (`PUT /api/v1/governance/access-profiles/{name}` and the profiles of a file repository sync) carry these binding fields:

| Field | Type | Default | Description |
|---|---|---|---|
| `filter` | string | required | Name of the Virtual Filter. |
| `target` | string | profile scope | Target pattern or regex (section 2.8). |
| `object_kinds` | string list | `["relation", "procedure_result"]` | `relation`, `procedure_result`. |
| `time_column` | string | none | Column compared with the filter's validity window. |
| `map` | object | same name | Key column of the filter to column of the protected object. |
| `strategy` | string | `adaptive` | `adaptive`, `short_circuit_only`, `duckdb_hash_join`, `pushdown_only` (section 3.2). |
| `pushdown_format` | string | none (endpoint capability) | `comma_separated`, `post_batch`; `repeated_param` and `odata_in` are recognized but always rejected (see below). |
| `max_pushdown_keys` | integer | `100` | Per-binding key budget. |

The profile response echoes `strategy`, `pushdown_format` and `max_pushdown_keys` for every binding. The values are part of the binding's canonical hash and therefore of the profile's `definition_hash`, so a change follows the same authorization, audit, draft and approval path as any other binding field. Omitted fields keep the defaults.

The rules are enforced by the administration service, so every path that writes a profile is covered: the HTTP API, the file repository sync (plan and apply) and the dbt governance import. A request that violates one of these rules answers HTTP 400 and stores nothing; a file repository sync with such a binding is rejected as a whole; a dbt import reports the profile as a warning and skips it:

- An unknown `strategy`, `pushdown_format` or `uncovered` value. Values must match exactly (snake_case, lower case, as listed above); other cases, names without underscores and numbers are rejected: error code `VF_BINDING_INVALID`.
- `max_pushdown_keys` outside `1..1000`, or an explicitly sent value above the gateway-wide `Gateway:VirtualFilters:Federation:MaxPushdownKeys`: error code `VF_PUSHDOWN_KEY_BUDGET_INVALID`. A binding that omits `max_pushdown_keys` keeps the default `100` and is not checked against the gateway-wide limit; at query time the effective budget is the lower of the two.
- A `pushdown_format` that no endpoint can satisfy (`repeated_param`, `odata_in`), or, when `target` is an exact `source.schema.table` name of a catalog table with an `HttpEndpointDescriptor`, one that the endpoint's capability cannot satisfy (`comma_separated` needs a query-parameter list, `post_batch` a JSON array body; a path lookup satisfies none): error code `VF_PUSHDOWN_FORMAT_UNSUPPORTED`. The check uses the same capability logic as the planner. For a glob or regex target the endpoints are not known at write time, so the planner decides per table at query time (`format_unsupported`: Tier 3, or a denial for `PushdownOnly`).

The error body is `{ "error": "...", "code": "VF_..." }`.

The Web-API table is a catalog entry with an `HttpEndpointDescriptor`. Tier 2 needs these fields:

| Field | Example | Effect |
|---|---|---|
| `BatchType` | `QueryParameterList` | Pushdown kind (`QueryParameterList`, `JsonBodyArray`, `ParallelSingleRequests` for a path lookup). `None` disables the pushdown. |
| `BatchParamName` | `ids` | Query parameter (or JSON property) that carries the keys: `GET /api/v1/customers?ids=101,102,105`. |
| `PrimaryKeyField` | `id` | The pushed column; for a path lookup also the placeholder name in `PathTemplate`. |
| `PathTemplate` | `/api/v1/customers` or `/api/v1/customers/{id}` | A placeholder other than `PrimaryKeyField` cannot be filled. |
| `Pagination` | unset | A paged endpoint has no pushdown capability. |
| `MaxConcurrentRequests` | `10` | Throttles the per-key requests of a path lookup. |

### 3.4 WebSQL cross-source queries

```sql
-- Point lookup. If 999 is not authorized, no HTTP request is sent and the
-- result is identical to the result for a customer that does not exist.
SELECT id, name, email
FROM web_crm.public.customers
WHERE id = 999;

-- Scan. The authorized keys are pushed when the endpoint supports it, otherwise
-- the table is read completely; the gateway removes unauthorized rows either way.
SELECT COUNT(*) FROM web_crm.public.customers;

-- Cross-source join of a protected Web-API table with a SQL table.
SELECT c.name, o.total
FROM web_crm.public.customers c
JOIN sales.dbo.orders o ON o.customer_id = c.id
WHERE o.status = 'OPEN';
```

---

## 4. Security, Zero-Trust & Fail-Closed Behavior

### 4.1 Denial codes and HTTP status

Every denial is a `GatewaySecurityException` with a stable code. The WebSQL endpoint and the global exception handler share one mapping (`GatewayDenialStatusMapper`): `401` for an unauthenticated caller, `400` for `RESPONSE_TOO_LARGE`, and `403` for every other code. The response text is generic; the code is only written to the audit record. A key source that is unreachable is also a `403` (`VIRTUAL_FILTER_SOURCE_UNAVAILABLE`).

| Code | HTTP | Meaning |
|---|---|---|
| `VF_PATTERN_EVALUATION_FAILED` | 403 | A binding's target pattern is invalid, unsupported, overlong or timed out. |
| `VF_ENFORCEMENT_UNAVAILABLE` | 403 | A Virtual Filter applies but a required component or the decision's bindings are missing; or the caller has no subject identity while the tenant has active Virtual Filter profiles (consent bypass). |
| `VF_ENFORCEMENT_INCOMPLETE` | 403 | An applied (filter, binding) pair is not covered by the enforcement, the enforcement belongs to another table, or the row filter was changed outside the supported helper. |
| `VF_KEY_SOURCE_UNSUPPORTED` | 403 | Stage-2 SQL filter, filter with a validity window, non-SQL or untenanted key source table, tables in different data sources, unsupported dialect, invalid definition, or a filter of another tenant. |
| `VF_KEY_TYPE_MISMATCH` | 403 | A key column has a catalog type that cannot be compared safely, or the key counts differ. |
| `VF_KEY_SET_TOO_LARGE` | 403 | More than `MaxAuthorizedKeys` authorized keys. |
| `VF_BINDING_MISCONFIGURED` | 403 | A binding maps a key to a column the protected object does not have, or to an invalid identifier. |
| `VIRTUAL_FILTER_SOURCE_UNAVAILABLE` | 403 | The key source could not be read (connection, query, timeout, cancellation). No upstream request is sent. |
| `VF_SCAN_NOT_PERMITTED` | 403 | A `ShortCircuitOnly` binding forbids a query that is not a point lookup. |
| `VF_PUSHDOWN_UNAVAILABLE` | 403 | A `ShortCircuitOnly` hit or a `PushdownOnly` binding needs a pushdown the endpoint, the binding format or the switch cannot provide. |
| `VF_PUSHDOWN_LIMIT_EXCEEDED` | 403 | The mandatory pushdown exceeds the key, URL or request budget. |
| `VF_PUSHDOWN_ARGUMENT_CONFLICT` | 403 | A pushdown argument would overwrite an argument the gateway already set. |
| `VF_PATH_TEMPLATE_UNFILLABLE` | 403 | The upstream would be read with a path template the gateway cannot fill. |
| `ROW_LIMIT_EXCEEDED` | 403 | A staging row limit or `DataSources:Http:MaxResponseBytes` was exceeded. |
| `RESPONSE_TOO_LARGE` | 400 | The governed result exceeds the byte limit. |

### 4.2 REST, GraphQL, OData and MCP deny before any upstream read

These channels do not federate. A table that cannot attest a database-side filter (an HTTP table) and carries a Virtual Filter predicate without an enforcement is denied with HTTP 403 **before the upstream is read**, so there is no egress and no unauthorized row in gateway memory. The check runs in the governed connector reader and in the executor branch of the execution service.

### 4.3 Consent bypass, identity and transport

- Virtual Filters also apply when the consent bypass is active. If the caller has no subject identity (the synthetic identity matches no grantee) and the tenant has active Virtual Filter profiles, the query is denied with `VF_ENFORCEMENT_UNAVAILABLE` instead of running unfiltered. Without a consent bypass, a missing identity or missing consent services deny the query.
- **Transport gate:** the cross-source path only runs for the transports in `WebSql:CrossSource:AllowedTransports` (default `WebSql`, `Trino`); a request that names another transport is denied.
- Cross-source queries are off until `WebSql:CrossSource:Enabled` is set. DML is rejected.

### 4.4 Fail-closed rules

- A missing decision for a table of the query denies the query; decisions are keyed by the source-qualified table (`domain.schema.table`), so two sources that share a `schema.table` name never share a decision.
- A pair that cannot be enforced exactly is denied; it is never treated as "condition satisfied".
- A key source error, timeout or cancellation denies the query; no upstream request is sent.
- A key set larger than `MaxAuthorizedKeys` is denied, never truncated.
- A pushdown never relaxes enforcement: the gateway filters every row, even when the API honors the pushed keys.
- Declarative HTTP responses are bounded by `DataSources:Http:MaxResponseBytes`; a paged read whose body size cannot be determined is rejected instead of counting zero bytes.
- The upstream client keeps its SSRF protection; the pushdown parameter name must not be a name the gateway sets itself.

### 4.5 Audit events

Audit records never contain key values.

| Event | Fields |
|---|---|
| `WEBSQL_CROSS_SOURCE_QUERY` (start) | `originalSql` (anonymized) and hash, `tables`, `transport`, and `virtualFilters[]` per table: `table`, `filters[]` (`name`, `bindingHash`, `strategy`), `tier`, `tierReason`, `shortCircuited`, `pushdownKeyCount`. |
| `WEBSQL_CROSS_SOURCE_SOURCE_READ` (key source, one per loaded key set) | `kind = virtualFilterKeySource`, `table`, `filter`, `source`, `keyCount`, `droppedKeys` (keys that could not be converted), `cacheHit`. Also written when the plan is denied after the keys were loaded. |
| `WEBSQL_CROSS_SOURCE_SOURCE_READ` (upstream read) | `table`, `stagingName`, `dataSourceType`, `sourceName`, `rowCount`, and for a protected table `vfTier`, `upstreamCalled`, `pushdownKeyCount`, `rowsReceived`, `rowsKept`, `upstreamRequests`, and for a path lookup `upstreamKeysNotFound`. |
| `WEBSQL_QUERY_DENIED` | `reasonCode` = the `VF_*` (or other) code, `error = "Access denied (<code>)"`, anonymized SQL and hash. |
| `QUERY_EXECUTION_ERROR` | `reasonCode` = the exception type name (or `POLICY_VIOLATION` for a policy denial), `error = "Query failed (<reasonCode>)"`, anonymized SQL and hash. The exception message is never stored: it can quote staged data values, and audit entries cannot be redacted. |

Notes:

- `table` and `TargetTable` use the source-qualified form `domain.schema.table` for every source. Consumers that match on `schema.table` must use the qualified form.
- `upstreamRequests` is `0` for a skipped read, one per key for a path lookup, and `1` for a single request. It is **absent for a paged read** (one request per page; the page count is bounded by the endpoint's `MaxPages` and the staged bytes are still counted).
- All records of one query share one trace id (`Activity.Current` trace id when present).

### 4.6 Logging

A denial is logged with its error code and the exception type only (`WebSQL denied by the gateway ({ErrorCode}, {ExceptionType})`), never with the exception object or the inner exception message, because the inner exception of a key source failure may quote data values. The audit `error` field of a denial stores the code only.

---

## 5. Performance

Tier 3 semi-join over 50,000 upstream rows against 10,000 authorized keys, half of the rows authorized (BenchmarkDotNet 0.15.8 `ShortRun`, in process, .NET 10, Release). Run it with `dotnet run -c Release --project benchmarks/Autheris.Benchmarks -- vfenf`. Timings of a short run are indicative; the allocation column is exact.

| Key shape | Mean | Allocated |
|---|---|---|
| Single integer column | 2.12 ms | 390.8 KB |
| Single text column | 2.55 ms | 390.8 KB |
| Composite (2 text columns) | 6.06 ms | 2.29 MB |

390.8 KB is the preallocated result list (50,000 references). The single-column probe allocates 0 bytes per row; the composite probe allocates its canonical key (about 40 bytes per row). The semi-join is far below the transfer cost of the rows.

---

## 6. Limitations

- Only the WebSQL cross-source path federates. REST, GraphQL, OData and MCP answer 403 for a Web-API table that carries a Virtual Filter.
- Only structured Virtual Filters (`from`/`where`/`joins`) are supported as key sources. Stage-2 SQL filters and filters with a validity window are denied (`VF_KEY_SOURCE_UNSUPPORTED`).
- The key source must be a SQL data source (SQL Server, PostgreSQL or SQLite). A Web-API table, a lakehouse table or any other dialect cannot be a key source. All tables of the definition must be in one data source.
- Key types: integer family, `decimal`/`numeric`, `uuid` and text. Other types are denied (`VF_KEY_TYPE_MISMATCH`). Key comparison is ordinal and case-sensitive; case-insensitive key columns are not supported.
- Pushdown formats: query parameter list, bare JSON array body and path lookup. `RepeatedParam`, `ODataIn` and `PostBatch` with an object body are not produced; a binding that asserts them reads Tier 3 (`Adaptive`) or is denied (`PushdownOnly`).
- Pushdown capability is taken from the `HttpEndpointDescriptor` batch fields and a gateway-wide budget; there is no per-endpoint URL length or key limit. The pushed column is the endpoint's `PrimaryKeyField`. Only single-key pairs on that column are pushed; composite keys are enforced by the semi-join only.
- A paged endpoint has no pushdown capability and is always read completely (Tier 3, or Tier 0/Tier 1 miss without a call).
- A request is never split into several requests, and there is no Tier 3 retry after an HTTP 413 or 414; any non-2xx answer fails the query.
- A path-template endpoint is only read through the path lookup: a full read is denied (`VF_PATH_TEMPLATE_UNFILLABLE`).
- A 404 for a key is treated as "key not present"; a misconfigured base path that answers 404 yields an empty result (visible as `upstreamKeysNotFound` in the audit).
- A revoked key stays authorized for up to `KeyCacheTtlSeconds`.
- A binding whose `target` is an exact `source.schema.table` name is checked against the catalog table's endpoint capability when the profile is written (`pushdown_format`, error code `VF_PUSHDOWN_FORMAT_UNSUPPORTED`). A glob or regex `target` can match many endpoints, so it is checked at query time (`VF_BINDING_MISCONFIGURED`, `format_unsupported`), not when the profile is written. `max_pushdown_keys` is checked at write time for every binding that sets it, against `1..1000` and the gateway-wide `Gateway:VirtualFilters:Federation:MaxPushdownKeys`.
- Metrics per tier are not exported; the audit records carry the tier and the denial code.
- Upstream rows count against `MaxStagedRowsPerTable` before the semi-join, so a large API without a pushdown capability can exceed the budget.
- A `NULL` or unconvertible key in the key source is dropped (counted in `droppedKeys`) and never authorizes a row.

---

## 7. Verification

- Unit tests: `VirtualFilterStrategyMatrixTests`, `VirtualFilterKeySetCacheTests`, `PathLookupConnectorTests`, `VirtualFilterEnforcementPerformanceTests`, `WebSqlGatewayDenialMappingTests`, `DeclarativeHttpClientWiringTests`.
- Integration tests: the `FederatedVirtualFilter*` suites cover every tier, composite keys, pushdown per endpoint capability with an upstream that ignores the parameters, path lookup, key source outage, the strategy matrix and denials, response size caps, consent row filters and the deny-before-read behavior of the non-federated channels.
