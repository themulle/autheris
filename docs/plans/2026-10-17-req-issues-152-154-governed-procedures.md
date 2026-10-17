# PRD: Issues #152, #153, #154 - Governed Stored Procedures: Scope-Aware Columns, Catalog Visibility, Limits and Error Hygiene

**Document ID:** `REQ-PROC-152` (covers #152), `REQ-PROC-153` (#153), `REQ-PROC-154` (#154); bundle `REQ-BUNDLE-152-154`
**Date:** 2026-10-17
**Status:** PRD READY (Phase 1 complete); next: Phase 2 (`csharp-architect`), then Phase 3 for the security slice
**Author:** Product Manager (Phase 1)
**Source:** GitHub issues #152, #153, #154 (owner, v1.1.15/1.1.16); this PRD is the binding English restatement, with deviations named in section 8.
**Parent plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Related:** [#111 dataset tags and catalog visibility](2026-10-16-plan-issue-111-dataset-tags-catalog-visibility.md), [#115 read-path switches](2026-10-16-plan-issue-115-read-path-switches.md), [#127 value profile scope rules](2026-10-16-plan-bug-127-value-profile-scope-rules.md), [#113 self-service requests](2026-10-16-plan-issue-113-self-service-access-requests.md), [waves 2/3 tracker](2026-10-16-plan-waves-2-3-delivery.md); open issues #143 (restricted scopes), #138 (catalog search at scale), #146 (multilingual display names) - no plan files yet.

**Binding constraints:** Autheris is not in production (breaking changes allowed); no upgrade migrations; fail closed; values bound, never spliced; no row values, parameter values or procedure message text in logs or audit.

---

## 0. Executive Summary

| Issue | Verdict | Size | Increment / wave |
|---|---|---|---|
| #152 scope-aware columns, masked/removed meta | **Sensible with changes** (virtual columns rejected, `removedColumns` names rejected, caller-aware OpenAPI rejected) | M | INC-1 slice (precedence bug, wave 1); rest INC-3 (wave 3) |
| #153 procedures in catalog and MCP | **Sensible with changes** (existence-hiding is a security bug and is split out; MCP call tool deferred) | M-L | INC-1 slice (wave 1); catalog entries INC-4 (wave 3, after #111/#138) |
| #154 limit, read-only POST, cap, error text | **Sensible with changes** (error text leak is a security bug; `limit` name collision; bounded scan instead of unbounded) | M | INC-1 slice (wave 1); INC-2 (wave 2) |

The three issues share one root: the procedure path (`GovernedProcedureExecutionService`) is a second, hand-built governance path next to tables, and it has drifted from the table rules (precedence, visibility, limits, throttling). The preferred direction is to **reuse the table-side decisions and the same visibility predicate**, not to add procedure-specific policy machinery.

Security findings that are bugs today (all confirmed, section 2.4): existence oracle through 403 vs 404 and 503, parameter-schema oracle through 400 before consent, raw business error text, missing concurrency cap, `cleared_columns` lifting an explicit scope-specific `Mask`. They go to wave 1 as INC-1 and must not wait for the feature work.

## 1. Problem

1. A data owner cannot treat a procedure output like a table in the scope model: `cleared_columns` is a coarse switch that defeats scope-specific rules, and computed columns have no per-scope control.
2. Clients (portal, agent) cannot discover procedures through the catalog, and callers without consent can still detect that a procedure exists.
3. The procedure path lacks the limits, verbs and error hygiene of the other governed read paths, and its `MaxRows` cut happens before row scoping, so results are silently incomplete.

## 2. Verified Current State (worktree = main `b5c1b5dd`)

### 2.1 Issue #152

| Claim | Result | Code |
|---|---|---|
| Sourceless column survives only if in `cleared_columns`, then `Clear` for all scopes | **Confirmed** | `GovernedProcedureExecutionService.cs:561-571` (`cleared.Contains(col)`, `plan.Add(... Clear, null)`); `cleared` set built at `:529` |
| Sourced column in `cleared_columns` lifts `Mask` to `Clear` | **Confirmed, wider than stated**: lifts any non-`Deny` level, including a `Mask` that comes from an explicit scope-specific consent rule, not only the catalog-sensitivity default | `:605-608` ("per contract" comment at `:604`) |
| Response has no masked/removed info | **Confirmed** | `ProcedureEndpointRoutes.cs:337-339` (`columns, rows, rowCount, truncated`); `GovernedProcedureResult` ctor `:210` |
| `openapi.json` not caller-aware | **Confirmed**, partly: path list is role-filtered (`:85`), columns are generic ("may be masked or removed", `:114-122`); in open-schema mode anonymous callers get **every** active procedure (`:41-55`, `:81-85`) | `ProcedureEndpointRoutes.cs:41-55,78-155` |
| Declared `source:` trusted; only guard `ddl_hash` | **Confirmed** | declared mode: `ProcedureDefinitionParser.cs:568`, drift check `ProcedureRegistrationService.cs:82,101-103`; declared mode itself is off by default (`ProcedureEndpointsOptions.AllowDeclaredValidation = false`, `RestApiOptions.cs:166`) |
| Scope-specific rules exist for tables | **Confirmed**: `ConsentResolutionService.cs:239-243,267-284` (scope-specific rule applies only if the caller has a matching scope; it can only harden) |  |

Missing in the issue: with browse-mode validation (catalog mode) sources are reported by the database, so only declared mode is trusted; the `ddl_hash` warning concerns declared mode only, which is already default-off.

### 2.2 Issue #153

| Claim | Result | Code |
|---|---|---|
| Only a hint line in MCP | **Confirmed** (line moved) | `McpDatasetCatalog.cs:740-742` |
| List and OpenAPI show procedures by `required_roles` only | **Confirmed** | `HandleList` `ProcedureEndpointRoutes.cs:63-76`, `IsVisibleTo` `:241-249` (no roles = every authenticated caller) |
| Roles only from token claims | **Confirmed** | `Sid.cs:153-163` (`GetUserRoles`) |
| No consent: 403; unknown: 404 | **Confirmed** | consent loop `GovernedProcedureExecutionService.cs:161-169` (`SecurityException`, mapped to 403 at `ProcedureEndpointRoutes.cs:354-356`); unknown `:110-113`; role gate already answers 404 (`:127-133`) |
| List has no output columns | **Confirmed** | `:68-75` |

Additional oracles not named in the issue: (a) a non-Active procedure answers **503** (`:135-138`, `ProcedureEndpointRoutes.cs:341-343`) before consent, so a caller without consent learns existence and health; (b) parameter validation errors (400, message names the parameter, `:141`, `:247-252`) run before consent; (c) `row-scope-key` unusable and `no-result-table` also answer 403 (`:152-156`, `:468-473`); (d) 403 body "Access to the procedure is denied." differs from the 404 body "Procedure endpoint not found." (`ProcedureEndpointRoutes.cs:344-355`); (e) referenced tables in `Validation.ReferencedTables` are all consent-checked (`:145-169`), so "visible" must mean "all of them pass", not only the result table.

### 2.3 Issue #154

| Claim | Result | Code |
|---|---|---|
| No `limit`; `MaxRows` cuts before row scope | **Confirmed**: invoker stops after `MaxRows` raw rows and sets `truncated` (`MssqlProcedureInvoker.cs:156-161`, cancel via `TryCancel`), row scope runs afterwards on the already cut set (`GovernedProcedureExecutionService.cs:196-205`, `ApplyRowScopeAsync :444-500`), `truncated` is taken from the raw read (`:210`) | |
| `ReadOnlyTokenMiddleware` lacks `/api/v1/procedures` | **Confirmed**: allow-list `ReadOnlyTokenMiddleware.cs:22-30`, POST check `:75-96`; GET passes at `:75-80`, so parameters travel in the URL today | |
| Concurrency cap not applied | **Confirmed**: used only at `GovernedSqlExecutor.cs:403`, `GatewayExecutionService.cs:265` (gate use `:543`), `GovernedTreeQueryService.cs:412`; default 4 (`DatabaseOptions.cs:21`); gate `ITableReadConcurrencyGate` | |
| Business error text returned unmasked | **Partly wrong**: the text is sanitised only mechanically (control characters to space, cut at 500 chars, `MssqlProcedureInvoker.cs:322-352`), so it is still free text chosen by the procedure author and can contain row data, table names or other callers' identifiers; returned as 422 (`ProcedureEndpointRoutes.cs:357-359`). THROW 50000-59999 and PostgreSQL SQLSTATE `P0001` (`:324-340`) | |
| Validation before consent | **Confirmed**: `:141` before `:158-169` | |

Missing in the issue: the POST body **is** the parameter dictionary (`ProcedureEndpointRoutes.cs:263-300`, `inputs` deserialised flat; unknown keys rejected at `GovernedProcedureExecutionService.cs:245-252`), so a new `limit` key collides with any procedure that declares a parameter called `limit`. `ProcedureMode.Write` exists in the model (`ProcedureModels.cs:9-13`); Phase 2 must confirm no write-mode procedure is reachable through this route before the read-only middleware opens POST for it.

### 2.4 Verified security bugs (basis for INC-1)

| ID | Bug | Where |
|---|---|---|
| SB-1 | Existence oracle: 403 vs 404 vs 503 differs by consent | 2.2 |
| SB-2 | Parameter-schema oracle: 400 before consent | 2.2 |
| SB-3 | Free-text procedure message returned to the caller before row/column governance | 2.3 |
| SB-4 | No per-user/table concurrency cap on a DB-backed path | 2.3 |
| SB-5 | Explicit scope-specific `Mask` lifted by `cleared_columns` (confidentiality gap for the narrower scope) | 2.1 |

## 3. Overlap With Existing Plans and Issues

| With | Overlap | Resolution |
|---|---|---|
| **#143** (sensitive columns clear only with scope-specific Clear rule) | Same precedence question for tables. If tables decide "scope rule wins over base/sensitivity", procedures must follow, otherwise two rule sets exist | `cleared_columns` is defined as exactly "a base `Clear` for catalog sensitivity", resolved by the same `GetEffectiveColumnAccess` oracle used for tables (`TableAccessDecision.cs:396-436`, see #127 plan section 1). #143's outcome is binding for procedures; INC-1's SB-5 fix is written to be compatible with either #143 outcome (R-152-1) |
| **#138** (unified visibility between `search_catalog` and `list_datasets`) | Procedure entries must enter the same search and list with the same predicate | Procedure visibility is one function `ProcedureVisibility` (R-153-1) consumed by REST list, OpenAPI, MCP list/describe/search; INC-4 plugs it into whatever #138 delivers; if #138 is not done, INC-4 uses the current `VisibleTablesAsync` path and the shared function, no second rule |
| **#111** (tiers `Hidden/Summary/Full`, tags) | Procedures have no tier or tag | Procedures are **not** subject to tiers: visible only with consent (equals `DataAccess` semantics). No `Summary` for procedures. Procedure tags are a non-goal; they may reuse #111's `CatalogTags` later |
| **#146** (multilingual display names) | Procedure `summary`/`description` | Not decided here; the procedure description DTO keeps single `summary`/`description` fields and is adapted when #146 defines the display-name mechanism. No localisation work in this PRD |
| **#115** (read-path switches, tool groups) | A future MCP call tool is a new `Query`-group tool; list/describe need switch awareness | Catalog entries obey the discovery switches of #115 where they apply; the call tool is deferred (R-153-9) |
| **#127** | Scope-aware column decision as oracle | Reused (see #143 row) |
| **#113** | `describe_my_access`/request path: a caller without consent could request access to a procedure's result table | Out of scope; `kind: procedure` entries are not requestable in this PRD. Note for #113: request text must not reveal a procedure name to a non-visible caller |
| Waves 2/3 tracker (`origin/feat/issue-107-wp-c`) | Lists no procedure item; waves 1-4 and chunk order unchanged | Lead adds pointers for INC-1..INC-5 (this PRD does not edit the tracker) |

## 4. Best-Practice Benchmark and Decisions

1. **Existence hiding (OWASP ASVS V4/V7, "same response for unknown and unauthorised").** Status, body, headers and audit-visible behaviour for "unknown", "no role", "no consent" and "unavailable without consent" must be identical. Timing is not equalised by sleeps or dummy work: the unknown-name branch is cheaper than a consent evaluation, a difference of milliseconds; adding artificial work is brittle and cache-dependent. Decision: accept and document the residual timing channel, rely on rate limiting and the new concurrency cap; do not add sleeps. 
2. **Authorise before validate.** Input validation reveals the parameter schema; run it only for callers who passed the visibility check (R-154-7).
3. **Error leakage (OWASP A09/CWE-209).** Never return database or procedure free text. Alternative to the issue's wording: default is a generic 422 with a stable code; an opt-in declaration maps a procedure error number (or SQLSTATE) to a fixed code and a fixed gateway-side message written in the declaration. DB text is never forwarded and never logged (log the number and the procedure name only).
4. **Limits after authorisation.** A limit applies to rows the caller may see; `truncated` must describe the scoped set. DB-side pushdown is not possible for an opaque procedure and not needed.
5. **No per-response caller-unaware leaks.** `openapi.json` stays a caller-filtered path list with generic column text; caller-specific column access belongs to an explicit describe call that is not cached across callers.
6. **One precedence for tables and procedures.** No procedure-specific override of consent rules.

### Anti-patterns rejected

| Anti-pattern (from issue text) | Why rejected | Alternative |
|---|---|---|
| "Virtual column with its own consent rules" in the result table's policy | Computed columns have no catalog identity (no type, sensitivity, owner); rules would be stored against names that exist only in a declaration file, orphaned when the procedure changes; needs schema in three repositories plus consent UI and approval flows (#113/#111 surfaces) | Declaration-level per-column policy (R-152-3): same data owner who writes the declaration states the scopes; evaluated with the same scope set, no storage |
| `removedColumns` (names) in the response | Names of `Deny` columns, computed columns and unknown columns are existence information for something the caller must not see; the catalog for this caller already hides denied columns (#111 G-2) | `maskedColumns` (names; those are present in `columns` anyway) and `removedColumnCount` (integer) |
| Caller-aware column access inside `openapi.json` | Per-caller, per-response spec defeats caching/CDN and invites leaks (anonymous open-schema mode lists all procedures) | Caller-aware `describe` (REST + MCP), spec stays generic |
| Return raw THROW text, or only add an optional map | Leaks data by default; opt-in safety is not safe | Generic by default, opt-in mapping to fixed text |
| Unbounded raw read to make row scoping complete | DoS and memory risk; the DB runs the whole procedure anyway | Hard scan cap, then cut (R-154-1) |
| New request key `limit` in the flat POST body | Collides with a declared parameter named `limit`; ambiguous | Reserved name, collision rejected at declaration load (R-154-3) |
| MCP tool that calls procedures now | Adds a free-form data-egress surface to agents before discovery, limits and errors are fixed (section 5.3) | Defer to INC-5 |

## 5. Requirements

### 5.1 Security and consistency slice (INC-1, wave 1)

- **R-153-1 (visibility predicate).** One function `ProcedureVisibility.IsVisible(principal, tenant, definition)` is true only if the role gate passes AND every table the execute path would consent-check (`Validation.ReferencedTables` plus `ResultTable`) is allowed for the caller (no table Deny, no consent row filter on a table other than the result table, same rule as `EvaluateTableAsync`, `GovernedProcedureExecutionService.cs:386-406`). For a non-Active procedure only the result-table consent is evaluated.
- **R-153-2 (unified denial).** Execution answers **404** with the body "Procedure endpoint not found." (identical bytes, headers and content type) for: unknown name, role denied, consent denied, `no-result-table`, and unavailable procedure when the caller fails the visibility check. A visible caller gets 503 for unavailable. The route layer no longer maps `SecurityException` from the visibility stage to 403. 403 remains only for failures that occur after visibility is established and that are not existence-revealing (row-scope key misconfiguration, R-153-2a: answer 503 "unavailable" instead, because it is an operator error and 403 is misleading).
- **R-153-3 (order).** Fixed order: identity, role gate, visibility/consent (404 on failure), concurrency lease (R-154-5), parameter validation (400), call, row scope, column governance, audit. Server-side audit keeps the precise reason (`role`, `consent`, `no-result-table`, `unavailable`); unknown names are audited with reason `unknown` at the existing endpoint audit level (no extra row flood: rate limited by the endpoint filter).
- **R-153-4 (list and OpenAPI filter).** `GET /api/v1/procedures/` and `openapi.json` list a procedure only if `IsVisible` is true and it is Active. In open-schema (anonymous documentation) mode procedures are not listed for anonymous callers (deviation, owner question Q-4).
- **R-154-4 (generic error default).** A `ProcedureBusinessException` returns 422 `{ "error": "The procedure rejected the request.", "code": "PROCEDURE_REJECTED" }` regardless of the raised text. The exception no longer carries message text out of the invoker; it carries the error number or SQLSTATE only. Logs and audit contain number and procedure name, never text.
- **R-154-5 (concurrency cap).** The gateway acquires a lease from `ITableReadConcurrencyGate` per tenant, user SID and each consent-checked table (same key shape as `GovernedSqlExecutor.cs:403-420`, limit `MaxConcurrentReadsPerUserAndTable`; 0 disables), after the visibility check (so a throttled response never reveals existence) and released in `finally` after the audit. Over limit: 429 with `Retry-After` through the existing `GatewayThrottledException` mapping.
- **R-152-1 (precedence bug SB-5).** `cleared_columns` may lift only a `Mask` that results from the catalog-sensitivity default; it never lifts a `Mask` or `Deny` produced by an explicit consent rule (base or scope-specific). Implementation oracle: the consent decision's explicit per-column level versus `GetEffectiveColumnAccess`. Behaviour when no explicit rule exists is unchanged. If #143 changes the sensitivity default (clear only with scope-specific Clear), `cleared_columns` follows that outcome unchanged.

### 5.2 Limits, verbs, errors (INC-2, wave 2)

- **R-154-1 (bounded scan, complete scoping).** When a consent row scope is active for the result table, the invoker reads up to `Procedures:MaxScanRows` raw rows (new option, default 10 x `MaxRows`, `[Range(1,100000)]`, never below `MaxRows`; still bounded by `MaxResponseBytes`), applies the row scope once on the materialised raw set, then cuts the scoped set to the effective limit. Without row scope the read stays at effective limit + 1. No unbounded read. Streaming batch scoping (stream, count after filter, stop at limit + 1) is allowed as a Phase 2 optimisation but not required.
- **R-154-2 (truncated semantics).** `truncated` is true if and only if more scoped rows exist than were returned, or the scan cap was reached with unread raw rows. It is computed after row scoping. The `X-Autheris-Truncated` header follows. A scoped result that fits is never flagged truncated because raw rows were cut.
- **R-154-3 (limit).** Optional `limit` (integer >= 1) as GET query key or POST body key. Effective limit = `min(limit, MaxRows)`; a value above `MaxRows` is clamped (same as WebSQL clamping, `QueryGovernorOptions.cs` SR-H-05), a value below 1 or non-integer is 400. `limit` is a **reserved name**: a declaration with a parameter named `limit` (case-insensitive) fails to load with a clear diagnostic. No offset, no cursor (a procedure has no stable order; non-goal). The limit is applied after row scoping and before column governance counts; it is not pushed into the procedure. Order of rows is the procedure's; document that a sample without `ORDER BY` inside the procedure is arbitrary.
- **R-154-6 (read-only POST).** `ReadOnlyTokenMiddleware` permits `POST /api/v1/procedures/{name}` (exactly one segment after the prefix, and not `openapi.json`) for read-only tokens. No other POST under the prefix. Precondition verified in Phase 2: only read-mode procedures are executable on this route (`ProcedureMode.Write` unreachable, and the SESSION_CONTEXT/RLS read-only contract in `MssqlProcedureInvoker.cs` holds); `X-Original-Method`/`X-Forwarded-Method` checks stay.
- **R-154-8 (declared error map).** A declaration may contain `error_codes: [{ number | sqlstate, code, message }]`. `code` matches `^[A-Z][A-Z0-9_]{2,63}$`, `message` is a plain fixed string of at most 200 chars (no placeholders, no DB text). A raised error whose number/SQLSTATE is in the map returns 422 with that `code` and `message`; any other returns the generic 422 of R-154-4. Duplicates or malformed entries fail declaration load.
- **R-152-6 (ddl_hash warning).** On registration and on every hot reload, a declared-mode procedure without `integrity: ddl_hash` logs one warning naming the procedure. New option `Procedures:RequireDdlHashForDeclared` (default false in Development, true otherwise): when true, such a declaration is rejected (stays unregistered). Catalog-mode procedures are exempt (sources come from the database).

### 5.3 Caller-aware columns and metadata (INC-3, wave 3)

- **R-152-2 (single column plan).** The column decision in `GovernResult` (`:503-700`) is extracted into a pure function `PlanColumns(definition, rawColumns, decisions, sources)` returning per column: output name, level (`Clear`/`Mask`/removed) and mask rule. Execution and describe both call it, so the description can never diverge from the data path.
- **R-152-3 (computed columns per scope).** `cleared_columns` entries become `name` or `{ name, cleared_for_scopes: [scope,...] }`. A sourceless column listed with scopes is `Clear` only for a caller whose scope set intersects the list, and for a caller without scopes (interactive full-access token, identical to today); for any other scope set it is **removed** (default) or masked with REDACT when the entry sets `otherwise: mask`. Unlisted sourceless columns stay removed (today's rule). The scope set is the same one the consent resolver uses (`callerScopes`, `ConsentResolutionService.cs:239`). An entry for a sourced column with scopes is a parse error (scope control for sourced columns is the consent rule, R-152-1).
- **R-152-4 (masked/removed meta).** Responses add `maskedColumns` (array of output column names present in `columns` with masked values) and `removedColumnCount` (integer). Names of removed columns are never returned. Always present (empty array/0 when none), for `Deny`, `Mask`, scope-specific `Mask`, and removal of computed columns. Audit gets counts only.
- **R-152-5 (caller-aware describe).** New `GET /api/v1/procedures/{name}/describe` (visibility check as R-153-2; 404 otherwise) returns summary, description, owner, parameters, `limit` support, and the output columns the caller would receive (`name`, `type` when declared, `access: clear|mask`), computed with `PlanColumns` and the caller's decision, without calling the database. `Cache-Control: no-store`. Removed columns are not listed. `openapi.json` stays generic and unchanged apart from R-153-4.

### 5.4 Catalog and MCP (INC-4, wave 3 after #111 and #138; INC-5 deferred)

- **R-153-5 (catalog entries).** `list_datasets` and `search_catalog` return, besides tables, entries `kind: "procedure"` for visible procedures (R-153-1) with id `procedure:<name>` (disjoint from table dataset ids, `McpDatasetCatalog.DatasetId`), summary, owner, parameter count. `describe_dataset("procedure:<name>")` returns summary, long description, owner, parameters (name, type, required, description) and caller-specific columns (R-152-5). An invisible or unknown id returns the identical not-found answer as an unknown dataset (same text, same audit pattern as `FindVisibleAsync`).
- **R-153-6 (search).** Search matches procedure name, summary, description and parameter names only; output column names are indexed only for the visible plan (never from the declaration). Procedure hits are filtered by the same visibility function before ranking and before the result limit, so the limit counts only visible entries (no `limit` applied to the unfiltered set, no total-count disclosure).
- **R-153-7 (hint line).** The static hint (`McpDatasetCatalog.cs:740-742`) is shown only if at least one procedure is visible to the caller.
- **R-153-8 (tenant).** Visibility is evaluated in the caller's tenant (`TenantId` into the decision). Procedure files are global; a procedure never appears to a tenant with no consent on its tables.
- **R-153-9 (MCP call tool, deferred).** No MCP tool to call procedures in this PRD. If added later (INC-5, own PRD) it must be a `Query`-group tool (#115), go through `IProcedureExecutionService` unchanged, require read-only-token compatibility, return the same meta (R-152-4), and carry a response-size guard for LLM clients.

## 6. Security Requirements (binding for Phase 3)

1. **SR-1** Differential test: for unknown, role-denied, consent-denied, no-result-table and unavailable-without-consent the response status, body and header set are byte-identical (GET and POST, with valid and invalid parameters, with and without `limit`).
2. **SR-2** Invalid parameters from a caller without visibility return 404, never 400.
3. **SR-3** No response, log line or audit row contains procedure-raised text; test with a procedure that raises a message containing a data value and a table name.
4. **SR-4** Row scope with `MaxRows` below the raw result: no row of another scope is returned; rows allowed by the scope but beyond the scan cap are reported as `truncated`, never silently dropped.
5. **SR-5** `cleared_columns` never raises an explicit `Mask`/`Deny` rule (base or scope-specific); a computed column with `cleared_for_scopes` is not returned in clear to a token with another scope; unmapped scope sets default to removal.
6. **SR-6** `maskedColumns`/describe never list a column the caller may not see; no removed-column names anywhere.
7. **SR-7** Lease and `finally` release verified under cancellation and exception; cap is per tenant+user+table; lease taken only after visibility.
8. **SR-8** Read-only POST: only `POST /api/v1/procedures/{name}`; other POSTs under the prefix and write-mode procedures stay rejected for read-only tokens.
9. **SR-9** Search/list limit is applied after the visibility filter; result counts and paging metadata carry no information about invisible procedures.
10. **SR-10** Parameter descriptions and procedure summaries are visible only to visible callers; describe output is `no-store`.

## 7. Acceptance Criteria

| ID | Criterion | Req |
|---|---|---|
| AC-1 | A caller without consent on the result table gets 404 from `GET`/`POST /api/v1/procedures/{name}`, identical to an unknown name, also with a malformed or missing parameter set | R-153-2/3, SR-1/2 |
| AC-2 | The same caller does not see the procedure in `GET /api/v1/procedures/` or `openapi.json`; with consent it appears | R-153-4 |
| AC-3 | A non-Active procedure answers 404 to a caller without consent and 503 to a caller with consent | R-153-2 |
| AC-4 | A procedure raising `THROW 50001, 'Asset 4711 of Mueller', 1` returns the generic 422 `PROCEDURE_REJECTED`; with an `error_codes` entry for 50001 it returns that fixed code/message; neither response, log nor audit contains the DB text | R-154-4/8, SR-3 |
| AC-5 | A call beyond `MaxConcurrentReadsPerUserAndTable` returns 429 for a caller with consent and 404 for one without | R-154-5, SR-7 |
| AC-6 | A column in `cleared_columns` with a base `Clear` rule and a `Mask` rule limited to scope A is masked for a token with scope A and clear for a token with scope B | R-152-1 |
| AC-7 | With a row filter that allows 3 of 100 raw rows and `MaxRows` = 10, the caller receives the 3 rows and `truncated=false`; with `MaxRows`/scan cap exhausted before all raw rows were read `truncated=true` | R-154-1/2 |
| AC-8 | `limit: 20` returns at most 20 scoped rows and `truncated=true` when more scoped rows exist; `limit: 0` is 400; `limit` above `MaxRows` is clamped; a declaration with a `limit` parameter does not load | R-154-3 |
| AC-9 | `POST /api/v1/procedures/{name}` with a read-only token succeeds; `POST /api/v1/procedures/` and a write-mode procedure are rejected | R-154-6, SR-8 |
| AC-10 | A computed column listed with `cleared_for_scopes: [app]` is clear for scope `app`, removed for scope `agent`, clear for a scope-less token | R-152-3 |
| AC-11 | Responses carry `maskedColumns` and `removedColumnCount` correct for Deny, Mask, scope-specific Mask and removed computed columns; no removed names | R-152-4, SR-6 |
| AC-12 | `describe` for scope `agent` and scope `app` differ exactly as the data responses differ (parity test against `PlanColumns`) | R-152-2/5 |
| AC-13 | A declared-mode procedure without `ddl_hash` logs a warning naming it; with `RequireDdlHashForDeclared=true` it is not registered | R-152-6 |
| AC-14 | MCP: a consented caller finds `procedure:<name>` through `list_datasets` and `search_catalog` and gets parameters and caller-specific columns from `describe_dataset`; a caller without consent gets the unknown-dataset answer for list, search and describe; search result limit counts visible entries only | R-153-5/6, SR-9 |

## 8. Deviations From the Issue Text (explicit)

| # | Issue says | This PRD |
|---|---|---|
| D-1 | "Virtual column with its own consent rules" | Rejected; declaration-level `cleared_for_scopes` (R-152-3) |
| D-2 | `maskedColumns` and `removedColumns` names | `maskedColumns` names, `removedColumnCount` only (R-152-4) |
| D-3 | "Caller-aware description of output columns" (read as OpenAPI or list) | Separate describe endpoint and MCP describe; OpenAPI stays generic (R-152-5) |
| D-4 | `cleared_columns` vs scope rules: either precedence or `cleared_for_scopes` | Precedence for sourced columns (R-152-1), `cleared_for_scopes` only for sourceless columns (R-152-3) - both, scoped to where each is sound |
| D-5 | Existence hiding as part of the catalog feature | Split out as security bugfix INC-1, delivered independently and first |
| D-6 | Unavailable and other denial paths not mentioned | 503 and `no-result-table` folded into the 404 equalisation (R-153-2) |
| D-7 | "Optional MCP tool to call a procedure" | Deferred (R-153-9) |
| D-8 | `limit` "bounded by MaxRows" | Plus reserved name, clamp rule, scan cap and no pushdown (R-154-1/3) |
| D-9 | Error map "optional per declaration", generic 422 without map | Same direction, but the map holds fixed text only and the generic default also applies when a map exists but misses (R-154-4/8); also removes number-less text from logs |
| D-10 | Concurrency cap per caller and result table | Per caller and every consent-checked table, after visibility (R-154-5) |
| D-11 | Not mentioned | Open-schema anonymous OpenAPI lists all procedures; changed (R-153-4, Q-4) |
| D-12 | `ddl_hash` warning "at start" | On registration and hot reload; applies to declared mode only (R-152-6) |

## 9. Non-Goals

1. Offset or cursor paging for procedures; sorting.
2. DB-side `limit` pushdown or passing `limit` into the procedure.
3. Constant-time response equalisation (sleeps, dummy consent evaluation).
4. Virtual or computed columns in the catalog/consent model; storage of procedure column rules.
5. Procedure tags, `Summary` visibility tier, localisation (#146).
6. MCP/GraphQL tool to call procedures; write-mode procedures.
7. Requesting access to a procedure's tables through #113 flows.
8. Changing consent or masking semantics for tables (owned by #143).

## 10. Increments and Waves

| Inc | Content | Size | Wave | Depends on |
|---|---|---|---|---|
| INC-1 | R-153-1..4 (visibility predicate, unified 404, order, REST list/OpenAPI filter), R-154-4 (generic error), R-154-5 (cap), R-152-1 (precedence fix) | M | **1 (security)** | none; R-152-1 coordinates with #143 decision |
| INC-2 | R-154-1..3, R-154-6, R-154-8, R-152-6 | M | 2 | INC-1 (shared service changes) |
| INC-3 | R-152-2..5 (column plan, `cleared_for_scopes`, meta, describe) | M | 3 | INC-1, INC-2 |
| INC-4 | R-153-5..8 (catalog/MCP entries) | M | 3 | INC-1, INC-3; #111 and #138 for the shared visibility/search path |
| INC-5 | MCP call tool (own PRD) | M | 4 | INC-2..4, #115 |

Order inside the procedure service: INC-1 and INC-2 both touch `GovernedProcedureExecutionService.ExecuteAsync`; they must be sequential, not parallel. INC-3 refactors `GovernResult`; keep it after INC-2 to limit merge conflict.

## 11. Open Owner Questions (recommended defaults)

1. **Q-1** Is the `cleared_columns` lift of an explicit scope-specific `Mask` documented intent for any existing procedure file? Default: no; treat as a bug and change (no production).
2. **Q-2** `cleared_for_scopes`: allow-list (clear only for listed scopes) as specified, or hardening list (`masked_for_scopes`) like table rules? Default: allow-list, fail-closed for new scopes. A scope-less token stays clear in both.
3. **Q-3** Who owns the declaration file edits (data owner vs platform team)? The scope control for computed columns moves from the consent UI to the declaration. Default: platform team with data-owner review; documented, not a code concern.
4. **Q-4** In open-schema (anonymous documentation) mode, should procedures be omitted from the anonymous document? Default: yes, omitted.
5. **Q-5** `MaxScanRows` default 10 x `MaxRows` (50 000 at default 5 000)? Default: yes, configurable up to 100 000.
6. **Q-6** `RequireDdlHashForDeclared` default true outside Development? Default: yes (declared mode is already off by default).
7. **Q-7** Should unknown-name probes (404) be audited with reason `unknown`? Default: yes, same audit level as other denials, endpoint rate limiting applies.
8. **Q-8** Confirm that the MCP call tool is not wanted now. Default: not now; revisit after INC-4 with real agent usage.
9. **Q-9** Row-scope key misconfiguration answered 503 instead of 403 (R-153-2a)? Default: yes.
