# REQ: Enforce per-data-source `AllowedTransports` on every ingress transport (issue #447)

Phase 1 (product manager). Source: GitHub issue #447 (themulle/autheris). Date: 2026-11-03.
Status tracking lives in `docs/plans/00-master-plan-overview.md` (PM links it).

## 1. Problem

`DataSourceProfileOptions.AllowedTransports` (`DataSourceTransportFlags`: WebSql, GraphQL, Rest, OData, Mcp, Trino, ArrowFlight; default `All`; SEC-DS05-03) is meant to restrict which ingress transports may query a data source. The check exists (`DataSourceAllowlistGuard.EnsureTransportAllowed`, `IDataSourceOptionsResolver.IsTransportAllowed`, `EffectiveDataSourceProfile.IsTransportAllowed`) but no production path calls it. A GraphQL-only source is still reachable over WebSQL, Trino `/v1/statement`, MCP `query_sql`, OData, REST and Arrow Flight SQL. This bypasses app-scoped trusted documents, GraphQL cost analysis and `MaxResponseRows`; there is no scope that limits WebSQL `SELECT`. Nothing warns the operator that the setting is inert.

Related but distinct: `Gateway:WebSql:CrossSource:AllowedTransports` (string list, `FederationGate`, `CrossSourceOptionsValidator`) controls which transports may run cross-source federation. It is already enforced and is out of scope except where REQ-ATR-07 composes with it.

## 2. Requirements

Core enforcement
- REQ-ATR-01: A single choke point `DataSourceTransportGate` (Application layer) decides `(dataSourceName, transport)`; all transports call it. No transport re-implements the flag logic.
- REQ-ATR-02: Fail-closed. Deny when: flag not set, resolver missing/not registered, data source unknown/unresolvable, transport value `None`/unmapped, or evaluation throws. `EnsureTransportAllowed` must no longer treat `resolver == null` as allow (remove the fail-open null branch; resolver becomes a required dependency).
- REQ-ATR-03: The check runs before any data access, query planning that reaches the source, or result streaming, and for EVERY data source a request touches (multi-source GraphQL, bulk, cross-source staging). One denied source denies the whole request (no partial results).
- REQ-ATR-04: Denial returns a transport-native error with a stable code `TRANSPORT_NOT_ALLOWED` (HTTP 403 for HTTP transports; GraphQL error with `extensions.code`; MCP tool error; Flight `PERMISSION_DENIED`; Trino error object). The message names the transport and data source, never other sources, config or internals. Must not be distinguishable from "forbidden" in a way that leaks the existence of sources the caller could not otherwise see (unknown source and denied transport return the same shape).
- REQ-ATR-05: Every denial is audited (existing audit pipeline): caller identity, transport, data source, correlation id, reason `TransportNotAllowed`, outcome Denied. Audit write failure must not turn a denial into an allow; denial stands. Add a bounded-cardinality metric `autheris_transport_denied_total{transport,datasource}`.
- REQ-ATR-06: Transport identification is derived by the server from the entry endpoint, never from client input (header, query string, tool argument). Internal re-entry (for example MCP `query_sql` delegating to the governed SQL service) must carry the ORIGINAL transport (Mcp), not WebSql, so the MCP flag is what is checked and a WebSql-only restriction cannot be bypassed or confused by delegation.
- REQ-ATR-07: Cross-source requests require the transport flag on every participating source AND the existing `CrossSource.AllowedTransports` gate; both must pass.
- REQ-ATR-08: Catalog/metadata surfaces (OData `$metadata`, MCP tool listing and schema discovery, GraphQL introspection, REST discovery, Flight GetTables/GetSchemas) must not list data sources or tables of sources whose transport is denied for that transport. Admin/governance endpoints are not data transports and are excluded (see non-goals).

Configuration and operability
- REQ-ATR-09: Startup validation (fail fast, existing options-validator pattern): `AllowedTransports == None` on a source that is otherwise enabled is a warning-level finding "source unreachable by any transport"; a source whose only allowed transport is a transport disabled globally (for example WebSql disabled) is reported as a warning. Warn once at startup and on options reload.
- REQ-ATR-10: Hot reload: changed flags apply to new requests immediately via the existing resolver rebuild; in-flight streaming results may finish, long-lived sessions (MCP session, Trino paged statements, Flight streams) re-check on each next request/page.
- REQ-ATR-11: Operator visibility: the effective allowed transports per source are exposed in the existing effective-profile/diagnostics output so an operator can verify a restriction took effect.

Optional (separable work package, lower priority)
- REQ-ATR-12: Per-caller restriction of WebSQL (for example deny WebSQL to tokens carrying only read-only app scopes). Proposed as a new scope `WebSql.Query` required by WebSQL/Trino/Flight SQL-style ingress when `WebSql:RequireScope` is true. Independent of the per-source flag; ships behind its own work package after REQ-ATR-01..11.

## 3. Acceptance criteria (one test per transport, plus shared gate)

For each transport: with source `S` configured `AllowedTransports = GraphQL` only (and, where noted, a second source `T` with `All`), the request is rejected with `TRANSPORT_NOT_ALLOWED`, no query reaches the database (assert with a counting fake connection/executor), and one audit record plus metric increment is written. A positive control with `S` allowing that transport succeeds.

- AC-ATR-01 GraphQL: with `S` = `Rest` only, a GraphQL query/mutation against `S` is rejected; mixed query touching `S` and `T` is rejected entirely.
- AC-ATR-02 WebSQL (`/v1/sql`/WebSqlEndpoints, including statement fetch/continue): rejected for `S`; SQL referencing `S` in a join with `T` rejected.
- AC-ATR-03 Trino (`/v1/statement` and next-URI paging): rejected on submit; paging a statement after a hot-reload that denies Trino is rejected.
- AC-ATR-04 MCP: `query_sql`, dataset tools, and tool discovery (REQ-ATR-08) deny/hide `S`; stdio runner and HTTP MCP both covered; delegation does not downgrade to the WebSql flag (REQ-ATR-06).
- AC-ATR-05 OData: entity set query, `$count`, `$batch` items, and `$metadata` deny/hide `S`.
- AC-ATR-06 REST (including REST bulk, dataset data endpoints, table stream, Arrow export): each rejected for `S`; bulk with one denied item rejects the request without partial effect.
- AC-ATR-07 Arrow Flight SQL: statement execution, GetTables/GetSchemas deny/hide `S`; Flight status `PERMISSION_DENIED`.
- AC-ATR-08 Cross-source federation: a federated request including `S` from a transport not allowed on `S` is denied even if `CrossSource.AllowedTransports` lists that transport.
- AC-ATR-09 Fail-closed: resolver absent, unknown data source name, `None` flags and a throwing resolver each deny on every transport (parameterised test over all seven transports).
- AC-ATR-10 Audit/metric: denial writes exactly one audit entry with the required fields; failing audit sink still denies.
- AC-ATR-11 Coverage guard (architecture test): enumerates all route/tool/resolver registrations that reach data execution and fails if one lacks a gate invocation or an explicit, reviewed exemption list entry (prevents the "implemented but never called" regression).
- AC-ATR-12 Validation: startup validator emits the warnings of REQ-ATR-09; default (`All`) configuration produces no warning.
- AC-ATR-13 Default behaviour unchanged for sources that keep `AllowedTransports = All`: existing integration suites pass untouched.

## 4. Non-goals

1. Caller-scope based restrictions beyond REQ-ATR-12; row/column governance changes.
2. Restricting admin/governance/SCIM/health/auth endpoints (they are not data transports).
3. New transports, or renaming `DataSourceTransportFlags`.
4. Per-table or per-operation (read/write) transport rules; per-source granularity only.
5. Changing `CrossSource.AllowedTransports` semantics or its string-list type.

## 5. Rejected anti-patterns

1. Calling the guard ad hoc in each endpoint with copy-pasted logic (drift; this is how the check ended up unused). Use one gate plus the coverage test AC-ATR-11.
2. Treating a null/missing resolver, unknown source or exception as allow (fail-open).
3. Taking the transport name from a client header or request parameter.
4. Enforcing only at the HTTP edge; GraphQL/MCP/Flight resolve sources deeper, so the check must follow the data-source resolution of each request.
5. Throwing `WebSqlPolicyException` from non-WebSQL transports; use a transport-neutral exception mapped per transport.
6. Silent no-op configuration: an operator-set restriction that is ignored without warning.
7. Error messages that enumerate sources or reveal which transports are allowed.

## 6. Data and migration notes

No new schema needed: `AllowedTransports` is configuration, not persisted state. Audit reuses existing audit records (new reason value is a string, no column change). If a new audit reason enum/lookup row or a `WebSql.Query` scope seed (REQ-ATR-12) needs persistence, it ships as a forward-only migration step to the newest DB version (v1.5.0 frozen base, no downgrade paths). Autheris is not in production: behaviour changes (sources previously "restricted but reachable" now denied) are acceptable; call it out in release notes.

## 7. Open questions (recommended defaults, "default taken")

1. Empty/unset list: unset means `All` (current default), explicit empty/`None` means deny everywhere. Default taken: keep `All` for unset, `None` denies all and warns (REQ-ATR-09).
2. Unknown data source name on a transport check: deny (fail-closed), same response shape as denied transport. Default taken: deny.
3. Which transport is a Trino/Flight request that carries WebSQL-style SQL internally: its own transport (Trino/ArrowFlight), never WebSql. Default taken.
4. Should denied-transport sources be hidden from discovery (REQ-ATR-08) or listed but unqueryable? Default taken: hide.
5. REQ-ATR-12 per-caller WebSQL scope: include in this issue or split? Default taken: split into a follow-up work package, same plan file, after core enforcement.
6. Is a distinct HTTP status preferable to 403 for denied transport (for example 405/422)? Default taken: 403 with `TRANSPORT_NOT_ALLOWED`.
7. Hot-reload for long-lived streams: re-check per page/request, do not kill running streams. Default taken.
