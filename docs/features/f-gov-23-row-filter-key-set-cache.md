# F-GOV-23: Row Filter Key-Set Cache (Evaluation Strategy Settings, Persistence & Administration)

**Components:** [`RowFilterEvaluation.cs`](../../src/Autheris.Domain/Model/RowFilterEvaluation.cs), [`RlsOptions.cs`](../../src/Autheris.Domain/Options/Security/RlsOptions.cs), [`DatabaseOptions.cs`](../../src/Autheris.Domain/Options/Storage/DatabaseOptions.cs), [`RowFilterEvaluationResolver.cs`](../../src/Autheris.Application/Policy/RowFilterEvaluationResolver.cs), [`RowFilterEvaluationInput.cs`](../../src/Autheris.Application/Policy/RowFilterEvaluationInput.cs), [`RowFilterKeySetCacheOptionsValidator.cs`](../../src/Autheris.Application/Policy/RowFilterKeySetCacheOptionsValidator.cs), [`RowFilterEvaluationAdminService.cs`](../../src/Autheris.Application/Governance/Services/RowFilterEvaluationAdminService.cs), [`AccessPlanningService.cs`](../../src/Autheris.Application/Governance/Services/AccessPlanningService.cs), [`McpAdminTools.cs`](../../src/Autheris.Application/Mcp/Tools/McpAdminTools.cs), [`GovernanceEndpoints.RowFilterEvaluation.cs`](../../src/Autheris.Api/Endpoints/GovernanceEndpoints.RowFilterEvaluation.cs), [`RowFilterEvaluationStore.cs`](../../src/Autheris.Infrastructure/Persistence/RowFilterEvaluationStore.cs)

---

## 1. Overview

A correlated row filter (`FilterType = SubqueryCorrelated`) restricts a table to the rows whose key appears in a dependent table. Two evaluation strategies are modelled for it (`RowFilterEvaluationStrategy`):

| Strategy | Meaning |
| :--- | :--- |
| `Exists` (default) | The correlated filter is evaluated per statement as an `EXISTS` subquery. Always exact. |
| `KeySetCache` | The filter is meant to be evaluated from a cached set of keys (bounded staleness of the dependent data). |

This feature provides the **settings model**: where a strategy, a cache duration and a maximum key count are stored, how the effective value is resolved, how administrators change it (REST and MCP), and how a change is audited.

**Current runtime behaviour.** No statement-execution path reads the resolved strategy. SQL generation, WebSQL, OData, GraphQL and the other query paths do not call `RowFilterEvaluationResolver`; every correlated row filter is evaluated with the `EXISTS` form regardless of the stored or effective strategy. The resolved values are visible through the admin read endpoints and the plan/apply results only.

---

## 2. Levels and Resolution

Settings exist at three levels. Each level holds a strategy, a duration in seconds and a maximum key count; a missing value means "inherit".

| Level | Storage / configuration | Scope |
| :--- | :--- | :--- |
| Rule (`Rule`) | Three nullable columns on `CONSENT_ROW_FILTERS` | One row filter of a consent |
| Object (`Object`) | Table `TABLE_ROW_FILTER_STRATEGIES` | One table (dataset) |
| Global (`Global`) | `Gateway:RowFilters:KeySetCache` | All correlated filters |

`RowFilterEvaluationResolver.Resolve` resolves strategy, duration and maximum keys **independently**, each as rule, then object, then global. Afterwards the duration is clamped to `MaxDuration` and the maximum keys to `MaxKeys` (so lowering a cap applies to existing stored values without rewriting them). The result (`EffectiveRowFilterEvaluation`) carries the strategy, the level that supplied the strategy (`strategySource`: `Global`, `Object` or `Rule`), the clamped duration and the clamped maximum keys.

A level that holds an invalid stored value (undefined strategy number, non-positive duration or key count, possible only through direct database access) is read as `Exists` for that level's strategy, and the invalid number is ignored.

### 2.1 Eligibility

A key-set strategy is only meaningful on an **ALLOW, correlated, non-temporal** rule (`RowFilterEvaluationResolver.IsEligibleRule`). Rule-level settings on any other rule are rejected on save with 400:

| Rule | Rejection |
| :--- | :--- |
| `DENY` effect | "Key-set cache settings are only supported on ALLOW rules." |
| Not a correlated subquery filter | "... only supported on correlated subquery row filters." |
| Temporal filter (target temporal column or dependent valid-from/valid-to column set) | "... not supported on temporal row filters." |

An all-inherit (or explicit `Exists` without tuning values) payload is always accepted, so an administrator can clear a setting on any rule. When a stored rule is not eligible, `ResolveForRule` ignores its settings and returns `Exists` from the rule level.

The object level is not tied to a rule: it applies as the default for the table's eligible filters.

---

## 3. Configuration

### 3.1 Global options (`Gateway:RowFilters:KeySetCache`)

| Option | Default | Range / rule |
| :--- | :--- | :--- |
| `DefaultStrategy` | `Exists` | `Exists` or `KeySetCache`; `KeySetCache` logs a startup Warning (every correlated filter without an explicit setting is meant to use a cache) |
| `DefaultDuration` | 5 minutes | at least 1 second, at most `MaxDuration` |
| `MaxDuration` | 1 hour | at least `DefaultDuration`, at most 24 hours |
| `MaxKeys` | 2000 | 1 to 10,000 |
| `SizeLimitBytes` | 64 MiB | greater than 0; a change needs a restart |
| `KeyQueryTimeout` | 5 seconds | greater than 0 and not above the smallest positive statement timeout of `Gateway:WebSql:ExecutionTimeoutSeconds` and `Gateway:SqlEndpoints:MaxQueryTimeoutSeconds` |
| `BindParameterReserve` | 256 | 0 to 1,000 |
| `MaxConcurrentKeyQueries` | 4 | 1 to 64 |

`RowFilterKeySetCacheOptionsValidator` validates the section on startup together with `GatewayOptions` (`ValidateOnStart`); every violation is reported and startup fails. The admin service reads the caps per call (`IOptionsMonitor`), so a lowered `MaxDuration` or `MaxKeys` applies to the next validation and resolution at once.

### 3.2 Data source attestation

`DataSources:Connections:<name>:KeySetCacheAllowed` (default `false`) is the operator's attestation that the dependent tables of correlated row filters are **not filtered per user at that source** (no source-native per-user row-level security). A key set is computed with a tenant-only session and would otherwise be shared between users with different source-side visibility. The flag is part of the connection options; it does not influence the REST or MCP administration described here.

---

## 4. REST Administration

All endpoints require the `GovernanceAdmin` policy. Request bodies are limited to 4 KiB and bound strictly: `strategy` must be the exact string `Exists` or `KeySetCache` (no numbers, other casings or whitespace), `durationSeconds` and `maxKeys` are integers, unknown properties, arrays, `null` and malformed JSON are a curated 400 without parser text. Omitted values mean "inherit".

| Verb | Route | Purpose |
| :--- | :--- | :--- |
| `GET` | `/api/v1/governance/row-filters/{id}/evaluation` | Read rule-level settings (`id` is the row filter GUID, format `D`) |
| `PUT` | `/api/v1/governance/row-filters/{id}/evaluation` | Replace rule-level settings |
| `GET` | `/api/v1/catalog/datasets/{id}/row-filter-strategy` | Read object-level settings (`id` is the table GUID or `domain.schema.table`) |
| `PUT` | `/api/v1/catalog/datasets/{id}/row-filter-strategy` | Replace object-level settings |
| `DELETE` | `/api/v1/catalog/datasets/{id}/row-filter-strategy` | Clear the object level (all inherit) |

Request body of both `PUT` routes:

```json
{ "strategy": "KeySetCache", "durationSeconds": 120, "maxKeys": 500 }
```

Response of every successful call (200):

```json
{
  "level": "rule",
  "id": "<row filter GUID or table identifier>",
  "stored": { "strategy": "KeySetCache", "durationSeconds": 120, "maxKeys": 500 },
  "effective": { "strategy": "KeySetCache", "strategySource": "Rule", "durationSeconds": 120, "maxKeys": 500 }
}
```

`stored` holds the values at that level (null = inherit); `effective` is the resolved, clamped result. For a rule, the effective value is resolved with the table's object level and the global options. An undefined stored strategy is shown as `Invalid`, never as a number.

| Status | Condition |
| :--- | :--- |
| 400 | Malformed or non-strict body, strategy name unknown, value not positive, value above the current cap, rule not eligible (section 2.1) |
| 403 | Missing `GovernanceAdmin`, or the caller has no user SID (fail closed, no placeholder actor is audited) |
| 404 | Unknown id, unparseable id, inactive dataset, or a row filter of another tenant. The body is a fixed text without the id, identical for unknown and foreign-tenant rules |
| 413 | Body larger than 4 KiB |

The rule level requires the consent's tenant to equal the caller's tenant. The object level is not tenant scoped (a table has one object-level setting); the caller's tenant is used for the audit entry.

---

## 5. MCP Administration

Both tools are admin tools (available to cluster-admin sessions only) and reject unknown arguments.

| Tool | Purpose |
| :--- | :--- |
| `admin_plan_row_filter_evaluation` | Validates a change and stores a plan; no mutation |
| `admin_apply_row_filter_evaluation` | Applies a confirmed plan |

**Plan.** Arguments: exactly one of `rowFilterId` (rule level, an eligible rule of the caller's tenant) or `datasetId` (object level, table id or `domain.schema.table`), plus `strategy`, `durationSeconds`, `maxKeys` and an optional `reason`. All values are replaced as given; an object-level plan with no values clears the object level. The result contains the `planId`, level, target, `before` and `after` settings, warnings (a `KeySetCache` plan warns about bounded staleness of dependent data) and the expiry. The plan is stored with `PlanKind = RowFilterEvaluation`, bound to the planning administrator and tenant, and counts toward the per-administrator pending plan limit.

**Confirm.** The plan is confirmed with the existing two-phase confirmation (TOTP step-up, ADR-03/ADR-05), which issues an HMAC-signed confirmation token.

**Apply.** Arguments: `planId`, `confirmationToken`. Apply accepts only plans of its own kind: an access plan cannot be applied through it and a row filter evaluation plan cannot be applied through the access apply, in both directions. It also checks that the plan is unexpired, belongs to the confirming administrator and tenant, and that the token matches the confirmed token and is consumed once cluster-wide. Before the token is consumed:

- the change is **re-validated against the current caps** (a cap lowered since planning rejects the plan), and
- **optimistic concurrency** is checked: the currently stored settings must equal the plan's `Before`, otherwise apply fails and a new plan is required.

The repository write itself is not conditional on `Before`; after the write the returned previous settings are compared with the plan and a Warning without values is logged on mismatch (an admin-versus-admin race between the check and the write, both changes are audited). A rule or dataset that does not exist, or a rule of another tenant, produces the same fixed text.

---

## 6. Audit and Policy Epoch

| Event type | Written for |
| :--- | :--- |
| `ROW_FILTER_EVALUATION_CHANGED` | Rule-level change |
| `TABLE_ROW_FILTER_STRATEGY_CHANGED` | Object-level change (set or clear) |

Both events use the fail-closed audit policy and are written through the HMAC-chained audit writer. The entry records the tenant, actor SID, target table, decision `ALLOW`, a trace id, and a details document with `level` (`rule` or `object`), `ruleId`, `before` and `after` (strategy name, duration, maximum keys) and the new `epoch`. No data values are recorded.

The audit entry is written **after the change is committed**. If the audit store is unavailable, the call fails with an error although the change has already been committed (it is not rolled back); the settings are in effect and the failure is visible to the caller.

Every change bumps the policy epoch of the target table in the same database transaction as the settings write, and the cached epoch of that table is invalidated after the commit, so consumers that validate against the epoch observe the change. For a rule-level change the target table is the table of the rule's consent.

---

## 7. Persistence

The baseline DDL of all three engines (PostgreSQL, SQL Server, SQLite) defines:

| Table | Column | Type (SQLite notation) | Meaning |
| :--- | :--- | :--- | :--- |
| `CONSENT_ROW_FILTERS` | `key_set_strategy` | `INTEGER NULL` | Rule-level strategy (0 `Exists`, 1 `KeySetCache`) |
| | `key_set_duration_seconds` | `INTEGER NULL` | Rule-level duration |
| | `key_set_max_keys` | `INTEGER NULL` | Rule-level maximum keys |
| `TABLE_ROW_FILTER_STRATEGIES` | `table_id` | `TEXT PRIMARY KEY` | Table the setting belongs to |
| | `strategy`, `duration_seconds`, `max_keys` | `INTEGER NULL` | Object-level settings |
| | `updated_by`, `updated_at` | `NOT NULL` | Actor SID and UTC timestamp of the last change |

The object level lives in its own table because catalog ingestion upserts rewrite the `TABLES` rows. Clearing the object level (or storing all-inherit) deletes its row. The shared `RowFilterEvaluationStore` implements read and write for all engines; the callers own the transaction and the epoch bump. Value ranges are not enforced in storage: the admin service validates against the current caps on write, and the resolver treats invalid stored values as described in section 2.

---

## 8. Accepted Residual Risks

- The admin-versus-admin race of section 5 (the check against `Before` precedes a non-conditional write) is detected afterwards and logged, not prevented.
- A failing audit store reports an error after the change was committed (section 6).
