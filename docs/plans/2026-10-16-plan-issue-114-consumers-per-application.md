# Implementation Plan: REQ-OBS-114 - Consumers per Calling Application

**Document ID:** `PLAN-OBS-114` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-OBS-114, section 5](2026-10-16-req-issues-111-115-catalog-access-analytics.md) (FR-114-1..8, SI-114-1..5, AC 1..7, Q-114-1..4, spikes T-114-1 and T-114-2)
**Branch:** `feat/issue-114-consumers-per-application` (from `main` after #113 increment A, see section 7); planning base `a5647e50`
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement `REQ-OBS-114`; delivery tracker [PLAN-DELIVERY-W23](2026-10-16-plan-waves-2-3-delivery.md) (step P-9, implementation step I-23)
**Status:** PROPOSED, Phase 3 security review done (2026-10-16): APPROVED WITH CONDITIONS (SEC-114-01..SEC-114-12; conditions C-114-1..C-114-12 bind the revision and Phase 4)

**Binding rules applied:** the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server is extended in place, the `0001` checksum constants stay unchanged, the shape snapshots under `src/Autheris.Infrastructure/Persistence/Migrations/Shape/` (`governance-shape.{sqlite,postgresql,sqlserver}.json`) and their tests are updated (the way #107 added `TABLE_ROW_FILTER_STRATEGIES`); no upgrade or repair migrations; fail closed for everything that exposes data; the caller headers are labels, never an identity and never an input to authorization, rate limits, cost or switches (SI-114-2); CI stays fast (`TimeProvider`/`FakeTimeProvider`, no sleeps); Phase-4 TDD developers are Sonnet; one sequential developer; subagents only implement, the lead builds and tests once at the end (Release, warnings as errors).

---

## 1. Verification of the PRD Against the Code (`a5647e50`)

### 1.1 How the calling application is identified today

| PRD claim | Result |
|---|---|
| Header names | **Confirmed.** `SecurityContextFactory.cs:21-26`: `X-Caller-Application` (alt `X-Application-Name`), `X-Caller-Version` (alt `X-Application-Version`), `X-Caller-Query-Name` (alt `X-Query-Name`). `ExtractCallerContext` (`:80-88`) returns `null` when none of the three is present. |
| Existing sanitation | **Weaker than the PRD assumes.** `SanitizeCallerHeader` (`:103-120`) trims, cuts at **128** characters and drops control characters; **everything else passes** (any Unicode, `=cmd|...` formula starts, markup, quotes, spaces, homoglyphs). The value is stored as given. The PRD's 64-character, `[A-Za-z0-9._ -]` rule therefore does not exist anywhere yet. |
| Value object | `CallerContext(Application, Version, QueryName)` (`src/Autheris.Domain/Security/CallerContext.cs:7`), carried on `SecurityPrincipalContext.Caller` (`SecurityPrincipalContext.cs:31`; anonymous principals keep it, `:118-127`). The doc comment names "audit logs and FinOps attribution". **Correction to the PRD:** `FocusCostAccountingService` (`src/Autheris.Application/FinOps/Services/FocusCostAccountingService.cs`) contains no caller handling at all (grep for `Caller` finds nothing); there is **no existing normalization to reuse**. This plan creates the one normalizer. |
| Ambient resolution | `ICallerContextProvider.ResolveCallerContext()` (`src/Autheris.Application/Interfaces/ICallerContextProvider.cs`), `HttpCallerContextProvider` (`src/Autheris.Api/Security/HttpCallerContextProvider.cs:9-17`) reads `SecurityPrincipalContext.Caller` from `HttpContext.Items`. Registered singleton in `GatewaySecurityServiceExtensions.cs:108`. Works on the request thread only (the group-commit drain loop has no ambient context, hence `AuditWriter.cs:46-49` resolves before enqueueing). |
| Authenticated client id | **Exists in three inconsistent places, none of them in the audit.** `EntraTokenPolicy.cs:59-61` (`azp`, then `appid`, then `client_id`, used for the app-only allowlist `JwtOptions.AllowedClientIds`, `JwtOptions.cs:177`); `ClientTierResolver.cs:152-154` (`client_id`, `azp`, `appid`); `IdentitySubjectResolver.cs:58-60` and `Sid.cs:34-36` (`appid`, `client_id`, `azp`, as a **subject fallback**). The MCP executor writes the session's service principal into `client_id` and `azp` of the forwarded principal (`GatewayMcpQueryExecutor.cs:662-663`). There is no user-agent based attribution. This plan fixes one order (`azp`, `appid`, `client_id`: the order of the allowlist check, which is the one security relevant use) in a new helper. |

### 1.2 Audit records (T-114-1)

| Question | Result |
|---|---|
| Where is the caller stored? | `AuditLogEntry.Caller` (`src/Autheris.Domain/Model/GovernanceModels.cs:52`) is merged into `details_json` as `caller: {app, ver, query}` by `EffectiveDetailsJson` (`:54-86`) **only when `Caller` is set and non-empty**. `Caller` is resolved in each repository's `RecordAuditEventAsync`/`RecordAuditEventsAsync` (`PostgreSqlGovernanceRepository.Audit.cs:24-26, 98-101`, SQLite and SQL Server analogues) and in `AuditWriter.cs:46-49`. It is the **raw sanitized header text**. |
| Tier-A / Tier-B | `AuditChainViolationGate.IsTierA` (`src/Autheris.Infrastructure/Persistence/AuditChainViolationGate.cs:38-44`): every `DENY` and every event type except `TABLE_QUERY` and `WEBSQL_QUERY` is Tier-A (synchronous, under `_auditLock`, `PostgreSqlGovernanceRepository.Audit.cs:37-59`); `TABLE_QUERY`/`WEBSQL_QUERY` `ALLOW` are Tier-B (bounded channel, 2 s timeout, fail closed when saturated, `:67-78`) unless `Audit:SynchronousQueryAudit`. `AuditPolicyTable.cs:26-27` marks both `FailClosed`. |
| Rows returned recorded? | **No.** The ALLOW entry of a table read is written **before** execution: `GatewayExecutionService.cs:260-262` records `is_allowed`, `reasons`, `virtual_filters`, `filter`, `order_by` (`GovernedQueryStages.DecisionDetails`, `WithQueryShape`, `GovernedQueryStages.cs:103-130`); the row count (`processedRows`, `:331`) is never written. The WebSQL entry is also pre-execution (`GovernedSqlExecutor.cs:330-352`) with `originalSql` (redacted), hash, `dataSource`, `virtual_filters`, compiled digest. `AccessAuditMiddleware.cs:162` can write `rows`, but `AuditContext.Rows` (`AuditContext.cs:21`) has no setter caller outside `AuditContext` itself (grep `\.Rows =` finds none). |
| Table attribution of the WebSQL/SQL channel | **Broken for this report.** `TargetTable` of `WEBSQL_QUERY` and `WEBSQL_QUERY_DENIED` is the **data source name** (`GovernedSqlExecutor.cs:302, 348, 556`), not a table. The accessed tables exist only in `GovernedRewrite.AccessedTables` (`src/Autheris.Application/Sql/Internal/GovernedRewrite.cs:13`) and are not audited. `getTableConsumers` therefore already misses every SQL read (it matches `target_table = <table id>`). |
| Rejections attributable to a table and category? | **Partly.** Policy denials of a table read are audited `DENY` with `TargetTable = table` (`GatewayExecutionService.cs:176-178`, column denial `:506-516`, `COLUMN_ACCESS_DENIED`); category is only free text in `details.reasons`. HTTP 401/403/429 are audited by `AccessAuditMiddleware.cs:129-139, 150-152` with `TargetTable = request path` and `reasonCode`; they carry **no table**. The per-user/table concurrency throttle (`GatewayThrottledException`, `GatewayExecutionService.cs:538-544`, `GovernedSqlExecutor.cs:414`, `GovernedTreeQueryService.cs:418`) throws without any audit entry. The cost rejection (`CostLimitExceededException`, only `AggregationCostCalculator.cs:81`, called at `GovernedAggregationService.cs:131`) writes no audit entry either. |
| Storage, partitioning, retention | `AUDIT_LOG_ENTRIES` (`PostgreSqlBaselineSchema.cs:162-183`, SQLite `:167-186`, SQL Server `:178-204`): `occurred_at`, `details_json` and `actor_sid` are **TEXT**; indexes `(target_table, occurred_at)`, `(actor_sid, occurred_at)`, unique `seq`; **no partitioning**; append-only by trigger (`PostgreSqlBaselineSchema.cs:525-540`, SQL Server `:620-625`), so no row can be updated or compacted. `Audit:Retention` classes exist (`AuditLoggingOptions.cs:26-31`: Security 3650, **DataAccess 400**, Aggregated 90, Operations 30) but only `SecurityDays >= 365` and `AuditLogRetentionDays >= 365` are validated (`GatewaySecurityOptionsValidator.cs:30-37`); grep finds **no purge job** and no reader of `DataAccessDays`. |
| Chain / group commit | `AuditChainEngine` (`src/Autheris.Infrastructure/Persistence/AuditChainEngine.cs:13`) hashes `details_json` into `entry_hash` (HMAC when keyed); WP-PF-04 `AuditGroupCommitSink` (`src/Autheris.Application/Auditing/AuditGroupCommitSink.cs:37`) coalesces fail-closed writes of concurrent requests. Adding fields to `details_json` of **new** entries is chain-safe (hash is computed at write over the final text); rewriting old entries is impossible. |

### 1.3 Existing report, authorization, ownership

| Item | Result |
|---|---|
| `getTableConsumers` | `QueryTypes.cs:540-596` (gate: GovernanceAdmin or ClusterAdmin; else owner/delegate `IsAuthorizedApproverForTableAsync`; else active `Allow` consent without unconditional `Deny`; denial `FORBIDDEN`, anonymous `UNAUTHORIZED`). Service `LineageImpactAnalyzerService.GetTableConsumersAsync` (`src/Autheris.Application/Lineage/LineageImpactAnalyzerService.cs:176-362`): `QueryAuditLogsAsync(... limit: 5000 ...)` (`:247-252`), `ALLOW` only (`:254-256`), group by `ActorSid` (`:262-264`), heuristic `ClientType` from SID substrings (`:271-283`), result gated by `IsAuthorizedForAccessAnalysisAsync` (`:367-383`: `callerContext.IsGovernanceAdmin`, role `GovernanceAdmin` or `PrivacyAdmin`, or `IsAuthorizedApproverForTableAsync`). **ClusterAdmin without GovernanceAdmin passes the GraphQL gate but not the per-person gate** (it gets the folded view in this plan, parity with M-15; see owner flag O-2). |
| Ownership / delegates | `IDataOwnershipRepository.IsAuthorizedApproverForTableAsync(table, sid, ct)` (`src/Autheris.Application/Interfaces/IGovernanceRepository.cs:246`) is the single owner-or-delegate check; `GetTransitiveRolesAsync` (`:247`) exists but is not needed here. |
| Audit query helper | `QueryAuditLogsAsync` (`IGovernanceRepository.cs:150-156`): newest `limit` rows by `rowid DESC`; PostgreSQL and SQL Server use `LOWER(target_table) = LOWER(@targetTable)` (`PostgreSqlGovernanceRepository.Audit.cs:700`, `SqlServerGovernanceRepository.Audit.cs:750`), which cannot use the `(target_table, occurred_at)` index; SQLite uses the exact form (`SqliteGovernanceRepository.Audit.cs:704`). `QueryAuditLogPageAsync` (WP-SC-10) is a keyset page over the chain, **unaggregated**. No audit query anywhere uses a JSON function; `details_json` is always hydrated into memory. |
| `GetPersonSid()` (PLAN-GOV-113) | Introduced for **requester** identity of access requests. Not used here: a consumer report attributes calls by whatever subject made them (an app-only token is a legitimate consumer); using `GetUserSid()` semantics keeps the actor identical to the audit `actor_sid`. |
| k-anonymity helpers (F-DATA-08) | `SmallGroupStrategy` (`src/Autheris.Domain/Model/TableModels.cs:27-32`, `Suppress`/`MergeIntoOther`) and `ResolveEffectiveMinGroupSize` (`GovernedAggregationService.Buckets.cs:355-398`) are bound to row buckets and `AggregationGroupRow`; the merge code (`ComputeBucketFromRows`, `MergePreAggregatedBuckets`, `:77, :228`) is private and measure-specific. **Not reusable as code.** Reused as **vocabulary only**: the folding is `MergeIntoOther` with a `Suppress` fallback for an `other` that is itself too small (section 3, A-7). |
| Admin API patterns | WP-SC-10 `PageCursor`/`AuditLogPageQuery` (`src/Autheris.Application/Governance/Paging/PagingModels.cs`) are for unbounded lists. The new report is bounded by construction (at most 51 applications x `MaxWindowDays` points), so it returns one bounded document and needs no cursor; the PRD's "paged queries" requirement is satisfied by the hard caps (A-9). |
| Telemetry | `GatewayDiagnostics` holds the metrics (e.g. `AuditWriteFailedCounter` used in `AuditWriter.cs:64`). |

### 1.4 PRD gaps found (closed by this plan)

| # | Gap | Resolution |
|---|---|---|
| G-1 | "Reuse the FinOps normalization": none exists | A-2: one new `CallerLabelNormalizer`; FinOps may adopt it later (out of scope) |
| G-2 | Rows returned are not in the audit anywhere | Q-114-4 decision (section 2) |
| G-3 | SQL/WebSQL reads have no table in the audit | Counter fed from `GovernedRewrite.AccessedTables` (A-5) |
| G-4 | Rejection categories do not exist; HTTP 401/403/429 and table-less throttles carry no table | A-5: categories `access`, `rateLimit` (concurrency throttle), `cost` (aggregation) per table; endpoint-level 429 is **not** table attributable and is out of the report (owner flag O-3) |
| G-5 | Existing header sanitation allows formula starts, markup, Unicode | A-2: strict allowlist normalizer; reject, do not repair |
| G-6 | The audit has no purge; "retention = audit class" would be empty | A-8: counters are purged at `Audit:Retention:DataAccessDays`; flagged (O-4) |
| G-7 | Min-actor folding needs distinct actors; the PRD counter has no actor information | A-6: separate capped pseudonymous actor sample table |
| G-8 | Authenticated client id is not in `CallerContext`; three divergent claim orders | A-3: `CallerContext.ClientId` filled from the **validated token**, one helper |

---

## 2. Decision on Q-114-4: Rows From the Audit, or a New Counter? (spike T-114-1)

**Decision: a new pre-aggregated counter (`CONSUMER_APP_DAILY` family), fed at the governed read seams. The audit is not changed and is not the data source of the report.**

Evidence (all file:line from section 1):

| Criterion | Audit rows | New counter | Winner |
|---|---|---|---|
| Rows returned | not recorded; the ALLOW entry precedes execution (`GatewayExecutionService.cs:260`, `GovernedSqlExecutor.cs:343`); adding it needs a second post-execution entry per read (doubles Tier-B volume and group-commit load) or a mutable audit (impossible, append-only trigger) | the executors know the count after execution (`processedRows`, `GatewayExecutionService.cs:331`; the SQL row writer) | counter |
| SQL/WebSQL channel | `target_table` is the data source (`GovernedSqlExecutor.cs:302, 348`); no table in the entry | `GovernedRewrite.AccessedTables` is at hand at the seam | counter |
| Rejection category | free text `reasons`; throttle and cost write no entry | the throw sites know the category | counter |
| Scale and cost | one row per call, ~0.5-1.5 KB `details_json` (shape, filters, virtual filters), TEXT timestamps, no partitioning. A per-day group-by needs `substr(occurred_at,1,10)` plus a JSON extract of `$.caller.app` on TEXT for every row of the window, implemented three times (`json_extract`, `jsonb`/`::json`, `JSON_VALUE`); no such SQL exists today (all audit reads hydrate rows). On PostgreSQL and SQL Server the table predicate is `LOWER(target_table) = ...` and misses the index (`:700`, `:750`), so the scan is a seq scan over the whole audit. | one indexed range read of at most `MaxWindowDays x MaxSeriesPerTableDay` rows | counter |
| Budget (FR-114-6: p95 < 500 ms, 30 days, 10^6 entries on the table) | **Estimate, not a measurement** (no benchmark harness exists for the audit read path; the plan adds one, T-6): parsing 10^6 TEXT JSON documents costs roughly 1-5 us each in SQLite `json_extract`, 3-10 us each when casting TEXT to `jsonb` in PostgreSQL, comparable in SQL Server `JSON_VALUE`, i.e. about 1-10 s, plus reading ~1 GB. 2-20x over budget even with a perfect index; unreachable on the `LOWER()` engines without a functional index (a baseline DDL change on the largest table anyway). | reading <= ~18,000 rows (90 days x 200 series) by primary key prefix: well under 50 ms on all three engines | counter |
| Truncation | the 5000-entry cap of `GetTableConsumersAsync` stays for the old field; a correct window needs an unbounded scan | counters are exact up to the documented caps and flag `truncated` | counter |
| Forensics | authoritative, hash-chained | best effort analytics (drops possible, section 9) | audit stays the forensic source; unchanged |

Rejected variants: (a) add a post-execution audit entry with `rows` (doubles Tier-B volume, and still no table for SQL, still the 3x JSON-aggregate SQL); (b) a materialized view over the audit (not available on SQLite, and the audit is append-only TEXT); (c) hybrid (calls from audit, rows from counter): two sources that disagree after a drop, and calls from audit would still miss SQL and be limited to Tier-B enqueue timing.

Consequence: the audit schema gets **no** new column. The only audit change is additive text inside `details_json.caller` for new entries (`client` = authenticated client id, A-3), which helps forensics and costs nothing; it does not change the chain algorithm. The PRD's "audit schema field in `0001`" (FR-114-4) is therefore not needed.

---

## 3. Architecture Decisions

### A-1 One recorder, two seams, no audit coupling
`IConsumerUsageRecorder` (Application) with `RecordCall(TableIdentifier, TenantId, Sid actor, ConsumerOutcome outcome)` returning a series token and `RecordRows(token, long rows)`. It resolves the caller itself from `ICallerContextProvider` (request thread, same mechanism as the audit) and never throws into the caller: all failures are swallowed into a metric (fail-open for the **counter**, fail-closed for **data exposure**, section 9). Seam 1 is the existing single write seam of the governed read stages, `GovernedQueryStages.WriteAsync` (both overloads, `GovernedQueryStages.cs:155-197`) via `RecordTableReadAsync` (`:199-220`): they receive an optional `IConsumerUsageRecorder?` and record `allow` or `deny_access` next to the audit entry (same `allowed` flag, same table, same actor). Seam 2 is the executors: `GatewayExecutionService` (rows after `processedRows`, throttle `:544`), `GovernedSqlExecutor` (one call per `AccessedTables` entry after a successful compile; rows from the row writer, attributed only when the statement read **exactly one** table, otherwise `rowsMeasured` stays false for that call), `GovernedTreeQueryService` throttle (`:418`), `GovernedAggregationService` (cost, `:131`). Call sites that do not pass a recorder (null) behave exactly as today.

### A-2 Untrusted label normalization (`CallerLabelNormalizer`, Domain, pure)
Applied **at write time into the counter** (raw text stays in the audit unchanged for forensics, as the PRD asks) and again defensively at read time for stored values.
- Input: the already control-stripped, 128-capped header text (`SecurityContextFactory.cs:103-120` stays as is).
- Allowed: ASCII `[A-Za-z0-9._ -]`, first character `[A-Za-z0-9]`, length 1..64 after trim, internal runs of spaces collapsed to one. **Anything else yields the sentinel `invalid` (rejected, never repaired)**: this removes CSV/spreadsheet formula starts (`=+-@`, tab), markup, quotes, log-line breakers, and homoglyphs/RTL tricks (non-ASCII is invalid) with one rule.
- Case folding: `ToLowerInvariant()`; grouping and display use the folded form.
- Missing or whitespace-only: `unknown`. A declared value equal (case-insensitively) to a reserved sentinel (`unknown`, `invalid`, `other`) becomes `invalid` so a client cannot impersonate the aggregation buckets.
- Version: same rule, max 32 characters, set `[A-Za-z0-9._+-]`, first character alphanumeric; query name: same rule as application, max 64.
- Authenticated client id: `[A-Za-z0-9._:-]` (GUID, URN-ish), max 64; longer or other characters: stored as `h:` + first 16 hex characters of SHA-256 (it is token-derived and trusted in origin, but the length is bounded anyway).
- Output encoding: the labels are only ever returned as JSON strings by GraphQL (serializer escapes) and written through parameters; the **only** place they can reach a text sink is logs and the optional CSV/export surface. Rule: labels are never interpolated into log templates as format strings (structured property only, and only the normalized form), and no CSV export is added by this plan; any later export must use the allowlist-validated form (already formula-safe by the first-character rule).
- Shared use: `#113` A (I-22, earlier in the order) has **no** shared helper (verified in `PLAN-GOV-113`: `context` is free text, 200 characters, JSON-escaped, control characters rejected). Free text and labels need different rules, so only a tiny `UntrustedText.ContainsControlOrFormat(string)` primitive is shared if #113 A already introduced one; otherwise this plan introduces it and #113 B reuses it. The normalizer itself is #114-specific.

### A-3 Authenticated client id from the validated token only
`CallerContext` gets `ClientId` (init, nullable). `SecurityContextFactory.CreateFromHttpContext` fills it from `context.User` using the new `ClientIdentity.Resolve(ClaimsPrincipal)` (claim order `azp`, `appid`, `client_id`, the `EntraTokenPolicy.cs:59-61` order), **never from a header**. `ExtractCallerContext` returns a context when only `ClientId` exists (today it returns `null` without headers, `:86`). `CallerContext.IsEmpty` keeps its meaning for the three labels but `EffectiveDetailsJson` also emits `client` when `ClientId` is set (additive; existing audit tests that assert the `caller` shape get `client` only for authenticated tokens that carry one). The identity-subject fallbacks (`Sid.cs:34-36`, `IdentitySubjectResolver.cs:58-60`) are not touched. `ClientId` is never used for authorization, quotas or switches (SI-114-2; enforced by a grep-based architecture test over `CallerContext` usages, T-37, plus the Phase 3 review item in section 12).

### A-4 Counter model: three small tables, additive upserts, per-node in-memory batching
- `CONSUMER_APP_DAILY` (one row per tenant, table, UTC day, application label, client id, version): `calls`, `rows_returned`, `rows_measured_calls`, `rej_access`, `rej_rate_limit`, `rej_cost`, `rej_other`.
- `CONSUMER_APP_ACTOR_DAILY` (pseudonymous actor sample for min-actor folding, A-6).
- `CONSUMER_APP_QUERYNAME_DAILY` (top query names per application and day, cap 20).
The recorder aggregates in a bounded `ConcurrentDictionary` per node (at most `Reporting:ConsumerPreAggregation:MaxBufferedSeries`, default 20,000 keys; beyond that increments are dropped and counted) and a hosted service flushes deltas every `FlushIntervalSeconds` (default 15) and on shutdown with additive upserts (`INSERT .. ON CONFLICT DO UPDATE SET calls = calls + excluded.calls` on SQLite/PostgreSQL, `MERGE .. WITH (HOLDLOCK)` on SQL Server) in one transaction per batch. Additive deltas make multi-node correct without coordination. No database write on the request path.

### A-5 Outcome categories and what is attributable
`ConsumerOutcome`: `Allow`, `DenyAccess` (policy `DENY` of a table read, including column denial, `GovernedQueryStages` seam), `DenyRateLimit` (`GatewayThrottledException` at the per-user/table concurrency gate, three sites), `DenyCost` (`CostLimitExceededException` from aggregation), `DenyOther` (any other table-attributable `DENY` the seam sees with `allowed:false` and a non-policy exception, e.g. validation failure `GatewayExecutionService.cs:244-258`). Endpoint-level HTTP 429/401/403 (`AccessAuditMiddleware.cs:129-139`) carry no table and are **not** counted per table; the report documents this (`rejections` count table-attributable rejections only). A rejection without a resolvable caller label is counted under `unknown` (FR-114-4). Rows: only `Allow` calls contribute `rows_returned`.

### A-6 Min-actor rule and the actor sample (privacy core)
The per-application view must never carry a person identifier (SI-114-1), yet folding needs distinct actors. `CONSUMER_APP_ACTOR_DAILY` stores `actor_hash = HMAC-SHA256(key, tenant || 0x00 || actor SID)` truncated to 16 bytes (hex, 32 chars), per (tenant, table, day, application), **capped at `ActorSampleCap` (default 16, validated `>= MinActorsPerApplication` and `<= 64`) hashes per cell**. `key` is the new secret `Reporting:ConsumerPreAggregation:ActorHashKey` (>= 32 bytes, base64); **the feature refuses to start enabled without it** (options validator, no random fallback so hashes stay stable across nodes). The hash is write-only for the application: no repository method returns a hash, only `COUNT(DISTINCT actor_hash)` per cell and the distinct union across a set of cells (for the `other` bucket). Because each cell keeps at most 16 hashes, the count saturates at 16; the folding test only needs `>= MinActors`, so saturation cannot change a decision (the cap is validated against the minimum). Owners/admins see exact calls and the saturated actor count flagged `actorCountCapped`; nobody sees hashes. Key rotation only resets distinct counts (documented). Pseudonymous data is still personal data under GDPR: purge at the retention horizon (A-8) and the Phase 3 review decides whether the hash is acceptable or the fold should use call counts only (O-5).

### A-7 Folding algorithm (non-privileged callers), differencing-safe
Operates on cells (application, UTC day) after the cardinality cap (A-8):
1. A cell is **visible** iff its distinct actor count `>= MinActorsPerApplication` (default 3).
2. Every non-visible cell of any application is moved into the cell (`other`, same day) (`MergeIntoOther`).
3. The `other` cell is visible iff the **union** of the merged actor samples `>= MinActors`; otherwise it is dropped (`Suppress` fallback) and `suppressedSmallGroups = true` is returned without any count.
4. Window totals per application are computed **from the visible cells only**, and the report carries no table-wide total, so subtracting visible numbers from any other published figure reveals nothing (`getTableConsumers` already publishes `ActiveReadersCount`/`LastAccessedAt` to the same audience; that predates this plan and is unchanged).
5. Non-privileged callers receive no `queryNames`, no `clientId`, no per-version counts of folded cells; `versions` only for visible application cells.
Privileged callers (A-10) receive exact cells, `clientId`, `queryNames`, `actorCountCapped`. The folder is a pure static class (`ConsumerFolding`) so the rule is unit-testable without a database.

### A-8 Cardinality caps, retention, window
- Normalization first (A-2), then caps at **write** time per node and re-checked at flush against the database:
  - `MaxSeriesPerTableDay` (default 200 distinct `(application, clientId, version)` per tenant, table, day): the 201st distinct combination is recorded under `(other, '', '')`.
  - `MaxSeriesPerTenantDay` (default 20,000 across all tables): beyond it, new series fold to `other`.
  - `MaxQueryNamesPerApplicationDay` (default 20): overflow recorded under `(other)`.
  - Cross-node races can overshoot a cap by at most (nodes x batch); bounded, documented.
- Read side: top `MaxApplicationsPerReport` (default 50) applications by window calls, rest into `other`; `truncated = true` whenever any cap fired in the window (a persisted `other` series with `capped = 1`), never silent (FR-114-6).
- Window: `Reporting:MaxWindowDays` default 90, 1..365; `timeWindowDays` outside `1..MaxWindowDays` is rejected `INVALID_WINDOW` (the old field keeps `Math.Clamp(.., 1, 3650)`, `LineageImpactAnalyzerService.cs:76`).
- Retention (Q-114-3): the flush hosted service purges rows with `day < today - Audit:Retention:DataAccessDays` (default 400, the data-access class) once per day, batched `DELETE` of at most 5,000 rows per statement. The counters never outlive the data-access class; the audit itself has no purge (G-6, O-4).

### A-9 Report shape and endpoint
New GraphQL field `getTableConsumersByApplication(domain, schema, tableName, timeWindowDays = 30)` on the same Query type (decision for FR-114-1: a separate field keeps the cached `TableConsumersReport` contract and its M-15 tests untouched). Returns one bounded document (<= 51 applications x `MaxWindowDays` points, each point a handful of integers). Authorization = the existing gate extracted from `QueryTypes.cs:540-596` into one private method used by both fields, then the new service classifies the caller as privileged or not by the **same** method as the per-person list: `IAccessAnalysisAuthorizer.IsAuthorizedForAccessAnalysisAsync` extracted verbatim from `LineageImpactAnalyzerService.cs:367-383` (public interface in Application, `LineageImpactAnalyzerService` and the new service both call it; behaviour unchanged, M-15 test passes unchanged, AC 7). One authorization function, no second implementation.

### A-10 Audit of the report read
Reading the report writes one Tier-B-style best-effort audit entry (`CONSUMER_REPORT_READ`, new event type, `BestEffort` in `AuditPolicyTable`; reader, table, `view = per-application`, `privileged = true|false`, window days, no values) through `IAuditWriter.RecordBestEffortAsync`. A failed write does not fail the read (the report exposes no more than the caller may see; the entry is observational, same class as `CATALOG_READ`).

### A-11 Metrics
`autheris_caller_application_requests_total{application_bucket,outcome}` in `GatewayDiagnostics`. `application_bucket` is produced by `CallerLabelBucketer` (process-wide, first 50 distinct **normalized** labels, plus pre-seeded `Reporting:KnownApplications` labels that always get a slot, everything else `other`; `unknown` and `invalid` are fixed buckets). Raw header text never reaches a metric. `outcome` is the fixed `ConsumerOutcome` vocabulary. Residual risk: an attacker can occupy the free slots first (O-6, mitigated by `KnownApplications`).

### A-12 Rejected alternatives
Generating the report from the audit (section 2); folding by a fixed `k` on call counts instead of actors (a single heavy user would pass); returning actor hashes to owners (no need, per-person view exists); storing raw labels in the counter (violates A-2); a second ownership check (A-9).

---

## 4. Contracts (fixed up front; first commit "contracts")

### C-1 Domain (`Autheris.Domain`)
```csharp
// Security/CallerContext.cs (changed)
public sealed record CallerContext(string? Application = null, string? Version = null, string? QueryName = null, string? ClientId = null);

// Security/CallerLabelNormalizer.cs (new, pure)
public static class CallerLabelNormalizer
{
    public const string Unknown = "unknown", Invalid = "invalid", Other = "other";
    public static string Application(string? raw);   // A-2, max 64
    public static string Version(string? raw);       // max 32
    public static string QueryName(string? raw);     // max 64
    public static string ClientId(string? raw);      // "" when absent
}

// Security/ClientIdentity.cs (new)
public static class ClientIdentity { public static string? Resolve(ClaimsPrincipal? principal); } // azp, appid, client_id

// Model/ConsumerUsageModels.cs (new)
public enum ConsumerOutcome { Allow, DenyAccess, DenyRateLimit, DenyCost, DenyOther }
public sealed record ConsumerUsageKey(string TenantId, string TableKey, DateOnly Day, string Application, string ClientId, string Version);
public sealed record ConsumerUsageDelta(ConsumerUsageKey Key, long Calls, long Rows, long RowsMeasuredCalls, long RejAccess, long RejRateLimit, long RejCost, long RejOther);
public sealed record ConsumerActorDelta(string TenantId, string TableKey, DateOnly Day, string Application, string ActorHash);
public sealed record ConsumerQueryNameDelta(string TenantId, string TableKey, DateOnly Day, string Application, string QueryName, long Calls);
```
`TableKey` = `TableIdentifier.ToString().ToLowerInvariant()` (case-insensitive like the audit's `LOWER(target_table)`).

### C-2 Options (`Autheris.Domain/Options`, section `Gateway:Reporting`; validator in `Autheris.Api/Configuration/Validation`)
```
ConsumerPreAggregation:
  Enabled (false by default; true makes ActorHashKey mandatory)
  ActorHashKey (secret, base64, >= 32 bytes; never logged)
  FlushIntervalSeconds = 15 (1..300); MaxBufferedSeries = 20000 (1000..500000)
MinActorsPerApplication = 3 (2..100); ActorSampleCap = 16 (>= MinActors, <= 64)
MaxApplicationsPerReport = 50 (1..200); MaxSeriesPerTableDay = 200 (10..2000); MaxSeriesPerTenantDay = 20000
MaxQueryNamesPerApplicationDay = 20 (1..100); MaxWindowDays = 90 (1..365)
KnownApplications = [] (normalized on load; max 50 entries)
```
`Enabled = false`: the recorder is a no-op, the field answers `FORBIDDEN`-free but empty? **No: fail closed, the field returns `FEATURE_DISABLED`** (an empty report would read as "no consumers", a dangerous false negative for a deprecation decision).

### C-3 Application
```csharp
public interface IConsumerUsageRecorder
{
    // Returns the series token of this call so RecordRows adds to exactly that series (no ambient state).
    ConsumerUsageToken RecordCall(TableIdentifier table, TenantId tenant, Sid actor, ConsumerOutcome outcome);
    void RecordRows(ConsumerUsageToken token, long rows);
}
```
The executors keep the token for the request. A `NullConsumerUsageRecorder` is registered when the feature is disabled (A-1 seam sketch in section 3 uses the same two members).
```csharp
public interface IAccessAnalysisAuthorizer { Task<bool> IsAuthorizedForAccessAnalysisAsync(TableIdentifier, CallerSecurityContext, CancellationToken); }
public interface IConsumerApplicationReportService
{
    Task<ConsumersByApplicationReport> GetAsync(TableIdentifier table, int timeWindowDays, CallerSecurityContext caller, CancellationToken ct);
}
public interface IConsumerUsageRepository   // implemented by the three governance repositories
{
    Task ApplyAsync(IReadOnlyList<ConsumerUsageDelta> usage, IReadOnlyList<ConsumerActorDelta> actors, IReadOnlyList<ConsumerQueryNameDelta> names, int maxSeriesPerTableDay, int maxSeriesPerTenantDay, int actorSampleCap, int maxQueryNames, CancellationToken ct);
    Task<ConsumerUsageSlice> ReadAsync(TenantId tenant, string tableKey, DateOnly from, DateOnly to, bool includeClientAndNames, CancellationToken ct);   // cells + per-cell distinct actor counts + per (day) union samples; never actor hashes to the caller
    Task<int> PurgeAsync(DateOnly olderThan, int batchSize, CancellationToken ct);
}
```
`ReadAsync` returns, per (application, day): the aggregate numbers, `ActorCount` (saturating), and for the folding union an opaque `ActorUnionCount` computed **inside the repository** for a requested set of applications (second method `CountUnionActorsAsync(tenant, tableKey, day, applications)` returning an int), so hashes never leave the repository.

Report DTO (GraphQL):
```
type ConsumersByApplicationReport {
  table: String!  windowDays: Int!  truncated: Boolean!  suppressedSmallGroups: Boolean!  privilegedView: Boolean!
  applications: [ApplicationConsumption!]!
}
type ApplicationConsumption {
  declaredApplication: String!        # self-declared label, never an identity
  isFolded: Boolean!                  # the "other" bucket
  calls: Int!  rowsReturned: Int!  rowsComplete: Boolean!   # false when some calls could not attribute rows (multi-table SQL)
  rejections: Rejections!             # { rateLimit access cost other }
  versions: [LabelCount!]!
  clientIds: [LabelCount!]!           # privileged only, else empty (authenticated client id, A-3)
  queryNames: [LabelCount!]!          # privileged only, else empty
  daily: [DailyConsumption!]!         # UTC day, calls, rows, rejections
  actorCountCapped: Boolean           # privileged only
}
```
No field can carry an actor SID, hash, e-mail, IP or token subject (SI-114-1); a reflection test enforces it (T-3).

---

## 5. DDL (in place in `0001`, checksums unchanged, three shape files regenerated)

```sql
CREATE TABLE CONSUMER_APP_DAILY (
  tenant_id TEXT NOT NULL, table_key TEXT NOT NULL, day TEXT NOT NULL,          -- 'yyyy-MM-dd' UTC; SQL Server nvarchar(10)
  application TEXT NOT NULL, client_id TEXT NOT NULL DEFAULT '', version TEXT NOT NULL DEFAULT '',
  calls BIGINT NOT NULL DEFAULT 0, rows_returned BIGINT NOT NULL DEFAULT 0, rows_measured_calls BIGINT NOT NULL DEFAULT 0,
  rej_access BIGINT NOT NULL DEFAULT 0, rej_rate_limit BIGINT NOT NULL DEFAULT 0, rej_cost BIGINT NOT NULL DEFAULT 0, rej_other BIGINT NOT NULL DEFAULT 0,
  capped INTEGER NOT NULL DEFAULT 0,                                            -- 1: the 'other' series absorbed a cap overflow
  PRIMARY KEY (tenant_id, table_key, day, application, client_id, version));
CREATE INDEX ix_consumer_app_daily_day ON CONSUMER_APP_DAILY (day);            -- purge
CREATE TABLE CONSUMER_APP_ACTOR_DAILY (
  tenant_id TEXT NOT NULL, table_key TEXT NOT NULL, day TEXT NOT NULL, application TEXT NOT NULL, actor_hash TEXT NOT NULL,
  PRIMARY KEY (tenant_id, table_key, day, application, actor_hash));
CREATE INDEX ix_consumer_app_actor_day ON CONSUMER_APP_ACTOR_DAILY (day);
CREATE TABLE CONSUMER_APP_QUERYNAME_DAILY (
  tenant_id TEXT NOT NULL, table_key TEXT NOT NULL, day TEXT NOT NULL, application TEXT NOT NULL, query_name TEXT NOT NULL, calls BIGINT NOT NULL DEFAULT 0,
  PRIMARY KEY (tenant_id, table_key, day, application, query_name));
CREATE INDEX ix_consumer_app_qn_day ON CONSUMER_APP_QUERYNAME_DAILY (day);
```
Types follow the neighbouring baseline tables (`TEXT` on SQLite/PostgreSQL as in `AUDIT_LOG_ENTRIES`, `nvarchar(n)`/`bigint` on SQL Server; key columns bounded to 128/10/64 on SQL Server for index key size). No FK, no CHECK (consistent with `TABLE_ROW_FILTER_STRATEGIES`). The read-only runtime login grants include the new tables; the tables are **not** append-only (counters update, purge deletes) and are not part of the audit chain. The cap on actor rows is enforced in `ApplyAsync` (insert-if-below-cap per cell), never by the schema.

---

## 6. Files per Layer and Work Package (ONE sequential developer)

Ownership is exclusive per work package; the developer works the packages in order and does not build in between (the lead compiles and tests once at the end).

| WP | Files (new unless marked "changed") | Notes |
|---|---|---|
| **WP-1 Contracts, normalizer, client id, options** | new: `src/Autheris.Domain/Security/CallerLabelNormalizer.cs`, `ClientIdentity.cs`, `src/Autheris.Domain/Model/ConsumerUsageModels.cs`, `src/Autheris.Domain/Options/ReportingOptions.cs`, `src/Autheris.Api/Configuration/Validation/ReportingOptionsValidator.cs`; changed: `src/Autheris.Domain/Security/CallerContext.cs`, `src/Autheris.Domain/Model/GovernanceModels.cs` (`EffectiveDetailsJson` adds `client`), `src/Autheris.Api/Security/SecurityContextFactory.cs` (fills `ClientId`, returns a context when only the client id exists), `src/Autheris.Domain/Options/GatewaySections.cs`, `GatewayOptions.cs` | tests first: normalizer matrix, client id order, validator |
| **WP-2 Persistence (3 engines)** | changed: `Migrations/{Sqlite,PostgreSql,SqlServer}BaselineSchema.cs`, `Migrations/Shape/governance-shape.{sqlite,postgresql,sqlserver}.json` (generated), shape tests; new: `src/Autheris.Application/Interfaces/IConsumerUsageRepository.cs`, `{Sqlite,PostgreSql,SqlServer}GovernanceRepository.ConsumerUsage.cs`, runtime-login grants where the baseline lists tables | one contract-test base class run on all three engines (upsert additivity, caps, actor cap, purge, union count, tenant isolation) |
| **WP-3 Recorder and seams** | new: `src/Autheris.Application/Reporting/IConsumerUsageRecorder.cs`, `ConsumerUsageRecorder.cs` (bounded buffer, caps, hashing), `NullConsumerUsageRecorder.cs`, `ConsumerUsageFlushService.cs` (hosted service: flush, daily purge), `CallerLabelBucketer.cs`; changed: `src/Autheris.Application/Policy/GovernedQueryStages.cs` (optional recorder parameter on both `WriteAsync` and both `RecordTableReadAsync`), the 11 call sites of `RecordTableReadAsync`/`BuildEntry` (`GatewayExecutionService.cs:176,254,260,506,653`, `GovernedAggregationService.Buckets.cs:496`, `GovernedChangeDetectionService.cs:243`, `CrossSourceRelationResolver.Helpers.cs:370`, `GovernedTreeQueryService.cs:405`, `SemanticMetricQueryService.ConsentAndEvaluation.cs:456`, `GovernedProcedureExecutionService.cs:690`) pass the recorder; `GatewayExecutionService.cs` (rows after `:331`, throttle `:544`), `GovernedSqlExecutor.cs` (calls per `AccessedTables`, rows, throttle `:414`), `GovernedTreeQueryService.cs:418`, `GovernedAggregationService.cs:131` (cost catch, rethrow unchanged); DI in `GatewayGovernanceServiceExtensions.cs` and the hosted-service registration | no behaviour change when `Enabled = false` (null recorder); throw sites rethrow the original exception unchanged |
| **WP-4 Report, authorization, GraphQL** | new: `src/Autheris.Application/Lineage/IAccessAnalysisAuthorizer.cs`, `AccessAnalysisAuthorizer.cs`, `src/Autheris.Application/Reporting/ConsumerApplicationReportService.cs`, `ConsumerFolding.cs`, `src/Autheris.GraphQL/Types/ConsumerReportTypes.cs`; changed: `LineageImpactAnalyzerService.cs` (delegates `IsAuthorizedForAccessAnalysisAsync` to the authorizer, behaviour byte-identical), `src/Autheris.GraphQL/Types/QueryTypes.cs` (gate extracted to one private method, new field), `src/Autheris.Domain/Audit/AuditEventTypes.cs` + `src/Autheris.Application/Audit/AuditPolicyTable.cs` (`CONSUMER_REPORT_READ`, `BestEffort`; `AuditPolicyTableTests` demands the row) | existing M-15 test untouched and green |
| **WP-5 Wrap-up** | `GatewayDiagnostics` metric, benchmark test (T-6), docs pointer to the master plan row (the architect does not edit `docs/`), cleanup | |

---

## 7. Sequencing

- Delivery tracker order: after #113 increment A (I-22), before #113 increment B (I-24). This plan **does not depend** on #113 A code (no shared helper exists, section A-2); the order is kept for the shared `UntrustedText` primitive and to keep baseline DDL edits serial (each DDL step is a separate PR with its own shape regeneration).
- Branch from `main` after I-22 is merged; rebase the three shape JSON files last (they conflict with any DDL PR).
- Inside the branch: WP-1, WP-2, WP-3, WP-4, WP-5 strictly in this order (WP-3 needs the repository contract, WP-4 the recorder's data).
- The lead builds Release with warnings as errors and runs `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj` plus the three-engine repository contract tests (`DOTNET_USE_POLLING_FILE_WATCHER=true` in WSL) once, after WP-5.

---

## 8. Test Strategy (TDD list, red first; write these before the production code of the WP)

**WP-1**
1. `CallerLabelNormalizer.Application`: accepts `Power BI`, `etl-nightly_v2.0`; folds case; collapses spaces.
2. Rejects to `invalid`: 65 characters, control character, `=cmd|' /C calc'!A0`, `+1`, `-x`, `@sum`, leading tab, `<script>`, quotes, `a;b`, non-ASCII (`Ünïcode`, RTL override, homoglyph `Рower BI`), embedded newline; missing and whitespace -> `unknown`; declared `Other`/`UNKNOWN`/`invalid` -> `invalid`.
3. Property test (FsCheck or a 10,000-sample random fuzz with fixed seed): output is always one of `unknown|invalid` or matches `^[a-z0-9][a-z0-9._ -]{0,63}$`.
4. Version and query-name limits (32, 64); client id charset, >64 hashed to `h:` + 16 hex.
5. `ClientIdentity.Resolve` order `azp` > `appid` > `client_id`; no claim -> null; `SecurityContextFactory` fills `ClientId` from the token **and never from any header** (a request header `X-Client-Id` or `azp` has no effect); returns a context when only the client id exists.
6. `AuditLogEntry.EffectiveDetailsJson` emits `client` only when set; existing `caller` shape tests unchanged for header-only callers.
7. `ReportingOptionsValidator`: `Enabled` without key fails; key < 32 bytes fails; `ActorSampleCap < MinActors` fails; ranges.

**WP-2 (contract tests on SQLite, PostgreSQL, SQL Server)**
8. Additive upsert: two flushes of deltas sum; concurrent flushes from two repository instances sum.
9. `MaxSeriesPerTableDay`: the 201st combination lands in `(other,'','')` with `capped = 1`; `MaxSeriesPerTenantDay` likewise.
10. Actor sample never exceeds `ActorSampleCap` per cell; duplicate hash idempotent; `ReadAsync`/union count return counts only (reflection: no public member returns a hash).
11. Query-name cap 20 per application and day; overflow -> `(other)`.
12. Tenant isolation: tenant A rows never in tenant B reads; `LOWER` table key equality.
13. Purge deletes only rows older than the horizon, in batches, all three tables.
14. Shape snapshots equal the baseline DDL; `0001` checksum constants unchanged.

**WP-3**
15. Seam: every `RecordTableReadAsync` call site passes the recorder (a source-level test lists the call sites, so a new site without the parameter fails).
16. ALLOW and DENY of a table read increment `calls`/`rej_access` of the right application and day (AC 3, access).
17. Concurrency throttle -> `rej_rate_limit`; aggregation cost rejection -> `rej_cost`; both rethrow the original exception type unchanged.
18. SQL channel: a two-table join increments `calls` for **both** tables, `rows` for neither (`rows_measured_calls` unchanged); a one-table statement adds rows.
19. Rows are added only for `Allow`; `RecordRows` with a stale token is ignored.
20. **Label cardinality flood:** 1,000 distinct `X-Caller-Application` values in one day yield at most `MaxSeriesPerTableDay` series plus `other`, memory buffer stays <= `MaxBufferedSeries`, metric labels never exceed 50 + `other` + `unknown` + `invalid`, and the report shows at most 50 applications plus `other` with `truncated = true` (AC 4).
21. Hostile labels recorded as `invalid`; the stored `application` column never contains a non-allowlisted character even if the normalizer is bypassed at the repository (defensive re-validation on read: stored garbage is returned as `invalid`).
22. Recorder never throws: repository down -> request unaffected, `dropped` metric increments, buffer bounded; `Enabled = false` -> no-op and no hosted service work.
23. Missing header -> `unknown`; anonymous never recorded (no actor).
24. Flush hosted service: interval via `FakeTimeProvider`, flush on shutdown, daily purge once per day.

**WP-4**
25. **Person-level data never reaches non-owners:** a non-owner consent holder's response (serialized to JSON) contains no actor SID, hash, e-mail, IP or token subject, contains `queryNames = []`, `clientIds = []`, `actorCountCapped = null`; reflection test over `ConsumersByApplicationReport` types: no property named/typed like an actor identifier.
26. Owner, delegate, GovernanceAdmin, PrivacyAdmin get `privilegedView = true` with `clientIds`, `queryNames`, exact small cells (AC 1, 5); ClusterAdmin without GovernanceAdmin gets the folded view (parity with M-15).
27. **Folding:** two actors only on application `a`, three on `b`: non-owner sees `b` and an `other` cell for `a` only when the union >= 3, otherwise `a` is dropped and `suppressedSmallGroups = true` (AC 2); per-day cells below 3 are folded/suppressed even when the window total of that application is >= 3; **differencing test:** the sum of visible numbers plus anything else the response exposes never reveals a suppressed cell (no table total field; window totals equal the sum of visible cells).
28. The saturating actor count (cap 16) never changes a folding decision (parametrized around `MinActors` 2, 3, 16).
29. `getTableConsumers` per-person gating test (M-15) passes unchanged (AC 7); both fields call the same `IAccessAnalysisAuthorizer` (test with a recording fake).
30. Unauthorized caller: same `FORBIDDEN` message as `getTableConsumers` (SI-114-4); anonymous `UNAUTHORIZED`; unknown table answers like an unauthorized one; tenant B caller never sees tenant A applications (SI-114-3).
31. `timeWindowDays` 0, negative, `> MaxWindowDays` -> `INVALID_WINDOW`; `Enabled = false` -> `FEATURE_DISABLED` (never an empty report).
32. Report of a table with 10^5 stored cells beyond the cap: `truncated = true`, totals consistent with the visible cells (AC 6).
33. `CONSUMER_REPORT_READ` audit entry written best effort with `privileged` and no values; failure of the write does not fail the read; `AuditPolicyTableTests` has the row.

**WP-5**
34. Benchmark (non-gating `[Trait("Category","Perf")]`): 18,000 cells report p95 well below 500 ms on SQLite and PostgreSQL (Testcontainers where the project already uses them), plus a documented comparison run of the audit-aggregate approach on 10^6 synthetic entries to replace the section 2 estimate with a measurement.
35. Metric test: label set of `autheris_caller_application_requests_total` bounded; raw header text absent.
36. No logging of raw labels: a log-capturing test sends a hostile header (newline attempt, 10 KB) and asserts no log line contains it and no template interpolation of the raw value occurs.
37. Architecture test: `CallerContext.Application/Version/QueryName/ClientId` are referenced only from audit, recorder, bucketer and the factory (SI-114-2).

---

## 9. Fail-Closed Behaviour

| Situation | Behaviour |
|---|---|
| Feature disabled (`Enabled = false`) | recorder no-op; the new GraphQL field answers `FEATURE_DISABLED` (never an empty "no consumers" report) |
| Enabled without or with a too short `ActorHashKey` | startup validation fails; no random fallback |
| Authorization lookup fails (ownership repository, consent repository) | `FORBIDDEN` (as `getTableConsumers`) |
| Caller privilege indeterminate | treated as **non-privileged** (folded view, no query names, no client ids) |
| Counter repository unreadable at report time | the report fails with a generic error; no partial report, no fallback to the audit |
| Counter write fails (flush) | analytics only: dropped and counted (`autheris_consumer_usage_dropped_total`), request never fails, buffer bounded, retry next interval with the same bounded buffer; the audit is untouched |
| Buffer full | new series dropped and counted, existing series keep counting |
| Label not in the allowlist | `invalid` (rejected, never repaired); missing -> `unknown`; reserved word -> `invalid` |
| Cap exceeded (series, tenant, query names, applications) | folded into `other`, `truncated = true`; never silent, never an error |
| Stored value fails re-validation on read | returned as `invalid` |
| Small cell / small `other` | suppressed (A-7) |
| Token without a usable client id | `clientId` empty; nothing derived from headers |
| Window invalid | `INVALID_WINDOW` |
| Chain violation latched (SR-L-08, `DenyDataAccess`) | unaffected: the counter is not part of the audit path; the report keeps working from counters |

Honest limitation: the counters are **best effort**. After a flush failure or buffer overflow numbers are lower than the truth; the audit remains the forensic source. The report states this in its documentation (Phase 6) and `truncated` covers caps only, not drops (drops are visible in the metric). Owner flag O-7 asks whether `truncated` must also reflect drops (needs a persisted drop marker per day).

---

## 10. Risks

| # | Risk | Mitigation |
|---|---|---|
| R-1 | Seam coverage: a governed read path that never calls the recorder is missing from the report (the audit-based report had the same gap for SQL) | T-15 source-level test over `RecordTableReadAsync` call sites; list of channels in the Phase 6 doc; SQL channel covered through `AccessedTables` |
| R-2 | Hot-path overhead of the recorder | one `ConcurrentDictionary` update, no I/O, no allocation beyond the key; benchmark T-34 |
| R-3 | Counter drift against the audit (drops, multi-table rows) | documented; `rowsComplete` flag; audit stays authoritative |
| R-4 | Pseudonymous actor hashes are personal data | capped sample, write-only, purged with retention, key secret; Phase 3 decides (O-5) |
| R-5 | Metric label squatting (attacker fills the 50 slots) | `KnownApplications` pre-seed; `other` bucket; metric is not used for decisions |
| R-6 | DDL conflict with parallel PRs (baseline + three shapes) | serial order (delivery tracker), shapes regenerated last |
| R-7 | `ClientId` in `CallerContext` changes audit `caller` JSON of new entries | additive field, chain computed at write; existing shape tests updated deliberately |
| R-8 | Cross-node cap overshoot | bounded by nodes x batch, documented |
| R-9 | Misreading `declaredApplication` as identity | field name, GraphQL description and doc say self-declared; `clientIds` shown next to it to owners so mismatches are visible (Q-114-1) |

---

## 11. Owner Flags

- **O-1 (decided, Q-114-4):** counter, not audit rows (section 2). Q-114-1 yes (owners/admins), Q-114-2 view with folding, Q-114-3 audit data-access class.
- **O-2:** ClusterAdmin without GovernanceAdmin sees the folded view (parity with M-15's per-person gate). Say so if cluster admins should be privileged here.
- **O-3:** endpoint-level 429/401/403 are not attributable to a table and are not counted per table; the PRD's "rate limit" rejection means the per-user/table concurrency throttle.
- **O-4:** the audit has no purge job although retention classes exist (`AuditLoggingOptions.cs:26-31`); this plan purges the counters only. A separate decision is needed for the audit.
- **O-5:** is a pseudonymous actor hash acceptable, or should the min-actor rule use a coarser signal? (Phase 3.)
- **O-6:** `Reporting:KnownApplications` pre-seeding for the metric buckets.
- **O-7:** should `truncated` also reflect dropped increments (needs a persisted marker)?
- **O-8:** new options `Gateway:Reporting:*` and three tables; new event type `CONSUMER_REPORT_READ`.

---

## 12. Security Notes for Phase 3

1. **Untrusted labels (SI-114-2, SI-114-5):** verify the normalizer is the only path from header text to the counter, the metric and any log; the allowlist is the sole sanitizer (no escaping logic to get wrong); confirm no policy, rate-limit, cost, switch or cache-key code reads `CallerContext.*` (grep `CallerContext` usages must stay at audit, recorder, bucketer); `ClientId` likewise.
2. **Spoofing:** the report must always label the dimension `declaredApplication` and show `clientIds` only to privileged callers; an attacker can still blend into another label (accepted, attribution only); assess whether `invalid`/`other` can be used to hide traffic (they are visible buckets with counts).
3. **Cardinality and resource exhaustion:** review the four caps and the buffer bound; check the flush path under 1,000-label floods for lock contention and for unbounded work in `ApplyAsync` (cap checks must be set-based, one query per batch, not per key); cross-node overshoot bound.
4. **Re-identification:** review A-6/A-7: min-actor on cells including per-day, union rule for `other`, suppression fallback, no table total, saturating actor count, differencing between this report, `getTableConsumers` (`ActiveReadersCount`, `LastAccessedAt`) and the existing tables; decide O-5.
5. **No person data on the wire:** reflection test (T-25), repository never returns hashes, `ActorHashKey` is a secret (not logged, not in the options dump, rotation procedure).
6. **Single authorization path:** `IAccessAnalysisAuthorizer` is one function used by both fields; a composition test must fail when the new service is built without it; no second owner check; fail closed to the non-privileged view.
7. **Tenant isolation:** every repository query carries `tenant_id`; the caller's tenant comes from `CallerSecurityContext`, never from the request; cross-tenant application names never appear (T-12, T-30). ClusterAdmin cross-tenant (`SecurityContextFactory.cs:18-23, 35-40`) selection by header stays with the existing tenant resolution.
8. **Existence oracle:** same `FORBIDDEN` text for unauthorized and unknown table (SI-114-4, aligned with #117); `FEATURE_DISABLED` must not be reachable by an unauthenticated caller (authentication is checked first).
9. **Audit integrity:** the `client` field only adds text to new entries; confirm `AuditCanonicalizer` hashes the final `details_json` (no hash drift, `AuditChainEngine.cs:20-60`), and the counter tables are outside the chain and cannot be used to alter audit data.
10. **Logs and export:** no raw label in any log template (T-36); no CSV export is introduced; the first-character rule is the formula-injection guard for any later export.
11. **Retention and GDPR:** purge horizon, key rotation behaviour, the hash as personal data (R-4).
12. **Least privilege:** the report reads only the three counter tables; no audit read; no data source access; the recorder holds no connection (the hosted service owns it).

---

## 13. LOC Estimate

| WP | Area | Production | Tests |
|---|---|---|---|
| WP-1 | normalizer, client identity, options, validator, `CallerContext`/factory/audit JSON | ~330 | ~520 |
| WP-2 | DDL x3 + shapes (generated, excluded), repository contract + 3 engine implementations | ~620 | ~560 |
| WP-3 | recorder, buffer, caps, flush/purge service, bucketer, seam changes (11 call sites + 4 executors) | ~520 | ~620 |
| WP-4 | authorizer extraction, report service, folding, GraphQL field and types, audit event | ~420 | ~680 |
| WP-5 | metric, benchmark, wrap-up | ~40 | ~160 |
| **Total** | | **~1,930** | **~2,540** |

Size: **M** (L if the three-engine repository work overruns, matching the PRD's own note); the decision for a counter instead of audit rows is what moves it from M-minus to M. Shape JSON diffs (~120 lines per engine) are excluded.

---

## 14. Changelog

| Revision | Date | Change |
|---|---|---|
| 1 | 2026-10-16 | Initial Phase 2 plan; Q-114-4 decided (counter), T-114-1 and T-114-2 answered by code reading |
| 1.1 | 2026-10-16 | Phase 3 security review appended (verdict: APPROVED WITH CONDITIONS, no revision of the architecture required; the architect folds C-114-1..C-114-12 into revision 2 or the Phase 4 developer implements them as binding) |

---

## Phase 3 Security Review (2026-10-16)

**Reviewer:** `csharp-security-expert` (tracker step S-9). **Scope:** sections 1-12 of this plan against PRD section 5 and the code at `a5647e50` (verified, file:line below). Related reviewed plans: PLAN-GOV-113 rev. 2 (shared person predicate), PLAN-GOV-111 rev. 3 (`Gateway:Tenancy:Mode`), PLAN-GOV-129 (subject binding). No source was changed.

### P3.1 Verdict

**APPROVED WITH CONDITIONS.** The core decision (Q-114-4, a separate best-effort counter, audit untouched) is sound and I confirm the evidence for it. The strict allowlist normalizer, token-only client id, write-only pseudonymous actor sample, single authorizer and fail-closed defaults are the right shape. One High finding (SEC-114-01: the min-actor guard is bypassable by the viewer joining a cell, because labels are self-declared) and four Medium findings must be closed by binding conditions before Phase 4 starts. Nothing here requires a change of architecture.

### P3.2 Threat model

| Asset | Threat actor | Attack | Plan control | Residual |
|---|---|---|---|---|
| Per-person access behaviour (who reads which table, how often) | Authenticated non-owner with a consent (the audience of the folded view) | Join a small cell by declaring the same label, then read the exact sum of the others (SEC-114-01); differencing over days; sybil accounts | min-actor folding, union rule, suppression | Closed by C-114-1; sybil accounts with k = 3 remain (documented) |
| Same | Operator/DBA with read access to the counter DB | Link a person across tables and days via the stable actor hash | HMAC with secret key | Closed by per-cell subkeys (C-114-4) |
| Integrity of other applications' numbers | Any authenticated caller (labels are self-declared, SI-114-2) | Inflate a victim label by spoofing it; squat the series/buffer/query-name caps so legitimate applications fold into `other` (deflation) | allowlist, caps, `other` bucket, visible `invalid` | Spoofing is accepted (attribution only); squatting closed by C-114-2 |
| Availability and storage | Any authenticated caller, including callers without any right on the table | Flood labels, versions, query names or table names (also for tables that do not exist) | four caps, bounded buffer | Closed by C-114-2/C-114-3 (per-actor budget, registered tables only) |
| Exports, logs, dashboards | Any caller | Formula, markup, log-line injection through a label | allowlist, first-character rule, structured logging | Raw 128-char labels stay in the audit (pre-existing, SEC-114-09) |
| Authorization | Cluster/tenant admins, PrivacyAdmin, delegates | Privilege confusion between the GraphQL gate and the privileged-view classifier | one `IAccessAnalysisAuthorizer` | PrivacyAdmin reachability inconsistency (SEC-114-07) |
| Tenant isolation | Tenant A user or ClusterAdmin with `X-Tenant-ID` | Cross-tenant counters, metric slot starvation | `tenant_id` in every key, tenant from `CallerSecurityContext` | Metric bucket is process wide (Low, SEC-114-11) |

### P3.3 Findings

**SEC-114-01 (High) - The min-actor guard is defeated by the viewer joining the cell.** A-7 step 1 makes a cell visible iff its distinct actor count is >= 3, and the application label is self-declared and attacker-chosen (`SecurityContextFactory.cs:21-26`, labels are free text today, `:182-199`). A non-owner consent holder who sees that application `a` has two actors (both folded into `other`) sends one call today with `X-Caller-Application: a`. The cell now has three actors, becomes visible, and the attacker reads the exact calls, rows, rejections and versions of the other two persons (minus their own known contribution). The same works with a fresh label that lands in `other` (the union of the `other` cell crosses 3). No collusion is needed. Required change: C-114-1.

**SEC-114-02 (Medium-High) - Series, buffer and query-name caps can be squatted by one low-privilege caller, deflating every legitimate application.** `MaxSeriesPerTableDay` is first come first served and the series key includes the attacker-controlled `version` (A-8, C-1 `ConsumerUsageKey`): one user cycles 200 versions of one label and every later legitimate `(application, client, version)` of that table and day is recorded under `(other,'','')`. The same user can reach `MaxSeriesPerTenantDay = 20,000` (across tables) and the node buffer `MaxBufferedSeries = 20,000` (A-4; after each flush legitimate series are the new ones that get dropped). The 20 query names per application-day are fillable the same way. Counters are best effort, but a deliberate, repeatable, zero-cost deflation of the very numbers a deprecation decision rests on is not an accidental drop. Required change: C-114-2.

**SEC-114-03 (Medium) - The recorder keys counters by whatever table the seam sees; a caller can create unbounded `table_key` values.** A-1 records `deny_access` at `GovernedQueryStages.WriteAsync` for every governed read including DENY. The table identifier of a request comes from the caller (`QueryTypes.cs:540-596` shows the pattern `new TableIdentifier(domain, schema, tableName)` from arguments). If a request for a non-existent or non-registered table reaches the seam, every distinct table name is a new series, bypassing `MaxSeriesPerTableDay` (which is per table), and only the tenant cap (20,000) ends it, after starving the tenant (SEC-114-02). Required change: C-114-3.

**SEC-114-04 (Medium) - Stable actor hashes are linkable across tables, days and applications.** A-6 hashes `HMAC(key, tenant || 0x00 || SID)`, the same value for a person in every cell. Anyone who can read the table (backup, replica, support, SQL injection elsewhere) can build a per-person cross-table access profile without the key, i.e. exactly the per-person data that SI-114-1 keeps out of the report, and it is far more than the folding needs (only a count per cell and a union per table and day). Required change: C-114-4.

**SEC-114-05 (Medium) - Key rotation can silently weaken k; key handling is under-specified.** After a mid-day rotation the same person appears with two hashes in the same cell, so two persons look like four and a cell with two actors passes the >= 3 test (over-count means under-protection). The plan says rotation "only resets distinct counts" (A-6), which is the opposite effect. Also the key must not be shared with the audit-chain HMAC key and must follow the existing secret handling (not in options dumps, not logged). Required change: C-114-5.

**SEC-114-06 (Medium) - Sub-cell breakdowns have no min-actor protection.** A-7 step 5 returns `versions` for visible application cells and the cell carries `rejections` and `rowsComplete`. A version or rejection class used by one person is an individual-level statement inside a cell that passed k only in total (example: `version 7.3.1: 41 calls` where one of the three actors is the only user of that version). `daily` points are per cell and fine. Required change: C-114-6.

**SEC-114-07 (Medium) - Authorization reachability and classification mismatch.** (a) The GraphQL gate (`QueryTypes.cs:552-587`) admits only `IsClusterAdmin || IsGovernanceAdmin`, owner/delegate, or an active Allow consent; the extracted authorizer (`LineageImpactAnalyzerService.cs:367-383`) additionally recognizes the role `PrivacyAdmin`. A `PrivacyAdmin` without ownership or consent is rejected by the gate and never reaches the classifier, while test T-26 expects `privilegedView = true` for PrivacyAdmin. Either the test or the gate is wrong. (b) `IsAuthorizedApproverForTableAsync(table, sid, ct)` (`IGovernanceRepository.cs:246`) has no tenant parameter; tenant scoping of owner and delegate rows is not visible at the call and is therefore not proven for the new privileged view, which exposes `clientIds` and exact small cells. (c) `FEATURE_DISABLED` (C-2) is thrown by the field; if thrown before authorization, any authenticated user can read the feature switch. Required change: C-114-7.

**SEC-114-08 (Medium-Low) - Authenticated client id: provenance is right, source scheme and issuer are not pinned.** Header spoofing is excluded by design (A-3) and `EntraTokenPolicy.cs:59-61` has the claim order the plan copies. But (a) the principal may come from authentication schemes other than a validated JWT: `BasicAuthenticationHandler`, `BasicAuthSessionMiddleware`, `ForwardAuth` (`Program.cs`, `ForwardAuthSecretStartupValidator.cs`), `DevEndpoints.cs`; the plan fills `ClientId` from `context.User` whatever the scheme. (b) With several `ValidIssuers` (`GatewayServiceCollectionExtensions.cs:363-364`) any trusted issuer can assert any `azp`; two issuers can collide on one client id. (c) For MCP calls `GatewayMcpQueryExecutor.cs:662-663` writes the session service principal into `client_id`/`azp` of the forwarded principal; the recorded client id on that path is the MCP session principal, not the end consumer (label and client id then legitimately disagree, which is visible but must be documented). (d) A delegated user token may carry `azp` that is the front-end SPA, so `clientId` identifies the client application of the token, not necessarily the calling system. Required change: C-114-8.

**SEC-114-09 (Low) - Raw labels remain in the audit and the new `client` text is unbounded in the audit JSON.** `SanitizeCallerHeader` (`SecurityContextFactory.cs:182-199`) strips control characters and caps 128 characters but lets `=cmd|...`, markup and any Unicode through, and these are stored in `details_json.caller` (`GovernanceModels.cs:54-86`) and returned by the audit read/export endpoints (`GovernanceEndpoints.Audit.cs`). The plan correctly keeps the raw text for forensics and adds no CSV export. The pre-existing exposure (audit CSV/JSON exports consumed by spreadsheets) is outside this plan, but the plan must not widen it. Required change: C-114-9.

**SEC-114-10 (Low) - Control stripping before normalization changes semantics of test and defence.** The factory deletes control characters before the normalizer sees the value (`SecurityContextFactory.cs:191`), so `Power\nBI` becomes `PowerBI` and the "embedded newline -> invalid" case of T-2 cannot occur on the real path. Duplicate headers are joined with `,` by `StringValues.ToString()` (`:173`) and become `invalid` (fine). The normalizer must be applied to the unsanitized header value for the counter, so a hostile value is rejected rather than silently repaired (A-2 says "reject, never repair"). Required change: C-114-10.

**SEC-114-11 (Low) - Metric bucketer is process wide and first-come.** A-11 gives the first 50 distinct normalized labels a slot across all tenants. One user can take all slots in seconds; other tenants' applications are then `other` in the operator metric, and tenant label names appear in a shared metric. The plan already accepts squatting (R-5, O-6). The metric is not used for decisions. Required change: C-114-11 (documentation and `KnownApplications` pre-seed only).

**SEC-114-12 (Low, confirms pre-existing) - Retention: the actor sample is kept far longer than it is useful; the audit still has no purge.** (a) A-8 purges all three tables at `DataAccessDays` (default 400), but the actor hashes are only ever consumed inside the report window (`MaxWindowDays` <= 365, default 90); keeping pseudonymous personal data 400 days is not required (data minimization, GDPR storage limitation). (b) Confirmed: there is no audit purge. `AuditEventTypes.AuditRetentionPurge` and its policy row (`AuditPolicyTable.cs`) exist, but grep finds no emitter, no job and no reader of `AuditLoggingOptions` `DataAccessDays` (`AuditLoggingOptions.cs:26-31`); the audit is append-only by trigger (`PostgreSqlBaselineSchema.cs:525-540`). The audit therefore retains raw labels and actor SIDs beyond the configured classes indefinitely, which contradicts the retention classes the options advertise. This is not caused by #114 and must not be hidden by #114's counter purge. Required change: C-114-12.

**Confirmed as sound (no finding):**
- Q-114-4 counter decision: evidence verified (pre-execution ALLOW entry `GatewayExecutionService.cs:260-262`; `processedRows` `:331`; WebSQL `TargetTable` = data source `GovernedSqlExecutor.cs:302, 348`; `LOWER(target_table)` `PostgreSqlGovernanceRepository.Audit.cs:700`; append-only trigger). The audit stays authoritative; the counters add no way to alter audit data and sit outside the chain.
- Allowlist-only normalization (ASCII, first character alphanumeric, 64 chars, reserved words folded to `invalid`) removes formula, markup, quote, RTL and homoglyph vectors with one rule. Case folding after the ASCII check is safe.
- Client id from claims only; `ClientId` never an input to authorization or quotas (SI-114-2); the architecture test T-37 is right.
- Fail-closed defaults: `Enabled=false` returns `FEATURE_DISABLED` instead of an empty report; indeterminate privilege treated as non-privileged; no partial report; no fallback to the audit.
- The report DTO carries no actor SID, hash, e-mail or IP; repository never returns hashes; `getTableConsumers` and its M-15 gate stay unchanged.
- Best-effort `CONSUMER_REPORT_READ` through `RecordBestEffortAsync` (`AuditWriter.cs:75`) is consistent with `CATALOG_READ`/`MetadataExport` (`AuditPolicyTable.cs`); a failed audit write cannot widen exposure because privilege is decided before the write. The privileged-view read of exact cells and client ids should, however, not be dropped silently, see C-114-7(d).

### P3.4 Answers to the review questions

1. **Can a caller inflate or deflate another application's numbers?** Inflate: yes, by declaring the victim's label (accepted, the label is a declaration, SI-114-2; mitigated by `clientIds` beside the label for owners and by the visible `invalid`/`other` buckets). Deflate: yes through cap squatting (SEC-114-02) and buffer starvation; this is closed by per-actor budgets (C-114-2), not accepted. Rejections can be inflated by any authenticated user on any registered table, which is acceptable because they are labelled declared and attributable only to the label.
2. **Untrusted labels:** the allowlist is sufficient for injection (SEC-114-10 only fixes the order). Cardinality: per-table caps alone are insufficient (SEC-114-02/03), they need per-actor and per-application sub-caps and a registered-table gate.
3. **Authenticated client id:** correct source; pin the scheme and the issuer (SEC-114-08). Header spoofing is impossible by construction (no header read); verify with the T-5 negative test, extended below.
4. **Privacy:** the folding design is right but bypassable by self-joining (SEC-114-01); sub-cell leakage (SEC-114-06); hash linkability and rotation (SEC-114-04/05). Differencing across days does not reveal suppressed cells because suppressed cells are dropped and no table total exists; this holds only as long as the report never exposes per-cell actor counts to non-privileged callers (C-114-6). Differencing against `getTableConsumers`: that field already publishes `ActiveReadersCount` and `LastAccessedAt` to the same audience; with per-cell actor counts hidden, the new report does not make this differencing stronger. Differencing between folded and unfolded views is not possible for one caller (the view is a function of the caller) except for a caller whose privilege changes; accepted, that caller is entitled to the unfolded view.
5. **Authorization:** single authorizer is right; reachability and tenant scope need proof (SEC-114-07).
6. **Tests:** the planned list is good for functionality and weak on adversarial cases; see P3.6.

### P3.5 Binding conditions (C-114-n)

**C-114-1 (SEC-114-01, blocking).** Visibility is evaluated per viewer. `ReadAsync`/`CountUnionActorsAsync` take the viewer's actor hash and count distinct actors **excluding the viewer** for every cell and for the `other` union; a cell is visible only if at least `MinActorsPerApplication` actors other than the viewer remain. The numbers of a visible cell still include the viewer's own calls (they are not separable) but the cell is then protected by >= k other actors, so joining or creating a label no longer changes any visibility decision. The viewer hash is computed in the service with the same derived key and never returned. Privileged callers are exempt. Document that colluding or sybil accounts (several SIDs under one person) reduce the effective k; `MinActorsPerApplication` default 3 stays, with the validator warning below 3 (reject values below 2).

**C-114-2 (SEC-114-02, blocking).** Add per-actor and per-application budgets in the recorder and at flush: (a) at most `MaxNewSeriesPerActorTableDay` (default 5) new `(application, clientId, version)` series and `MaxNewQueryNamesPerActorTableDay` (default 5) new query names per (actor, table, day), tracked with the actor hash in a bounded node-local map (excess goes to `(other)` and sets `capped`); (b) a `MaxVersionsPerApplicationDay` (default 10) sub-cap per application before the table-day cap; (c) series whose application is in `Reporting:KnownApplications` or already present in the database for that table-day are never dropped by the table, tenant or buffer caps (reserved slots), so squatting cannot evict them; (d) when `MaxBufferedSeries` is hit, increments of series already in the buffer continue and only new keys are dropped and counted; the drop metric is labelled by tenant-agnostic reason only. The `ApplyAsync` cap check stays one set-based query per batch (section 12 item 3).

**C-114-3 (SEC-114-03, blocking).** The recorder records only for a table that resolved against the catalog/registry in the same request (the seam already holds the resolved `TableIdentifier`); requests for unknown or unregistered tables, and anonymous callers, are never recorded. Unit test below. `TableKey` is derived from the catalog identifier, never from raw request text.

**C-114-4 (SEC-114-04, blocking).** Replace the global actor hash by a derived per-cell value: `subkey = HMAC(ActorHashKey, "autheris/consumer-actor/v1" || tenant || 0x00 || tableKey || 0x00 || day)`, `actor_hash = HMAC(subkey, SID)` truncated to 16 bytes. This keeps the union across applications within one table and day (what A-7 step 3 needs) but makes a hash unlinkable across tables and days. Hashes stay in a table that is not granted to the read-only reporting login for direct SELECT where the engine allows column/table grants (only the repository's counting method reads it).

**C-114-5 (SEC-114-05, blocking).** Key handling: (a) the key is a separate secret from the audit-chain HMAC key and every other key (validator rejects equality with them), >= 32 bytes, loaded through the existing secret mechanism, excluded from options dumps/logs (the validator and startup banner never print it); (b) `ActorHashKeyId` (short string) is a second option; rows store `key_id` in `CONSUMER_APP_ACTOR_DAILY` (part of the primary key) and the distinct count of a cell is the **maximum over key ids**, so rotation can never over-count; (c) rotation is documented as: add a new key and id, keep the old id valid for reads until the horizon of C-114-12; (d) no random fallback (as planned).

**C-114-6 (SEC-114-06, blocking).** For non-privileged callers: `versions` are omitted entirely (the plan already omits `clientIds` and `queryNames`); `rejections` are returned only as one summed number per cell, not split by category (category split is privileged); per-cell and per-version actor counts are never returned; `rowsComplete` is allowed. Privileged callers keep everything. If the owner later wants non-privileged `versions`, each version needs its own min-actor test with viewer exclusion.

**C-114-7 (SEC-114-07, blocking).** (a) Decide and test PrivacyAdmin: either the extracted gate admits `PrivacyAdmin` (then `QueryTypes.cs:552-587` is changed deliberately for the new field only and M-15 for the old field is untouched), or T-26 drops PrivacyAdmin from the privileged list. Recommendation: PrivacyAdmin is privileged for the new field (it is already privileged for per-person data in the service). (b) Before Phase 4, the architect proves that `IsAuthorizedApproverForTableAsync` is tenant scoped (owner and delegate rows keyed by tenant) on all three engines; if it is not, the new view must add an explicit tenant equality check and the gap is recorded as a finding for the owner. (c) Order of checks: authentication, then the authorization gate, then `FEATURE_DISABLED`, then window validation, so an unauthorized caller learns neither the switch nor the window rule. (d) The privileged read (exact cells, `clientIds`) writes `CONSUMER_REPORT_READ` as planned; additionally the number of applications returned and `privileged` flag are logged in the audit entry; it stays `BestEffort`.

**C-114-8 (SEC-114-08, binding).** `ClientIdentity.Resolve` is applied only when the principal's authentication type is the validated JWT/OIDC scheme (not Basic, ForwardAuth, dev); other schemes yield no client id. The stored client id is prefixed with a short issuer discriminator (first 8 hex of SHA-256 of the `iss` claim) when more than one `ValidIssuers` is configured, so two issuers cannot collide; display strips nothing and the field is documented as "client of the token, not necessarily the calling system". The MCP path (`GatewayMcpQueryExecutor.cs:662-663`) is recorded with the MCP session principal's client id; the Phase 6 doc says so. T-5 gets a negative test per non-JWT scheme and one for the issuer discriminator.

**C-114-9 (SEC-114-09, binding).** `client` in `EffectiveDetailsJson` is written only in the normalized form of `CallerLabelNormalizer.ClientId`. The plan adds no audit export; if any audit export or UI consumes `details_json.caller`, a follow-up must normalize or prefix formula starters. A one-line note is added to the Phase 6 doc. No other change to the audit chain.

**C-114-10 (SEC-114-10, binding).** `ExtractCallerContext` hands the **raw header string** (length-capped at 256 for memory) to the counter path in a second property or the normalizer is called inside the factory on the raw value before control stripping; the control-stripped form remains for the audit only. Normalizer tests run on the raw form: `Power\nBI`, `Power\u0000BI`, `a\tb` all give `invalid`. Comma-joined duplicate headers give `invalid`.

**C-114-11 (SEC-114-11, binding).** The bucketer is documented as a squattable, process-wide, operator-only signal; `KnownApplications` is the mitigation. The metric never carries a tenant id or a client id. No decision, alert-to-person mapping or report reads the metric.

**C-114-12 (SEC-114-12, binding).** (a) `CONSUMER_APP_ACTOR_DAILY` is purged at `MaxWindowDays + 7` days, not at `DataAccessDays`; the two aggregate tables are purged at `DataAccessDays`. (b) The audit purge gap is raised as a separate tracked item (new owner flag O-9 below); #114 neither implements nor masks it. (c) The purge deletes in bounded batches using engine-correct syntax (`DELETE TOP (n)` on SQL Server, `ctid`/primary-key subselect on PostgreSQL, rowid subselect on SQLite), and the daily purge result (row counts) is logged and a metric, with no label values.

### P3.6 Mandatory security tests (in addition to section 8; red first)

| ID | Test | Closes |
|---|---|---|
| S-114-T1 | **Self-join attack:** application `a` has actors v1, v2 on day D; attacker (non-owner, consent holder) sends one call with label `a`; report for the attacker shows no cell `a` and no `other` with their numbers (`suppressedSmallGroups = true`). Variant: attacker uses a fresh unique label; `other` stays suppressed. Variant: attacker is one of three real actors; the cell is visible only if at least k others remain (parametrize k = 2, 3, 5). | SEC-114-01 |
| S-114-T2 | Viewer hash excluded in the count and union, never returned; privileged callers see exact cells. Saturated sample (16) with viewer outside the sample still decides correctly. | SEC-114-01 |
| S-114-T3 | **Squatting:** one actor sends 500 versions of one label and 500 query names; the series per actor stay <= `MaxNewSeriesPerActorTableDay`, a second legitimate application and a `KnownApplications` label still get their own series after the flood, `capped = 1`, `truncated = true`. Repeat across tables and after a flush with a full buffer. | SEC-114-02 |
| S-114-T4 | Requests for a non-existent table, an unregistered table, a non-governed path and anonymous requests create no counter row and no buffer key (table-name flood of 10,000 values leaves the buffer unchanged). | SEC-114-03 |
| S-114-T5 | Actor hash of one SID differs per table and per day, is identical across applications of the same table-day (union works), and never equals the plain SID or an unkeyed hash; a DB-only reader cannot link two cells (assert distinct values). | SEC-114-04 |
| S-114-T6 | Key rotation mid-day with `key_id`: the distinct count is the max over key ids (two persons never look like four); validator rejects equality with the audit-chain key and keys < 32 bytes; the key never appears in any log, options dump or exception text (log-capture test). | SEC-114-05 |
| S-114-T7 | Non-privileged JSON contains no `versions`, no per-category rejections, no per-cell actor count; privileged JSON contains them; reflection test over the DTO still finds no actor-like member. | SEC-114-06 |
| S-114-T8 | Differencing: for a fixed data set, the union of everything a non-privileged caller can obtain (this field for several windows, plus `getTableConsumers`) never determines a suppressed cell (window 30 vs 29 vs 1 day; one cell flips from 2 to 3 actors on the last day). | SEC-114-01/06 |
| S-114-T9 | PrivacyAdmin without ownership: result matches the decision of C-114-7(a) on gate and classifier (one test, both fields); tenant-B owner of a same-named table gets no privileged view of tenant A (all engines or a recording fake plus one real-repository test); order of checks: unauthenticated -> `UNAUTHORIZED`, unauthorized with feature disabled -> `FORBIDDEN` not `FEATURE_DISABLED`. | SEC-114-07 |
| S-114-T10 | ClusterAdmin without GovernanceAdmin selecting tenant B by `X-Tenant-ID` gets the folded view of tenant B only; tenant claim and header tenant mismatch for a non-cluster-admin still throws before any counter read (`SecurityContextFactory.cs:109-145`). | O-2, tenant isolation |
| S-114-T11 | Client id: header `X-Client-Id`, `azp`, `client_id` and `appid` request headers have no effect; Basic, ForwardAuth and dev principals produce no client id; two issuers with the same `azp` produce different stored client ids; MCP path records the session principal's id (documented behaviour). | SEC-114-08 |
| S-114-T12 | Normalizer on raw header values: embedded `\n`, `\r`, NUL, tab, comma-joined duplicates, `=cmd`, `+1`, `@x`, `-x`, `\t=1`, right-to-left override, 10 KB value, `Other` in all case variants, `h:` prefix lookalikes all give `invalid` or the allowlisted form; fuzz with fixed seed over all 0x00-0xFFFF code units. | SEC-114-10 |
| S-114-T13 | Hostile label never reaches: log output (log-capture), metric labels, GraphQL error messages, `CONSUMER_REPORT_READ` details, an export-style CSV render (rendered through a test CSV writer: no cell starts with `= + - @ tab CR`). | injection |
| S-114-T14 | Tenant isolation at the repository: same table name and label in tenants A and B never mix in `ReadAsync`, union count, purge or cap accounting (per-tenant cap counts are independent); tenant cap of A cannot be exhausted by B. | tenant isolation |
| S-114-T15 | Purge: actor rows older than `MaxWindowDays + 7` removed, aggregate rows at `DataAccessDays`; batch syntax verified on all three engines; purge failure does not break request path. | SEC-114-12 |
| S-114-T16 | Authorization composition: building `ConsumerApplicationReportService` without `IAccessAnalysisAuthorizer` fails DI validation (`ValidateOnBuild`); authorizer exception or repository failure yields `FORBIDDEN`/non-privileged, never the privileged view. | fail closed |
| S-114-T17 | Counter failure isolation: repository down or buffer full never changes the response status, body or latency class of a governed read (rethrow of the original exception type preserved at all 4 throw sites); a poisoned recorder (throws) is swallowed. | integrity of the data path |
| S-114-T18 | Architecture test T-37 extended: `CallerContext.ClientId`, `.Application`, `.Version`, `.QueryName` are not read by policy, throttle, cost, cache-key, tenant or switch code; also covers `ICallerContextProvider` consumers. | SI-114-2 |

### P3.7 Recommendations for the owner flags

- **O-1 (decided):** agree (counter, not audit rows).
- **O-2:** recommend **keep ClusterAdmin without GovernanceAdmin on the folded view.** ClusterAdmin is a platform operator role selected across tenants by header (`SecurityContextFactory.cs:109-145`), not a data steward; M-15 already withholds per-person data from it. Revisit only if an operational need is documented.
- **O-3:** agree; document that the report counts table-attributable rejections only. No change of audit.
- **O-4:** the audit purge gap is real (SEC-114-12). Recommend a separate plan (a controlled purge path respecting the append-only trigger and the chain, with an `AUDIT_RETENTION_PURGE` event, whose type and policy row already exist but have no emitter); #114 must not depend on it.
- **O-5:** accept the pseudonymous actor hash **only with C-114-1, C-114-4 and C-114-5**; with those, a coarser signal (call counts) is not needed and would be weaker (SEC-114-01 shows call-count folding is also bypassable). Treat the hash as personal data; keep the shortened retention of C-114-12.
- **O-6:** yes, pre-seed `KnownApplications`, and in addition reserve series slots for them (C-114-2c).
- **O-7:** recommend **yes, cheaply:** set `capped`/`truncated` on the report when the drop metric for the tenant incremented since the day start, using an in-memory per-node flag persisted only as the existing `capped` marker on the affected day's `other` series; do not build a separate drop table. If the owner declines, the Phase 6 doc states that drops are invisible in the report.
- **O-8:** approve the three tables, the options and the event type, provided the options validator enforces C-114-5 and that `Reporting:ConsumerPreAggregation:Enabled` defaults to `false`. New owner flag **O-9:** open a separate tracked item for the audit purge job (O-4) and for normalizing audit exports (C-114-9).

### P3.8 Residual risks accepted

- Label spoofing (attribution only, SI-114-2); sybil accounts reduce effective k below the nominal value; best-effort counters can under-count after drops; cross-node cap overshoot (nodes x batch); metric slot squatting; pre-existing raw labels in the audit; the existing `getTableConsumers` fields (`ActiveReadersCount`, `LastAccessedAt`) stay as they are.

### P3.9 Next step

The architect folds C-114-1..C-114-12 into revision 2 (a short delta is sufficient: section 3 A-6, A-7, A-8, A-3, section 5 `key_id` in `CONSUMER_APP_ACTOR_DAILY`, section 8 tests S-114-T1..T18); Phase 4 does not start before that. A narrow re-review is needed only for C-114-1 and C-114-4 if their implementation deviates from the text above.
