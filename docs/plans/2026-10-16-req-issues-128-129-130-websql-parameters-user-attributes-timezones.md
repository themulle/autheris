# PRD: Issues #128, #129, #130 - List Parameters, Caller-Attribute Row Filters, Governed Time Zone Conversion

**Document ID:** `REQ-SQL-128` / `REQ-GOV-129` / `REQ-SQL-130` (bundle `REQ-BUNDLE-128-130`)
**Date:** 2026-10-16
**Status:** PRD READY (Phase 1 complete); SECURITY REVIEW pending (Phase 3 after the Phase 2 architecture plan); next: Phase 2 (`csharp-architect`)
**Author:** Enterprise Product Manager (Phase 1)
**Source:** GitHub issues #128, #129, #130 (owner, v1.1.15); this PRD is the English, binding restatement.
**Branch:** `fix/sqlserver-ntlm-catalog-diagnostics` (docs only)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Related plans:** [REQ-FED-01](2026-10-16-req-cross-source-all-consumers.md) (#131 needs #128), [PLAN-SCALE-5000](2026-10-16-plan-scale-5000-users.md) (`WP-SC-04`, `WP-SC-05`, `WP-SC-07`), [REQ-GOV-107](2026-10-15-req-issue-107-row-filter-key-set-cache.md), [PLAN-SQLGATE-CUTOVER-03](2026-10-15-plan-sqlgate-cutover-and-accepted-differences.md)
**Related feature docs (read-only references):** [F-DIALECT-02](../features/f-dialect-02-ast-sql-compiler.md), [F-DATA-02 governed WebSQL](../features/f-data-02-governed-websql.md), [F-GOV-14 federated virtual filters](../features/f-gov-14-federated-virtual-filters.md), [F-GOV-21 declared queries](../features/README.md)

**Binding project constraints:** no upgrade or repair migrations (schema changes go into the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server in place plus the shape snapshots); Autheris is not in production, breaking changes are allowed; fail closed; least privilege on sources (SELECT only); values bound, never spliced; no row values, filter literals or attribute values in logs or audit.

---

## 0. Executive Summary

| Issue | REQ id | Verdict | Size | One-line outcome |
|---|---|---|---|---|
| #128 | `REQ-SQL-128` | **Approved with changes** | M | List parameters (`string[]`, JSON array) only on the right side of `IN`/`NOT IN`; expanded to typed bound parameters; strict header types; bounded against the bind budget. Prerequisite of #131 and of list-valued caller attributes. |
| #129 | `REQ-GOV-129` | **Approved with changes; realises and replaces `WP-SC-04`** | L | `{{caller.<name>}}` / `USER_ATTRIBUTE` row filters as bound parameters with one attribute-provider abstraction (token claims of a trusted realm, SCIM store, governed attribute table); fail closed; same polarity as today. Not a second feature next to SC-04. |
| #130 | `REQ-SQL-130` | **Approved with changes** | S-M | Governed `convert_timezone` with an explicit source zone, validated IANA zone names, per-dialect generation; result type decided by the owner (portability trap, see 4.3); `AT TIME ZONE` stays rejected, accepted-difference process kept. |

**Suggested order:** #128 (M, enables two others) -> #130 (S-M, independent, can run in parallel with #128) -> #129 (L, after #128; lands as `WP-SC-04` inside the scale plan, needs the provider interface from `WP-SC-05`, not its implementations). #131 (REQ-FED-01 section 7.2) follows #128. Rationale in section 5.

## 1. Cross-Cutting Principles

1. **Values are data.** Every list element, zone name and attribute value reaches the database as a typed bound parameter, or (zone names) as a value taken from a closed, gateway-owned map. Never from request text spliced into SQL.
2. **Fail closed with one outcome.** Invalid, missing or over-limit input is refused at validation with a stable error code; for policy attributes the caller sees the same generic outcome for every cause (#30).
3. **One mechanism per concept.** One list-expansion mechanism (shared by #128 list parameters, #129 list attributes, #131 federated lists, and consistent with the batched `IN` of #124 / `Federation/KeyBatching`), one attribute provider interface, one zone map.
4. **The SQL gate stays the authority.** #130 changes the function allowlist only through the accepted-differences process (`accepted-differences.json`, cutover plan), never by loosening the generator guards.
5. **Audit without payload.** Names, counts, codes; no list values, no attribute values (administrator simulation excepted, audited).

---

## 2. REQ-SQL-128 - List Parameters in WebSQL and Declared Queries

### 2.1 Verified current behaviour (branch head `5ae3c046`)

| Claim in the issue | Verified | Evidence |
|---|---|---|
| WebSQL rejects arrays | **Confirmed** | `src/Autheris.Application/Sql/WebSqlParameterValues.cs:13-52`: `Normalize` handles `String/Number/True/False/Null`; arrays and objects hit `default:` and throw `ArgumentException("SQL parameters must be scalar values ...")` (answered 400) |
| Declared-query header knows only scalar types; `string[]` is silently ignored | **Confirmed**, and worse: unknown scalar types also fall back to `string` | `src/Autheris.Application/SqlEndpoints/Services/SqlEndpointLoader.cs:28-30` (`ParamHeaderRegex` allows `[a-zA-Z0-9_]+` for the type, so `string[]` does not match); `:521-527` `if (!match.Success) return;` (the whole header line is dropped, type then inferred); `:536-546` the `switch` default is `typeof(string)`, so `-- @param x: varchar` or a typo is accepted as string |
| Client values are bound by name, type inferred at bind time | Confirmed | `src/TrinoSqlEngine/Ast/Emit/ICompiledSqlBinder.cs:196-207`: for `ParameterOrigin.ClientNamed` the value comes from `clientParameterValues[SourceName]` and `type = InferType(value)`; compile sees one `SqlParameterType.String` placeholder (`SqlEmitterContext.cs:171`). The SQL text is therefore fixed at compile time with exactly one bound slot per client parameter |
| Bind budget exists | Confirmed | `src/TrinoSqlEngine/Ast/Capabilities/DialectCapabilityTable.cs:39` (SQL Server 2100), `:97` (PostgreSQL 65535), `:126` Oracle 32767, `:156` Databricks 1000, `:191` SQLite 32766; `src/Autheris.Domain/Interfaces/IParameterBudgetProvider.cs` (`GetMaxParameters(dialect)`, `CalculateEffectiveChunkSize`) |
| GraphQL supports `in` with a list | Confirmed (`QueryTypes`/GraphQL filter types; row filter `IN` in `TypedRowFilterBuilder`) | issue text, not re-verified line by line |

**New finding (design-relevant):** because compile happens before values are known and the text depends on the number of bound slots, a list parameter cannot be a plain `ClientNamed` slot. The compiler must emit a *list marker* that the binder expands (or the statement is compiled per list length). This also decides the compile-cache and the bind-budget accounting (section 2.5, FR-128-6).

### 2.2 Qualification and deduplication

- **Not a duplicate.** #107 binds subquery results as `IN (@p0, @p1, ...)` internally (policy keys, trusted); #124/`KeyBatching` builds `IN` clauses for staging (gateway keys). #128 is the **client-controlled** list and needs the validation, limits and typing of a client value. The three share one dialect-aware expander (principle 3); the client path adds validation only.
- **Direct dependents:** #131 (federated lists), REQ-GOV-129 (list-valued caller attributes bind like list parameters), FR-25 of REQ-FED-01 (list parameters pushed to a source as bound values).
- GraphQL `in` is unaffected.

### 2.3 Benchmark (brief)

| Product | Practice | Consequence |
|---|---|---|
| **SSRS / Power BI paginated** | Multi-value parameter expands to `IN (@p1, @p2, ...)` in the query text | The common user expectation; their older text-splicing expansion is the anti-pattern to avoid |
| **Metabase, Superset (field filters)** | Multi-select filter becomes `IN (?, ?, ...)` with bound values | Same shape, bound |
| **Hasura / PostGraphile** | `_in: [..]` typed list in GraphQL; PostgreSQL `= ANY($1::type[])` | Array binding is dialect specific; per-value expansion is portable |
| **SQL Server** | Table-valued parameters or `STRING_SPLIT` | TVP needs a user-defined table type (a schema change in the source, rejected: least privilege); `STRING_SPLIT` is a table function (rejected on the governed path) |
| **Hibernate / jOOQ** | Expand `IN` lists, optionally pad to bucket sizes (powers of two) so plan caches are reused | Padding option for Phase 2 (dedupe first, then pad with a repeat of the last element; harmless for `IN` and `NOT IN`) |
| **Anti-patterns rejected** | (1) splicing literals; (2) delimited string plus split function; (3) unbounded lists; (4) `NOT IN` with `NULL` elements | Bound, bounded, `NULL` rejected |

### 2.4 Gatekeeper review (vetoes)

- **V-128-1 Never spliced.** List elements are bound parameters (one per value, or the dialect's array binding where proven safe); a test with quotes, comment markers and `'; DROP` shapes asserts the values never appear in the generated text, in logs or in audit (the audit text of a client statement stays literal-free, `CompiledSqlAuditDigester`).
- **V-128-2 Typed.** Each element is validated against the declared element type (header) or, for undeclared WebSQL parameters, all elements must have the same JSON type class; mixed lists are rejected. The type of every expanded slot is the element type, not `InferType` of the first value.
- **V-128-3 Position restriction.** A list parameter is allowed only as the right side of `IN` / `NOT IN` (and nowhere else: not in `=`, function arguments, `LIMIT`, `OFFSET`, `ORDER BY`, `CASE`, `VALUES`, select list). Checked in the AST (after parse, before compile) with a stable code. `= ANY(@list)` is not accepted (one form only).
- **V-128-4 `NULL`.** A `NULL` element is rejected at validation (an `IN` list with `NULL` never matches, `NOT IN` matches nothing: a silent logic trap and a data-leak-by-confusion in policy contexts).
- **V-128-5 Bounded, counted against the statement.** The expanded count (after de-duplication) plus tenant, policy, mask and all other parameters must stay within `IParameterBudgetProvider.GetMaxParameters(dialect)`; plus a separate, configurable list maximum (`WebSql:MaxListParameterValues`, default 1000, hard cap below the dialect budget). Above either: refuse with the stable code, never truncate. Because the policy parameter count is known only after compile, the check is made after compile at bind time, before any connection is used.
- **V-128-6 Empty list semantics are rendered by the compiler** (`IN ()` is not valid SQL on most engines): `IN` -> `1 = 0`, `NOT IN` -> `1 = 1`. Polarity trap: an empty list in a **policy** context (row filter) must keep the existing polarity rules (ALLOW matches no rows, DENY matches all, RR-L4-01), so the empty-list rendering must not be reused unchanged for policy lists; #129 states the policy polarity explicitly (FR-129-8).
- **V-128-7 Unknown header type is a validation error** (loader refuses the file, as for other invalid headers), including a malformed `type[]`. Consequence for existing declared queries that use informal type names (`varchar`, `text`, `datetime2`, ...): they currently work as `string` by accident; the strict rule needs an explicit alias list (Q-128-2).
- **V-128-8 Row filters, masks and tenant predicate are untouched**; list expansion happens in the client-parameter layer only and must not change the position or number of policy parameters (a test asserts policy bound values identical with and without the list).
- **V-128-9 No oracle.** The error for a list above the bound states the bound and the received count (the caller supplied the list; no governed data is involved).

### 2.5 Functional requirements

- **FR-128-1 Header syntax.** `-- @param <name>: <type>[]` (types: the existing scalar set plus the explicit aliases of Q-128-2), optional `!` (required), optional default `= ["a", "b"]` (JSON array; element types validated at load; invalid default refuses the file), optional `-- @param-max <name>: <n>` is **not** introduced (the global maximum applies; per-parameter maximum only if Q-128-3 says yes).
- **FR-128-2 Unknown type.** Any type token that is not a known scalar, an alias or `<known>[]` refuses the declared query at load with a diagnostic naming the file and parameter (no silent fallback; the whole header line is no longer dropped when it does not match the regex).
- **FR-128-3 WebSQL.** `WebSqlParameterValues.Normalize` accepts a JSON array of scalars for a parameter that is, by the declaration or by use (only as `IN`/`NOT IN` right side), a list; arrays elsewhere keep failing with the stable code. Nested arrays and objects stay rejected. Undeclared client parameters used as a list are allowed in ad hoc WebSQL with the same element rules.
- **FR-128-4 REST declared endpoints.** A list is passed as a JSON array in the body, or as a repeated query-string key (`?departments=a&departments=b`); a comma-separated single value is a **single** string value (never split).
- **FR-128-5 Validation order.** Parse -> position check (V-128-3) -> value validation (type, `NULL`, size, de-duplication, V-128-5 first part) -> compile -> bind-time budget check (V-128-5 second part) -> execute. Each step refuses with its own stable code (`SQL_LIST_PARAM_POSITION`, `SQL_LIST_PARAM_TYPE`, `SQL_LIST_PARAM_NULL`, `SQL_LIST_PARAM_TOO_LARGE`, `SQL_PARAM_TYPE_UNKNOWN`; names to be fixed in Phase 2 against the existing `SqlCompilerErrorMapper` codes).
- **FR-128-6 Expansion.** Compile emits a list marker node for a list parameter; the binder expands it to `IN (@p_0, ..., @p_n)` with one typed bound parameter per distinct value (order preserved, duplicates removed before counting), or uses the dialect's array binding where a capability flag says it is proven (default: per-value). The compiled statement cache key includes the expanded count (or the padding bucket, Phase 2). Empty list per V-128-6.
- **FR-128-7 Analysis and catalog.** Query analysis (#79) and the catalog description of declared queries (`describe_dataset`, declared-query endpoint description) show list parameters with element type, required flag, default, and the effective maximum. Defaults are shown as declared (they are author-written, not caller data).
- **FR-128-8 Federation.** A list parameter in a cross-source query is carried as in REQ-FED-01 section 7.2 (#131); the first step may refuse it with the stable `FEDERATION_*` parameter code until #131 is delivered.

### 2.6 Acceptance criteria

| ID | Criterion |
|---|---|
| AC-128-1 | A declared query with `IN (@departments)` returns the rows for a list of three values over REST and WebSQL |
| AC-128-2 | One value, duplicate values (removed), the maximum number of values, and the empty list behave as specified (`IN` empty: no rows; `NOT IN` empty: all rows) |
| AC-128-3 | One value above the configured maximum, and a list that fits the maximum but exceeds the bind budget together with tenant, policy and mask parameters (SQL Server 2100 fixture), are refused with the stable code; nothing truncated |
| AC-128-4 | A `NULL` element, a list parameter outside `IN`/`NOT IN` (`=`, function argument, `LIMIT`, `ORDER BY`), a mixed-type list and an unknown header type are rejected at validation |
| AC-128-5 | Values with quotes, comment markers and statement separators never appear in the generated SQL text, the audit text or logs |
| AC-128-6 | Row filters, masks and the tenant predicate apply unchanged (two persons, different row filters, same list) and policy parameter values are identical with and without a list |
| AC-128-7 | Analysis and catalog show list parameters with element type and bound |
| AC-128-8 | On PostgreSQL, SQL Server, SQLite (and Oracle where the fixture exists) the same declared query returns the same rows |

### 2.7 Open owner questions

| ID | Question | PM recommendation |
|---|---|---|
| Q-128-1 | Default value of the list maximum? | 1000 (below the SQL Server budget of 2100 minus policy parameters), configurable, hard cap = dialect budget minus 100 |
| Q-128-2 | Strict header types break informal names (`varchar`, `text`, `datetime2`): accept an alias list (`string/text/varchar/nvarchar/char`, `date/datetime/datetime2/timestamp`, ...) or require the canonical names? | Alias list for the informal names that exist in current declared queries (grep the repository samples in Phase 2), everything else is an error. Breaking changes are allowed |
| Q-128-3 | Per-parameter maximum in the header? | No; one global maximum, per-parameter only on demand |
| Q-128-4 | Pad lists to size buckets for plan-cache reuse? | Decide in Phase 2 with a measured plan-cache effect; default off |
| Q-128-5 | `LIKE ANY`/`IN` with a list on both sides, tuple `IN ((a,b),...)`? | Not supported (single-column `IN` only) |

### 2.8 Dependencies and size

Depends on nothing. Blocks #131 (lists), REQ-GOV-129 (list attributes), FR-25 of REQ-FED-01 (list pushdown). Files: `WebSqlParameterValues.cs`, `SqlEndpointLoader.cs`, `SqlEmitterContext.cs`, `ICompiledSqlBinder.cs`, `CompiledSql.cs` (new list marker; `CompiledSqlIsOnlyConstructedByTheEngine` architecture guard applies), `DialectCapabilityTable.cs`, AST list-position visitor, `QueryAnalysisService.Sql.cs`, declared-query description. **Size M:** about 900 LOC production, 1,200 LOC tests.

---

## 3. REQ-GOV-129 - Row Filters Based on Attributes of the Calling Person

### 3.1 Verified current behaviour

| Claim in the issue | Verified | Evidence |
|---|---|---|
| The model has `USER_ATTRIBUTE` | **Confirmed** | `src/Autheris.Domain/Model/ConsentModels.cs:51-52` (`ValueSource` = `LITERAL` or `USER_ATTRIBUTE`, `UserAttribute`) |
| The builders turn it into a constant | **Confirmed** (three places, not two) | `src/Autheris.Application/Sql/Policy/TypedRowFilterBuilder.cs:135-139` (`Constant(isDeny)`); `src/Autheris.Application/Services/RowFilterSqlBuilder.cs:~140-146` and `:~281-289` (`isDeny ? "1 = 1" : "1 = 0"`); the polarity is RR-L4-01 (ALLOW matches no rows, DENY matches all, so `NOT (1 = 1)` blocks) |
| Virtual Filters only know `{{tenant}}` | **Confirmed** | `src/Autheris.Application/VirtualFilters/SqlFilterCompiler.cs:32-35` (`TenantPlaceholder`); the tenant column must be compared in a conjunctive position; the typed form binds the tenant |
| Correlated filters cannot reference the caller | **Confirmed** | `AdvancedRlsPredicateBuilder.CorrelatedExpression` (`src/Autheris.Application/Sql/Policy/AdvancedRlsPredicateBuilder.cs:33`) takes the filter and a parameter namer only; no caller input. So the "attribute table" pattern cannot be written as a correlated filter today either |
| #107 key-set cache can be the basis | **Partly** | The mechanism (single-flight, size-limited cache, governed read connection, `Max+1` read, no database messages) is reusable (REQ-GOV-107 D-4). The #107 cache itself is **not** per caller: its key query runs "with a tenant-only session" and its cache key is the filter definition plus policy epochs. Reusing the cache instance for per-person values would mix a policy cache with an identity cache; use the mechanism, a separate cache instance and key (tenant, realm, subject key, attribute name, epochs) |
| The scale plan already contains this | **Confirmed** | `docs/plans/2026-10-16-plan-scale-5000-users.md` `WP-SC-04` (reduced scope): "`USER_ATTRIBUTE` row filters (bound parameters) for Autheris-enforced sources; attributes from the realm resolver chain" (M, ~400 LOC production / 600 tests); `SEC-SC-11`: attribute values always bound; only resolver chain or verified token claims; missing, empty or over-cardinality values fail closed; values part of the cache shape. `ANALYSIS-SCALE-5000-GOV`: `USER_ATTRIBUTE` fails closed today |

### 3.2 Deduplication and reconciliation with the scale plan (decision)

**#129 is the Phase-1 PRD of `WP-SC-04`: it realises and replaces it, it is not a second work item.** Differences that #129 adds and that `WP-SC-04` must absorb (the scale plan is amended by a pointer, the detailed package plan is written when SC-04 enters Phase 4 against this PRD):

| Topic | `WP-SC-04` (scale plan rev. 4) | #129 | Reconciled position |
|---|---|---|---|
| Where attributes come from | realm resolver chain only | token claims (own trusted issuer, allowlist) + governed attribute table (keyed by `oid`/SID) | One `ICallerAttributeProvider` abstraction; providers: **(1) token claims** of a trusted realm (allowlist claim -> attribute), **(2) SCIM store** (attributes of the SCIM user, e.g. enterprise-user `employeeNumber`, `costCenter`, `department`; arrive with `WP-SC-07`), **(3) governed attribute table** (data-source table read through the gateway's identity), later **(4) Graph/LDAPS** providers of `WP-SC-05`. The chain order and per-attribute provider binding are admin configuration |
| Placement of the placeholder | none (consent filters only) | consent row filters **and** SQL-defined Virtual Filters (`{{caller.<name>}}`) | Both. The consent form keeps `ValueSource=USER_ATTRIBUTE` and `UserAttribute` (no new field on `ConsentRowFilter`, as the scale plan's file-ownership note requires); the Virtual Filter form is the `{{caller.<name>}}` placeholder |
| Token claims vs "identity-only tokens" (scale plan Q1) | not used | claims allowlist | With identity-only Entra tokens the useful token claims are few (`oid`, `tid`, optional `employeeid`); the claim provider is cheap and trustworthy (signed) but narrow. The SCIM store and attribute table carry the business attributes. Both stay in the design; the claim provider is first because it needs no store |
| Scope | Autheris-enforced sources only | all read paths | Autheris-enforced sources (consistent with the scale plan: on `Delegated*` sources the source enforces per user). On those sources an attribute row filter is refused at definition time, not silently ignored |
| Size | M (400/600) | L | Size grows to L (see 3.8); SC-05/SC-07 implementations are **not** part of it |

Dependencies: `ICallerAttributeProvider` interface and the claim provider are delivered by #129 itself (no wait for `WP-SC-05`); the SCIM provider is added when `WP-SC-07` has its store; the attribute-table provider is delivered by #129. If `WP-SC-05` lands first, its resolver chain hosts the providers; if #129 lands first, `WP-SC-05` adopts the interface (Q-129-1).

### 3.3 Benchmark (brief)

| Product | Practice | Consequence |
|---|---|---|
| **PostgreSQL RLS / Snowflake row access policies** | Policy references a session identity function (`current_user`, `CURRENT_ROLE`) or a mapping table joined in the policy | Mapping-table pattern (#129's attribute table) is the standard; identity comes from the session, never from a request value |
| **Databricks Unity Catalog row filters** | Function of `current_user()`/`is_account_group_member()` with a mapping table | Same; in Autheris this stays at the source for `Delegated*` |
| **Power BI RLS** | `USERPRINCIPALNAME()` filter against a user-to-dimension table | UPN-keyed is the known weakness (mutable names): #129 keys on `oid` |
| **Hasura / Cube** | Session variables (`X-Hasura-User-Id`) from verified JWT claims in permission rules | Token-claim provider; the risk is a client-settable header, which #129 forbids |
| **Anti-patterns rejected** | (1) attribute from request header, parameter or body; (2) key by UPN/mail; (3) fail-open on lookup failure; (4) attribute names and values disclosed to the caller; (5) unbounded attribute lists | See vetoes |

### 3.4 Gatekeeper review (vetoes)

- **V-129-1 Source of truth.** Attribute values come only from the registered providers; no header, query, body, parameter, or claim of another issuer influences them. The subject key is the validated `oid`/SID resolved by the existing principal accessors (`GetUserSid()`), never UPN, mail or display name. A test sends every client-controllable channel with a forged attribute and asserts no effect.
- **V-129-2 Always bound.** The value is a typed bound parameter, never part of the SQL text, the cache key as clear text (hash it, like #107 D-4), logs or audit.
- **V-129-3 Fail closed, one outcome.** Missing attribute, empty list, over-cardinality, provider failure, timeout, unknown attribute name at runtime: ALLOW matches no rows, DENY matches all (RR-L4-01 polarity unchanged). The caller sees one generic outcome for every cause (#30); the audit record names the cause (stable code, no values). Row count and timing classes must not reveal which cause occurred (the failed-lookup path and the missing-attribute path run the same constant predicate; a failed lookup is not cached as a hit).
- **V-129-4 Definition-time validation.** An attribute name in a filter is checked against the registered attribute names when the consent filter or Virtual Filter is saved; an unknown name, a non-list attribute used with `IN`/list semantics where a scalar is required (or the reverse), and an attribute filter on a `Delegated*` source are refused at save time. The runtime still fails closed if a name disappears.
- **V-129-5 Attribute-table provider is a privileged read.** It runs with the gateway's service identity (SELECT only) outside the caller's consents, so it is a **fixed, administrator-defined lookup**: table, key column, value column(s), attribute name, data type, maximum cardinality; the SQL is built by the gateway from that definition (AST/typed, never user text), keyed by a bound subject key. Creating or changing a provider definition needs the existing admin four-eyes flow and is audited. The attribute table is not exposed through catalog, WebSQL or MCP to ordinary callers unless separately granted (provider registration marks it `AttributeSource`; a consent on it is refused or warned, Q-129-5).
- **V-129-6 Cache.** Per tenant, realm, subject key and attribute name; short TTL (default 60 s, hard maximum 300 s), size-limited, single-flight, failures never cached; dropped on policy-epoch change, consent or profile revocation, SCIM lock (`WP-SC-07`) and attribute-provider reconfiguration. A separate cache instance from the #107 key-set cache (see 3.1).
- **V-129-7 Cardinality.** `MaxAttributeValues` per attribute (default 200, hard cap below the bind budget; counts against it with tenant, policy and mask parameters, #128 mechanism). Over the cap: fail closed, never truncate (a truncated allow-list would silently narrow, a truncated deny-list would silently widen).
- **V-129-8 Scope never widens.** A restricted token scope (#26) never skips or relaxes an attribute filter; the filter is part of the table decision for every scope.
- **V-129-9 Application-only tokens.** No person behind the token means no subject key: ALLOW matches no rows, DENY all, unless an explicit **service attribute set** is configured for that application (static attributes bound to the client/application id, admin four-eyes, audited).
- **V-129-10 Disclosure (#35, #117).** `describe_my_access`, GraphQL/MCP caller views: "an attribute filter applies" only. Attribute names and resolved values only in the administrator simulation (`effective-filters`, FilterAdmin), itself audited.
- **V-129-11 Operators.** Attribute filters support `EQ`, `NEQ`, `IN`, `NOT IN` against scalar or list attributes with matching cardinality; no `LIKE`, no ordered comparison on a list. `NEQ`/`NOT IN` with a list that may contain `NULL` is refused (as V-128-4).
- **V-129-12 Federated and nested reads.** The same filter applies on the staging read of a federated table, on nested and cross-source GraphQL relations (key lookup reads through the governed path), and under Virtual Filter pushdown (F-GOV-14). A pushed attribute list counts against `MaxPushdownKeys` and the source bind budget; when it does not fit, the read fails closed (not truncated, not unfiltered). The Virtual Filter's enforcement mode that removes rows gateway-side after the read must receive the same resolved attribute values as the pushed form (one resolution per request, shared).
- **V-129-13 One resolution per request.** The attribute set of a caller is resolved once per request (and per table need), then reused by every filter and every nested read, so concurrent reads inside one request cannot see two different attribute states.

### 3.5 Functional requirements

- **FR-129-1 Placeholder.** SQL-defined Virtual Filters accept `{{caller.<name>}}` (`<name>`: `[a-z][a-z0-9_]{0,63}`), replaced by a reserved, typed, bound parameter (origin `CallerAttribute`, new `ParameterOrigin` member in the compiler); scalar attributes in value positions (`employee_id = {{caller.employee_id}}`), list attributes only on the right side of `IN`/`NOT IN` using the #128 expansion (`cost_centre IN ({{caller.cost_centres}})`; the parenthesised form keeps the filter text parseable before substitution, Q-129-2). The placeholder is rewritten before parsing to a reserved synthetic name (existing pattern: `GovernedSqlExecutionService.NormalizeClientParameters`, prefix `__param_`), so a client cannot spell it.
- **FR-129-2 Consent filters.** `ConsentRowFilter` with `ValueSource=USER_ATTRIBUTE` and `UserAttribute=<name>` compiles to `column <op> bound(attribute)` in both builders (`TypedRowFilterBuilder`, `RowFilterSqlBuilder`, three call sites), using the caller context; the constant fallback remains only for the fail-closed outcomes of V-129-3.
- **FR-129-3 Provider abstraction.** `ICallerAttributeProvider` (resolve one or more named attributes for a `SubjectContext`: tenant, realm, subject key, token claims), ordered per attribute by admin configuration; result: values (typed, bounded), `Missing`, or `Failed` (reason code). Providers: token claims, SCIM store (when available), governed attribute table, further providers by `WP-SC-05`.
- **FR-129-4 Attribute registry.** An administrator registers attribute names with type (`string`, `int`, `guid`, ...), cardinality (`single`/`list`), maximum values, provider binding and optional description. Persisted in the governance store (baseline DDL x3, shape snapshots; `SEC-SC-11` shape applies), manageable over REST, MCP admin tools (plan/apply two-phase) and declarative manifest later.
- **FR-129-5 Claims provider.** Allowlist mapping claim type -> attribute name, only for claims of validated tokens whose issuer is a trusted realm; multi-valued claims map to list attributes.
- **FR-129-6 Attribute-table provider.** Per V-129-5; uses the typed read pipeline with `Max+1` detection and the #107 mechanism (single-flight, own timeout, no database messages), batch of attribute names per table in one read.
- **FR-129-7 Cache and invalidation** per V-129-6; counters for hit, miss, failure by code.
- **FR-129-8 Polarity.** ALLOW with an unresolved or empty attribute matches no rows; DENY with an unresolved attribute matches all rows (existing RR-L4-01). The generic empty-list rendering of the compiler (V-128-6: `IN` empty is `1 = 0`, `NOT IN` empty is `1 = 1`) is **not** used for policy values; the policy polarity decides, with explicit tests for ALLOW and DENY.
- **FR-129-9 All read paths.** WebSQL, declared queries, GraphQL (including nested and cross-source relations), OData, REST, MCP, export, async jobs, federated staging, key-set cache key queries (a filter with an attribute is not eligible for the #107 `KeySetCache` strategy unless the attribute value is part of the cache key; first step: not eligible, falls back to `EXISTS`).
- **FR-129-10 Simulation and audit.** `effective-filters` admin simulation shows attribute names and values for a chosen subject (audited as an administrative read); decision audit records carry `attributeFilter: true`, attribute names hashed or by registry id, the outcome code (`Resolved`, `Missing`, `Failed:<code>`, `OverCardinality`), never values.
- **FR-129-11 Service attribute sets** per V-129-9.
- **FR-129-12 Documentation of failure behaviour.** The feature doc states that unresolved attributes yield no rows, and the diagnostics endpoint for administrators shows per-subject resolution state.

### 3.6 Acceptance criteria

| ID | Criterion |
|---|---|
| AC-129-1 | Two persons with different cost centres query the same table through one access profile and each gets only their own rows; a third person without the attribute gets no rows |
| AC-129-2 | The same holds for an attribute from a token claim and from an attribute table, on WebSQL, a declared query, GraphQL (nested and cross-source relation), federated staging and under a restricted scope |
| AC-129-3 | No header, parameter, request claim from another issuer or query text changes the resolved value (forged-input corpus) |
| AC-129-4 | A failed lookup (source down, timeout) and a missing attribute give the caller the same outcome (status, body, row count, timing class within tolerance); the audit codes differ |
| AC-129-5 | After a change in the attribute table the new value applies after the cache TTL at the latest; after revoking the consent or profile, or a SCIM lock, immediately |
| AC-129-6 | An application-only token gets no rows from a table with an attribute filter unless a service attribute set is configured |
| AC-129-7 | `describe_my_access` and other caller views show only "an attribute filter applies"; the administrator simulation shows names and values and is audited |
| AC-129-8 | List attribute above `MaxAttributeValues` fails closed (ALLOW no rows, DENY all), never truncated; list plus policy parameters above the bind budget fails closed |
| AC-129-9 | DENY polarity: a missing attribute in a DENY filter blocks the access (matches all rows in the deny predicate); an empty list in ALLOW matches no rows |
| AC-129-10 | An attribute filter on a `Delegated*` source and an unknown attribute name are refused at save time |
| AC-129-11 | Values with quotes and comment markers in an attribute never appear in SQL text, logs or audit |
| AC-129-12 | The Virtual Filter pushdown and the gateway-side enforcement mode use the same resolved attribute values within one request; a pushed attribute list that exceeds the pushdown bound fails closed |

### 3.7 Open owner questions

| ID | Question | PM recommendation |
|---|---|---|
| Q-129-1 | Confirm that #129 realises and replaces `WP-SC-04` (scale plan row 22) and that the `ICallerAttributeProvider` interface is defined here and adopted by `WP-SC-05`? | Yes. One abstraction, SC-05 hosts the chain and the Graph/LDAPS providers later |
| Q-129-2 | Placeholder form: `IN ({{caller.x}})` (parseable) or `IN {{caller.x}}` as written in the issue? | Parenthesised form; the issue example is accepted as an alias only if the pre-parse rewrite can normalise it without ambiguity (Phase 2) |
| Q-129-3 | First delivery: token claims + attribute table; SCIM store provider when `WP-SC-07` is done; Graph later. Agreed? | Yes |
| Q-129-4 | Default cache TTL 60 s, max 300 s; `MaxAttributeValues` 200 | Yes, configurable per attribute within the hard caps |
| Q-129-5 | May an attribute table also be a normal catalog table (readable by consent)? | No: registered attribute sources are excluded from catalog exposure by default (explicit opt-in per table with a warning) |
| Q-129-6 | Service attribute set for application-only tokens: per application id with four-eyes? | Yes |
| Q-129-7 | Are attribute filters allowed in the `KeySetCache` strategy later (attribute value in the cache key)? | Not in the first step |

### 3.8 Dependencies and size

Depends on #128 (list binding, expander, budget accounting) and the `ParameterOrigin` extension. Coordinates with `WP-SC-05` (interface), `WP-SC-07` (SCIM provider, lock invalidation), REQ-GOV-107 (shared mechanism, file ownership: `ConsentModels.cs`, `RlsOptions.cs`, `DatabaseOptions.cs` after 107 merges, as the scale plan states), REQ-FED-01 (WP-FED-06/11 `TableFilterClause` carries parameters; Virtual Filter pushdown). Files: `TypedRowFilterBuilder.cs`, `RowFilterSqlBuilder.cs` (+ interface), `SqlFilterCompiler.cs`, `CompiledSql.cs` (origin), binder, new `Application/Identity/Attributes/*`, governance repositories x3 (registry tables), baseline DDL x3 + shapes, admin endpoints and MCP admin tool, effective-filters simulation, `EffectiveAccessExplanationService.cs`. **Size L:** about 1,600 LOC production and 2,200 LOC tests (replaces the 400/600 of `WP-SC-04`; the SC-05 chain and SC-07 SCIM implementations are excluded).

---

## 4. REQ-SQL-130 - Governed Time Zone Conversion in WebSQL

### 4.1 Verified current behaviour

| Claim in the issue | Verified | Evidence |
|---|---|---|
| `AT TIME ZONE` is rejected as an accepted difference | **Confirmed** | `tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json` (entry listing `AT TIME ZONE`, justification "Intended fail-closed rejection of unsafe or non-portable SQL constructs ... and AT TIME ZONE", `:93`); `PLAN-SQLGATE-CUTOVER-03` (Task 1.3: `TIMESTAMP '... UTC' AT TIME ZONE ...` stays `AstBuildException`, fixture `80d5942dea95`) |
| No time zone function in the allowlist | **Confirmed** | `src/TrinoSqlEngine/SqlFunctionAllowlists.cs:17-84`: only `extract`, `cast`, `current_date`, `current_timestamp` common; dialect lists have `date_trunc`, `date_part`, `to_char`, `make_*` (PostgreSQL) etc.; no `at_timezone`, `with_timezone`, `convert_timezone`, `switchoffset`, `todatetimeoffset` |
| DuckDB ICU is available offline in the gateway image | **Not verified** | no `icu` reference found in the Dockerfiles or in `DuckDbOlapEngine.cs`; the engine blocks `INSTALL`/`LOAD` for user SQL (`DuckDbOlapEngine.cs:464`). Official DuckDB builds usually include ICU statically; Phase 2 must verify in the image, otherwise the DuckDB mapping is not delivered (Q-130-4) |
| `TimeZoneInfo.TryConvertIanaIdToWindowsId` for SQL Server | **Available (.NET), environment dependent** | needs ICU and tzdata in the container image; with `InvariantGlobalization` the conversion fails. No `InvariantGlobalization` setting was found in the project files (Phase 2 verifies the runtime image). The mapping must be built **once at startup** and cached; a failed startup self-test disables the function (fail closed) |

### 4.2 Qualification, deduplication, benchmark

- **Not a duplicate;** it revisits an accepted difference. The security rationale of that entry (non-portable construct, parser differential) does not apply to a **named function**, which the gate already handles through the allowlist; it applies to the infix `AT TIME ZONE` operator, which stays rejected. The accepted-differences entry for `AT TIME ZONE` is **kept unchanged** (the operator is still rejected); a new, separate entry is not needed because the function is new surface, not a loosened difference. The SQL-gate corpus gains positive and negative cases for the function.
- **Benchmark.** Trino: `at_timezone(ts_with_tz, zone)`, `with_timezone(ts, zone)` (zone is part of the value type; plain timestamps carry none). Snowflake/Redshift/Spark: `CONVERT_TIMEZONE(source_tz, target_tz, ts)` (explicit source and target; argument order **source, target, timestamp**). SQL Server: `AT TIME ZONE` with Windows zone names. PostgreSQL: `ts AT TIME ZONE zone` (IANA; **semantics flip** between `timestamp` and `timestamptz`). DuckDB ICU: `timezone(zone, ts)`/`AT TIME ZONE`. Lesson: the explicit-source function exists in three major products; dialects differ in result type.
- **Anti-patterns rejected:** (1) fixed offset literals (wrong half of the year); (2) accepting free-text zone names or `{{...}}`-built names; (3) passing a user string to the database as a zone and relying on the database to reject it (error oracle, DoS via lookups); (4) enabling the `AT TIME ZONE` operator generally.

### 4.3 Gatekeeper review (vetoes) and findings

- **V-130-1 Closed set of zone names.** The zone argument must be a string literal or a bound client parameter whose value is, case-sensitively, an entry of the gateway's IANA zone list (the .NET `TimeZoneInfo.GetSystemTimeZones()`/ICU list resolved at startup, or an embedded tzdb id list, Q-130-3). Anything else (column reference, expression, concatenation, function result, subquery) is rejected at validation with a stable code. For a bound parameter the check runs at bind time before execution.
- **V-130-2 No injection through the dialect map.** The text or value that reaches the database for the zone is **taken from the gateway's map** (IANA id -> Windows id for SQL Server, IANA id for PostgreSQL/DuckDB), never the client's string. SQL Server: the mapped Windows id is emitted as a bound parameter (or a validated literal from the closed map), not concatenated client text.
- **V-130-3 Result type is not portable (new finding).** SQL Server `datetime AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time'` returns `datetimeoffset`; PostgreSQL `timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Europe/Berlin'` returns a **timestamp without time zone** (local wall clock), and the intermediate `timestamptz` carries the **session** zone offset, not the target's. A portable "timestamp with offset" result therefore cannot be produced by the native operator alone on PostgreSQL. Options: (A) result is the **local wall-clock plain timestamp** in all dialects (SQL Server: cast the converted value to `datetime2`; PostgreSQL native); simple and sufficient for display and grouping by local day, ambiguous only in the repeated hour of the fall-back transition; (B) additionally provide a second function for the offset. Recommendation: (A) in the first step (Q-130-2); the issue's "result is a timestamp with offset" holds only for dialects that support it and is stated per dialect in the documentation if the owner insists.
- **V-130-4 Input types.** `from_zone` conversion applies only to plain timestamp/datetime/date-time inputs; an input that already carries an offset (`datetimeoffset`, `timestamptz`) is rejected (stable code) to avoid double interpretation. Column type from the catalog; unknown type: rejected.
- **V-130-5 Daylight saving.** Transitions follow the database's rules (SQL Server: operating-system zone data; PostgreSQL: its tzdata; DuckDB: ICU); the documentation says that different engines may differ for historic dates and during zone-rule updates. Ambiguous and non-existent local times follow the database's behaviour (documented), identical rows between engines are **not** a requirement outside the tested transition days.
- **V-130-6 Masks and policy.** The function is allowed in the select list and in `WHERE`/`GROUP BY` on `Clear` columns only; on a masked column the existing masked-column guardrails apply (no function result can reveal a masked value; the function is treated like any other function over a masked column).
- **V-130-7 Gate process.** The function enters through the function allowlist and the gate corpus (positive: winter, summer, both transition days, bound-parameter zone; negative: non-literal zone, unknown zone, offset-carrying input, wrong arity, operator form). `accepted-differences.json` is touched only if the differential test shows a new classified difference, and then through the signed-off process.
- **V-130-8 Federated.** In a federated query the function runs in DuckDB (needs ICU, V-130-1 list identical) or, inside a same-source subtree (#123), in the source dialect; `SqlFunctionAllowlists` scope-aware collection of REQ-FED-01 FR-28 applies.
- **V-130-9 No new oracle.** An invalid zone gives the same stable validation error regardless of the data; DST behaviour never makes an error depend on row data (no function that raises for specific timestamps is exposed).

### 4.4 Functional requirements

- **FR-130-1** Function `convert_timezone(from_zone, to_zone, ts)` (argument order per Q-130-1; the issue's example order is `(ts, from_zone, to_zone)`): `ts` a plain timestamp expression, both zones literal or bound parameter per V-130-1; returns the local timestamp of `to_zone` per V-130-3.
- **FR-130-2** Generator mapping: SQL Server `(ts AT TIME ZONE <windows(from)>) AT TIME ZONE <windows(to)>` then cast per V-130-3; PostgreSQL `(ts AT TIME ZONE <from>) AT TIME ZONE <to>`; DuckDB per ICU (verified, Q-130-4); SQLite, Oracle, Databricks, Snowflake and ANSI: rejected with a stable code (`SQL_FUNCTION_UNSUPPORTED_DIALECT`), listed in the dialect capability table as `TimeZoneConversion: false`. Oracle (`FROM_TZ`/`AT TIME ZONE`) and Databricks (`from_utc_timestamp`/`convert_timezone`) are later additions.
- **FR-130-3** Zone registry: startup-built, immutable; IANA list, IANA -> Windows map, startup self-test (one known pair per transition rule); failure disables the function with a clear startup warning and a stable error at use (fail closed).
- **FR-130-4** `cast(<converted> as date)` and `date_trunc('day', <converted>)` are allowed on the result (grouping by local day); both are verified for the day around midnight UTC.
- **FR-130-5** Catalog and analysis (#79): the function shows up in the analysis function list; the documented function list endpoint exposes it per dialect.
- **FR-130-6** Optional follow-up (not in the first step): infix `AT TIME ZONE` for inputs that already carry an offset, only after the owner confirms and with a new accepted-difference review.

### 4.5 Acceptance criteria

| ID | Criterion |
|---|---|
| AC-130-1 | Converting a plain UTC timestamp to `Europe/Berlin` returns the correct local time on SQL Server and PostgreSQL in winter and summer and at both transition days (last Sunday of March, October) |
| AC-130-2 | `GROUP BY cast(convert_timezone('UTC','Europe/Berlin', ts) as date)` assigns rows around midnight UTC to the correct local day |
| AC-130-3 | An unknown zone name, a column or expression as zone, a concatenated zone and an offset-carrying input are rejected at validation with the stable codes; an unknown zone in a bound parameter is rejected before execution |
| AC-130-4 | A zone parameter value containing quotes or comment markers is rejected by the closed-set check and never reaches the database text |
| AC-130-5 | The same query runs in a federated query on the DuckDB side (if Q-130-4 verified) and inside a same-source subtree in the source dialect |
| AC-130-6 | A dialect without support rejects the query with the stable code |
| AC-130-7 | The gate corpus contains the positive and negative cases; `AT TIME ZONE` (operator) is still rejected; the accepted-differences file is unchanged unless a new classified difference was signed off |
| AC-130-8 | Startup self-test failure disables the function and the use returns the stable error |

### 4.6 Open owner questions

| ID | Question | PM recommendation |
|---|---|---|
| Q-130-1 | Argument order: the issue's `(ts, from_zone, to_zone)` or the industry order `(from_zone, to_zone, ts)` of Snowflake, Redshift and Spark? | Industry order (analysts migrating reports expect it); decide before the first release, it cannot change later |
| Q-130-2 | Result type: local wall-clock plain timestamp in all dialects (A) or offset-carrying where supported (B)? | (A) first; offset function later if needed (V-130-3) |
| Q-130-3 | Zone list source: runtime (`TimeZoneInfo`, depends on the image) or embedded tzdb id list? | Embedded id list for validation plus runtime self-test, so validation is identical on every node |
| Q-130-4 | If DuckDB ICU is not in the image, ship SQL Server and PostgreSQL only (federated use blocked until ICU is verified)? | Yes |
| Q-130-5 | Which dialects beyond SQL Server, PostgreSQL, DuckDB in the first step? | None; Oracle and Databricks on demand |

### 4.7 Dependencies and size

No dependency on #128/#129. Interacts with #123 (function scope) and `PLAN-SQLGATE-CUTOVER-03` (corpus). Files: `SqlFunctionAllowlists.cs`, dialect generators (SQL Server, PostgreSQL, DuckDB), `DialectCapabilityTable.cs`, new `TimeZoneRegistry`, AST function-argument validator, `SqlCompilerErrorMapper.cs`, gate corpus fixtures, `QueryAnalysisService.Sql.cs`. **Size S-M:** about 650 LOC production, 900 LOC tests (integration cases on SQL Server and PostgreSQL containers for the transition days).

---

## 5. Cross-Issue Dependencies, Order, Risks

```
#128 (list parameters)  ----> #131 (federated parameters, REQ-FED-01 7.2) ----> #122 list pushdown (FR-25)
        \----------------> #129 (caller attributes; list-valued attributes)  [= WP-SC-04]
#130 (time zones)      independent (parallel to #128)
```

1. **#128 first** (M); it carries the list expander and the `ParameterOrigin` extension that #129 reuses.
2. **#130** in parallel (disjoint files except the dialect generators, coordinate by region).
3. **#129** after #128, as `WP-SC-04` in lane B of the scale plan; the provider interface is delivered first, so `WP-SC-05` can adopt it.

| Risk | Mitigation |
|---|---|
| R-1 List expansion changes compiled-statement caching and the bind budget | Expansion in the binder with count in the cache key; budget check after compile (V-128-5) |
| R-2 Strict header types break existing declared queries | Alias list (Q-128-2); loader diagnostics name file and parameter |
| R-3 Attribute table read with the gateway identity becomes an access path | Fixed administrator definition, four-eyes, not exposed in the catalog (V-129-5, Q-129-5) |
| R-4 Attribute staleness grants access after a person moved | Short TTL, epoch and lock invalidation (V-129-6); owner accepts TTL as the maximum staleness |
| R-5 Timing or row-count side channel between missing attribute and failed lookup | Same constant predicate and generic outcome (V-129-3); timing test with tolerance |
| R-6 Zone conversion semantics differ between engines | Documented; tested on transition days; result type decision (Q-130-2) |
| R-7 Container image lacks ICU/tzdata | Startup self-test, function disabled fail closed (FR-130-3) |

## 6. Security Review Status

Phase 3 pending for all three; the conditions V-128-1..9, V-129-1..13 and V-130-1..9 are the Phase-1 gatekeeper input for the Phase 2 plan and the Phase 3 binding conditions. `REQ-GOV-129` requires a dedicated Phase 3 review (privileged attribute-table read, identity-bearing cache).

## 7. Changelog

- 2026-10-16: Initial PRD (Phase 1), verified against `5ae3c046`.
