# Implementation Plan: REQ-GOV-111 - Persist Dataset Tags and Catalog Visibility (`Hidden` / `Summary` / `Full`)

**Document ID:** `PLAN-GOV-111` (revision 2, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-GOV-111, section 2](2026-10-16-req-issues-111-115-catalog-access-analytics.md) (D-111-1..4, FR-111-1..9, SI-111-1..4, AC 1..8, T-111-1)
**Branch:** WP-0: `fix/catalog-schema-disclosure` from `main`, merged first (before #107 and #111); WP-1..WP-6: `feat/issue-111-dataset-tags-visibility` from `main` after #115 and after WP-0; planning base `3ece5edd`
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement `REQ-GOV-111`; delivery tracker [PLAN-DELIVERY-W23](2026-10-16-plan-waves-2-3-delivery.md) (step P-6, implementation step I-7)
**Status:** PROPOSED rev. 2 (awaiting Phase 3 re-review). Revision 2 closes SEC-111-01..14 and BC-1..BC-14 of the Phase 3 review (mapping in the section "Revision 2 - response to Phase 3" before the review). **Delivery split:** WP-0 (security bugfix of the pre-existing catalog leaks) is self-contained and ships as its own PR before #111 and before #107 (tracker step `I-6a`); WP-1..WP-6 are the #111 feature.

**Binding rules applied:** the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server is extended in place, the `0001` checksum constants stay unchanged (`*GovernanceMigrationRunner.cs:18/22/22`), the shape snapshots under `src/Autheris.Infrastructure/Persistence/Migrations/Shape/` and their tests are updated (the way #107 added `TABLE_ROW_FILTER_STRATEGIES`); fail closed; rights are checked by the backend from the authenticated principal and the ownership store, never from request content or the calling agent; owner decision **Q-111-1**: raising a dataset to `Full` is governance-admin-only, data owners and delegates may set only `Hidden` and `Summary`; Q-111-2 (`Summary` tenant-wide now) and Q-111-3 (new sources default `Hidden`, option `Catalog:DefaultVisibility`) follow the PRD recommendation; Phase-4 developers are Sonnet; one sequential developer.

**Revision 2 scope rule:** everything that leaks today, independent of the new tiers (REST catalog, search index and `RelevantColumns`, `/datasources`, the GraphQL `AllowAuthenticatedCatalogDiscovery` branch, query-analysis column evaluation, `sample_rows` guard form), is moved out of #111 into **WP-0**, which uses only the current consent-based `CatalogVisibility` and introduces no tier, no new type of #111 and no schema change. #111 then adds the tiers on top of the already-closed channels and re-runs the WP-0 leak tests with `Summary`/`Full` fixtures.

---

## 1. Verification of the PRD Against the Code (`3ece5edd`, includes PR #141 / #127)

### 1.1 Current catalog visibility

| Item | Result (file:line) |
|---|---|
| Model fields | `Table.Tags` and `Table.Visibility` (default `Hidden`) at `src/Autheris.Domain/Model/TableModels.cs:53-54`; `DatasetVisibility { Hidden = 0, Summary = 1, Full = 2 }` at `:17-22`. |
| Not persisted, confirmed | The `TABLES` DDL has no column (`SqliteBaselineSchema.cs:26-41`); all readers build `Table` without them: SQLite `SqliteGovernanceRepository.Catalog.cs:111` (single table, cached), `:258` (`GetAllTablesAsync`), PostgreSQL `PostgreSqlGovernanceRepository.Catalog.cs:52`, SQL Server `SqlServerGovernanceRepository.Catalog.cs:53`. The `Table` that `UpsertTableMetadataAsync` returns (`SqliteGovernanceRepository.Catalog.cs:644`) also omits them. `ReleaseProfileToCatalogViewers` (`TableModels.cs:160`) is not persisted either (no hit under `src/Autheris.Infrastructure`), so today it is always `false`. |
| `FilterForSubject` after PR #141 | `CatalogVisibility.cs:59-140`. A table without an (undenied) Allow consent is dropped for `Hidden` (`:90`), returned **with columns** for `Summary` when `ReleaseProfileToCatalogViewers` (`:95-101`), and returned **with all columns, unfiltered** for `Full` (`:103-107`): no column Deny rule, no `ColumnMaskingRules` DENY removal. #127's scope-aware logic (`:112-138`) applies only to consented tables. Tier rows are never tenant-filtered: the method takes `tenantId` only to filter consents (`:65`). |
| `VisibleTablesAsync` | `CatalogVisibility.cs:220-259`: GovernanceAdmin/ClusterAdmin see everything (`:239-243`), no SID or no consent repository = nothing (`:245-249`), otherwise `FilterForSubject` (`:256`). **No tenant filter on `Summary`/`Full` rows.** `FilterToTenant` (`:151-174`, consent-derived ownership + `Gateway:Catalog:SharedTables`, `DataCatalogOptions.cs:76`) is used only by the GraphQL admin/discovery branch (`QueryTypes.cs:437-441`). Once tiers are persistable, a `Summary` table of tenant A is listed to every authenticated person of tenant B in MCP, Trino, Arrow and Iceberg: **FR-111-8 is violated by the existing code** (PRD risk confirmed, see G-4; latent today because no tier is persisted, closed inside #111 by the `SharedTables` rule of A-4). |
| Tag filter | `McpDatasetCatalog.cs:140` (case-insensitive exact), tags returned at `:166`, `:299`; GraphQL `catalog(tag:)` `QueryTypes.cs:~450+` (`FilterAndPaginateCatalog`), DTO `Tags` `:456`. `FilterByContract` (`CatalogVisibility.cs:176-216`) already treats `Table.Tags` and `Sensitivity` as contract tags: **persisting tags changes which tables a schema contract includes/excludes** (new, intended, but a regression risk, R-6). |
| Only other visibility readers | `BundledConsentRequestService.cs:126` and `QueryAnalysisService.Evaluation.cs:62` (`Hidden` and no grant/owner/admin = "unknown table" diagnostic). With persisted tiers `Summary`/`Full` tables become requestable here, which is the intended discovery-to-request path. No other file reads `Table.Visibility` (grep over `src`). |

### 1.2 All callers of the visibility functions (T-111-1 inventory)

| # | Surface | Call site | Purpose | Treatment in this plan |
|---|---|---|---|---|
| 1 | MCP `list_datasets` / `search_catalog` (alias of list, `McpDatasetTools.cs:70-76`) | `McpDatasetCatalog.cs:148` -> `VisibleTablesAsync` `:788-795` -> `McpCatalogVisibility` (`McpCatalogVisibility.cs:24-44`) | **discovery** | tiers apply (`Discovery` surface); `Summary` rows carry no columns, `Columns.Count` is reported as 0 |
| 2 | MCP `describe_dataset` | `McpDatasetCatalog.cs:198-312` via `FindVisibleAsync` `:653/:780` | **discovery** | tiers apply; `Summary` answer is the restricted view (A-4) |
| 3 | MCP `describe_dataset` relations | `McpDatasetCatalog.cs:653-660` | discovery | relation targets restricted to tables listed on the discovery surface |
| 4 | MCP resources (`autheris://datasets/...`, catalog summary) | `McpResourceProvider.cs:51,68` -> `ICatalogDiscoveryService` (REST discovery service, **not** `CatalogVisibility`) | discovery | see gap G-1 |
| 5 | GraphQL `catalog` query | `QueryTypes.cs:444` (non-admin branch; admin/discovery branch `:437` uses `FilterToTenant`) | **discovery** | tiers apply; tenant filter added (G-4) |
| 6 | REST `GET /api/v1/catalog/datasets`, `/datasets/{id}`, `/search`; `invoke_api` composition | `CatalogApiEndpoints.cs:24-66,98`; `CatalogDiscoveryService.cs:57-84,98-170,180-260`; `ApiDispatcherService.cs:145` | discovery | **gap G-1**, routed through `CatalogVisibility` (WP-5) |
| 7 | Trino `SHOW ... / information_schema` | `TrinoMetadataQueryHandler.cs:88` (columns view `:212-232`) | query surface | `DataAccess` surface: consented tables only, `Summary`/`Full`-without-consent do not appear |
| 8 | Arrow Flight SQL metadata | `ArrowFlightSqlServer.cs:131` | query surface | `DataAccess` surface |
| 9 | Iceberg REST catalog | `IcebergRestCatalogFederationService.cs:113` | query surface | `DataAccess` surface |
| 10 | AI guardrail (four-eyes gate and golden queries) | `AiDataGuardrailService.cs:290` (`isVisible` before the four-eyes gate), `:354` | access gate | **must stay consent-based** (`DataAccess`); a `Summary` table that merely is visible must not pass this gate |
| 11 | Semantic MCP resources | `SemanticMcpCompiler.cs:116` | compiles resources with columns/measures | `DataAccess` surface |
| 12 | `describe_my_access` | `EffectiveAccessExplanationService.cs:59,93` | own access | `DataAccess` (explains consent-derived access only) |
| 14 | REST search engine / hybrid index (`CatalogDiscoveryService.cs:200-262`, `CatalogSearchWarmupService.cs:43`) and MCP `Matches` (`McpDatasetCatalog.cs:798-803`) | search over column names; `RelevantColumns` in results | **discovery** | **G-7, WP-0** (index result post-filtered to the visible set and re-matched without column-derived hits; `RelevantColumns` only for tables whose columns the caller may see); #111 adds `Full` DENY-column removal before matching |
| 15 | REST `GET /api/v1/catalog/datasources` (`CatalogApiEndpoints.cs:55-63`, `CatalogDiscoveryService.cs:268-300`) | domains, endpoint `BaseUrl`, auth configuration state | admin metadata | **G-8, WP-0** (GovernanceAdmin/ClusterAdmin only, GovernanceAdmin tenant-filtered) |
| 16 | GraphQL `catalog` admin/discovery branch (`QueryTypes.cs:435-441`, flag `AllowAuthenticatedCatalogDiscovery`) | `FilterToTenant` result **with all columns** for any non-admin when the flag is on (leaks today with the flag on) | discovery | **G-9, WP-0** (non-admins always take the consent path; flag no longer widens); O-5 |
| 17 | Query analysis `QueryAnalysisService.Evaluation.cs:62-161` (REST/MCP/GraphQL analysis entry points) and `BundledConsentRequestService.cs:111-135` | `select *` expansion, `OutputColumns`, `UnknownColumn` existence oracle for a table without grant | analysis | **G-10, WP-0** (latent today because every table is `Hidden`; fixed consent-based so it cannot become a leak when tiers exist) |
| 18 | MCP `sample_rows` (`McpDatasetCatalog.cs:575-595`) | synthetic rows for a caller without consent when `ReleaseProfileToCatalogViewers` | data-adjacent | **G-11, WP-0** (positive guard) and #111 (`CatalogView` positive) |
| 19 | MCP resources `autheris://catalog/summary`, `autheris://datasets/{id}/schema` (`McpResourceProvider.cs:51,68`) and `invoke_api` (`ApiDispatcherService.cs:111,145`) | same service as row 6 | discovery | **WP-0** through the fixed service; verified per channel in M-12 |
| 13 | Tables read for other purposes without visibility rules | OData `ODataEndpoints.Query.cs:37`, `ODataHandler.cs:137` (per-table authorization), `DynamicOpenApiGenerator.cs:53,72,134`, `TenantCatalogLookup.cs:114` (consent-derived), `GovernanceEndpoints.Access.cs:196` (admin) | n/a | they never consult `Visibility` and filter by authorization or tenant; they stay unchanged, but a **leak regression test per channel** (T-5) pins that a `Summary`/`Hidden` table without consent exposes no column |

### 1.3 PRD gaps found (closed by this plan)

| # | Gap | Evidence | Resolution |
|---|---|---|---|
| G-1 | **(Moved to WP-0, standalone security bugfix; #111 only adds the `Discovery` surface on top.)** **The REST catalog service does not use `CatalogVisibility` at all.** `GET /api/v1/catalog/datasets` and `/{id}` require only an authenticated caller (`CatalogApiEndpoints.cs:33,51`); without ReBAC (`_rebacEvaluator is not { IsEnabled: true }`) `ListDatasetsAsync` returns **every table of every tenant** (`CatalogDiscoveryService.cs:67-79`) and `GetDatasetDetailAsync` returns **all columns with types** (`:141-165`); the MCP resource provider and `invoke_api` use the same service. `Hidden` and the "Summary never exposes columns" invariant cannot hold while this exists. | cited | **WP-0** (consent-based, no tier): for callers that are not GovernanceAdmin/ClusterAdmin, list/detail/search go through `CatalogVisibility.VisibleTablesAsync` (5-arg, `DataAccess` semantics = today's rule) first; ReBAC remains an additional filter (intersection); GovernanceAdmin through `FilterToTenant`; detail of an invisible table is the same 404 as an unknown one. Behaviour change for non-admins without consent: they see their consented tables only (today: everything when ReBAC is off). Owner flag O-1 (answered: yes, accelerate). #111 WP-5 then only switches these call sites to `Discovery`. |
| G-2 | `Full` currently returns columns **ignoring** column-level DENY masking rules and Deny consents (`CatalogVisibility.cs:103-107`). A persisted `Full` would show denied column names to everybody. | cited | `Full` for a caller without consent removes every column that has a `ColumnMaskingRules` DENY rule (as `:122-130` does for consented callers). Unit test in T-2. |
| G-3 | `Summary` listing shows `Columns.Count` and `describe_dataset` returns GraphQL field names, filter/order type names, golden query texts, endpoint URLs and indexes for any visible table (`McpDatasetCatalog.cs:166,300-308`, golden examples `:268-275`). With emptied columns most is empty, but golden queries and GraphQL access (`QueryField`, example query) are column-derived. | cited | A-4: the `Summary` view is an allow-list, built explicitly, not derived by removing fields. |
| G-4 | The tenant concept for tables does not exist (`TableMetadata` has no tenant; ownership is consent-derived, `CatalogVisibility.cs:143-149`). "`Summary` tenant-wide" (Q-111-2) therefore needs a definition. The consent-derived signal is forgeable (a tenant admin can create a consent, also a Deny consent, on a foreign table identifier) and circular (SEC-111-02). | cited | **Rev. 2 resolution (BC-3, O-2):** in multi-tenant mode a `Summary`/`Full` tier applies to a table **only when it is in the operator list `Gateway:Catalog:SharedTables`**; the consent-derived part of the signal is dropped for tiers. No consent (Allow, Deny, pending, expired) can make a table tier-visible. Single-tenant (`TenantId.LegacySingleTenant`) is unrestricted. A dedicated tenant-owner attribute for tables is the scheduled follow-up (F-1). Consequence documented (R-3, O-2). |
| G-5 | **Corrected in rev. 2 (SEC-111-01).** All three repositories have a **process-local** `_metadataCache` with a 30 s TTL (`MetadataCacheTtlTicks`): SQLite `SqliteGovernanceRepository.Catalog.cs:68-216` (generation invalidation via `QueueMetadataInvalidation`, `SqliteGovernanceRepository.cs:47`), PostgreSQL `PostgreSqlGovernanceRepository.cs:43,49`, `PostgreSqlGovernanceRepository.Catalog.cs:22,155,180,385,429,503,516`, SQL Server `SqlServerGovernanceRepository.Catalog.cs:23,156,181`. `GetAllTablesAsync` consults the cache on PostgreSQL and SQL Server too. There is no cross-node invalidation, so on a multi-replica deployment another replica would serve an old tier (including `Full` after lowering to `Hidden`) for up to 30 s. | cited | A-6: tier and tags are read **outside** the cache on every engine (always fresh on every replica); the cache keeps only structural data; all new write methods additionally remove the local key. No staleness window is accepted for restrictions. |
| G-7 | Search is a column-name oracle: the REST search engine / hybrid index is built from the whole catalog including columns and returns `RelevantColumns`; MCP `Matches` matches column names; the index result is filtered only by the (unfiltered) list (`CatalogDiscoveryService.cs:200-262`, `CatalogSearchWarmupService.cs:43`, `McpDatasetCatalog.cs:798-803`). Exists today for REST (G-1). | cited (P3.2/SEC-111-05) | WP-0: post-filter and re-match (A-0.2). |
| G-8 | `GET /api/v1/catalog/datasources` discloses every domain, endpoint `BaseUrl` and auth state to any authenticated caller (`CatalogApiEndpoints.cs:55-63`, `CatalogDiscoveryService.cs:268-300`). Exists today. | cited | WP-0: GovernanceAdmin/ClusterAdmin only (A-0.3). |
| G-9 | With `AllowAuthenticatedCatalogDiscovery = true` (default `false`, `DataCatalogOptions.cs:69`) any non-admin takes the `FilterToTenant` branch and receives **all columns** of every tenant-visible table (`QueryTypes.cs:435-456`). Leaks today when the flag is on. | cited (SEC-111-04) | WP-0: non-admins always take the consent path (A-0.4); O-5. |
| G-10 | Query analysis continues column evaluation for a table without grant/owner/admin unless `Visibility == Hidden` (`QueryAnalysisService.Evaluation.cs:62-79,103-161`): `select *` expansion, `OutputColumns`, `UnknownColumn` oracle. Latent today (every table is `Hidden`), live the moment any tier is persisted. | cited (SEC-111-06) | WP-0: consent-based stop at table level, independent of `Visibility` (A-0.5). |
| G-11 | `sample_rows` (synthetic) allows a caller without consent when `ReleaseProfileToCatalogViewers` (`McpDatasetCatalog.cs:589`); the planned `CatalogView != Summary` guard was negative and the enum default `Granted` fail-open (SEC-111-07). `ReleaseProfileToCatalogViewers` is always `false` today (not persisted). | cited | WP-0 pins the guard positively (A-0.6); #111 A-4 uses a restrictive enum default and a positive `Full` check. |
| G-12 | Tenant-scoped GovernanceAdmin could change tier/tags of any dataset (global catalog; `/state` and `/keys` precedent, `CatalogApiEndpoints.cs:121-150`, `GovernanceEndpoints.Catalog.cs:69-74`) (SEC-111-03). | cited | A-5 (rev. 2). |
| G-6 | PRD FR-111-3 asks for an MCP admin tool "two-phase HITL". The plan store (`StoredAccessPlan`, `IAccessPlanStore.cs:12`) is typed to access grants and has no plan kind; the established single-call precedent for catalog mutations is `admin_set_dataset_state` (`McpAdminTools.cs:23,126,197-203`). | cited | A-8: one tool following the `admin_set_dataset_state` precedent (cluster admin only); revisit if the plan kind of PLAN-GOV-107 has landed (O-3). |

### 1.4 Where tags exist today

| Source | State |
|---|---|
| dbt manifest | `DbtModelDefinition.Tags` / `DbtColumnDefinition.Tags` (`DbtMetadataModels.cs:11,31`) are parsed but never written to `Table.Tags`. `DbtMetadataIngestionService` upserts at `:493` (model tables) and `:976`; the only tag literal is the synthetic `["dbt","semantic-model"]` in `DbtSemanticManifestIngestionService.cs:333`. Ingestion upserts rebuild `TABLES` and never touch a tag store (and will not, A-2). |
| OpenMetadata / DataHub | `OpenMetadataModels.cs:62,99` carry tags in import/export models only; no write into the catalog. Out of scope. |
| `ClassificationEngine` / F-GOV-13 | separate namespace (sensitivity, PII). Not touched; tags never influence masking or policy (SI-111-4). |
| In memory only | Yes: `Table.Tags` exists solely on the model and is empty after every read. |

### 1.5 Persistence, shapes, admin API, manifest, ownership, audit, epochs

- **Baseline pattern (#94/#107):** `CREATE TABLE IF NOT EXISTS TABLE_ROW_FILTER_STRATEGIES` appended at the end of the baseline in all three engines (`SqliteBaselineSchema.cs:508-516`, `PostgreSqlBaselineSchema.cs:518-526`, `SqlServerBaselineSchema.cs:606-616`); types verified: `table_id TEXT PRIMARY KEY` / `NVARCHAR(128)`, `updated_by TEXT / NVARCHAR(256)`, `updated_at TEXT / TEXT / NVARCHAR(64)`. Shape snapshots `governance-shape.{sqlite,postgresql,sqlserver}.json` (e.g. sqlite `:1480` for the strategy table) and `GovernanceSchemaShapeSnapshotTests` are regenerated/extended. Checksum constants are unchanged.
- **Repository writes:** `ReplaceTableKeyDefinitionAsync` (`SqliteGovernanceRepository.Catalog.cs:675-709`) is the template: write scope + transaction, `IncrementTableEpochInternalAsync`, `QueueMetadataInvalidation`, `_epochValidationService.InvalidateEpochAsync`. Interface defaults throw `NotSupportedException` (`IGovernanceRepository.cs:10-14`, #107 style).
- **Admin API:** `CatalogApiEndpoints.cs:121-151` (`/datasets/{id}/state`, `/datasets/{id}/keys`, both `GatewayPolicies.GovernanceAdmin`, `WithAudit`); id resolution `ResolveActiveDatasetAsync` `:158-190` (identical 404 for unknown/inactive); post-commit `RecordRequiredAsync` audit pattern `:245-263`; event type registration `AuditEventTypes.cs:60` and `AuditPolicyTable.cs:56` (`FailClosed`).
- **Manifest (F-GOV-15):** `DeclarativeConsentManifest(Version, Policies)` (`DeclarativeConsentModels.cs:9-11`) is consent-only; apply service `DeclarativeConsentApplyService.cs` resolves tables with `GetTableMetadataAsync` (`:85`) and records `GITOPS_CONSENT_APPLY`. Extended by an optional `Datasets` list (A-7).
- **Ownership:** `IDataOwnershipRepository.IsAuthorizedApproverForTableAsync(table, sid)` (`IGovernanceRepository.cs:242-247`) is true for the data owner and active delegates; used the same way for owner-gated admin actions in `GovernanceEndpoints.Access.cs:465-476` (fail closed 503 when the repository is missing).
- **Read-only tokens:** `EntraTokenPolicy.cs:74-86` marks read-only scopes; non-GET mutation is rejected for them by the existing token policy (unchanged; test T-4).
- **Epochs / catalog version:** a visibility or tag change does not affect any access decision (FR-111-9), so **no policy epoch bump** (a bump would flush decision caches for nothing). `CatalogVersionProvider` (`CatalogVersionProvider.cs:17-60`) hashes only structure (identifiers, activity, source type, column names and types) and drives GraphQL schema rebuilds (`CatalogGraphQlTypeModule.cs:70,142`); neither tags nor visibility change a generated type, so the digest stays as it is (a unit test pins "tags/visibility do not change the catalog version", avoiding schema rebuilds on every tag edit). Discovery reads (`GetAllTablesAsync`) are uncached; only the SQLite single-table cache needs invalidation (G-5).

---

## 2. Architecture Decisions

### A-0 WP-0 - standalone security bugfix of the pre-existing catalog leaks (independent of #111)

WP-0 is a self-contained change set. **Invariants:** it uses only the current consent-based `CatalogVisibility` (5-argument overloads = `DataAccess` semantics, no `CatalogSurface`, no tier, no `CatalogView`, no `TABLE_TAGS`/settings table, no migration, no new public contract of #111); it compiles and is green with `main` alone; #111 and #107 rebase onto it, not the other way round. Owner rule "security bugs first": it is merged before #107 and before #111 (tracker step `I-6a`, BC-1). The owner flag O-1 applies to this PR (answered: yes).

**A-0.1 REST list/detail (G-1).** `CatalogDiscoveryService.ListDatasetsAsync` and `GetDatasetDetailAsync` resolve the caller class first: ClusterAdmin = all (unchanged); GovernanceAdmin = `CatalogVisibility.FilterToTenant` (the rule already used by GraphQL, SR-L-21; this keeps the existing consent-derived tenant signal for an admin view, its weakness is tracked as F-1 and is not widened); everybody else = `CatalogVisibility.VisibleTablesAsync` (consents of the caller, #127 column logic). The ReBAC evaluator stays an additional filter (intersection of both sets, never a union). Detail for a table outside the set is the same `404` body/status as an unknown dataset; the detail columns come from the filtered `TableMetadata` (only columns the caller's consent allows). No tenant or caller input beyond the authenticated principal is used. Missing consent repository or principal without SID = empty list / 404 (fail closed).

**A-0.2 Search (G-7).** REST search (index ready and fallback) and the `invoke_api`/MCP resource paths: the index hit list is intersected with the visible set **before** shaping; each surviving hit is re-evaluated against the filtered `TableMetadata` (only visible columns): a hit whose only match is a column the caller cannot see is dropped; `RelevantColumns` is limited to visible columns (empty for admins' own scope rules unchanged). MCP `Matches` already searches the filtered table; a test pins it. Highlights, `MatchedTerms` and scores of dropped hits never leave the service.

**A-0.3 `/datasources` (G-8).** Endpoint policy: GovernanceAdmin or ClusterAdmin (`GatewayPolicies`), non-admin 403; the service additionally tenant-filters domains for a non-cluster admin by the domains of its `FilterToTenant` set and never returns `BaseUrl`/auth state for a domain outside it. A service-level call by a non-admin returns an empty list (fail closed). Behaviour change flagged with O-1 (release note).

**A-0.4 GraphQL flag (G-9, O-5).** `QueryTypes.cs`: the `isGovernanceAdmin || allowDiscovery` branch becomes `isGovernanceAdmin`; non-admin callers always use `FilterForSubject` (consent path), the flag no longer widens anything and logs one Warning at startup when set (deprecation, O-5). No new listing of consent-less tables exists in WP-0 (tiers in #111 provide the sanctioned form of "consent-less tenant users may see a listing").

**A-0.5 Query analysis (G-10).** In `QueryAnalysisService.Evaluation.cs`, a table without table grant, ownership or admin role yields exactly the existing `UnknownTable` diagnostic when `Visibility == Hidden` (unchanged, today always) **and**, for any other value, the table-level `MissingConsents` item and then `return` (no `select *` expansion, no `OutputColumns`, no `UnknownColumn`, no column-level `MissingConsents`). The decision no longer depends on `Visibility` for the column stage, so it cannot regress when #111 persists tiers. `BundledConsentRequestService.cs:111-135` is verified: a column-level request for a table without grant/owner/admin is converted to the table-level READ request or answered as unknown (no per-column validation message), pinned by test.

**A-0.6 `sample_rows` guard (G-11).** The synthetic branch is rewritten positively: `allowed = isAdmin || isGranted || (table.ReleaseProfileToCatalogViewers && <explicitly full-marked>)`; in WP-0 the third term is `false` (the flag is not persisted, so the effective behaviour stays "consent or admin only") and a code comment/test states that the term is replaced by `CatalogView == Full` in #111 WP-3; every other case throws `GatewayForbiddenException`. Non-synthetic `sample_rows` keeps its consent requirement; a test pins both.

**Not in WP-0:** tiers, tags, tenant rule for `Summary`, admin APIs, DDL, DENY-removal for `Full` (needs tiers), any `CatalogSurface` plumbing. WP-0 changes no signature that #111 changes (the signature work of C-2 happens in #111; WP-0 only calls existing overloads).

### A-1 Storage: two tables, separate from `TABLES` (D-111-1 confirmed)
`TABLE_TAGS (table_id, tag, source, created_by, created_at; PK (table_id, tag))` and `TABLE_CATALOG_SETTINGS (table_id PK, visibility, updated_by, updated_at)`. Reason as F-GOV-21/#107: ingestion upserts rewrite `TABLES`; owner decisions must survive. No foreign keys and no CHECK constraints (consistent with `TABLE_ROW_FILTER_STRATEGIES`); validity is enforced on write and re-validated on read. `table_id` is the `TABLES.id` GUID in its `D` form. A missing visibility row = `Hidden`.

### A-2 Tag sources and merge rule (FR-111-4)
`source` is `admin` or `dbt`. Admin writes (`PUT/POST/DELETE`, manifest) store `admin`. The dbt import calls `ReplaceDatasetTagsAsync(table, tags, Dbt)`: inside one transaction it deletes rows with `source = 'dbt'` that are not in the new set, inserts missing tags as `dbt`, and **never** deletes or re-sources an `admin` row (a tag present as `admin` stays `admin`). The ingestion upsert (`UpsertTableMetadataAsync`) neither reads nor writes tags or visibility: no hidden coupling, and re-ingestion cannot reset an owner decision (AC 5). The dbt hook is a call after the existing upserts at `DbtMetadataIngestionService.cs:493` and `:976` using `model.Tags` (model-level tags only; column tags are not dataset tags).

### A-3 Tag syntax, normalization, limits (D-111-3)
`DatasetTagRules` (Domain, pure): trim, lower-case invariant, 1-64 chars of `[A-Za-z0-9._:-]`, distinct, at most `Catalog:MaxTagsPerDataset` (default 32, hard cap 256, validated on start) **per dataset in total across sources**. Invalid dbt tags are dropped one by one with a Warning (count, no values logged beyond a truncated length-capped sample); an invalid admin tag rejects the whole request (`INVALID_TAG`). Overflow from dbt drops the excess in sorted order with a Warning; overflow from admin = `TAG_LIMIT_EXCEEDED`. Tag filters keep matching case-insensitively (stored lower-case; `McpDatasetCatalog.cs:140` unchanged). Tags are escaped by the JSON serializers on every output (no SQL or policy use, SI-111-4); the only code that reads tags for a decision is `FilterByContract` (R-6).

### A-4 Visibility surfaces and the `Summary` allow-list view (D-111-2, G-3)
New enum `CatalogSurface { DataAccess = 0, Discovery = 1 }` (Application). `DataAccess` is the **default** of every overload (fail closed): only admins and consented tables, exactly today's behaviour for tables without a persisted tier. Only the discovery surfaces of section 1.2 (rows 1-6) pass `Discovery`, where the tiers apply:

- `Hidden`: not listed, `describe_dataset` answers as for an unknown table (`TableNotFoundException`, `McpDatasetCatalog.cs:659-660`; #117 behaviour).
- `Summary`: listed for every authenticated caller of the tenant for tables in `Gateway:Catalog:SharedTables` (multi-tenant) or every table (single-tenant), see G-4 (rev. 2: consent records never make a table tier-visible). **Columns are always empty; `ReleaseProfileToCatalogViewers` is ignored for `Summary`** (the `:97-100` branch is deleted).
- `Full`: columns of the table minus every column with a table-level DENY masking rule (G-2); **the removal happens inside `FilterForSubject`, before any search matching or index result shaping (BC-6)**; `ReleaseProfileToCatalogViewers` keeps its meaning for `Full` only (XML doc at `TableModels.cs:159` corrected from "Summary or Full").
- A caller with an undenied Allow consent always takes the consent path (columns per consent, #127 scope logic untouched).
- **`DataAccess` removes the tier branches entirely** (BC-12): in that mode the `Hidden`/`Summary`/`Full` switch is not executed at all (a table without Allow consent is dropped), it is not "conditionally skipped".
- **GraphQL `AllowAuthenticatedCatalogDiscovery` (BC-5, O-5):** tiers always win; the flag is deprecated and has no effect on columns or listing (WP-0, A-0.4); non-admin GraphQL discovery uses `FilterForSubject(Discovery)` only.

`FilterForSubject` returns a new init property `TableMetadata.CatalogView` (`Unmarked = 0 | Granted | Summary | Full`; **the default is `Unmarked`, which every consumer treats as the most restrictive value, never as `Granted`** (SEC-111-07/BC-8); the filter sets `Granted`, `Summary` or `Full` explicitly on every row it returns, including the consent path) so that consumers know which restriction applied without recomputing, and every consumer checks **positively** (`== Full`, `== Granted` with a consent), never negatively. **`describe_dataset` builds the `Summary` answer from an allow-list** (new `McpDatasetCatalog.BuildSummaryDescription`): dataset id and domain/schema/table, `Description`, `Sensitivity`, `RequiresFourEyes` (not column-derived; needed by a requester, Phase 3 to confirm), `Tags`, `DataOwner`, `DataOwnerDelegates`, `AccessStatus`, `View = "summary"`; **not** included: `LongDescription` (free text, may name columns), columns, examples (golden queries), `GraphQl` access block, `Access` endpoint hints, `Indexes`/keys, value profiles, row counts, freshness. `McpDatasetSummary` for `Summary` rows reports `ColumnCount = 0` and no GraphQL query field. `search`/`Matches` therefore cannot match on column names; the REST index path is already post-filtered by WP-0 (A-0.2) and #111 adds that `Summary` hits are re-matched on identifier/description only and `Full` hits only on non-DENY columns. **`DataOwner` and `DataOwnerDelegates` in the `Summary` view are display names only, never SIDs, e-mails or UPNs (BC-14)**; a reflection test pins the exact allowed property set (any new property of `McpDatasetDescription` defaults to excluded). `sample_rows`: the WP-0 positive guard (A-0.6) is extended so that the no-consent synthetic branch is allowed **only** for `CatalogView == Full` with `ReleaseProfileToCatalogViewers` (flag on; DENY columns excluded); `Summary`, `Granted` without a consent, `Unmarked` and every future value throw `GatewayForbiddenException`. GraphQL `catalog` returns `Columns = []` for `Summary`.

### A-5 Who may change what (Q-111-1, rights checked by the backend)
One application service `DatasetCatalogAdminService` is the only writer; REST, the manifest and the MCP tool all call it. It authorizes from the **authenticated `ClaimsPrincipal`** and the repositories, never from request fields:

| Caller | Tags | `Hidden` / `Summary` | `Full` |
|---|---|---|---|
| GovernanceAdmin (tenant-scoped role) and canonical ClusterAdmin | yes | yes | **yes** |
| Data owner or active delegate (`IsAuthorizedApproverForTableAsync`) | yes | yes | **no** (`FORBIDDEN` 403) |
| Everybody else, read-only tokens, anonymous | no | no | no |

Rules: a caller who is neither admin nor owner gets the same `404` as for an unknown dataset (no existence oracle for hidden datasets); an owner who requests `Full` gets `403 FORBIDDEN` (the dataset is known to them) and the attempt is audited as `DENY`; an owner may always **lower** a `Full` dataset to `Summary`/`Hidden` (restricting is allowed); an owner "setting" `Full` on an already-`Full` dataset is still refused (no special-casing). Ownership repository unavailable -> `503`, fail closed (as `GovernanceEndpoints.Access.cs:470-473`). The tier decision uses the **requested** value only after parsing it with `Enum.TryParse` + `Enum.IsDefined` (strings or numbers outside the three names -> `INVALID_VISIBILITY` 400; `Full` is never reachable through an unrecognized value). **Tenant scope (SEC-111-03/BC-4, rev. 2):** the dataset catalog is global but a tier on a `SharedTables` table is visible to **every** tenant, so a tenant-scoped lever is a cross-tenant lever. In **multi-tenant mode only the canonical ClusterAdmin may change tags or tier of a dataset by role**; a tenant-scoped GovernanceAdmin is treated like "everybody else" (manageable only if the ownership store makes it the owner/delegate of that table, else the identical 404). In single-tenant mode GovernanceAdmin is unrestricted as before. This holds for REST, manifest (a `Datasets` entry the caller cannot manage is reported as unknown and skipped) and the MCP tool. The mutation audit carries the request tenant and the principal's authentication type (token vs session) (BC-13).

**Mandatory order (SEC-111-08/BC-9):** (1) authenticate; (2) if the caller is not an admin per the rule above and the ownership store is unavailable: **503 for every such caller, before any dataset is resolved**; (3) resolve the dataset (unknown, inactive: same 404); (4) determine `manageable` (admin or owner/delegate), else the identical 404 (same body, same headers, no distinguishing detail, no early exit on a cheaper path; a DENY audit entry is written for the probe without influencing the response); (5) validate the body (400 codes) only now; (6) enforce the `Full` rule (owner: 403 `FORBIDDEN`, DENY audit); (7) write. The 403 is returned only after ownership is established.

**Read modes (SEC-111-09/BC-10):** the catalog read path degrades to `Hidden` (A-6); the admin service `GetAsync`/`ApplyAsync` reads the before-state through a **strict** repository read that throws on failure (503, nothing written, no audit `before` of a fake `Hidden`).

### A-6 Persistence semantics, caches, no epoch

**Cache rule (rev. 2, SEC-111-01/BC-2, replaces the rev. 1 premise):** `_metadataCache` is process-local with a 30 s TTL on **all three** engines and has no cross-replica invalidation. Decision: **tier and tags are never stored in or served from `_metadataCache`.** The cached entry stays structural (columns etc., with `Visibility = Hidden`, no tags, `CatalogView = Unmarked`); `GetTableMetadataAsync` and `GetAllTablesAsync` overlay a **fresh** settings/tags read on every call (single table: two PK lookups by `table_id`; list: two set-based queries), on every engine, so a restriction (e.g. `Full` -> `Hidden`) is effective on every replica on the next read with **zero** accepted staleness. In addition each write method removes the local cache key on commit (`TryRemove` on PostgreSQL/SQL Server, generation/`QueueMetadataInvalidation` on SQLite) so a replaced structural entry never carries an overlay (test: cache entries contain no tier/tags). Residual: column structure (not security-relevant for tiers: `Summary` shows no columns, `Full` columns are DENY-filtered from fresh rules) keeps the existing <= 30 s TTL. Extra cost: one small indexed query per single-table read; measured in the contract test, accepted.

Repository writes run in one transaction with the `before` read, return a `DatasetCatalogChange` (before/after tags and visibility), and then removes the local cache key on **every** engine (SQLite: `QueueMetadataInvalidation` + `ApplyMetadataInvalidations`; PostgreSQL/SQL Server: `_metadataCache.TryRemove`). No `IncrementTableEpoch*` (reasoning in 1.5). Replace semantics are idempotent (`PUT` tags with the same set = no change, `Changed = false`, no audit entry, status 200). Both readers (`GetAllTablesAsync`, `GetTableMetadataAsync`) load tags and the settings row **outside the metadata cache** (see cache rule) inside the existing snapshot read scope with two additional set-based queries (all tags, all settings; for a single table by `table_id`), no per-table queries. **Two read modes:** the catalog read mode degrades per row to `Hidden` (below); the admin read mode (`GetDatasetCatalogSettingsAsync(strict: true)`, used by `DatasetCatalogAdminService`) throws on any store error. **Read failures degrade to the secure value**: a failing, missing or malformed tag/settings query (store error, unknown stored integer, tag failing `DatasetTagRules`) yields `Hidden` and empty tags for the affected rows, an Error log with row id (no values), and a counter; it never throws the whole catalog read and never yields `Full` (SI-111-2). Stored visibility integers are mapped with an explicit switch; every value other than 0, 1, 2 is `Hidden`.

### A-7 Declarative import (FR-111-5)
`DeclarativeConsentManifest` gains an optional trailing `IReadOnlyList<DeclarativeDatasetCatalog>? Datasets = null` (`Domain, Schema, TableName, Tags?, Visibility?`; null field = unchanged, never a reset; an empty `Tags` list = replace with none, only when the property is present). It is applied by `DeclarativeConsentApplyService` through `DatasetCatalogAdminService` with the caller principal (the apply endpoint is GovernanceAdmin-only, `GovernanceEndpoints.cs`), so `Full` through the manifest is an admin act and audited like the REST path. Dry run reports the planned changes and writes nothing (as for consents), **but runs exactly the same authorization, tenant rule (A-5) and validation as apply** (BC-11), so it is not a visibility oracle; a non-admin dry run is 403. `Visibility` is parsed with `DatasetTagRules.TryParseVisibility` (names only; numeric strings, comma lists, numbers rejected), never with the `JsonStringEnumConverter` of `GitOpsJsonOptions`. `DatasetCatalogAdminService` never accepts a null or system principal for `Full` (unit test); a future non-HTTP apply (GitOps pull job) must pass a ClusterAdmin/GovernanceAdmin principal. A manifest entry for an unknown table is reported and skipped (existing wording `:89`). Old manifests (without `Datasets`) deserialize unchanged.

### A-8 Admin surfaces
REST (all through `DatasetCatalogAdminService`, `RequireAuthorization()` plus the in-service check of A-5, **not** `GatewayPolicies.GovernanceAdmin`, because owners must reach them):
- `GET /api/v1/catalog/datasets/{id}/catalog-settings` -> `{ datasetId, tags: [{tag, source}], visibility }` (admin or owner; else 404).
- `PUT /api/v1/catalog/datasets/{id}/tags` body `{ tags: [..] }` replace admin-source tags; `POST .../tags` body `{ add: [..], remove: [..] }`; both idempotent.
- `PUT /api/v1/catalog/datasets/{id}/visibility` body `{ visibility: "Hidden"|"Summary"|"Full" }`.
- Routes mirror the `/keys` registration including the legacy `/api/catalog/...` alias; `WithAudit(AuditLevel.Full, CatalogVisibilityChanged / CatalogTagsChanged)`. Dataset id resolution reuses `ResolveActiveDatasetAsync` (extracted to an internal static helper shared with `/keys`, behaviour unchanged).
- Errors: `INVALID_TAG`, `TAG_LIMIT_EXCEEDED`, `INVALID_VISIBILITY` (400), `FORBIDDEN` (403, owner asks for `Full`), `NOT_FOUND` (404 identical for unknown, inactive and not manageable), `503` when the ownership store or audit store is unavailable.
MCP: `admin_set_dataset_catalog` (arguments `dataset`, optional `tags`, optional `visibility`), registered like `admin_set_dataset_state` (`McpAdminTools.cs:23,38,126,197`): usable only by the canonical cluster admin through a non-read-only session (`CanUseAdminTools` `:42-52`), calls the same service with the session principal rebuilt from the session (`McpProtocolHandler.BuildPrincipalFromSession`), so an agent cannot widen anything beyond what its principal may do. Group/gating by PLAN-SEC-115: the five/six admin tools are "ungrouped" there, so the new name is added to the pin test of `McpToolGroups` (decision: ungrouped, only `DisabledTools` can disable it).

### A-9 Audit, telemetry, configuration
- Audit events `CATALOG_VISIBILITY_CHANGED` and `CATALOG_TAGS_CHANGED` (`AuditEventTypes`, `AuditPolicyTable` = `FailClosed`), written post-commit with `RecordRequiredAsync(..., CancellationToken.None)` as `CatalogKeyDefinitionChanged` (`CatalogApiEndpoints.cs:245-263`): actor SID, target table id, `before`/`after`, `source` (`admin|manifest|mcp|dbt`); an unavailable audit store surfaces as 503 through the exception handler. A refused attempt (owner asks `Full`, caller not manageable) is recorded with `Decision = "DENY"` via the normal writer (best effort). dbt tag replacement writes one `CATALOG_TAGS_CHANGED` entry per table only when the set changed. **Rev. 2 additions (SEC-111-12/BC-13):** (a) refused attempts are audited with `Decision = "DENY"` for the owner-asks-`Full` case **and** for foreign/non-manageable callers (probing signal), without changing the identical 404 response; (b) `before`/`after` are explicit structured fields (`visibility`, `tags`), so an alert can key on `after.visibility == Full`, and the entries carry the request tenant and the authentication type (token vs session); (c) the effective `Catalog:DefaultVisibility` is audited once at startup and on every configuration change, and each ingestion run that created tables with a non-`Hidden` default writes **one aggregated** audit entry (count, source; no table names required).
- Metric `autheris_catalog_visibility_total{tier}` (observable gauge over a cached count, refreshed on change and at start; no table names or ids as labels), counter `autheris_catalog_visibility_degraded_total` (A-6 degradations).
- Configuration (`DataCatalogOptions`, section `Gateway:Catalog`): `MaxTagsPerDataset` (32; range 1-256), `DefaultVisibility` (`Hidden`; only `Hidden` and `Summary` are valid, anything else, including `Full`, fails startup validation). The default is applied **at table creation only** (A-10), never at read time, so flipping the option never exposes existing tables.

### A-10 New-source default (Q-111-3)
Interface `ICatalogVisibilityDefaults` (Domain, `DatasetVisibility NewTableDefault`, default `Hidden`) is injected optionally into the three governance repositories. In the **insert branch** of `UpsertTableMetadataAsync` (SQLite `:473` `INSERT INTO TABLES`; PostgreSQL and SQL Server analogues), when the default is `Summary`, the repository inserts the `TABLE_CATALOG_SETTINGS` row in the same transaction with `updated_by = 'system:default'`. Existing tables and the `Hidden` default write nothing. No per-table audit entry for the system default (volume); instead the aggregated creation audit and the startup/configuration-change audit of A-9(c) (BC-13). The default affects only tables created after the option change; existing tables never change (test M-14).

### A-11 Alternatives rejected
- Tags/visibility columns on `TABLES`: re-ingestion overwrites owner decisions (PRD veto 2).
- Read-time default visibility option: flipping it would expose every existing table retroactively.
- Applying tiers on all surfaces (Trino, Arrow, Iceberg): those are query metadata surfaces; showing a table that every query will refuse is noise and a column-name leak vector for `Full`. Tiers apply to discovery surfaces only (A-4).
- Per-dataset group scoping of `Summary`: later extension (Q-111-2).
- Bumping the policy epoch on tier change: no access decision changes (FR-111-9), would flush caches for every admin edit.
- Adding visibility/tags to `CatalogVersionProvider`: would rebuild the GraphQL schema on every tag edit for no type change.

---

## 3. Contracts (fixed up front; first commit "contracts")

### C-1 Domain (`src/Autheris.Domain`)
```csharp
// Model/TableModels.cs additions
public enum CatalogView { Unmarked = 0, Granted = 1, Summary = 2, Full = 3 }   // default Unmarked = most restrictive; consumers check positively
public sealed record TableMetadata { /* ... */ public CatalogView CatalogView { get; init; } = CatalogView.Granted; }

// Model/DatasetCatalogModels.cs (new)
public enum DatasetTagSource { Admin = 0, Dbt = 1 }
public sealed record DatasetTag(string Tag, DatasetTagSource Source);
public sealed record DatasetCatalogSettings(IReadOnlyList<DatasetTag> Tags, DatasetVisibility Visibility);
public sealed record DatasetCatalogChange(TableIdentifier Table, DatasetCatalogSettings Before, DatasetCatalogSettings After)
{ public bool Changed => /* tags set or visibility differ */; }

// Model/DatasetTagRules.cs (new, pure)
public static class DatasetTagRules
{
    public const int MaxTagLength = 64;
    public static bool TryNormalize(string? raw, out string tag);                       // trim, lower, charset, length
    public static bool TryNormalizeSet(IEnumerable<string?> raw, int max, out IReadOnlyList<string> tags, out string errorCode); // INVALID_TAG | TAG_LIMIT_EXCEEDED
    public static DatasetVisibility ReadStored(int? stored);                            // 0/1/2 else Hidden (fail closed)
    public static bool TryParseVisibility(string? raw, out DatasetVisibility v);        // names only, Enum.IsDefined, no numeric strings
}

// Interfaces (Domain or Application, next to IClock-style options)
public interface ICatalogVisibilityDefaults { DatasetVisibility NewTableDefault { get; } }

// Audit/AuditEventTypes.cs
public const string CatalogVisibilityChanged = "CATALOG_VISIBILITY_CHANGED";
public const string CatalogTagsChanged = "CATALOG_TAGS_CHANGED";

// Options/Integrations/DataCatalogOptions.cs
public int MaxTagsPerDataset { get; init; } = 32;
public DatasetVisibility DefaultVisibility { get; init; } = DatasetVisibility.Hidden;

// Model/DeclarativeConsentModels.cs
public sealed record DeclarativeConsentManifest(string Version, IReadOnlyList<DeclarativeConsentPolicy> Policies,
    IReadOnlyList<DeclarativeDatasetCatalog>? Datasets = null);
public sealed record DeclarativeDatasetCatalog(string Domain, string Schema, string TableName,
    IReadOnlyList<string>? Tags = null, string? Visibility = null);
```

### C-2 Application (`src/Autheris.Application`)
```csharp
public enum CatalogSurface { DataAccess = 0, Discovery = 1 }

// Services/CatalogVisibility.cs (signature changes; DataAccess default = fail closed)
public static IReadOnlyList<TableMetadata> FilterForSubject(
    IEnumerable<TableMetadata> allTables, IEnumerable<Consent> activeConsents, TenantId tenantId,
    IReadOnlySet<string>? callerScopes = null,
    CatalogSurface surface = CatalogSurface.DataAccess,
    IReadOnlySet<TableIdentifier>? sharedTables = null);   // Discovery + multi-tenant: tiers apply ONLY to this set (= Gateway:Catalog:SharedTables); null/empty => tiers off (fail closed). Consents never extend it.
public static Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(/* existing params */, CatalogSurface surface = CatalogSurface.DataAccess, CancellationToken ct = default);
// McpCatalogVisibility.VisibleTablesAsync gains the same optional surface parameter.

// Interfaces/IGovernanceRepository.cs, ITableMetadataRepository (defaults throw NotSupportedException, #107 style)
Task<DatasetCatalogSettings?> GetDatasetCatalogSettingsAsync(TableIdentifier table, bool strict = true, CancellationToken ct = default);   // strict: throws on store error (admin read mode)
Task<DatasetCatalogChange?> ReplaceDatasetTagsAsync(TableIdentifier table, IReadOnlyList<string> tags, DatasetTagSource source, Sid actor, CancellationToken ct = default);
Task<DatasetCatalogChange?> ChangeDatasetTagsAsync(TableIdentifier table, IReadOnlyList<string> add, IReadOnlyList<string> remove, Sid actor, CancellationToken ct = default); // admin source only
Task<DatasetCatalogChange?> SetDatasetVisibilityAsync(TableIdentifier table, DatasetVisibility visibility, Sid actor, CancellationToken ct = default);
// all: null when the table is unknown/inactive; one transaction (strict before-read, write); no epoch bump; every engine removes its local metadata cache key on commit; tier/tags are never cached (A-6).
// ReplaceDatasetTagsAsync(Admin) replaces ONLY admin-source rows, (Dbt) only dbt-source rows (A-2).

// Governance/Interfaces/IDatasetCatalogAdminService.cs + Governance/Services/DatasetCatalogAdminService.cs (new)
public sealed record DatasetCatalogEdit(IReadOnlyList<string>? ReplaceTags, IReadOnlyList<string>? AddTags, IReadOnlyList<string>? RemoveTags, string? Visibility);
public enum DatasetCatalogOutcome { Applied, Unchanged, Forbidden, NotFound, InvalidTag, TagLimitExceeded, InvalidVisibility, Unavailable }
Task<DatasetCatalogResult> ApplyAsync(ClaimsPrincipal caller, TableIdentifier table, DatasetCatalogEdit edit, string source, CancellationToken ct);
Task<DatasetCatalogResult?> GetAsync(ClaimsPrincipal caller, TableIdentifier table, CancellationToken ct);   // null => 404
```
The service decides authorization (A-5) before any repository write, validates the whole edit before writing anything (visibility and tags together are atomic from the caller's view: if the visibility part is `Forbidden`, no tag change is applied), and records the audit entries.

### C-3 Governance DDL (in place in `0001`, checksums unchanged)

| Engine | `TABLE_TAGS` | `TABLE_CATALOG_SETTINGS` |
|---|---|---|
| SQLite | `table_id TEXT NOT NULL, tag TEXT NOT NULL, source TEXT NOT NULL, created_by TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY (table_id, tag)` | `table_id TEXT PRIMARY KEY, visibility INTEGER NOT NULL, updated_by TEXT NOT NULL, updated_at TEXT NOT NULL` |
| PostgreSQL | same as SQLite (`TEXT` timestamps, as `TABLE_ROW_FILTER_STRATEGIES`, `PostgreSqlBaselineSchema.cs:518-526`) | same as SQLite |
| SQL Server | `table_id NVARCHAR(128) NOT NULL, tag NVARCHAR(64) NOT NULL, source NVARCHAR(16) NOT NULL, created_by NVARCHAR(256) NOT NULL, created_at NVARCHAR(64) NOT NULL, CONSTRAINT PK_TABLE_TAGS PRIMARY KEY (table_id, tag)` | `table_id NVARCHAR(128) NOT NULL PRIMARY KEY, visibility INT NOT NULL, updated_by NVARCHAR(256) NOT NULL, updated_at NVARCHAR(64) NOT NULL` |

Idempotent (`CREATE TABLE IF NOT EXISTS` / `IF OBJECT_ID ... IS NULL`), appended after the #107 block with the same comment style. Shape snapshots regenerated (`GovernanceSchemaShapeSnapshotTests` gets `EmbeddedSnapshot_CoversTheCatalogTagAndSettingsTables`); the PostgreSQL/SQL Server shape integration tests and the least-privilege grant tests (runtime login: SELECT/INSERT/UPDATE/DELETE on both new tables) are extended. SQL Server `tag` is `NVARCHAR(64)` and compared on the stored lower-case value (case-insensitive default collation cannot create a duplicate that the normalizer would not already merge).

### C-4 REST DTOs (`src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs`, new records)
`SetDatasetTagsDto(IReadOnlyList<string>? Tags)`, `ChangeDatasetTagsDto(IReadOnlyList<string>? Add, IReadOnlyList<string>? Remove)`, `SetDatasetVisibilityDto(string? Visibility)`; response `DatasetCatalogSettingsDto(string DatasetId, IReadOnlyList<DatasetTagDto> Tags, string Visibility)`. Request bodies are size-capped by the tag count cap and `MaxTagLength` before allocation (reject > `MaxTagsPerDataset + remove count` elements up front).

---

## 4. Files per Layer and Work Package (ONE sequential developer)

Ownership is single (one developer owns every file); the work packages are an ordering, each ends green and committed. "Red first" applies to every package (section 6).

### WP-0 Security bugfix: pre-existing catalog leaks (own PR, merged FIRST; independent of #111 and #107)

Branch `fix/catalog-schema-disclosure` from `main`. Consent-based only (A-0). No Domain/Infrastructure/DDL/shape change, no checksum change, no new options except the deprecation Warning.
- Application: `Catalog/Services/CatalogDiscoveryService.cs` (list, detail, search incl. index post-filter and column re-match, `ListDatasourcesAsync`), `Governance/Analysis/QueryAnalysisService.Evaluation.cs` (table-level stop), `Governance/Analysis/BundledConsentRequestService.cs` (verify/pin, change only if the test is red), `Mcp/Services/McpDatasetCatalog.cs` (positive `sample_rows` guard only; `Matches` pinned by test), `Mcp/Services/McpResourceProvider.cs` only if it needs the principal passed through (it already calls `ICatalogDiscoveryService` with the principal).
- GraphQL: `Autheris.GraphQL/Types/QueryTypes.cs` (branch `isGovernanceAdmin` only, flag deprecation Warning).
- Api: `Endpoints/CatalogApiEndpoints.cs` (`/datasources` admin policy; detail 404 unchanged shape).
- Tests (red first): `Tests.Unit/Catalog/CatalogDiscoveryConsentVisibilityTests.cs` (new: ReBAC off/on x user/GovernanceAdmin/ClusterAdmin x list/detail/search/datasources), `Tests.Unit/Catalog/CatalogSearchColumnOracleTests.cs` (new, M-5 baseline), `Tests.Unit/Governance/QueryAnalysisUnconsentedTableTests.cs` (new, M-6 baseline), `Tests.Unit/Mcp/McpSampleRowsGuardTests.cs` (new, M-7 baseline), `Tests.Unit/GraphQL/CatalogDiscoveryFlagTests.cs` (new, M-4 baseline), `Tests.Integration/CatalogChannelParityTests.cs` (new in WP-0, SQLite fixture: REST list/detail/search/datasources, MCP resources and `invoke_api` for a consent-less tenant-B user versus tenant-A tables, M-12; #111 extends the same class with tier fixtures).
- Existing suites to adapt: tests asserting that non-admins list every table when ReBAC is off.
- Size: **~260 LOC production, ~700 LOC tests** (S-M). Release note for O-1 (non-admins lose the unfiltered REST catalog and `/datasources`).

### WP-1 Contracts and persistence (all three engines)
- Domain: `Model/TableModels.cs` (`CatalogView`, doc fix of `ReleaseProfileToCatalogViewers`), `Model/DatasetCatalogModels.cs` (new), `Model/DatasetTagRules.cs` (new), `Model/DeclarativeConsentModels.cs`, `Options/Integrations/DataCatalogOptions.cs`, `Audit/AuditEventTypes.cs`, `Interfaces/ICatalogVisibilityDefaults.cs` (new).
- Application: `Interfaces/IGovernanceRepository.cs` (C-2), `Audit/AuditPolicyTable.cs` (2 entries), options validator for `MaxTagsPerDataset` / `DefaultVisibility` (start fails on `Full`).
- Infrastructure: `Persistence/Migrations/{Sqlite,PostgreSql,SqlServer}BaselineSchema.cs` (C-3), `Persistence/Migrations/Shape/governance-shape.{sqlite,postgresql,sqlserver}.json`, `Persistence/{Sqlite,PostgreSql,SqlServer}GovernanceRepository.Catalog.cs` (fresh overlay of tags/visibility **outside `_metadataCache`** in `GetAllTablesAsync` and `GetTableMetadataAsync`, four new methods with cache-key removal on all three engines, strict admin read, default on insert, degradation per A-6); SQLite generation invalidation in `SqliteGovernanceRepository.Catalog.cs`/`.cs`, `_metadataCache.TryRemove` in the PostgreSQL/SQL Server files (cache entry never holds tier/tags); `SqliteGovernanceLintSource.cs` only if it enumerates table lists.
- Tests: `Tests.Unit/Domain/DatasetTagRulesTests.cs`, `Tests.Unit/Persistence/GovernanceSchemaShapeSnapshotTests.cs` (extend), `Tests.Integration/CatalogSettingsRepositoryContractTestsBase.cs` + SQLite/PostgreSQL/SQL Server subclasses (shared container fixtures), least-privilege tests.

### WP-2 Visibility engine
- Application: `Services/CatalogVisibility.cs` (surface, tier rules removed entirely for `DataAccess`, `Full` DENY-column removal before matching, `Summary` columns always empty, `SharedTables`-only tenant gate, explicit `CatalogView` on every returned row), `Mcp/Services/McpCatalogVisibility.cs` (pass-through of the surface; default `DataAccess`), the discovery call sites choose `Discovery`: `Mcp/Services/McpDatasetCatalog.cs:788-795` (list, describe, relations) and `Autheris.GraphQL/Types/QueryTypes.cs` (non-admin branch; the flag branch no longer exists after WP-0); **all other callers of section 1.2 (rows 7-12) are not edited** (their default is `DataAccess`), and a source-level test asserts it (T-3).
- Tests: `Tests.Unit/Catalog/CatalogVisibilityTierTests.cs` (new), adaptation of `Wave3CatalogOwnershipTests.cs`, `Issue127ScopeAwareLevelTests.cs`, `Issue116ScopeOnlyColumnRuleConsistencyTests.cs`, `DynamicSchemaContractTests.cs` where they assume `Summary`/`Full` through the 4-argument call.

### WP-3 MCP and GraphQL discovery output
- Application: `Mcp/Services/McpDatasetCatalog.cs` (`BuildSummaryDescription` with display-name-only owners, `ColumnCount`, `sample_rows` third term becomes `CatalogView == Full`, `Summary`/`Full` re-match for search), `Domain/Model/McpDatasetModels.cs` (`View` property on description, nullable `GraphQl`).
- GraphQL: `Types/QueryTypes.cs` (tenant tables for the tier, `Columns = []` for `Summary` by construction).
- Tests: `Tests.Unit/Mcp/McpCatalogSummaryViewTests.cs`, GraphQL catalog tests.

### WP-4 Admin surfaces, manifest, dbt
- Application: `Governance/Services/DatasetCatalogAdminService.cs` and interface (new), `Governance/Services/DeclarativeConsentApplyService.cs` (`Datasets` section), `Mcp/Tools/McpAdminTools.cs` (`admin_set_dataset_catalog`), `Mcp/Services/McpToolGroups.cs` pin list (PLAN-SEC-115 table: ungrouped admin tool).
- Extensions: `Dbt/DbtMetadataIngestionService.cs` (tag replacement hook after `:493` and `:976`, dry run writes nothing), `Dbt/DbtSemanticManifestIngestionService.cs:333` (the synthetic `["dbt","semantic-model"]` becomes `ReplaceDatasetTagsAsync(.., Dbt)` after the upsert; the `Tags` literal on the upserted `Table` is removed because the upsert ignores it).
- Api: `Endpoints/CatalogApiEndpoints.cs` (routes, DTOs, shared `ResolveActiveDatasetAsync`), DI registration in `Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs`, `GovernanceEndpoints.Catalog.cs` only if the manifest endpoint needs the principal passed through.
- Tests: `Tests.Unit/Catalog/DatasetCatalogAdminServiceTests.cs`, `Tests.Unit/Catalog/CatalogTagsVisibilityEndpointTests.cs`, `Tests.Unit/Governance/DeclarativeConsentApplyDatasetsTests.cs`, dbt ingestion tests, MCP admin tool tests.

### WP-5 Tiers on the REST catalog service and leak parity (builds on WP-0)
- Application: `Catalog/Services/CatalogDiscoveryService.cs` (WP-0 already closed the consent-based leak; WP-5 switches the non-admin path to `VisibleTablesAsync(..., Discovery)` with ReBAC intersection; `Summary` detail returns the allow-listed shape without columns; `Summary`/`Full` search re-match; admin `FilterToTenant` view unchanged), `Catalog/Interfaces/ICatalogDiscoveryService.cs` if the context needs the principal (`RequestContext.User` already carries it, `CatalogApiEndpoints.cs:293`).
- Tests: `Tests.Unit/Catalog/CatalogDiscoveryVisibilityTests.cs`, `Tests.Integration/CatalogChannelParityTests.cs` (extended from WP-0 with `Summary`/`Hidden`/`Full` fixtures: the per-channel leak matrix of T-5, SQLite fixture; PostgreSQL/SQL Server for the repository contract only).

### WP-6 Cleanup
- Tests: update existing tests adapted in WP-2; `ArchitectureTests` if a rule lists catalog consumers. Phase 6 (not this plan) updates `docs/features/f-gov-16-catalog-ownership-transparency.md` (removes the "not persisted" limitation, `:62`), F-GOV-12, F-GOV-15, `docs/features/README.md`, and the master plan.

Not changed (stated so the reviewer can check): `FilterByContract` code, `QueryAnalysisService` after WP-0, `ComputeAccessStatus`, `ConsentResolutionService`, any data path (`TableAccessPolicy`, governed rewriter), `CatalogVersionProvider`, `IEpochValidationService`, OData/OpenAPI generators, Trino/Arrow/Iceberg call sites.

---

## 5. Sequencing

**WP-0 first and separately:** WP-0 is implemented, reviewed and merged as its own PR before #107 and before #111 (security bugs first, BC-1; tracker step `I-6a` ahead of I-7); it has no dependency on any other plan. #111 starts from `main` after WP-0 is merged (and after #115).

#111 itself strictly sequential, one developer: WP-1 (contracts commit first, then DDL x3 + shapes, then repositories) -> WP-2 -> WP-3 -> WP-4 -> WP-5 -> WP-6. WP-2 may start only after the repository contract tests of WP-1 (including the replica/cache tests M-1) are green on SQLite (Testcontainers runs for PostgreSQL/SQL Server may finish later in CI). No parallel agents; per project memory the lead compiles and tests once at the end (Release build, warnings as errors, before the push; `DOTNET_USE_POLLING_FILE_WATCHER=true` for integration tests under WSL).

---

## 6. Test Strategy (TDD list, red first; AC mapping)

T-1 Tags and tag rules (WP-1; AC 1, 5)
1. `DatasetTagRules`: lower-casing, trim, charset, 64 chars ok / 65 rejected, duplicates merged, 32 ok / 33 -> `TAG_LIMIT_EXCEEDED`, configured cap respected, SQL/HTML/control characters and Unicode look-alikes rejected (`INVALID_TAG`).
2. `ReadStored`: 0/1/2 map; 3, -1, null, huge -> `Hidden`. `TryParseVisibility`: `"Summary"` ok (any case), `"2"`, `"full "` with trailing junk, `"Full,Summary"`, empty -> reject.
3. Repository contract (SQLite, PostgreSQL, SQL Server, same base class): tags written through `ReplaceDatasetTagsAsync(Admin)` read back through `GetAllTablesAsync` and `GetTableMetadataAsync` after a repository restart (new instance) (AC 1); visibility persists; missing row = `Hidden`; `Replace(Dbt)` replaces dbt rows and keeps admin rows, a tag present in both stays `admin` (AC 5); `Replace(Admin)` keeps dbt rows; re-running `UpsertTableMetadataAsync` (re-ingestion) leaves tags and visibility untouched (AC 5); idempotent replace reports `Changed = false`; unknown table -> null; total cap counts both sources.
4. Store degradation: a corrupt `visibility` integer, a tag that violates the rules and a dropped `TABLE_TAGS` table each yield `Hidden`/no tags for the affected rows, never an exception from `GetAllTablesAsync`, never `Full` (SI-111-2).
5. `DefaultVisibility = Summary`: a newly inserted table gets a `Summary` row, an existing table does not change, the default `Hidden` writes no row; option `Full` fails startup validation.
6. Schema shape: snapshots contain both tables on all three engines, `GovernanceSchemaShapeVerifier` passes on fresh and pre-existing SQLite databases (AC 7); checksum constants unchanged (existing test).
7. SQLite metadata cache: a tag/visibility change is visible through `GetTableMetadataAsync` immediately (no TTL wait).

T-2 Visibility engine (WP-2; AC 2, 3, 4, 6)
8. `DataAccess` surface (default): a `Summary` and a `Full` table without consent are **not** returned (regression guard that Trino/Arrow/Iceberg/guardrail behaviour cannot widen by accident).
9. `Discovery`: `Hidden` absent; `Summary` present with `Columns` empty; `Summary` with `ReleaseProfileToCatalogViewers = true` still has no columns (AC 4); `Full` returns columns minus DENY-masked columns (G-2); consent path unchanged (existing #127 tests green).
10. Tenant: multi-tenant caller, `Summary` table without consent record of the tenant and not in `SharedTables` -> not listed; with a tenant consent record or in `SharedTables` -> listed; other tenant's consent does not make it visible (AC 6); `tenantTables == null` in multi-tenant -> tiers off; single tenant unrestricted.
11. An unconditional Deny consent on a `Hidden` table keeps it hidden; a Deny consent on a `Summary` table lists it as `Summary` with `accessStatus` `NONE`.
12. `accessStatus` for `Summary`: `NONE`, `PENDING` (pending request), `GRANTED` after consent with columns per consent (AC 2).

T-3 Surfaces that must not widen (WP-2/WP-5; AC 8; SI-111-1)
13. Source-level guard: a test enumerates every `CatalogVisibility.VisibleTablesAsync` / `McpCatalogVisibility.VisibleTablesAsync` call site and asserts that only the discovery sites (`McpDatasetCatalog`, `QueryTypes`, `CatalogDiscoveryService`) pass `CatalogSurface.Discovery` (a new call site without a decision stays `DataAccess`).
14. AI guardrail: a `Summary`/`Full` table without consent is **denied** at the four-eyes gate (`AiDataGuardrailService.cs:290`), unchanged for consented and admin callers.

T-4 Authorization of changes (WP-4; Q-111-1, owner rule)
15. **A data owner or delegate cannot set `Full`**: `403 FORBIDDEN`, no row written, one `DENY` audit entry; the same owner can set `Hidden` and `Summary`; an owner can lower `Full` to `Summary`; owner `Full` on an already-`Full` dataset is refused.
16. GovernanceAdmin and ClusterAdmin can set `Full`; the audit entry carries before/after, actor and source.
17. A caller who is neither admin nor owner gets the identical `404` body for an existing hidden dataset, an unknown dataset and an inactive dataset (no oracle); ownership store unavailable -> `503`, no write.
18. Rights come from the principal: a body or header claiming `role`, `owner`, `admin` or a different tenant changes nothing; a read-only token (`Agent.Read`) cannot call any mutating route (existing token policy) and `admin_set_dataset_catalog` is invisible and uncallable for non-cluster-admin and read-only MCP sessions; an MCP caller cannot raise to `Full` unless the session principal is cluster admin.
19. Invalid input: unknown visibility name, numeric string, over-long or illegal tags, too many tags -> 400 with the specific code and **no partial write** (combined tags+visibility edit with a forbidden visibility applies no tag change).
20. Audit failure (audit store down) -> 503 per fail-closed policy; the change is already committed (documented as for `/keys`), the event type is `FailClosed` in `AuditPolicyTable`.
21. Manifest: `Datasets` entries apply tags/visibility by admin only; absent field = unchanged; `Tags: []` replaces with none; dry run changes nothing; unknown table reported and skipped; manifest without `Datasets` unchanged.
22. dbt: model tags land as `dbt` source and are replaced on re-import, admin tags survive; invalid dbt tags dropped with a Warning; dry run writes nothing; the synthetic semantic-model tags are persisted as `dbt` tags.

T-5 Leak matrix: `Summary` never exposes columns through any channel (WP-3/WP-5; AC 8, SI-111-1/3)
For a `Summary` table (and, separately, a `Hidden` table) with a column named `secret_col`, asserted for a caller without consent in tenant, scanning the entire serialized response for the column name, its type, key names and GraphQL field names:
23. MCP `list_datasets`, `search_catalog` (search term = column name returns no hit), `describe_dataset` (restricted allow-list shape, no `LongDescription`, no examples, no `graphQl` block, no indexes), `sample_rows` (refused), MCP resources `autheris://datasets/...` and catalog summary, `describe_my_access`.
24. GraphQL `catalog` query (columns `[]`), GraphQL schema introspection is covered by its existing switch/`DisableIntrospection` (`GatewayServiceCollectionExtensions.cs:630`); OData `$metadata` and entity-set requests, `DynamicOpenApiGenerator` output, Trino `SHOW TABLES` / `information_schema.tables|columns`, Flight SQL metadata, Iceberg REST listing and table load, `SemanticMcpCompiler` resources, REST `GET /api/v1/catalog/datasets`, `/datasets/{id}`, `/search`, and `invoke_api` composition.
25. `Hidden` table: every one of the above answers exactly as for an unknown table (identical bodies/status for `describe_dataset`, `/datasets/{id}`).
26. FR-111-9: a consent-less caller cannot read rows of a `Summary` or `Full` table through REST data, GraphQL data, SQL, MCP `query_dataset` (each denied as before).
27. Contract interplay: persisted tags change `FilterByContract` results (included/excluded tags) as intended; test documents the behaviour (R-6).

T-6 Operations (WP-1/WP-4)
28. Metric `autheris_catalog_visibility_total` counts per tier without table labels; the degraded counter increments in test 4.
29. `CatalogVersionProvider.GetVersionAsync` is unchanged by a tag or visibility change.
30. `McpToolGroups` pin test (PLAN-SEC-115) includes `admin_set_dataset_catalog` as ungrouped.

T-7 Phase 3 mandatory tests (rev. 2; WP column says where each is implemented). Tests marked WP-0 are written without tiers (consent-based fixtures) and re-run with tier fixtures in #111.
31. **M-1 (WP-1)** per engine (SQLite, PostgreSQL, SQL Server): set `Full`, read via `GetTableMetadataAsync` and `GetAllTablesAsync`, lower to `Hidden`, read again on the same instance without TTL wait: no stale tier; a second repository instance (replica simulation, own `_metadataCache`) observes the change on the next read; the cached entry holds no tier/tags.
32. **M-2 (WP-2)** tenant B admin creates Allow and Deny consents (REST, bulk, manifest) on a tenant-A `Summary` table: it does not become visible to tenant B users (tiers apply only to `SharedTables`); `SharedTables` listing positive/negative; multi-tenant with empty `SharedTables`: tiers off.
33. **M-3 (WP-4)** tenant-B GovernanceAdmin in multi-tenant mode: REST, manifest and MCP attempts to set tier/tags on a tenant-A or shared dataset: identical 404 / skipped-as-unknown, nothing written; ClusterAdmin succeeds; single-tenant GovernanceAdmin succeeds; owner of the table succeeds for `Hidden`/`Summary`.
34. **M-4 (WP-0, re-run in WP-3)** GraphQL `catalog` with `AllowAuthenticatedCatalogDiscovery = true`: consent-less caller sees no columns and no hidden/unconsented table; with tiers: `Hidden` absent, `Summary` columns `[]`, `Full` minus DENY columns.
35. **M-5 (WP-0, extended in WP-2/3)** searching the exact column name and a prefix of an unconsented/`Summary`/`Hidden` table returns no hit and no `RelevantColumns` via MCP `list_datasets`/`search_catalog`, REST `/search` and `/datasets?search`, with the index ready and with the fallback; `Full` with a DENY-masked column: name neither listed nor matched.
36. **M-6 (WP-0, re-run in WP-2)** query analysis (REST, MCP, GraphQL entry points): `select *`, a guessed existing and a non-existing column on an unconsented table (`Hidden` today, `Summary` with tiers): identical responses, no column names/types, only the table-level item; `BundledConsentRequestService` column request pinned.
37. **M-7 (WP-0 baseline, WP-3 tiers)** `sample_rows` synthetic and non-synthetic: unconsented -> refused with and without `ReleaseProfileToCatalogViewers` (WP-0); with tiers: `Summary` refused, `Full` flag off refused, `Full` flag on allowed with DENY columns excluded, an `Unmarked` `TableMetadata` without consent refused.
38. **M-8 (WP-4)** status/body equality matrix: unknown, inactive, foreign-tenant, hidden-and-not-owned dataset, each with valid and invalid body, as owner-less user: identical 404; ownership store down: 503 for every non-admin caller independent of existence and before resolving; owner `Full`: 403 + `DENY` audit + no write; foreign probe writes a `DENY` audit entry.
39. **M-9 (WP-1/WP-4)** settings read failure: catalog list degrades non-admins to `Hidden`; admin `GetAsync`/`ApplyAsync` answer 503, no write, no audit with a fake `Hidden` before.
40. **M-10 (WP-4)** manifest: `Visibility: "2"`, `"Full,Summary"`, `2` rejected; non-admin dry run 403; admin dry run writes nothing and reports the `Full` change; tenant-B admin `Full` entry for a tenant-A dataset skipped as unknown; null/system principal cannot raise `Full`.
41. **M-11 (WP-2)** call-site guard over the three `VisibleTablesAsync` overloads and `McpCatalogVisibility`: default surface `DataAccess`; `AiDataGuardrailService`, `SemanticMcpCompiler`, Trino, Arrow, Iceberg sources contain no `CatalogSurface.Discovery`; behavioural: unconsented `Summary`/`Full` absent in Trino `SHOW TABLES`/`information_schema`, Flight SQL, Iceberg listing, guardrail gate (denied), golden queries, semantic resources.
42. **M-12 (WP-0, extended in WP-5)** REST catalog ReBAC off and on: tenant-B user without consent gets no tenant-A table in `/datasets`, 404 on `/datasets/{id}`, empty `/search`, `/datasources` 403 (service: empty); MCP resources `autheris://catalog/summary`, `autheris://datasets/{id}/schema` and `invoke_api` composition follow the same result.
43. **M-13 (WP-4)** owner lowering `Full` is audited with `before = Full`; combined tags+visibility edit with forbidden `Full` applies nothing; `after.visibility == Full` present as a structured field.
44. **M-14 (WP-1)** `Catalog:DefaultVisibility = Summary`: system rows only for tables created after the change, one aggregated audit entry per creating run, startup/config-change audit; `Full` fails startup.
45. **Display names only (BC-14, WP-3)** `Summary` view contains no SID/e-mail/UPN for owner and delegates; reflection test pins the exact allowed property set of the `Summary` allow-list.
46. **Search `Summary` re-match (WP-3)** `Summary` hits only via identifier/description; `RelevantColumns` always empty.

Existing suites that must stay green: #127 (`Issue127*`), #116, `RowFilterChannelParityTests`, F-GOV-12 shape tests, `Wave3CatalogOwnershipTests` (adapted only where noted), SQL gate suites (untouched).

---

## 7. Fail-Closed Behaviour

| Situation | Behaviour |
|---|---|
| Unknown, negative or out-of-range stored visibility | `Hidden` |
| Visibility/tag query fails or table missing | affected rows `Hidden`, no tags, Error log + counter; catalog read still succeeds for everything else |
| Missing `TABLE_CATALOG_SETTINGS` row | `Hidden` |
| Discovery on multi-tenant without a tenant table set | tiers off (only consented tables) |
| New call site / overload without surface | `DataAccess` (no tier) |
| Request visibility not one of `Hidden`/`Summary`/`Full` by name | 400 `INVALID_VISIBILITY`, nothing written |
| Ownership store unavailable on an owner-gated change | 503, nothing written |
| Audit store unavailable | 503 (`FailClosed`) |
| `Catalog:DefaultVisibility` = `Full` or unknown | startup validation fails |
| Principal without SID / anonymous MCP client | no tables (existing `McpCatalogVisibility.cs:36-40`) |
| Caller neither admin nor owner | 404 identical to unknown dataset (same body/headers; probe audited) |
| Ownership store unavailable, non-admin caller | 503 for every such caller before dataset resolution (no existence oracle) |
| Tier/tags read in admin mode fails | 503, nothing written (strict read) |
| `CatalogView` unset (`Unmarked`) | treated as most restrictive; no consumer may read it as `Granted` |
| Multi-tenant, table not in `SharedTables` | tiers do not apply; consent path only |
| Multi-tenant, tenant-scoped GovernanceAdmin changes tier/tags of a non-owned dataset | identical 404, nothing written |
| Unconsented table in query analysis | table-level `MissingConsents` only, no column evaluation |
| `sample_rows` without consent | refused unless `CatalogView == Full` and the owner flag; everything else refused |
| REST catalog / search / `/datasources` caller without consent | consented tables only; `/datasources` admin-only |

---

## 8. Risks

| # | Risk | Mitigation |
|---|---|---|
| R-1 | A forgotten surface leaks `Summary`/`Full` columns | Section 1.2 inventory, default `DataAccess`, source-level call-site test (13), channel matrix (23-25) |
| R-2 | WP-0 changes REST catalog and `/datasources` behaviour for non-admins (today everything is listed when ReBAC is off) | Separate PR first; owner flag O-1 (answered: yes); release note; intersection with ReBAC kept; admins unchanged |
| R-3 | In multi-tenant mode tiers work only for `Gateway:Catalog:SharedTables` tables (no tenant ownership model); a tenant-scoped GovernanceAdmin cannot change tiers | Documented route; owner flag O-2 (answered); follow-up F-1: dedicated tenant-owner attribute for tables; ClusterAdmin or the table owner changes tiers |
| R-9 | WP-0 and #111 touch `CatalogDiscoveryService` and `QueryTypes.cs` in sequence | WP-0 merges first; #111 starts from the merged `main`; WP-5 is a small delta (`Discovery` switch) |
| R-10 | Tier/tag read outside the metadata cache adds a query per single-table read | Indexed PK lookup, measured in the contract test; correctness over latency for restrictions |
| R-4 | Existing tests build `Summary`/`Full` tables and call the 4-argument `FilterForSubject` | Planned adaptation (WP-2); the new default is deliberately narrower |
| R-5 | `UpsertTableMetadataAsync` callers might pass `Tags`/`Visibility` expecting persistence | Documented in XML doc on `Table.Tags`/`Visibility` ("persisted via the catalog settings API, ignored by the upsert"); upsert test pins it |
| R-6 | Persisted tags activate `FilterByContract` tag include/exclude, which previously saw empty tags | Test 27; Phase 6 documentation; dbt-derived tags like `pii` may now exclude tables from contracts, intended |
| R-7 | Three engines drift | One contract-test base class on all three engines, shape snapshots |
| R-8 | Owner-writable tags are free text that appear in agent-facing output (prompt injection into agents) | Charset limited to `[a-z0-9._:-]`, 64 chars, so tags cannot carry instructions beyond short tokens; JSON-escaped (SI-111-4) |

## 9. Owner Flags

Answers follow the Phase 3 recommendations (P3.7); items marked DECIDED are resolved by this revision and only need the owner's veto.

- **O-1 (DECIDED: yes, accelerated)** REST `GET /api/v1/catalog/datasets*` and `/datasources` currently list/describe all tables (and data source URLs) of all tenants to any authenticated caller when ReBAC is off (G-1, G-8). Closed in **WP-0**, a separate security PR merged before #111 and #107. Release note: non-admins without consent no longer see foreign tenants' tables, columns or data sources through REST, MCP resources and `invoke_api`.
- **O-2 (DECIDED: interim restriction)** Multi-tenant tiers apply to `Gateway:Catalog:SharedTables` only; the consent-derived tenant signal is forgeable and circular and is not used for tiers (G-4). Tenant-scoped GovernanceAdmin cannot change tiers/tags by role in multi-tenant mode (A-5). Follow-up **F-1** (to schedule in the master plan): dedicated tenant-owner attribute for tables, then tiers become tenant-wide and tenant admins can manage their own datasets.
- **O-3 (DECIDED: single call)** MCP admin tool is single-call like `admin_set_dataset_state`, cluster admin only, non-read-only session, audit `source = mcp` with the session principal, ungrouped in `McpToolGroups` with the pin test. Switch to plan/confirm when PLAN-GOV-107's plan kind is merged (no security difference, the `Full` rule lives in the service).
- **O-4 (DECIDED: keep)** `RequiresFourEyes` stays in the `Summary` view (not column-derived, useful to a requester); owner veto removes it, no security dependency.
- **O-5 (DECIDED: deprecate)** `AllowAuthenticatedCatalogDiscovery`: tiers always win; the flag has no effect on columns or listing from WP-0 on and logs a startup Warning (deprecation); removal of the option in a later breaking-change window. Behaviour change for deployments that enabled it: they lose the all-column tenant listing; the sanctioned replacement is `Summary` tiers on `SharedTables` tables (#111) and consents.

## 10. Security Notes for Phase 3

1. Verify the allow-list construction of the `Summary` view (A-4): no field is derived by subtraction, every future field of `McpDatasetDescription` defaults to excluded (record `with`-free construction; a reflection test lists the property names allowed for `Summary`).
2. Review `CatalogSurface` default and every call site (section 1.2); in particular `AiDataGuardrailService.cs:290/354` must remain consent-based, and Trino/Arrow/Iceberg/semantic resources must never use `Discovery`.
3. Tenant isolation of tiers (G-4): confirm the `FilterToTenant` derivation is an acceptable tenant-ownership signal for `Summary` and that `tenantTables` retrieval cannot be bypassed (null = off).
4. Authorization of changes (A-5): backend-only rights, the `Full` restriction (including through manifest, MCP and a tag/visibility combined edit), identical 404 for non-manageable datasets, no partial writes, audit of denied attempts, owner lowering from `Full`.
5. REST catalog closure (G-1): ReBAC off and on, `invoke_api` and MCP resource paths through the same service; detail endpoint for `Summary` returns no column.
6. Fail-closed read degradation (A-6): confirm no path maps an error to `Full`; the integer mapping; the per-row skip precedent.
7. Tag handling (SI-111-4, R-8): charset, escaping on every output channel, no use in SQL, masking or policy; `FilterByContract` interplay.
8. `Full` semantics: the DENY-masked column removal (G-2), `ReleaseProfileToCatalogViewers` scope reduced to `Full`, sample rows guard for `Summary`.
9. Default visibility option (A-10): creation-time only, `Full` impossible, no retroactive exposure.
10. Audit completeness and policy: `FailClosed` entries, before/after contents, no data values, sources (`admin|manifest|mcp|dbt`).
11. Least-privilege runtime login grants on the two new tables; no FK/CHECK reliance.
12. (rev. 2) WP-0 channels (REST list/detail/search/datasources, GraphQL flag, query analysis, `sample_rows` guard): consent-based only; verify the post-filter of the search index and the identical 404.
13. (rev. 2) Cache rule A-6: tier and tags never cached; per-engine replica test M-1.
14. (rev. 2) Tenant rule: `SharedTables` only, no consent-derived signal; ClusterAdmin-only tier writes in multi-tenant mode; authorization order of A-5.

## 11. LOC Estimate

| Area | Production | Tests |
|---|---|---|
| **WP-0** security bugfix (own PR, first): REST list/detail/search/datasources, GraphQL flag, query analysis, `sample_rows` guard, channel parity tests | ~260 | ~700 |
| WP-1 Domain, options, DDL x3, shape JSON (generated), repositories x3 (incl. cache rule, strict read) | ~760 | ~720 |
| WP-2 Visibility engine and call sites (SharedTables rule, `CatalogView`) | ~170 | ~460 |
| WP-3 MCP/GraphQL discovery output | ~190 | ~360 |
| WP-4 Admin service (order, tenant rule, strict read), REST, MCP tool, manifest, dbt hook, audit additions | ~620 | ~650 |
| WP-5 Tiers on the REST catalog service (delta on WP-0), channel matrix extension | ~60 | ~300 |
| #111 total (WP-1..WP-6) | **~1,800** | **~2,490** |
| WP-0 + #111 | **~2,060** | **~3,190** |

Size of #111: M-L (PRD said M); WP-0 is S-M and independently shippable. Shape JSON diffs (~200 lines per engine) excluded.

## 12. Changelog

| Revision | Date | Change |
|---|---|---|
| 1 | 2026-10-16 | Initial Phase 2 plan (PROPOSED), based on `3ece5edd`. |
| 1+P3 | 2026-10-16 | Phase 3 security review appended (CHANGES REQUIRED); no change to the plan body yet. |
| 2 | 2026-10-16 | Revision 2 responding to Phase 3 (SEC-111-01..14, BC-1..BC-14, M-1..M-14, O-1..O-5): new standalone WP-0 security bugfix (pre-existing leaks G-1, G-7..G-11) ahead of #111 and #107; cache premise corrected (all engines, tier/tags read outside the cache); tenant signal reduced to `SharedTables`, ClusterAdmin-only tier writes in multi-tenant mode; inventory and tests extended (T-7); owner flags answered. Awaiting Phase 3 re-review. |

---

## Revision 2 - response to Phase 3

Every finding and binding condition of the review below maps to a concrete change in this plan. The review text itself is unchanged.

### Findings

| ID | Change made in revision 2 |
|---|---|
| SEC-111-01 | G-5 corrected (all three engines, 30 s process-local cache, no cross-node invalidation); A-6 cache rule: tier/tags read outside `_metadataCache` on every engine (zero accepted staleness), all write methods also remove the local key; C-2 contract note; WP-1 files; tests T-7 31 (M-1), R-10. |
| SEC-111-02 | G-4 rewritten: tiers in multi-tenant mode apply only to `Gateway:Catalog:SharedTables`; consent-derived signal dropped; `FilterForSubject` parameter `sharedTables` (C-2); tests T-7 32 (M-2); O-2 decided; follow-up F-1. |
| SEC-111-03 | G-12; A-5 tenant scope: in multi-tenant mode only ClusterAdmin (or the table owner/delegate) changes tags/tier; tenant-scoped GovernanceAdmin gets the identical 404 on REST, manifest and MCP; tests T-7 33 (M-3). |
| SEC-111-04 | G-9 and inventory row 16 moved to WP-0 (A-0.4); A-4 bullet "tiers always win"; O-5 decided (deprecate flag); tests T-7 34 (M-4). |
| SEC-111-05 | G-7 and row 14: WP-0 (A-0.2) post-filter and re-match of the index result; #111 adds `Full` DENY removal before matching and `Summary` identifier/description-only re-match (A-4); tests T-7 35, 46 (M-5). |
| SEC-111-06 | G-10 and row 17: WP-0 (A-0.5) table-level stop independent of `Visibility`; `BundledConsentRequestService` verified/pinned; tests T-7 36 (M-6). |
| SEC-111-07 | G-11: WP-0 positive guard (A-0.6); `CatalogView` default `Unmarked` (most restrictive), explicit on every returned row, positive `== Full` check (C-1, A-4); tests T-7 37 (M-7). |
| SEC-111-08 | A-5 mandatory order (503 for all non-admins before resolve, resolve, manageable-or-404, validate, `Full` rule, write); tests T-7 38 (M-8). |
| SEC-111-09 | A-6 two read modes (catalog degrades, admin strict, `GetDatasetCatalogSettingsAsync(strict)`); A-5 read modes; tests T-7 39 (M-9). |
| SEC-111-10 | A-7: dry run authorizes like apply, strict name parser (no `JsonStringEnumConverter`), null/system principal cannot raise `Full`; tests T-7 40 (M-10). |
| SEC-111-11 | A-4 `DataAccess` removes tier branches entirely; call-site guard extended to three overloads and `McpCatalogVisibility`; tests T-7 41 (M-11). |
| SEC-111-12 | A-9 additions: DENY audit also for foreign probes, structured `before`/`after`, tenant and authentication type, startup/config-change audit of `DefaultVisibility`, aggregated creation audit; A-10; tests T-7 43, 44 (M-13, M-14). |
| SEC-111-13 | Unchanged design (charset, escaping); Phase 6 documentation requirement kept: tags are public metadata on `Summary` and not auto-derived from sensitivity/column names; dbt tag import documented (test 27 stays). |
| SEC-111-14 | A-4: display names only for owner/delegates, reflection test pins the allowed `Summary` property set; test T-7 45. |

### Binding conditions

| BC | Closed by |
|---|---|
| BC-1 | WP-0 as a separate first PR with own file list, tests, LOC (A-0, section 4, section 11); tracker step `I-6a`; independent of the tiers (uses current consent-based `CatalogVisibility`); WP-5 reduced to the `Discovery` delta. |
| BC-2 | G-5 corrected, A-6 cache rule, M-1 (T-7 31). |
| BC-3 | G-4: `SharedTables`-only (interim), no Deny/pending/expired/any consent counts; M-2. |
| BC-4 | A-5 tenant scope (stricter than required: shared datasets are ClusterAdmin/owner only); M-3. |
| BC-5 | A-0.4, A-4, O-5. |
| BC-6 | A-0.2 plus A-4 (DENY removal inside `FilterForSubject`, before matching); M-5. |
| BC-7 | A-0.5, inventory row 17; M-6. |
| BC-8 | A-0.6, C-1 `CatalogView.Unmarked = 0`, A-4 positive guard; M-7. |
| BC-9 | A-5 mandatory order; M-8. |
| BC-10 | A-6/A-5 read modes; M-9. |
| BC-11 | A-7; M-10. |
| BC-12 | A-4 `DataAccess` bullet, test T-7 41 (M-11). |
| BC-13 | A-9 additions, A-10; M-13, M-14. |
| BC-14 | A-4 display names only, reflection test, T-7 45. |

### Mandatory tests M-1..M-14

All fourteen are listed verbatim in test group T-7 (items 31-44) with the work package that implements them; M-4, M-5, M-6, M-7 and M-12 are first written in WP-0 without tiers and re-run with tier fixtures in #111. Items 45 and 46 add the display-name and `Summary` re-match tests.

### Owner flags

O-1 decided yes and accelerated (WP-0); O-2 decided interim `SharedTables`-only plus follow-up F-1; O-3 single call kept; O-4 keep `RequiresFourEyes`; O-5 decided: deprecate `AllowAuthenticatedCatalogDiscovery` (tiers win). See section 9.

---

## Phase 3 Security Review (2026-10-16)

**Reviewer:** `csharp-security-expert` (step S-6 of PLAN-DELIVERY-W23). **Base:** worktree `plan-w23` at `d4a03571` (code identical to the architect's `3ece5edd` for the files cited). **Method:** every architect claim in section 1 and every channel of section 1.2 was re-read in the current code; line numbers below are from that read. Related: PLAN-SEC-115 Phase 3 (REST catalog and sample-rows are read paths).

### P3.1 Threat Model

| # | Asset | Adversary | Threat | Plan control | Verdict |
|---|---|---|---|---|---|
| TM-1 | Column names/types of a dataset the caller has no consent on | Authenticated tenant user, agent (MCP), any REST/GraphQL client | Schema enumeration through a channel that ignores the tier (existence and type of columns, golden queries, search hits, analysis diagnostics) | `Summary` allow-list, default `DataAccess` surface | Insufficient: channels missed (SEC-111-04..06) |
| TM-2 | Tenant isolation of the catalog | Tenant B user or admin | See or alter tier of a tenant A table | `FilterToTenant` rule, admin gate | Insufficient: forgeable signal and global admin (SEC-111-02/03) |
| TM-3 | Integrity of `Hidden`/`Summary`/`Full` | Data owner, delegate, tenant admin, agent | Self-service escalation to `Full` (Q-111-1), cross-dataset write, partial write | Service-level authorization A-5 | Sound after ordering fixes (SEC-111-08) |
| TM-4 | Existence of hidden datasets | Any authenticated caller | Oracle by status code, error code, timing, 503 vs 404 | identical 404 | Mostly sound; ordering and 503 gaps (SEC-111-08) |
| TM-5 | Stale tier after restriction | Anyone with access via a replica | Window in which a dataset lowered to `Hidden` is still served | SQLite cache invalidation only | Plan premise wrong (SEC-111-01) |
| TM-6 | Fail-open defaults | Misconfiguration, store fault, new code | Unknown value or missing wiring exposes columns | `Hidden` on unknown, `DataAccess` default | Sound; two fail-open defaults found (SEC-111-07, SEC-111-09) |
| TM-7 | Agent-facing free text (tags) | Owner (malicious or compromised) | Prompt injection into agents via tags | charset `[a-z0-9._:-]`, 64 chars | Acceptable (low), see SEC-111-13 |
| TM-8 | Audit trail | Insider | Silent visibility change (manifest, MCP, dbt, system default) | FailClosed events | Acceptable with SEC-111-12 |

### P3.2 Verification of the Pre-existing Defects

**G-1 REST catalog ignores `CatalogVisibility` - CONFIRMED, severity HIGH (CVSS-style 6.5; existing, exploitable today, independent of #111).**
- `GET /api/v1/catalog/datasets` and `/datasets/{*id}` require only `RequireAuthorization()` (`src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs:33,51`; `:63` for `/datasources`; `:101` for `/search`).
- `CatalogDiscoveryService.ListDatasetsAsync` (`src/Autheris.Application/Catalog/Services/CatalogDiscoveryService.cs:62-79`): with ReBAC off (`_rebacEvaluator is not { IsEnabled: true }`) it returns every active table of every tenant (`GetAllTablesAsync`, no tenant, no consent, no `Visibility`) with domain, schema, table, source type, sensitivity and description. `GetDatasetDetailAsync` (`:98-170`) has no consent or tenant check at all when ReBAC is off and returns every column name, type, sensitivity, masking state, PII indicator, primary keys and indexes. With ReBAC on, the gate is a single relation check, still no tenant/consent/tier logic.
- Same service feeds MCP resources (`McpResourceProvider.cs:51,68`, detail rendered as a column table at `:68+`) and `invoke_api` (`ApiDispatcherService.cs:111`), so an MCP agent bypasses the carefully consent-filtered `list_datasets` through `autheris://datasets/...` resources.
- Additional channels of the same service not named by the architect: `SearchCatalogDetailedAsync` returns `RelevantColumns` and matches through the search index, which is built from all tables (`CatalogSearchWarmupService.cs:43`) and is filtered only by the permitted set of the (unfiltered) list (`CatalogDiscoveryService.cs:200-216`); `ListDatasourcesAsync` (`:268-300`, route `CatalogApiEndpoints.cs:55`) discloses every domain, endpoint `BaseUrl` and auth configuration state to any authenticated caller.
- Data access itself stays protected (consents), so this is a metadata/schema disclosure, not a data breach, but it breaks tenant isolation (cross-tenant schema and data source URL disclosure) and defeats the product's "Hidden" promise (#117/MCP-1).
- **Recommendation: fix as a separate security bug BEFORE #111 (new bugfix PR, tracker step ahead of I-7, small: ~150 LOC production).** Minimal scope for the standalone fix, using today's rules only (no new tiers): for non-GovernanceAdmin/non-ClusterAdmin callers, list/detail/search/datasources go through `CatalogVisibility.VisibleTablesAsync` (consent-based, 5-arg overload) intersected with ReBAC; GovernanceAdmin through `FilterToTenant`; `ListDatasourcesAsync` GovernanceAdmin/ClusterAdmin only (tenant-filtered); detail for an invisible table returns the same 404 as an unknown one; search results only for visible tables and no `RelevantColumns` for tables whose columns are not visible. #111 WP-5 then only adds the `Discovery` surface on top. Folding it into #111 delays a live disclosure behind a size-M-L feature and mixes a behaviour change (O-1) with new functionality; the owner flag O-1 still applies to the bug fix.

**G-2 `Full` ignores DENY masking rules - CONFIRMED, severity MEDIUM (latent: not reachable today because visibility is not persisted, becomes HIGH-impact the moment #111 ships).** `CatalogVisibility.cs:103-107` adds the table unchanged for `Full`; the DENY-column removal (`:112-130`) is reached only for consented callers. Today `Table.Visibility` is always `Hidden` (not persisted, `TableModels.cs:53`), so the branch is dead; it must be fixed inside #111 (the plan does, A-4/T-2.9). Not a standalone bug. Additional requirement in SEC-111-05 (removal must happen before search matching).

**G-4 `FilterForSubject` never tenant-filters `Summary`/`Full` - CONFIRMED, severity MEDIUM (latent today, HIGH once persisted).** `CatalogVisibility.cs:59-140`: `tenantId` only selects consents (`:65`); tier rows (`:95-107`) are appended for any tenant. `VisibleTablesAsync` (`:220-259`) never calls `FilterToTenant`; only the GraphQL admin/discovery branch does (`QueryTypes.cs:437-441`). Latent for the same reason as G-2; fixed inside #111 (A-4 plus `tenantTables`), but see SEC-111-02 on the quality of the tenant signal. Additional confirmed hazards: the SQL/Trino/Arrow channels already apply a tenant data-source allowlist (`ArrowFlightSqlServer.cs:125-130`, `IcebergRestCatalogFederationService.cs:107-111`), MCP and GraphQL do not.

### P3.3 Findings

Severity scale: CRITICAL (data exposure now), HIGH (blocks implementation unless fixed in plan), MEDIUM (condition), LOW (recommendation).

| ID | Sev | Finding | Evidence | Required change |
|---|---|---|---|---|
| SEC-111-01 | HIGH | **G-5 premise is wrong.** The plan says only SQLite has a single-table metadata cache. PostgreSQL and SQL Server have the same process-local `_metadataCache` with a 30 s TTL (`PostgreSqlGovernanceRepository.cs:43,49`, `PostgreSqlGovernanceRepository.Catalog.cs:22,155`, `SqlServerGovernanceRepository.Catalog.cs:23,156`; `GetAllTablesAsync` also consults it, `PostgreSqlGovernanceRepository.Catalog.cs:180`, `SqlServerGovernanceRepository.Catalog.cs:181`); they invalidate by `_metadataCache.TryRemove` after writes (`...Catalog.cs:385,429,503,516`). A visibility or tag write that does not remove the key leaves the old tier (including `Full` after lowering to `Hidden`) served for up to 30 s. On multi-replica deployments other replicas keep the old tier for up to the full TTL (no cross-node invalidation exists for these caches). | see left | All three engines: the four new repository methods remove the local cache key on commit (and clear the SQLite generation as designed). Contract test per engine: tier change visible immediately on the same instance. Document the residual window of at most 30 s (`MetadataCacheTtlTicks`) on other replicas as accepted for restrictions, or bypass the cache for the settings read (tags and settings are two set-based queries, A-6; read them outside the metadata cache so the tier is always fresh, columns stay cached). Recommended: read tier/tags outside the cache (no stale restriction on any replica). Correct section 1.3 G-5 and A-6. |
| SEC-111-02 | HIGH | **Tenant visibility signal is forgeable and circular.** "Tenant-visible = tenant holds any consent record (Allow or Deny) or `SharedTables`" (`CatalogVisibility.cs:143-174`). A tenant admin who can create a consent (or a Deny consent, which the signal also counts) on a foreign table identifier makes that table tenant-visible and therefore `Summary`/`Full`-listed (and, for `Full`, column-visible) to every user of the tenant. The plan did not verify that consent creation validates that the table belongs to the tenant. It is also circular: a `Summary` dataset nobody in the tenant has a consent on is never discoverable, so the "discover, then request" flow (the purpose of `Summary`) does not work without `SharedTables`. | `CatalogVisibility.cs:143-174`; `GetTablesWithConsentsForTenantAsync` `SqliteGovernanceRepository.TenantTables.cs:12` | Phase 4 must first prove (test) that consent creation (REST, bulk, manifest, request approval) is refused for a table outside the tenant signal, or the signal for tiers must be `SharedTables` plus tables with an **active Allow** consent (not Deny, not pending, not expired) AND the consent must have been created through a path that already tenant-validates. If this cannot be shown, interim rule: in multi-tenant mode tiers apply only to `Gateway:Catalog:SharedTables` (explicit operator list), and the consent-derived part is dropped (the owner flag O-2 then recommends the dedicated tenant-owner attribute as follow-up). Single-tenant unchanged. Add test: tenant B admin creates a Deny consent on tenant A's `Summary` table and still does not see it. |
| SEC-111-03 | HIGH | **Tenant-scoped GovernanceAdmin can change the tier of any dataset.** The catalog is global; A-5 reuses the `/state` and `/keys` gate (`GatewayPolicies.GovernanceAdmin`, `CatalogApiEndpoints.cs:121-150`) and the manifest gate (`GovernanceEndpoints.Catalog.cs:69-74`). A tenant B GovernanceAdmin can therefore raise a table that tenant A owns to `Full` (exposing its columns to every tenant A user who passes the tier gate), or hide it (availability). Today `/state` is the only precedent and it is an existing weakness, but `Full` is a new confidentiality lever. | `CatalogApiEndpoints.cs:121-150`; `GovernanceEndpoints.Catalog.cs:69-74` | `DatasetCatalogAdminService` applies to a non-ClusterAdmin GovernanceAdmin only for datasets in the caller tenant's tenant-visible set (same signal as SEC-111-02), else identical 404. ClusterAdmin unrestricted. Tests in T-4: tenant B admin gets 404 and no write for tenant A's dataset on REST, manifest and MCP. |
| SEC-111-04 | HIGH | **GraphQL `AllowAuthenticatedCatalogDiscovery` bypasses every tier.** With the option on (default `false`, `DataCatalogOptions.cs:69`), non-admin callers take the `FilterToTenant` branch (`QueryTypes.cs:435-441`) and receive every tenant-visible table with **all columns** (`:456`), including `Hidden` tables, for any caller. The plan edits only the `else` branch (`:444`). | `QueryTypes.cs:402-441,456` | For non-admins the discovery flag must never widen beyond the tiers: `Hidden` stays hidden, `Summary` has no columns, `Full` per G-2. The flag keeps only its meaning "consent-less tenant users may see `Summary`-level listing"; simplest binding rule: non-admin always goes through `FilterForSubject(Discovery)`, flag ignored for columns. Owner flag O-5. Test: flag on, caller without consent, `Hidden` and `Summary` tables expose no column and `Hidden` is absent. |
| SEC-111-05 | HIGH | **Search is a column-name oracle and a column leak even for `Summary`.** The REST search engine path (`CatalogDiscoveryService.cs:200-216,240-262`) and the semantic/hybrid index are built from the whole catalog including columns and return `RelevantColumns`; MCP `Matches` (`McpDatasetCatalog.cs:798-803`) also matches column names. Emptying `Columns` for `Summary` (A-4) fixes MCP only if the empty list is what is searched; the index is not. A caller searching `secret_col` learns which `Summary`/`Hidden` datasets contain it. G-2 corollary: for `Full` the DENY-column removal must run before `Matches` and before the index result is shaped. | as left | For callers without consent on a table, search must neither match nor return column-derived data (`RelevantColumns` empty, no hit that is only a column-name hit). Implementation rule: the index result is post-filtered against the visible-table set and, for non-granted tables, re-matched on identifier/description only (or hits whose `MatchedTerms` come from columns are dropped). Tests T-5 23/24 extended: searching for the column name returns no hit via MCP and REST, with the index ready and with the fallback. |
| SEC-111-06 | HIGH | **Query analysis leaks all columns of any non-`Hidden` table.** `QueryAnalysisService.Evaluation.cs:62-79` only blocks `Hidden` without grant; for any other tier it adds a `MissingConsents` item and **continues**: `select *` enumerates `table.Columns` (`:103-105`), emits `OutputColumns` with name and data type (`:129-133`), and `UnknownColumn` vs found answers form a per-column existence oracle (`:109-121,:147-161`). Unreachable today only because no table can be non-`Hidden`. After #111 it exposes every `Summary` table's schema to anyone who can call analysis, a channel absent from the plan's inventory (section 1.2). Same for `BundledConsentRequestService.cs:126-135` column-level request validation (verify). | `QueryAnalysisService.Evaluation.cs:62-79,103-133,147-161` | For a table without grant/owner/admin the analysis returns the table-level `MissingConsents` item and stops (no column evaluation, no `UnknownColumn`, no `OutputColumns`). Requesting columns of such a table is by table-level READ only. Add to section 1.2 and to the T-5 matrix (REST/MCP/GraphQL query analysis with `select *`, guessed column, `Summary` and `Hidden`). |
| SEC-111-07 | MEDIUM | **`CatalogView` default `Granted` is a fail-open marker; sample-rows guard is defined negatively.** A-4 guards `sample_rows` with `CatalogView != Summary`. Any code path that builds a `TableMetadata` without setting the property yields `Granted`. Today the no-consent synthetic path is allowed when `ReleaseProfileToCatalogViewers` (`McpDatasetCatalog.cs:589-592`); the guard must be positive. | `McpDatasetCatalog.cs:575-595` | The no-consent branch is allowed only for `CatalogView == Full` (and consent/admin as today); every other value, including `Granted` without a consent and `Summary`, is `GatewayForbiddenException`. Make the enum default the most restrictive (`Summary = 0`, `Granted` explicit) or add the positive test. Same positive principle for any future consumer of `CatalogView`. |
| SEC-111-08 | MEDIUM | **Authorization order can re-introduce an existence oracle.** A-5 says identical 404 but does not fix the order. If body validation (`INVALID_VISIBILITY`/`INVALID_TAG` 400) or the ownership lookup (503) runs before the resolve-and-authorize step, a caller learns whether a dataset exists and whether they own it. Also: owner `403` for `Full` is correct (the owner already knows the dataset) but the 403 must be returned only after ownership is established. | plan A-5, `GovernanceEndpoints.Access.cs:465-476` (503 branch precedent) | Mandatory order: authenticate, resolve dataset, determine `manageable` (admin per SEC-111-03 or owner), else 404 identical (same body, same headers, comparable latency, no 503 variant: an unavailable ownership store returns 503 for **every** non-admin caller, regardless of dataset existence, before resolving), then validate body, then enforce the `Full` rule (403 for owner), then write. Test: 400-invalid body on a hidden foreign dataset yields 404; unknown/inactive/foreign all identical. |
| SEC-111-09 | MEDIUM | **Read degradation must not apply to authorization decisions and must stay per row.** A-6 returns `Hidden` on a failing settings read (good). Confirm it cannot become a DoS: a failing `TABLE_CATALOG_SETTINGS` query hides every dataset for non-admins (acceptable fail-closed), but admins and owners must still be able to see and repair; the admin service `GetAsync`/`ApplyAsync` must read settings through a path that throws (503) instead of returning `Hidden`, otherwise an admin "set Summary" after a failed read would diff against a fake `Hidden` before-state and write a misleading audit `before`. | A-6, A-5 | Two read modes: catalog reads degrade to `Hidden`; admin `GetAsync`/`ApplyAsync` before-read is strict (503 on failure). Test both. |
| SEC-111-10 | MEDIUM | **Manifest and MCP paths verified; two gaps.** The apply endpoint is GovernanceAdmin/ClusterAdmin only (`GovernanceEndpoints.Catalog.cs:69-74`) and passes the principal to the apply service (`IDeclarativeConsentApplyService.ApplyManifestAsync(ClaimsPrincipal caller, ...)`), so Q-111-1 is not bypassed provided A-7 routes through `DatasetCatalogAdminService` with that principal. Gaps: (a) the dry run must execute the same authorization and validation (including `Full` and SEC-111-03 tenant rules) so that a dry run is not a visibility oracle; (b) a manifest `Visibility` value must be parsed with the strict name parser, not the `JsonStringEnumConverter` of `GitOpsJsonOptions` (numeric and comma-list enum forms are accepted by the stock converter). | `GovernanceEndpoints.Catalog.cs:69-100`, A-7 | Add to A-7 and T-4 21: strict parse of the `Visibility` string (declared as `string?` in C-1, good, keep), dry run authorizes, numeric/flag-list strings rejected. Any future non-HTTP apply (GitOps pull job) must pass a ClusterAdmin/GovernanceAdmin principal; a unit test pins that `DatasetCatalogAdminService` never accepts a null/system principal for `Full`. |
| SEC-111-11 | MEDIUM | **`DataAccess` default surface is correct; enforce it as an invariant, not only by omission.** Trino (`TrinoMetadataQueryHandler.cs:88`), Arrow (`ArrowFlightSqlServer.cs:131`), Iceberg (`IcebergRestCatalogFederationService.cs:113`), AI guardrail (`AiDataGuardrailService.cs:290,354`), semantic resources (`SemanticMcpCompiler.cs:116`) and `describe_my_access` all call the 5-arg `VisibleTablesAsync`, which becomes `DataAccess`. Consent-only is right for these: they are query surfaces where a listed table implies queryability, and the guardrail four-eyes gate and golden queries (`AiDataGuardrailService.cs:290,354`) are access decisions (a `Summary` table must not pass). `McpCatalogVisibility.VisibleTablesAsync` is shared by the discovery tools **and** by the guardrail/semantic compiler (same helper), so its `surface` parameter default must be `DataAccess` and the discovery sites must opt in. | as left | In `DataAccess` the Hidden/Summary/Full branches are removed entirely (a `Summary`/`Full` table without Allow consent is dropped), not "conditionally skipped". The source-level test (T-3 13) must run against the three `VisibleTablesAsync` overloads and the shared `McpCatalogVisibility` helper, and a second test must prove the guardrail and semantic compiler cannot observe a `Discovery` result (no `Discovery` outside `McpDatasetCatalog`, `QueryTypes`, `CatalogDiscoveryService`). |
| SEC-111-12 | MEDIUM | **Audit completeness.** (a) Refused attempts: audit DENY for owner-asks-`Full` (planned) and also for foreign callers (rate/probing signal) without revealing existence to the caller. (b) `source` values `admin|manifest|mcp|dbt` plus the `system:default` rows: the default-`Summary` insert writes no per-table audit (A-10), and flipping `Catalog:DefaultVisibility` to `Summary` retroactively applies to every **future** source, which is a bulk exposure with no trail. (c) `before`/`after` of `Full` raises must be explicit fields so an alert can key on `after.visibility == Full`. | A-9, A-10 | One audit entry at startup and on each configuration change reporting the effective `DefaultVisibility`; one aggregated audit entry per ingestion run that created tables with a non-`Hidden` default (count, source, no table names required). Tag/visibility audit events carry the request tenant (A-5) and the principal's authentication type (token vs session). |
| SEC-111-13 | LOW | **Tags.** Charset and escaping are sound (A-3, R-8). Two additions: tags are visible on `Summary` (including to other tenants if the table is `SharedTables`), so they must not be derived from column names or sensitive classification automatically (dbt `pii`-style tags are public metadata on `Summary`); and `FilterByContract` tag include/exclude (`CatalogVisibility.cs:176-216`) now changes contract slicing (R-6). | `DbtMetadataModels.cs:11`, A-2 | Phase 6 doc states that tags are public on `Summary`; dbt tag import is allow-listed by `Catalog` option or at least documented; test 27 stays. |
| SEC-111-14 | LOW | **`RequiresFourEyes` and other `Summary` fields.** See O-4 below; additionally `Sensitivity` and `Description` are already returned on the REST list today and stay in the allow-list; `LongDescription`, `DataOwner` display name and delegates are personal data and are listed under `Summary` in A-4 (owner/delegate SIDs in a tenant-wide listing). | A-4 | Return owner/delegate display names only, never SIDs or emails, in the `Summary` view; reflection test pins the allowed property set (Security Note 1). |

### P3.4 Answers to the Review Topics

1. **`Summary` never leaks column names:** not yet. Channels that would leak after #111 as planned: REST detail/list/search/datasources and MCP resources/`invoke_api` (G-1, plan covers by WP-5 but not search index or datasources: SEC-111-05), GraphQL discovery flag (SEC-111-04), query analysis (SEC-111-06), `sample_rows` guard (SEC-111-07), search matching on columns (SEC-111-05). Confirmed safe with `DataAccess` default: Trino/Arrow/Iceberg metadata, AI guardrail, semantic MCP resources (these list consented tables only). Error messages: `Unknown table` diagnostics keep the `Hidden` wording (`QueryAnalysisService.Evaluation.cs:62-68`), `TableNotFoundException` for MCP; the `UnknownColumn` message (`:113-121`) is the leak (SEC-111-06). OData/OpenAPI generators filter by authorization and do not read `Visibility`; leak test stays (T-5).
2. **`CatalogSurface` default `DataAccess`:** correct. See SEC-111-11.
3. **Tenant visibility definition:** insufficient as designed (SEC-111-02/03). Cross-tenant leakage: today's `Summary`/`Full` branch lists across tenants (G-4); the plan's fix is correct in direction.
4. **Authorization:** Q-111-1 enforcement in the service is correct; owner `403` on `Full` is not an oracle (the owner is entitled to know the dataset); everyone else identical 404 is not an oracle **if** the order of SEC-111-08 holds.
5. **Unknown visibility -> `Hidden`:** sound (A-6, explicit switch). Strict parser for requests is sound (A-5, `DatasetTagRules.TryParseVisibility`).
6. **Default `Hidden` for new sources:** sound; creation-time only, `Full` refused at startup; audit gap in SEC-111-12.
7. **Cache invalidation:** plan premise wrong (SEC-111-01).
8. **Audit of visibility changes:** adequate with SEC-111-12.
9. **Manifest path:** cannot bypass Q-111-1 provided it goes through the service (SEC-111-10).
10. **Planned tests:** good structure (inventory, matrix, DataAccess guard), but incomplete: missing query analysis, search oracle, GraphQL discovery flag, cross-tenant admin, forged-consent tenant signal, replica/cache staleness, authorization-order oracle. See P3.6.

### P3.5 Binding Conditions (must appear in plan revision 2 before Phase 4)

- **BC-1** G-1 is fixed first as a separate security bug (scope in P3.2) and merged before I-7; WP-5 of #111 builds on it. The tracker gets a new step (suggested `I-6a`) ahead of I-7.
- **BC-2** Correct G-5/A-6: all three engines have a 30 s process-local metadata cache; the new write methods invalidate it on every engine, and tier/tags are read outside the cache (SEC-111-01).
- **BC-3** The tenant signal for tiers is either proven tenant-validated (test) or reduced to `SharedTables` in multi-tenant mode (SEC-111-02). A Deny/pending consent never counts.
- **BC-4** Non-cluster GovernanceAdmin may change tags/tier only of tenant-visible datasets; REST, manifest and MCP (SEC-111-03).
- **BC-5** `AllowAuthenticatedCatalogDiscovery` never widens beyond the tiers (SEC-111-04); owner flag O-5.
- **BC-6** Search (REST index, hybrid, fallback, MCP) never matches on or returns column-derived data for a caller without consent on the table; the DENY-column removal for `Full` precedes matching (SEC-111-05).
- **BC-7** Query analysis returns only the table-level `MissingConsents` item for an unconsented table: no column evaluation, no `UnknownColumn`, no output columns (SEC-111-06). Add the channel to section 1.2.
- **BC-8** The no-consent synthetic `sample_rows` branch is positively restricted to `CatalogView == Full`; `CatalogView` default must not be `Granted` for an unset value, or the guard is positive (SEC-111-07).
- **BC-9** Authorization order: resolve, manageable-or-404, validate, `Full` rule, write; the ownership-store 503 applies uniformly to all non-admin callers (SEC-111-08).
- **BC-10** Admin read mode is strict, catalog read mode degrades to `Hidden` (SEC-111-09).
- **BC-11** Manifest dry run authorizes like apply; strict visibility parsing; no non-privileged or null principal can raise `Full` (SEC-111-10).
- **BC-12** `DataAccess` removes the tier branches entirely; the call-site test covers all three overloads and the shared `McpCatalogVisibility` helper (SEC-111-11).
- **BC-13** Audit additions of SEC-111-12 (default-visibility configuration audit, aggregated creation audit, DENY audit for refused attempts, `Full` flagged in `after`).
- **BC-14** `Summary` view shows owner/delegate display names only (no SIDs or emails) and the reflection allow-list test pins the exact property set (SEC-111-14).

### P3.6 Mandatory Security Tests (additions to section 6)

M-1 (SEC-111-01) Per engine contract test: set `Full`, read through `GetTableMetadataAsync` and `GetAllTablesAsync`, lower to `Hidden`, read again on the same instance without waiting for the TTL: no stale tier; a second repository instance (replica simulation) observes the change on the tier read (not through the cached columns).
M-2 (SEC-111-02) Tenant B admin creates a consent (Allow and Deny, bulk, manifest) on tenant A's `Summary` table: the table does not become visible to tenant B users; consent creation on a foreign table is refused or ignored by the signal.
M-3 (SEC-111-03) Tenant B GovernanceAdmin: REST, manifest and MCP attempts to set tier/tags on tenant A's dataset return the identical 404 and write nothing; ClusterAdmin succeeds.
M-4 (SEC-111-04) GraphQL `catalog` with `AllowAuthenticatedCatalogDiscovery = true`: a caller without consent sees no columns for `Hidden`/`Summary` tables and no `Hidden` table.
M-5 (SEC-111-05) Search for the exact column name (and for a prefix) of a `Summary`/`Hidden` table returns no hit and no `RelevantColumns` through MCP `list_datasets`/`search_catalog`, REST `/search` and `/datasets?search`, with the search index ready and with the fallback path; `Full` with a DENY-masked column: the denied column name is neither listed nor matched.
M-6 (SEC-111-06) Query analysis (REST, MCP, GraphQL analysis entry points) of `select *`, of a guessed existing and of a non-existing column on a `Summary` and a `Hidden` table without consent: responses are identical for existing and non-existing columns and contain no column names or types.
M-7 (SEC-111-07) `sample_rows` (synthetic and non-synthetic) on `Summary` without consent: refused with and without `ReleaseProfileToCatalogViewers`; on `Full` without consent and flag off: refused; flag on: allowed with DENY columns excluded; an unmarked `TableMetadata` (default `CatalogView`) without consent: refused.
M-8 (SEC-111-08) Matrix of status/body equality: unknown, inactive, foreign-tenant, hidden-and-not-owned dataset, each with a valid and an invalid body, as owner-less user: all identical 404; ownership store down: 503 for every non-admin caller independent of dataset existence; owner `Full` request: 403 with `DENY` audit and no write.
M-9 (SEC-111-09) Settings read failure: catalog list degrades non-admins to `Hidden`; `admin` `GetAsync`/`ApplyAsync` answer 503, no write, no audit `before` of `Hidden`.
M-10 (SEC-111-10) Manifest: `Visibility: "2"`, `"Full,Summary"`, `2` (number) rejected; dry run by a non-admin 403; dry run by admin writes nothing and reports `Full` change; manifest `Full` entry by a tenant-B admin for tenant A dataset: skipped as unknown.
M-11 (SEC-111-11) Source-level call-site guard also asserts `McpCatalogVisibility.VisibleTablesAsync` default surface and that `AiDataGuardrailService`, `SemanticMcpCompiler`, Trino, Arrow and Iceberg sources contain no `CatalogSurface.Discovery`; behavioural: `Summary`/`Full` table without consent is absent in Trino `SHOW TABLES`/`information_schema`, Flight SQL, Iceberg listing, guardrail four-eyes gate (denied), golden queries and semantic resources.
M-12 REST catalog (G-1 fix and WP-5), ReBAC off and on: tenant B user without consent gets no tenant A table in `/datasets`, `/datasets/{id}` 404, `/search` empty, `/datasources` without foreign domains and `BaseUrl`; MCP resources `autheris://catalog/summary` and `autheris://datasets/{id}/schema` and `invoke_api` composition follow the same result.
M-13 Owner lowering of `Full` is audited with `before = Full`; a combined tags+visibility edit with forbidden `Full` applies nothing (already T-4 19, keep as security test).
M-14 `Catalog:DefaultVisibility = Summary`: system-inserted rows exist only for tables created after the option change and one aggregated audit entry is written; `Full` fails startup.

### P3.7 Recommendations for Owner Flags

- **O-1** Confirm and **accelerate**: fix G-1 as a separate security bug first (BC-1). Release note: non-admins without consent no longer see foreign tenants' tables or columns through REST/MCP resources. Recommended answer: yes.
- **O-2** Accept `SharedTables`/consent-derived tenancy only if SEC-111-02 is proven by test; otherwise multi-tenant tiers work through `SharedTables` only until a tenant-owner attribute exists. Recommended answer: yes, with the interim restriction, and schedule the tenant-owner attribute.
- **O-3** Single-call MCP tool: **acceptable** for cluster admin only through a non-read-only session, matching `admin_set_dataset_state`; the `Full` rule is enforced in the service so a two-phase flow adds no security, only reviewability. Condition: audit `source = mcp` plus the session principal; ungrouped in `McpToolGroups` with the pin test. Switch to plan/confirm later when the plan kind exists.
- **O-4** `RequiresFourEyes` in `Summary`: **acceptable**, not column-derived and useful to a requester; it reveals that a dataset needs extra approval, which is not sensitive in a tenant-visible listing. If the owner is in doubt, remove it; no security dependency.
- **O-5 (new)** Decide the fate of `AllowAuthenticatedCatalogDiscovery`: recommended, tiers always win (BC-5) and the flag is deprecated; behaviour change for deployments that enabled it.

### P3.8 Verdict

**CHANGES REQUIRED.** The authorization model (Q-111-1), fail-closed storage, `DataAccess` default and `Summary` allow-list concept are sound. The plan cannot go to Phase 4 until revision 2 incorporates BC-1..BC-14: a wrong premise (cache on PostgreSQL/SQL Server), an unproven and forgeable tenant signal, a global tenant-admin write, and four column-leak channels (GraphQL discovery flag, search index, query analysis, `sample_rows` default) the plan did not inventory. G-1 is a live, exploitable cross-tenant schema disclosure and should ship as a standalone security fix ahead of #111. After revision 2 (re-review limited to BC-1..BC-14 and M-1..M-14), the expected verdict is APPROVED WITH CONDITIONS.
