# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- `Analytics:Aggregation:DefaultTimeZone` (default `UTC`, validated at startup) sets the zone of a time bucket whose `timeZone` is omitted or null; an explicit `timeZone` still wins. The typed aggregate schema default and the MCP query guide follow the configured value (#1190).

## [2.1.2] - 2026-11-27

Full release: ships the `autheris-full` container image and the signed module archive (including the dbt module).

### Added
- Opt-in governed WebSQL result cache: node-local, in-memory, for complete successful reads; every hit still runs authentication, the governed rewrite, admission, rate limits and a fail-closed audit, and only skips execution at the source. Paged requests are sliced from one cached full result. Enabled globally with `Gateway:WebSql:ResultCacheMaxMegabytes` and per data source with `ResultCacheTtlSeconds` (optionally `ResultCacheTables`); invalidated on governance, table and catalog epoch changes; delegated and on-behalf-of sources are not eligible (#1163).
- Consent events: observer external systems receive all consent events and can read requests (#1161); `request.decided` and `request.updated` for decisions, first approvals and withdrawals (#1165); `grant.revoked` for deprovisioning and external-system revocations (#1170); `grant.renewed` on renewal (with `previousGrantId`) and `grant.revoked` for profile-assignment revocations (#1173).

### Fixed
- GraphQL aggregations report `truncated: true` when the governed SQL result was cut, and refuse to aggregate a truncated result without group counts (#1164).
- A duplicate of the caller's own pending consent request is answered with `409 DUPLICATE_PENDING_REQUEST` and the open request's id instead of a generic `400` (#1168).
- Consent request index `ux_consents_request` is replaced by indexes keyed on request, profile item and renewal generation (migration `0051`, PostgreSQL and SQL Server), so grant renewals and multi-target profile assignments no longer collide; `renewal_count` is now persisted (#1187).
- MCP `_meta.isMasked` reports column masks applied by the governed executors for every data tool, not only the guardrail's own scrub (#1181).
- `simulate_query` resolves the tables of a query in the gateway's GraphQL schema from its AST, like `query_graphql` (same access decision per table, fail-closed when a table cannot be resolved), and returns the estimate instead of always denying (#1180).
- WebSQL page tokens are verified against the data source the request resolves to, so continuation works for requests without `dataSource` (including the MCP `query_sql` cursor) (#1207).
- GraphQL `extensions.autheris.indexHints` render column objects instead of a C# type name (#1184).
- Pre-flight simulator block reason in English; stale `table(domain:, name:)` references removed from the docs (#1197).

### Changed
- The `table(domain:, schema:, name:)` form of `simulate_query` is no longer accepted; it is not part of the gateway schema and is now denied as an unresolvable query (#1180).

### Security
- Classification fails closed on regex match timeouts: a timed-out PII pattern counts as a match and the column is flagged for steward review; timeout raised to 1 s, `RegexOptions.NonBacktracking` where possible, and the pattern cache is case-sensitive (#1177).
- Catalog hydration fails closed on unknown persisted object kinds, and the governed SQL catalog, the GraphQL schema and the REST table-query path refuse `Procedure` entries (#1183, #1196).

## [2.1.1] - 2026-11-26

Patch release on top of 2.1.0. Ships the `autheris-full` container image and the signed module archive (including the dbt module) with this version.

### Fixed
- Windows builds no longer skip every governance migration step after the frozen baseline. Embedded step resources are matched independently of the platform path separator, and SQL Server batches are joined with `\n` so the executed text matches the LF-normalised, checksummed step text on every platform (#1166, #1167).

## [2.1.0] - 2026-11-26

### Added
- Explicit table-level write policies (`TableWritePolicy`, PostgreSQL and SQL Server migration `0049_table_write_policies`, `TableWritePolicyStore`, `TableWritePolicyService`, admin endpoints, and `TARGET_NOT_WRITABLE` enforcement in `WriteGrantEvaluator`, `AccessDecisionPoint`, and `PermissionRequestEngine`) (#1154, #1157).
- Bounded single-table DML (`INSERT`, `UPDATE`, `DELETE`) on Databricks reusing existing `SELECT` row-level security predicates via `PreExecutionRowCheck` (SQL 3VL check prior to database execution), `BoundedPkPredicateValidator`, `DmlWritePermit`, `GovernedSqlCompiler`, and `DatabricksCheckedDmlExecutor` (#1155, #1157).
- Typed GraphQL row-write API (`insertDatasetRow`, `upsertDatasetRow`, `updateDatasetRowByPk`, `deleteDatasetRowByPk`, `setTableWritePolicy`) backed by `BoundedRowWriteService` and catalog-quoted parameterized `BoundedWriteSqlBuilder` (`MERGE` on Databricks/SQL Server, `INSERT ... ON CONFLICT DO UPDATE` on PostgreSQL) (#1156, #1157).
- Externally sourced deputy relations as eligible deciders in governance approval workflows (#1126, #1131) and four-eyes approval for grants on secret data (#1130).

### Changed
- Removed legacy multi-step approval API (#1132, #1143) and raised the PR net-LOC ceiling to 4000 lines (#1157).
- Improved startup, catalog, and GraphQL performance: reused catalog snapshot at startup, skipped disabled federation interceptor, avoided blocking host start on GraphQL schema build, registered transport ingress field middleware as a delegate, and completed PDP/access-view cache consolidations (#1063, #1089, #1117, #1129, #1135, #1136).

### Fixed
- Enforced classification masks on baseline and explain paths and allowed open-domain `describe_dataset` with `accessStatus: "REQUEST_ACCESS"` while keeping `get_table_schema` grant-gated (#1144, #1150, #1151).
- Accepted datasets on domain table assignment, exposed domain tables and containers, resolved domain owners in the caller tenant (#1153), fixed PostgreSQL consent error handling, exposed unmask grants, and refused duplicate unmask requests (#1152).
- Streamed string cells in `DatabricksDataReader.GetChars` (#1142), kept `SET TRANSACTION READ ONLY` as the sole transaction-control statement on Oracle governed reads (#1141), stabilized flat/child-derived GraphQL relation field names (#1127), self-healed legacy SQL `source_type` on startup and dbt re-ingestion (#1128, #1134), and preserved keys over masked columns with restricted-scope explanations and dedicated time-bucket limit error codes (#1125).
- Used injected `TimeProvider` for consent validity in `ConsentResolutionService` (#1118) and expanded discovery/hidden-object surface parity and served GraphQL schema pinning test coverage (#1123, #1124, #1133, #1137).

### Security
- Enforced group SIDs, pattern ownership, write masking, fresh renewal IDs, and batch SQL filters across policy and grant evaluation (#1098, #1113).

## [2.0.0] - 2026-11-25

### BREAKING
- Governance & authorization v2 model (#1009, #1024, #1026, #1030, #1031, #1033, #1039, #1041, #1051, #1057, #1060, #1067, #1075, #1085, #1086, #1104): replaced legacy per-table data-owner assignments, legacy consent pipelines, four-eyes second-approver rules, classification clear grants, and recertification workflows (migrations `0036`–`0046`) with governance domains, protection classes (`ProtectionSnapshotProvider`), container ownership and tenant-scoped container visibility, single-approval rules for attributes/virtual filters/profiles, category masking with time-bounded unmask grants, unified `PermissionRequestEngine` decision inbox, IT self-grants, staged expiry reminders, and declarative IT manifest proposals.
- Separate database migration credentials and `INSERT`-only runtime audit role enforcement (#1029), and retirement of the catalog-mode procedure validator in ADR-018 (#1071, #1073).

### Added
- Databricks support in the single-statement GraphQL nested-relation tree compiler (`TreeSqlCompiler`, `TreeSecuredSourceBuilder`, `GovernedTreeQueryService`) with Catalyst-compatible JSON/struct projection, `SetBased` CTE keysets, binary tenant isolation, and Spark 4.0 integration coverage (#1110, #1114).
- Governed cursor pagination on MCP `query_sql` (#1064), per-scope query concurrency, duration, and row limits (#1065), and token-scope to resource-group tier mapping (#1054, #1062).
- SCIM deprovisioning revocation sweeps, cache synchronization, and group-exit TTL bounds (#1033, #1067, #1095, #1108).

### Changed
- Shared catalog visibility evaluation, cached tenant consents, batched discovery artifacts, whole-catalog read ratchets, and consolidated `GovernanceWriteInvalidator` across governance stores (#1063, #1076, #1077).
- Batched protection lookups and cached tenant access policy settings (#1049, #1056, #1061), plus .NET, container, Compose, NuGet, GitHub Actions, and Swagger UI dependency updates (#1078–#1084).

### Fixed
- Resolved `Table.SourceType` from the configured connection provider dialect (`Databricks`, `Oracle`, `PostgreSql`, `SqlServer`, including `WebSql:DataSourceMappings` and self-healing on re-PUT) in `PutDatasetDefinitionHandler` instead of storing the raw connection name, and propagated `TimeProvider` deterministically into `ConsentResolutionService` (#1115, #1116).
- Classified `SetOperationDegreeMismatchException` as a typed rejection in the SQL compiler gate (#1032, #1058) and synced `Sim.ApiHost` lockfile and SQLite `OFFSET 0` fence handling (#1034, #1059).

### Security
- Enforced realm-bound user, service-principal, and group identifiers, isolated tenant role caches, rejected Active Directory SIDs in non-workforce realms, and hardened Entra group overage and degraded resolution fail-closed checks (#1027, #1028, #1091, #1101).
- Unified MCP/GraphQL caller principal resolution and verified TOTP replay invariants (#1025), preserved app-only MCP client claims, and enforced tenant-bound `GovernanceAdmin` roles (#1093, #1103).
- Unified tenant catalog boundaries, container visibility across discovery/external catalog surfaces and import samples, realm group validation, and grant revoke authority (#1066, #1072, #1074, #1092, #1096, #1102, #1112).
- Hardened consent cache invalidation, runtime async-job policy fingerprint verification, governance write atomicity, virtual filter sync/ownership, app-only self-widening guards, scope quotas, and `DENY` masking-rule ranking (#1046, #1094, #1097, #1100, #1111).

## [1.10.7] - 2026-11-24

### Fixed
- `describe_dataset` without `brief` (including `include` narrowing on densely related sources) no longer performs a blind transitive graph walk up to 500 datasets when loading golden query examples or repeated per-relation target metadata queries. When a dataset has zero candidate golden queries, example loading returns immediately without touching relation targets; otherwise `GoldenQueryCallerView` walks only the relation fields referenced in candidate GraphQL queries while preserving fail-closed visibility and hidden-column verification. `McpDescribeNarrowing` section (`include`) and `relations` filters now run before computing skipped sections, relation target visibility and visible columns are resolved in single-evaluation snapshot batches (`ICatalogVisibilityService.FindVisibleManyAsync` with `ICatalogSnapshotProvider`), and example/describe resolution durations are logged numerically (#1068).
- `CatalogGraphQlMap` no longer rebuilds its `CatalogSchemaModel` every 30 seconds inside request threads via multi-page metadata/relation scans. It reuses the live `CatalogGraphQlTypeModule` model when available, builds from the in-memory `CatalogSnapshotProvider` when rebuilding, keeps the cached model while the catalog version is unchanged, serves warm requests lock-free with O(1) dictionary lookups and memoized table mappings, coalesces concurrent cold starts via single-flight with per-caller wait cancellation isolation, and refreshes asynchronously in the background on `catalog:changed` events and the 10-minute backstop without blocking active callers (#1069).

## [1.10.6] - 2026-11-23

### Fixed
- Catalog discovery with an access filter no longer costs time in proportion to the caller's grants after the first request. The caller's access view (which tables are granted) is cached across requests per tenant, user, channel and claim set, and keyed on the policy invalidation log, the catalog version and the virtual-filter generation. A revoke, a new grant, a deny, a classification or a catalog change on this replica takes effect before the next request; only the invalidated tables are decided again. With Redis, a cluster-wide invalidation count detects an announcement this replica missed, and the view is rebuilt. The view selects only which objects are in the filtered set (list, facets, search); every listed object and every data request is still decided per request, and a listed object whose own decision contradicts the view drops it. Views are bounded in memory (`Gateway:Catalog:Discovery:AccessViewCache`, default 64 MB, 300 s) and end at the earliest consent expiry or scheduled deny. They are not used while the invalidation signal is degraded, group resolution is degraded, or authorization plugin hooks are active (#1044).
- The access filter decides only whether a table is allowed. The column classification stage is skipped unless it can turn the allow into a deny. The caller's consents are read once per filter run instead of once per block of 500 tables. The Discover, Insert, Update and Delete decisions of a catalog page share one consent read, and the page's artifacts and column classifications are read in one statement each (#1047).
- `GET /api/v1/catalog/objects/{id}` resolves the object from the catalog snapshot instead of reading the whole catalog, and checks hidden-object ownership for that object only. An unknown id is answered at once. The row-scope descriptions of granted objects read the consents of the listed objects only (#1050).
- Measured on PostgreSQL with 10,000 granted tables (22 columns each), warm requests:
  - facets with `accessStatus=granted`: 5.5 s → 0.06–0.1 s;
  - 50-item list: 7 s → 0.25–0.3 s;
  - search: 7.2 s → 0.2 s;
  - a single object: 0.16–0.2 s; an unknown id: 0.02 s.
  The statement count per request no longer depends on the number of granted tables (8 for facets, 36 for a 50-item page).

### Security
- Every change to a pattern consent (including those of profile assignments) re-evaluates cached access decisions consistently.

## [1.10.5] - 2026-11-23

### Fixed
- The dbt governance import no longer rejects an unchanged governance file as a masking relaxation after an upgrade from 1.8.x: a rule that persists to the same column mask override as the stored one (same mask function, parameters and key; `REDACT` and `TYPE_SAFE_REDACT` with the same constant count as equal) is not a weakening, and `HMAC`/`HMAC_SHA256`/`HASH` count as one rule type. Genuine weakenings are still rejected; upgraded deployments recover on the next import without a data migration (#1042).
- `GET /api/v1/catalog/objects?accessStatus=granted` and `/objects/facets?accessStatus=granted` no longer issue governance statements per granted table and column. A batch of access decisions (`IAccessDecisionPoint.DecideManyAsync`) now reads the caller's consents, the tenant's pattern consents, and the column classifications and mask overrides once per block of 500 objects in set-based statements. Each decision re-applies the per-table consent validity rules at its own time, a prefetch older than 5 s is not used, and decisions built from it are not written to the consent decision cache. Classification and mask override reads in the classification resolver, the indexing hidden-text resolver (search index build) and the discovery page are per table instead of per column; the table sensitivity lookup reads the structural metadata without the dataset overlay and its tenant classifications in one statement, and stops when the request is cancelled (#1043).

## [1.10.4] - 2026-11-22

### Fixed
- Optimizer evaluation fences (`OFFSET 0`, `ORDER BY (SELECT NULL) OFFSET 0 ROWS`, `ROWNUM >= 1`) for PostgreSQL, SQL Server, and Oracle subqueries under row or tenant predicates (keeping DuckDB, Databricks, and ANSI SQL unfenced), together with actionable `ROW_POLICY_EXPRESSION_NOT_ALLOWED` errors and safe `TRY_CAST`/`NULLIF` hints (#1038).
- Non-raising widened SQL generation (`TRY_CAST`/`CAST` to `decimal`/`double` and `BigInteger` bucket conversion) and typed `ROW_POLICY_EXPRESSION_NOT_ALLOWED` error mapping for typed GraphQL `SUM`/`AVG` aggregates under row predicates (#1035).
- Suppressed per-table `TENANT_UNRESOLVED` dbt sync warnings when `meta.golden_queries` is not declared (#1036).

### Security
- Consistent `UNKNOWN_COLUMN` diagnostics and omitted-column handling for denied columns in `POST /api/v1/governance/queries/analyze` (#1037).

## [1.10.3] - 2026-11-22

### Added
- `EnglishOnlySourceRatchetTests` enforcing English-only code, comments, workflows, and deployment configurations across source, modules, `.github/`, and `deploy/` (#999, #1014).
- Testcontainers real-Redis integration coverage for Redis rate-limiting and cluster lease Lua scripts (#1000, #1001).

### Changed
- Excluded transitive host native runtime assets (`runtime;native`) from module project references so signed modules reuse the host's native libraries (`libduckdb`, `libonnxruntime`) instead of bundling multi-platform copies (#989).
- Retired completed `docs/plans/` documents and moved planning/coordination to GitHub Issues (#990).
- Replaced hardcoded route-prefix checks in edge and middleware routing with endpoint metadata (`GatewayEndpointMetadata`) and replaced broad prefix stripping with an explicit internal deny-list (`InternalHeadersToStrip`) (#1016).
- Hardened CI, deployment, and repository hygiene: bound `release.yml` nightly verification gates to the exact tagged commit SHA, updated Compose image digests and Azurite healthcheck failure behavior, removed `deploy/*` from documentation-only CI skips in `.github/scripts/ci-changes.py`, and translated remaining non-English source comments and deployment text (#999, #1014).
- Architectural security consolidations (#1000):
  - Unified GraphQL caller-principal resolution across resolvers and interceptors via `GraphQlCallerPrincipal` (#1004).
  - Unified consent workflow routing and submission controls across consent endpoints, profile assignments, and bundled consent requests via `ConsentSubmissionPipeline` (#1005).
  - Unified separation-of-duties evaluation (`SeparationOfDutiesService`) and hardened four-eyes approval and TOTP verification controls (#1006).
  - Consolidated SQL identifier validation and quoting into `SqlIdentifierGuard` (#1018, #1022) and unified row-filter representation around `DecisionRowFilter` (#1022).
  - Enforced required constructor dependencies on `ODataHandler` and `StreamRlsPolicyEnforcer` (#1020).
  - Standardized endpoint authorization on declarative named `GatewayPolicies` and typed `ConnectorSecurityAttestation` (#1021).

### Removed
- Unwired legacy security types and obsolete protocol/cache members (`PolicyShapeAnalyzer`, `SubgraphSecurityValidator`, `DynamicTableType`, `IMcpProtocolHandler`, and `IConsentCacheService.EvictTableDecisionsAsync`) (#998, #1019).
- Unbuilt drifted `Dockerfile.production` and `Dockerfile.staging` files (#999, #1014).

### Fixed
- Packaged and signed all 5 release modules (`lakehouse`, `dbt`, `openmetadata`, `events`, `lineage`) into the `autheris-full` container image and trimmed non-Linux module runtimes in release archives (#989).
- Regenerated `Autheris.Tests.Simulation` NuGet lockfile for `Simulation nightly` (#988, #991).
- Cross-source federation: hardened subtree compilation, per-subtree parameter isolation, pushdown identifier quoting, and dialect function policy enforcement (#993, #1008).
- Webhooks: preserved inbound product-sync webhook headers (`X-Autheris-Timestamp`, `X-Autheris-Delivery`, `X-Autheris-Signature-256`) at the edge header-stripping middleware and ensured webhook audit attribution (#994, #1002, #1016).
- Concurrency and resource limits: added atomic WebSQL statement session slot reservation, `McpPathResolver`-based MCP/GraphQL workload-tier and audit-channel routing, async SQL analysis (`McpDataTargetResolver`) and async parse budgets (`FastSqlEngine`), bounded GraphQL paging and batch previews, linear-time PII regex evaluation, and request body, DuckDB pool, and query cost limits (#995, #1011, #1012, #1016).
- Lakehouse and connectors: fixed S3 SigV4 RFC 3986 URI and query encoding, hardened Iceberg metadata discovery via `version-hint.text` and storage listing with fail-closed loading, refused unsigned Azure Blob requests by default, routed `OpenLineageApiKey` through `SecretReferenceResolver` with fail-closed resolution outside Development and URI/path log sanitization, and hardened relative and root-relative `@odata.nextLink` resolution with HTTP(S)-only scheme checks and post-resolution host matching (#996, #1010).
- Query and runtime correctness: fixed coalesced table-cache single-flight and fallback handling (`TableCacheManager`), federated result masking for read-only dictionaries (`SubgraphResultMasker`), event bus unsubscription under lock (`InMemoryGatewayEventBus`), and catalog discovery, grant-validity, Databricks token cache, and CDC/OData query correctness defects (#997, #1013, #1015).
- Redis rate limiting: fixed Redis Lua script key and value parameter passing and decimal fence-token preservation (#1000, #1001).

### Security
- Hardened persistent DuckDB table cache metadata with HMAC verification and per-table DEK derivation, made MCP session `Touch` atomic without resurrecting expired sessions, switched MCP table fast-path dispatch from tool names to explicit `TargetTable`, gated Fusion subgraph client registration on `Federation.Enabled`, and tightened latent fail-closed guards across policy, analysis, and endpoint paths (#998, #1017, #1019).
- Enforced canonical `ClusterAdmin` evaluation and tightened MCP golden examples and discovery sample visibility (#1000, #1003).
- Enforced realm `UserKeyClaim` identity binding (`RealmUserKeyResolver`), tenant request scoping, and role-resolver cache isolation and prefix-aware key bounds (`RoleResolverCache`) (#1000, #1007).

## [1.10.2] - 2026-11-21

### Added
- In-process local ONNX multilingual catalog embeddings (`#628`, `#986`) for cross-language and semantic catalog search without external embedding APIs.
- Unified catalog search (`#978`, `#984`): `/api/v1/catalog/objects`, facets, and MCP `list_datasets` share `DiscoveryService` + `CatalogSearchEngine` with per-caller visible-text verification and unindexed-candidate fallback.
- Explicit `sqlName` field on MCP datasets and columns (`#710`, `#981`) and `graphQlField` lookup support across `describe_dataset`, `describe_my_access`, and `list_datasets` (`#678`, `#682`, `#964`).
- Per-policy `key_set_max_keys` on virtual filters (`WP-KS-1`, `#955`) and `SetDiscoveryAsync` / catalog batch upsert scale harness (`#672`, `#673`, `#960`).

### Changed
- Catalog scalability optimizations: incremental snapshot & BM25 delta updates (`#675`, `#676`, `#959`), dbt manifest resync unchanged-table skipping (`#674`, `#958`), snapshot & paged discovery reads (`#677`, `#681`, `#965`), and GraphQL catalog schema build optimization for 1k–50k tables (`#679`, `#979`).
- Configuration surface simplification (`WP-SIMPL-32 PR 1–4b`: `#954`, `#961`, `#966`, `#972`, `#982`, `#983`), removal of legacy consent row filter key-set model (`WP-KS-2`/`3`: `#963`, `#975`), and declared-indexes-only dbt verification (`#459`, `#976`).
- Documented installing signed `Autheris-modules-<version>.tar.gz` release archives into the core image (`#977`).

### Fixed
- `Autheris.ModuleSigner` mixed-case entry assembly and type resolution (`#977`, `#985`), enabling signed release module archive packaging (`Autheris-modules-<version>.tar.gz`).
- Security & hardening fixes: AD FS realm token read-only scopes (`#945`, `#952`), discovery artifacts restricted-scope withholding (`#707`, `#957`), retention startup validation & usage pseudonymization (`REQ-SEC-271`, `#956`), atomic catalog insert-if-absent & GraphQL LOWs (`SEC-282-5`, `#962`), query analysis target scope (`#492`, `#971`), DML hardening (`#505`, `#973`), integration test hardening (`#503`, `#974`), parameterized cache filters (`#491`, `#980`), simulation SQLite planner & error classification (`#950`, `#953`), and resource/rate-limiting fixes (`#479`, `#485`, `#571`, `#713`).

## [1.10.1] - 2026-11-20

### Added
- Databricks Metric Views governed read access (#916, #920): `DataObjectKind.MetricView`, governance migration step `0033_metric_view_kind_and_column_role`, dbt ingestion support, `MEASURE()` aggregate / `GROUP BY` / `WHERE` pushdown guards, and discovery / OData / GraphQL / MCP visibility.
- Functional right mapping four-eyes pending approval support (#922, #939): nullable `approved_by` / `approved_at` with governance migration step `0032_functional_right_mapping_pending_approval` and cross-engine contract tests.
- Access profile items governance migration step `0031_access_profile_items` (#937).
- Databricks typed query results and long-statement execution polling (#925, #938).
- Catalog object paging and `kind` facets (#894, #898, #934).
- Extracted runtime-loaded signed modules `Autheris.Modules.Events`, `Autheris.Modules.Lineage` (#941), `Autheris.Modules.Dbt`, and `Autheris.Modules.OpenMetadata` (#942).
- `WP-SIMPL-32 PR 0` configuration flag decision matrix and `OptionsAuditTests` (#947), `S34b_UncoveredDenyOnly` grant-model parity row (#946), and Issue `#443` latest-per-parent CTE parity/security/benchmark tests (#948).

### Changed
- Enforced final `WP-SIMPL-23` test, mock, sleep, naming, and ceiling ratchets (#940) and updated master-plan / audit documentation (#943, #949).
- Trino protocol metadata follow-ups (#887, #929) and security test inventory scanner literal/comment masking (#909, #930).

### Fixed
- SQL Server governance migration step batch error attribution and retry (#849, #926), product definer provenance (#891, #927), `GenericOidc` realm token read-only scopes (#889, #928), CDC reset watermark lock order (#915, #931), SQL Server procedure row-scope aggregate handling (#921, #932), OpenMetadata webhook replay fail-closed guard (#918, #933), virtual filter require-approval SQL filter handling (#923, #935), and Databricks OBO certificate and authority handling (#924, #936).

## [1.9.0] - 2026-11-19

### Added
- Trino protocol metadata: `system.jdbc.{catalogs,schemas,tables,table_types,columns}` list the caller's discoverable objects with access status from the decision point (denied and no-consent columns are omitted); `SHOW` and `information_schema` use the same visible set. `WHERE` supports `=`, `IS NULL`, `IN` and `LIKE ... ESCAPE` over the view columns (at most 8 conjuncts, pattern length 256) plus `ORDER BY`. Every other metadata shape (`OR`, joins, `UNION`, subqueries, `LIMIT`, aggregates, other `system.*` schemas) is rejected as not supported and never forwarded to a source.
- `callerState.rowScopeDescription` on `GET /api/v1/catalog/objects/{id}` and the list route: the owner-authored description of the caller's own applicable allow consent (the one `describe_my_access` shows; user or service principal before group, then earliest expiry), `null` unless `access` is `granted` (#858).
- Workload identity: a GenericOidc realm maps an exact OIDC subject (for example a Kubernetes service account) to gateway roles through `SubjectRoles` configuration, and takes its signing keys from a static JWKS file (`Jwks:File`, no outbound key fetch). `ClusterAdmin` needs `AllowPrivilegedSubjectRoles`, a fixed tenant and the role in `AllowedGatewayRoles`; every mapped authentication writes a required `WORKLOAD_ROLE_MAPPING_APPLIED` audit record. GenericOidc realms now validate issuer, audience, lifetime and signature per realm (#853).
- `GET /api/v1/catalog/objects` accepts a text query `q` over names, descriptions and the column names the caller may see (never values). It is applied before `totalCount` and `nextCursor`, combines with `accessStatus` and the other filters, and is verified per caller like the other catalog channels. The `nextCursor` is bound to `q` and `accessStatus` (another value gives 400); `q` is trimmed, at most 256 characters, and for user callers only (#857).
- Linked documents and applications appear on discovery objects (`documents`, `applications`, per-column `documents`; GraphQL and MCP `describe_dataset`, `documentCount` in `list_datasets`). A document is returned only when it is readable for the caller (official or same tenant), not hidden from discovery, and its target is discoverable. At most 10 documents per object and 5 per column, newest first.
- MCP tool `read_document` returns one whole document as sanitised Markdown with `trust`, the untrusted-content notice for tenant text and a 64 KiB window (`truncated`, `nextOffset`). `search_docs` accepts `dataset` and `column` filters and lists the discoverable `linkedTargets` of each hit.
- `search_catalog` hits and `find_relevant_tables` candidates carry `accessStatus`, the caller's own status as `list_datasets` reports it (`GRANTED`, `PENDING`, `EXPIRED`, `REQUEST_ACCESS`); the REST `/api/v1/catalog/search` hits carry it too. `find_relevant_tables` recommends an access request instead of the query tools for candidates the caller cannot read (#804).
- Optional `accessStatus` filter on `list_datasets`, `search_catalog`, `find_relevant_tables` and `GET /api/v1/catalog/objects` (`granted`, `requested`, `expired`, `none` there), applied before ranking, limit and paging, so `total`, `nextOffset`, `totalCount` and `nextCursor` count the filtered set. The filter evaluates only the caller's grant candidates: one consent read per request and one access decision per table the caller holds a grant on, kept for the whole request (also across the internal pages of the REST list), not one per catalog table. Empty and unknown values are rejected (#798, #804).
- `GET /api/v1/catalog/graphql-schema-status` (authenticated) reports whether the GraphQL schema this instance serves is behind the catalog: `200 {"status":"Current"}` or `503 {"status":"Updating"}` with `Retry-After: 1`. A client that writes the catalog and then queries (a seeding job, integration tests) polls it until 200. It compares the served schema with the catalog version in the store, so it is correct even before the change event arrives; the body carries no catalog names, counts or versions. `/health/ready` is unchanged and does not turn 503 during a schema rebuild (#802).
- A dbt model with `materialized: view` is registered with `kind` `view` on `GET /api/v1/catalog/objects`, `/objects/{id}`, `/catalog/search`, MCP and GraphQL (every other materialization stays `table`). dbt sources and entries without a model take `meta.autheris.object_kind` (`table` or `view`); any other value registers as `table` with a sync warning and never fails the sync. Governance step 0030 adds `TABLES.object_kind` (existing rows read `table` until the next sync). The kind is descriptive only and never changes an access decision (#896).

### Changed
- Governed SQL accepts only expressions that cannot raise a data-dependent error on statements that touch a table with a row policy or a tenant predicate, in queries and in UPDATE and DELETE. Other expressions are refused with a fixed message; statements without such a table are unchanged (#847). Client values that meet a typed column in such a statement are checked against the column family when they are bound, and a refused value gets the same fixed message. Breaking query shapes: arithmetic on columns, casts of columns, functions outside the allowlist, comparisons or CASE results made only of client parameters (for example `CASE WHEN c THEN @a ELSE @b END`), and text compared with numbers.
- Table reads decide through the access decision point itself: the policy decision adapter that built a new decision point per call is removed. Analytics reads (aggregation, semantic metrics) are decided with the `Analytics` transport and REST reads (table stream, data query, change detection, relations) with `Rest`; the decision cache and audit of the shared decision point now apply to them. The aggregation and semantic metric services require the decision point and deny when it is missing.
- CDC subscriptions fail closed without a transport gate and for the all-tables topic, Iceberg namespace and table listings hide sources whose transport is denied, and webhook events dropped by the transport gate are counted under their own reason (GHSA-92vp-663x-pp52).
- One rule for the metadata of a denied column on every catalog channel (#871). A column the caller cannot see is never named, described or counted: `GET /api/v1/catalog/datasets/{id}` no longer returns `maskingState: "Deny"` rows, and an object without access lists table-level metadata and no column. `GET /api/v1/catalog/objects` for a person no longer lists denied columns, never uses `accessState: "noaccess"` and omits the hidden-column appendix. Partition columns, keys, indexes and required filters name only visible columns on REST, GraphQL and MCP, and a denied column never matches a catalog search. The external catalog service-principal view now uses the same predicate as the API authorization layer: a token with a user subject never gets it.
- `describe_dataset(brief: true)` returns column signatures only (`name`, `type`, `access`, and `primaryKey: true` on key columns) plus `primaryKeyOrder`, `indexes` and `requiredFilterColumns(Groups)`. The brief answer no longer carries the per-column `description`, `displayName`, `sensitive`, `classifications`, `clearRequestable` and `graphQlType`, nor the top-level `domain`, `schema`, `table`, `longDescription`, `tags`, `dataOwner`, `dataOwnerDelegates` and the empty `examples`. `list_datasets(format: "compact")` lists each dataset as `id`, `displayName` (only when it differs from the table name), the first sentence of the description (at most 120 characters), `sensitivity`, `accessStatus` and `kind` (only when not `table`). Measured on the serialized tool result: a 287-column dataset 92,541 to 15,757 characters with `brief` (-83.0 %), a 100-dataset list 38,624 to 18,924 characters with `compact` (-51.0 %). The legacy describe path labels a column with an explicit Clear on a sensitive column as `access: "clear"` (the caller's effective level); for the administrator view a sensitive column stays `mask` and is no longer part of a reported key or index. List and search summaries no longer end the first sentence at abbreviations such as "z. B." or "Nr." and cut long text at a word boundary (#799).
- Core consumers of the dbt integration depend on core contracts only: the new `ISemanticMetricImport` (in `Autheris.Application.Catalog.Semantic`) is the import seam for semantic manifests, and `IDbtHealthCircuitBreaker` defaults to the core `NoDbtHealthData` (every table healthy, nothing recorded) until the dbt integration registers the recording implementation.
- `consents/apply` treats more differences as a change and replaces the stored consent (#806):
  - An active consent with write rules, row scopes or classification access (for example the write grant of an approved consent request) is now revoked and replaced by the declarative consent when a manifest policy for the same grantee and table is applied. The manifest never creates these, so such a consent no longer counts as `unchanged`.
  - An earlier `validUntil` is a change for group policies too (before: individual grants only). Group grants stored with a later expiry than the manifest's `validUntil` are therefore replaced once on the first apply after the upgrade. A later `validUntil` still never extends a grant.
  - A row filter `valueType` or filter group difference is a change (before: ignored).
- F-GOV-15 shows a group and a user manifest instead of a role grantee and describes the current apply rules (`validUntil` for every policy, no `validityDays`, no role grantees) and the four-eyes rule for individual grantees, including how to seed test and demo environments; F-GOV-22 explains `INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES` on the manifest path; the simplification migration guide describes what the upgrade does to role consents (#800, #801).
- Documents hidden from discovery are no longer returned by `search_docs`, `list_doc_topics`, `list_applications` counts, `get_procedure`, `lookup_error_code` or the document search, list and read routes, except to their writer. A hidden or foreign document answers exactly like an unknown one.
- **Breaking:** a loopback `Gateway:CatalogSearch:OllamaEndpoint` (`localhost`, `127.0.0.0/8`, `::1`) is refused at startup validation, because the outbound egress policy never allowed it. Private Ollama hosts need `Gateway:Egress:TrustedInternalHosts` or `TrustedInternalNetworks` plus `CatalogEmbeddings` in `Gateway:Egress:TrustedIntegrations`.

### Removed
- The unwired audit archive export (WORM export service, `Gateway:Audit:Worm:*`, egress name `AuditWorm`) is removed. Any `Gateway:Audit:Worm` key now fails startup, with any value. Retention purge still writes signed purge checkpoints and anchors, and chain verification still passes across a purged range.
- Configuration key `Gateway:CatalogSearch:Enabled`. Catalog search validation and the index warm-up are always active; use `Gateway:CatalogSearch:WarmupOnStartup` to control the startup warm-up. The key fails startup with any value, including `false` or empty.
- **Breaking:** role grantees are removed from consents, access profile assignments and virtual filter profiles. `GranteeType` has no `Role` member (the persisted value 3 stays unassigned), `consents.role_id` and `consents.role_name` and the index `ix_consents_tenant_role` are dropped by governance schema step `0029_drop_role_grantee` (PostgreSQL and SQL Server), the REST consent grant DTO (and the OpenAPI document) no longer carries `roleName`, and the GraphQL `Consent.roleName` field is removed. The step aborts with the id of the first row and changes nothing while an active (unrevoked) role grantee exists in `CONSENTS`, in a virtual filter access profile or in an access profile assignment: revoke it, or recreate it as a group or user Deny, before the upgrade. Revoked role rows and their child rows are deleted by the step. Pending consent requests for a role grantee are set to the terminal `REJECTED` status (a request carries no grant). A stored consent row with an unsupported grantee type text fails its read instead of being mapped to another grantee. All nodes must run this build before the step applies. A role grantee is refused with a fixed 400 on every input path (bulk grants, virtual filter profiles, profile assignments, declarative manifests reject the whole document before any write); the dbt and OpenMetadata imports skip such an entry and report it in the run summary (dbt behaviour change: a virtual filter profile with an unknown or unsupported `grantee.type` is now skipped with a warning instead of defaulting to a user grantee); the authorization migration report no longer lists revoked role consents.
- Governance schema step `0027_drop_legacy_governance_columns` (PostgreSQL and SQL Server) drops `CONSENT_REQUESTS.itsm_ticket_id` with its unique index `ux_consent_requests_ticket` and `TABLE_CATALOG_SETTINGS.visibility`. The GraphQL consent request types no longer expose `itsmTicketId`. All nodes must run this build before the step applies.

### Fixed
- SQL Server CDC streams: a stream descriptor read no longer deadlocks with a concurrent producer append (error 1205). The read locks the stream row and then the watermark row, the append took them in the reverse order; the append now updates the stream row first. A rarely used admin watermark reset can still take the locks in the reverse order; tracked in #915.
- A failed governance migration step now reports the phase, the SQL Server error line, the batch number and the first 200 characters of the failing statement (static migration or catalog text, never row values or the server message), including failures outside the step batches (shape check, post-conditions, history row); this makes the intermittent SQL Server error 102 attributable (#849).
- Catalog keyword search now computes its BM25 corpus statistics (document count, term frequency, partial-match vocabulary) over the tables the caller may see, so tables the caller cannot see no longer move scores. The catalog embedding client now follows the outbound egress policy (integration name `CatalogEmbeddings`, usable in `Gateway:Egress:TrustedIntegrations`); private targets are refused unless allowlisted.
- `GET /api/v1/catalog/objects` and `/catalog/objects/{id}` answer interactive person tokens without a gateway role or `Autheris.*` scope, like MCP and `/catalog/datasets`. Discovery still decides what each caller sees; app-only and service-principal tokens still need `Autheris.Catalog.Read` or a gateway role (#856).
- Data products hardening (#851): a cached result is no longer served after a base object was deactivated, the Git sync scan rejects with `PRODUCT_SYNC_TOO_MANY_PRODUCTS` instead of truncating, the write procedure rate limit is cluster wide and fails closed, rejected webhook deliveries are audited at most once per tenant and minute, and the webhook answers "Delivery accepted".
- GraphQL schema: a catalog change that removes a root field, type, column or relation field (hide, deny, retire) rebuilds the schema without waiting for the quiet period or the deferral cap; the single-outstanding rebuild latch still applies. The change-signal check keeps the version short-circuit (it reads the store version uncached but skips the full check when it is unchanged), and polls of `GET /api/v1/catalog/graphql-schema-status` are audited as one aggregated row per minute instead of one row per poll (#835).
- A column the caller has no access to is no longer named or counted on MCP `get_table_schema`, `get_my_permissions`, the `catalog://` schema resource, the `list_datasets` `columnCount` and GraphQL catalog discovery (`catalog`, `catalogObject`: `columns`, `columnNames`). One shared rule feeds these and `describe_dataset`: only a Clear or Masked column is listed, an ungranted table lists no columns on MCP, and an unresolved column state lists nothing (#845, #838).
- WebSQL caller parameters named like an internal bind marker (`@p0`, `@p1`, `@P12`) are accepted: the compiler rejected them with 403 `SQL_POLICY_VIOLATION`, although caller names are bound by source name and never alias an internal slot. Names starting with `__`, `gql_` or `autheris` stay refused with 403. A request that sends two spellings of one parameter name (`p0` and `P0`, `@p0` and `p0`) on the federated route is refused with 400 `BAD_REQUEST` instead of keeping the first key (#869).
- Re-importing a dbt semantic manifest no longer loosens stored governance of an existing virtual table (GHSA-jw88-9j35-3c96): stricter settings, deactivation and masking rules are kept.
- Module host: a module loads only when its manifest `hostVersion` equals the host version exactly (major.minor.patch and prerelease; build metadata is ignored), outside Development a symlinked module root or module directory is rejected, and listed module files with group or other write bits are rejected. Module versions must be three-part (`major.minor.patch`); `Autheris.ModuleSigner` rejects two-part versions.
- `benchmarks/load` configures the PostgreSQL governance provider instead of the removed SQLite one: the compose files and `appsettings.bench.json` point at a `governance` database, and the cold-start scripts read `AUTHERIS_BENCH_GOVERNANCE_CONNECTION`; stale SQLite entries in the sleep-count and G7 weight baselines are removed (#812).
- The simulation nightly corpus harness binds the PostgreSQL-compiled statements to its SQLite reference with named parameters, so every row-filter probe and tenant-parameter query no longer ends as `crash or hang`; a first execution timeout is retried once, so a stall on a starved runner no longer turns the run red while a repeated hang or any leak still fails it (#746).
- `consents/apply` is idempotent for columns with several rules, for example a base rule plus a scope rule on the same column (#806). Column rules and row filters are compared as multisets independent of order, scopes as sets in their stored form. Re-applying an unchanged manifest reports `unchanged` and writes no revocation, consent, grant record, audit entry or epoch bump. Duplicated rules or scopes can no longer hide a difference.
- `consents/apply` ignores the grantee's pattern grants (#806). The subject read also returns the grantee's pattern grants of the whole domain. Before, a matching pattern grant, even one for another table, could count as the identical consent (no grant was created for the table), and a changed policy revoked the grantee's domain-wide pattern grants.
- Consent manifests (`consents/apply`) and consent request decisions (column masking input) reject a scope entry that is blank or contains a comma with `400` before anything is written. The store keeps a rule's scopes comma-separated and drops empty entries on read, so `applicableScopes: [" "]` silently became a base rule for every scope (for example `Clear` for all callers instead of one scope), and a comma split one entry into several scopes. An empty list still sets the base level (#806).
- MCP `describe_dataset` and the REST catalog detail (`GET /api/v1/catalog/datasets/{id}`) report the declared primary key again (`primaryKey` per column, `primaryKeyOrder` with `ASC`/`DESC`, REST `primaryKeys` in key order with `primaryKeyDirections`) and the declared indexes with their column order and directions (#805). Since 1.8.0 the discovery-service path returned none of them. A key or index is reported only when every one of its columns is Clear for the caller, as before.
- A `describe_dataset` answer above `Mcp:MaxTokensPerCall` is shortened at column boundaries instead of being replaced by the `TOKEN_BUDGET_EXCEEDED` notice: id, descriptions, keys, indexes, required filters, relations and access status stay, the answer keeps the longest column prefix that fits, and `_meta` reports `returned`, `omitted` and `nextColumnOffset` (call again with `columnOffset`, or select columns by name with `columns`). Also applies to `brief: true`. With the default 4,096 tokens a table with more than about 53 columns used to return nothing. When not even one column fits, the notice names the ways to a smaller answer (`columns`, `include` without `longDescription`, `brief: true`). A shortened `list_datasets` / `search_catalog` result keeps at least one dataset (otherwise the notice) and is written with the same relaxed JSON encoder as the unshortened answer: the values are unchanged, non-ASCII text is no longer escaped, so more datasets fit (#820).
- `describe_dataset` reports `requiredFilterColumns` and `requiredFilterColumnGroups` again; the REST catalog detail gains both members (omitted when the dataset has none). A required filter column is named only when the caller has at least masked access to it and it is not discovery-hidden; a group only when every member qualifies (#807).
- New datasets are queryable within seconds after a dbt sync, a governance import or another catalog write instead of after the next periodic catalog check (about 60 s of `GRAPHQL_VALIDATION_FAILED`): the GraphQL catalog module now listens to `catalog:changed` and runs a debounced check (1 s after the first change, at the end of a burst after a quiet period of the last build's duration, 2 to 60 s). Rebuilds stay coalesced as in #786, a write without schema effect (for example a consent apply) never rebuilds, and the `reloadSchema` mutation now reaches the module. Other instances learn about the change through the Redis event bus, or at their periodic check without Redis (#802).

- A tree query whose root has a set-based list child no longer fails with an invalid column when the root `orderBy` omits the unique key and the key is not selected: the page CTE now projects the tie-break key columns it orders by; they are not returned unless selected. The root and child tie-break keys are resolved to their catalog spelling, and access is checked on that name: a key declared as `ID` for column `id` is written as `id` (the same column on SQL Server, a fix on PostgreSQL), and a key column that is unknown, not a safe name, masked or denied is never used as tie-break (the next clear unique index is used, else the list stays correlated) (#836).

### Security
- Fixes for GHSA-92vp-663x-pp52, GHSA-jw88-9j35-3c96, GHSA-66xj-4459-x8gx, GHSA-x8g9-cm3h-p6jq and GHSA-rxr4-mf28-p2hv. Details: private advisory.

### Upgrade notes
- Back up the governance database first. There is no downgrade; v1.5.0 is the oldest supported base.
- Governance schema steps run automatically at startup, forward-only and in order: 0023 `drop_is_sensitive`, 0024 `drop_superseded_governance_columns`, 0025 `golden_queries`, 0026 `doc_links`, 0027 `drop_legacy_governance_columns`, 0028 `data_products`, 0029 `drop_role_grantee`, 0030 `table_object_kind`. Steps 0023, 0024, 0027 and 0029 drop data structures: all nodes must run this build before they apply (no rolling upgrade across them).
- Before upgrading, revoke or recreate every active role grantee as a group or user grantee; step 0029 refuses to run while one exists.
- Remove `Gateway:CatalogSearch:Enabled`, `Gateway:Catalog:DefaultVisibility` and every `Gateway:Audit:Worm:*` key from all configuration sources; each fails startup with any value. Move a loopback Ollama endpoint to a trusted internal host.
- Realms that use `SubjectRoles` or `Jwks` must be defined in host configuration (appsettings, environment, command line), not only in code; startup fails closed otherwise.
- Container images: the default tag is the `core` flavor without modules; installations that load modules use the `-full` image, whose modules are signature-verified at image build.
- Clients: governed SQL refuses more query shapes on tables with a row policy or tenant predicate (see Changed); `describe_dataset(brief: true)` is slimmer; denied columns are no longer listed on any catalog channel; case-only duplicate WebSQL parameter keys answer 400.

## [1.8.5] - 2026-11-17

### Added
- `get_table_schema` columns carry `access` (`clear` or `mask`), the caller's own column state (#711).
- The DML intent and executed audit records carry `sql.bound_values_digest`, a keyed HMAC-SHA256 over the client and literal values the statement binds, so a record is tied to the exact new values without storing any (#505).

### Changed
- Dependency updates: coverlet.collector 10.1.1, dotnet-stryker 5.1.0, node 26.13.1 in the load benchmark image.
- `docs/configuration-guide.md` is in English (#485).
- The container image is 409 MB uncompressed (was 421 MB): the published `libduckdb.so` is stripped of its static symbol table (-11 MB) and the localized satellite resource assemblies of the dependencies are no longer published (`SatelliteResourceLanguages=en`). The footprint budget is lowered from 440 MB to 430 MB.
- Governed SQL compile cache hits no longer allocate the cache key string: the key is built in a thread-local builder, hashed from a pooled buffer and compared chunk by chunk (allocation per cached compile 6.6 KB to 3.2 KB for a 0.5 KB statement, 13.9 KB to 3.2 KB for 4 KB; cache keys and hashes are unchanged).
- Audit entry hashing (`AuditChainEngine`, `AuditCanonicalizer`) streams the canonical payload into a pooled buffer instead of building a payload string and a byte array (152 B instead of 1.8 KB per entry, about 25 percent faster); hash values are byte-identical.
- The access decision point builds the role set without LINQ, pre-sized, and shares one empty set for subjects without roles.

### Removed
- The unused OpenJev client, `IOpenJevClient`, `JustificationTriageService`, `IJustificationTriageService` and the justification triage result types (#503).
- `IDelegationCapability.SourceEnforcesRowSecurity` and `SourceEnforcesMasking`: no code read them (#503).

### Fixed
- The footprint budget nightly workflow no longer fails at start-up: the reusable footprint workflow used the same concurrency group as its caller.
- `describe_dataset` returns the column `longDescription` (when requested with `include`) on the discovery-service path too (#714); the GraphQL `DiscoveryColumn` type gains the nullable field `longDescription` (sanitized, visible columns only).
- The native DuckDB library is no longer loaded at startup: the time zone conversion state runs the DuckDB ICU probe lazily on the first DuckDB conversion check instead of in the startup hosted service, so a node that never uses DuckDB does not map `libduckdb.so` (the footprint lazy-load check reports `lazy: true`).
- Idle CPU and memory growth after a large catalog sync (#786): the GraphQL catalog module now gets the catalog version provider in the application composition, so an idle refresh tick exits on an unchanged version instead of rebuilding the whole candidate model every minute (a full check still runs every 10 minutes as a backstop); rebuild requests are coalesced (one running, one waiting with the newest model, instead of one full schema build per change line); a burst of changes rebuilds once it settles (at the latest after 5 minutes) while a single change still rebuilds at once; the refresh timer no longer overlaps a running check; the bulk relation read is authoritative (no per-table fallback reads) and reverse relations are found through an index (O(n) instead of O(n^2)); the log gains `schema build started` and `schema build finished` lines with the duration and a `phase=check|rebuild` field on progress lines (counts and durations only, no catalog names).
- A retry that started right after a failed key-set load could receive the finished load's failure instead of starting its own load (the single-flight cache released its waiters before unregistering the load); the Virtual Filter key set cache and every other user of `SingleFlightKeySetCache` are fixed.

## [1.8.4] - 2026-11-17

### Fixed
- The release footprint gate budgets the .NET 11 / Ubuntu 26.04 image at 440 MB uncompressed (measured 421 MB, was 360 MB on .NET 10); v1.8.3 was not published because the 340 MB budget failed. 1.8.4 is the first published release containing the .NET 11 platform, the module trust anchor rotation and the module container fixes listed under 1.8.3.

## [1.8.3] - 2026-11-17

### Changed
- The platform runs on .NET 11 (SDK 11.0.100, `net11.0` for all projects) and the container images use Ubuntu 26.04 (`aspnet:11.0.0-resolute`). `double` and `float` values are converted to `decimal` through `DecimalConversions` (15 and 7 significant digits) because .NET 11 converts exactly; quota thresholds, filter coercion, row filters, masking input and exports use it. An empty string for a boolean setting now fails startup with the key name; a ZIP entry with a wrong CRC raises `InvalidDataException`.
- The module trust anchor (`module-release-pub.pem`) was rotated; release modules are signed with the matching key from the `AUTHERIS_MODULE_SIGNING_KEY_PEM` secret. The maximum size of a single module file is 256 MiB (was 64 MiB) so the DuckDB native library of the Lakehouse module can load.

### Fixed
- Module directories in the container image are read-only (0555/0444); production refused to load modules from the writable directory the image shipped with.
- The signed-module container e2e runs against a PostgreSQL sidecar.

## [1.8.2] - 2026-11-17

### Fixed
- The release footprint gate starts the Production image with a TLS-verified PostgreSQL sidecar and measures it with `Migrations:Mode=Verify`; v1.8.0 and v1.8.1 were not published because the gate could not start the container. Budgets: image 340 MB, cold start 5.0 s (10 percent tolerance unchanged). 1.8.2 is the first published release of the 1.8 line and contains everything listed under 1.8.0 and 1.8.1.

## [1.8.1] - 2026-11-17

### Added
- `GET /api/v1/catalog/objects` accepts a text query `q` over names, descriptions and the column names the caller may see (never values). It is applied before `totalCount` and `nextCursor`, combines with `accessStatus` and the other filters, and is verified per caller like the other catalog channels. The `nextCursor` is bound to `q` and `accessStatus` (another value gives 400); `q` is trimmed, at most 256 characters, and for user callers only (#857).
- MCP discovery ergonomics: `list_datasets` and `search_catalog` return short summaries; `describe_dataset` selects sections and columns and filters relations by term; `search_catalog` filters by tag; over-budget results are shortened by whole datasets; `get_table_schema` reports the GraphQL field per table and visible column.

### Fixed
- The release footprint gate builds the container image with an empty `dist/modules` directory; v1.8.0 was not published because this gate failed. 1.8.1 is the first published release of the 1.8 line.

## [1.8.0] - 2026-11-16

### BREAKING
- OpenSchema removal (OD-19): The `Gateway:OpenSchema` and `Gateway:Catalog:OpenSchema` keys were removed and setting either key triggers startup failure via `LegacySwitchGuard`. Anonymous catalog access is not supported. Authenticated users discover the catalog by default ("visible unless hidden"); objects and columns can be explicitly hidden using `PUT /api/v1/catalog/datasets/{id}/discovery`, and the legacy `visibility` tier is ignored.
- MCP `search_catalog` and `find_relevant_tables` name a table hit and its join targets with the `list_datasets` id `domain.schema.table` instead of `domain.table` (`table`, `join_relations[].target_table`, `candidates[].dataset`, `candidates[].relations[].targetTable`), so a hit can be passed to `describe_dataset` and tables with the same name in two schemas are distinct hits (#627). `join_condition` keeps the table names.
- An unsupported governance provider fails startup: `Gateway:GovernanceDb:Provider` accepts only `PostgreSql` (default) and `SqlServer`; `Sqlite` and SQLite connection strings are rejected in every environment, and an empty connection string fails startup. There is no migration tool from SQLite governance files. The deploy stack (compose, Helm, benchmark) uses PostgreSQL and the `governance-seed` container is removed. See [Governance store migration](docs/operations/migration-simplification.md#3-governance-store-migration-postgresql-and-sql-server).
- Role grantees retired (ADR-020): a role never matches a grant. `POST /api/v1/governance/consents/apply` refuses a policy with `granteeType: "Role"` (any effect) for a table registered in the catalog with `400 BAD_REQUEST`, and every such manifest policy, group and `Deny` policies included, needs an absolute `validUntil` within `Gateway:Governance:Grants:DeclarativeMaxDuration` (`MaxDuration` when unset); `validityDays` has no effect. Governance step `0011_role_grantee_and_profile_assignment_retirement` revokes every non-`Deny` role consent on upgrade (revoke reason `ADR020_ROLE_GRANTEE_UNSUPPORTED`) and deactivates access profile assignments; role `Deny` consents stay and keep restricting. The canonical ClusterAdmin lists the revoked consents with `GET /api/v1/admin/authz/migration-report`; re-create the access as group grants. See [Role grantees and declarative consent manifests](docs/operations/migration-simplification.md#4-role-grantees-and-declarative-consent-manifests-180).
- SQLite data-source support removed (OD-2): `Gateway:DataSources:*` with `Provider = "Sqlite"` (or `"sqlite3"`) or an empty `Provider` fails startup fail-closed with `UnsupportedDataSourceDialectException`; `Microsoft.Data.Sqlite` is removed from production and module projects.

### Added
- `GET /api/v1/catalog/objects` accepts a text query `q` over names, descriptions and the column names the caller may see (never values). It is applied before `totalCount` and `nextCursor`, combines with `accessStatus` and the other filters, and is verified per caller like the other catalog channels. The `nextCursor` is bound to `q` and `accessStatus` (another value gives 400); `q` is trimmed, at most 256 characters, and for user callers only (#857).
- Authorized data discovery replaces the anonymous OpenSchema: catalog listing, search and describe tools return only what the caller may see, with per-column access states (`Clear`, `Mask`, `Deny`).
- External catalog and consent API with a configurable workflow mode, time-limited signed grants (grant records with a verifiable hash chain), owner grants, profile assignments, consent events and a webhook with HMAC signatures.
- Unified authorization model: one grant model and one decision point for REST, GraphQL, MCP, SQL, Envoy ext_authz, streaming, Lakehouse and federation; row policies, dynamic object coverage and per-cell column state.
- Classification-based masking and governed classifications; masked catalog text is configurable per scope.
- Usage accounting per user and AD group; MCP row cap and truthful catalog hints for query clients; WebSQL client contract with cost and quota, typed columns, syntax positions and paging.
- Docs ingestion and MCP doc tools on a governance document store (migration step `0022_docs_store`); governed stored procedures with a clean core design.
- Catalog performance for large catalogs: paged and filtered catalog reads, batch upserts for table metadata, one version bump and change event per multi-table write, incremental search index updates.
- Catalog search: BM25F per-field weights and matching query tokens as parts of indexed words.

### Changed
- Grant validity: grants may be valid for several years. There is no compiled-in maximum; `Gateway:Governance:Grants:MaxDuration` and `DefaultDuration` are required configuration (startup fails without them), `DeclarativeMaxDuration` is optional and defaults to `MaxDuration`, and `Gateway:Governance:IndividualGrants:*` are optional operator limits. The former per-class ceilings (180/90/30/7/365/90 days) and the group exemption are gone. REST requests without a duration use `DefaultDuration`.
- Governance schema steps `0013` to `0022` are new (`0012_dbt_unique_id` stays as released); only PostgreSQL and SQL Server are supported governance stores.
- Authentication and authorization core with direct and upstream-header modes; the Casbin engine and its options were deleted.

### Removed
- Anonymous `OpenSchema` options (`Gateway:OpenSchema`, `Gateway:Catalog:OpenSchema`, `OpenApiDocumentOptions.OpenSchema`) and branches across GraphQL, OData, WebSQL, SQL endpoints, and stored procedures.
- SQLite governance store (repositories, migration runner, baseline, steps), the `governance-seed` compose container and `tools/migrations/upgrade_governance_v1_1_to_v1_2.sql`, and SQLite data-source runtime support (`DatabaseDialect.Sqlite`, `Microsoft.Data.Sqlite` production dependency, and the CI `parity-sqlite` job) per OD-2.

### Moved to modules
- None yet.

### Fixed
- WebSQL logs why a catalog entry cannot be used: no catalog metadata registered, entry inactive, or no declared columns (for example a SQL Server synonym registered without columns). The caller's answer is unchanged (#669).
- WebSQL reaches tables of a data source mapped through `WebSql:DataSourceMappings`, for example a dbt database mapped with `Dbt:DatabaseMappings`: the governed catalog now uses the mapped connection for connection, dialect and fingerprint, and admission stays on the requested name. Startup fails for a mapping with an empty or unknown target, a chain, a cycle or a self-mapping, and warns for a dbt-mapped database in `WebSql:AllowedDataSources` without a WebSQL mapping (#668).
- dbt sync registers sources under their `identifier` and models and seeds under their `alias` (falling back to `name`), so catalog entries, relations and generated SQL endpoints point to the physical table; new entries show the dbt name as display name. A catalog entry records the dbt node that owns it (governance step 0016); a node whose physical name is taken by an entry of another origin, and all nodes of one manifest that map to the same table, are skipped with `PHYSICAL_NAME_COLLISION` (before, the second of two such nodes was skipped as `DUPLICATE`). Entries registered under the logical name are left unchanged and reported with the warning `PHYSICAL_NAME_CHANGED` (#666).
- Catalog search hits (MCP and `GET /api/v1/catalog/search`) suggest the real GraphQL root field of the table (`domain_schema_table`, sanitized) from the catalog GraphQL schema, or none when the table has no root field, instead of a non-existent `domain_table` (#682).

- A first approval on a table with a sensitive column left the consent request `APPROVED` without creating a consent; the approval path no longer routes by grant class.
- SCIM realms other than the default realm always answered 403; the realm of the authenticated SCIM handler is kept.
- The grant record hash chain no longer breaks when PostgreSQL stores timestamps with microsecond precision.
- The grant expiry sweep accepts consents of the platform tenant; owner grants without a request tenant are readable.
- `CONSENT_REQUEST_NOT_FOUND` and the related decision codes are no longer masked as `INTERNAL_SERVER_ERROR`.
- Audit details keep fields such as `primaryKeyDirections` and `pushdownKeyCount` instead of redacting every key that contains `key`.
- `POST /api/v1/authz/explain` and the grants view answered 404 for every REST caller (#733); the request principal is now attached to the security context.
- The attribute table provider reads `citext` columns and compares tenant and issuer ordinally in the database.
- `is_active` is bound as a boolean, so classification reads work on PostgreSQL; timestamps of consent and virtual filter writes are bound as `timestamptz`.

### Security

- Container images come in two flavors: `autheris` (core, no modules) and `autheris-full` (signed modules, verified offline against the embedded release key at image build). The module signing key is only reachable from jobs in a protected environment.
- GET on a SQL endpoint refuses every definition that is not a single SELECT (#730).
- A hard row policy whose stored pattern cannot be evaluated denies access (`VF_PATTERN_EVALUATION_FAILED`) instead of failing open.
- Streaming applies the stricter column level of the decision point (strict access profile) over the consent level.

## [1.7.0] - 2026-10-15

- Added Relay keyset cursor connections with HMAC tamper binding.
- Implemented index-aware pricing for root-list plans in GraphQL cost model.
- Added admission estimate and complexity ceiling per tier for GraphQL root lists.
- Reconciled physical key and index directions during dbt ingestion and admin refresh.
- Decomposed gateway authentication into scheme-specific configuration.
- Closed security advisory GHSA-mgv5-4r69-mwww (Admin role model).

## [1.6.0] - 2026-09-01

- Added physical non-unique indexes in describe_dataset and catalog detail endpoints.
- Introduced single default tenant configuration, explicit realm definitions, and tenant resolver.
- Enforced table stream slice limits and governed status responses.
- Implemented governed aggregation whole-key reads on SQLite backends.
- Bound query cost options from Gateway:QueryCost with unified admission multiplier.
- Closed security advisory GHSA-6523-m29q-c397 (Governed aggregation).

## [1.5.0] - 2026-08-01

- Hardened SCIM mass-deprovisioning guard to fail closed.
- Connected Helm high-availability timings to gateway configuration with render validations.
- Introduced TimeProvider abstractions for ReBAC and consent cache expiration.
- Standardized closed rejection error codes without inner exception leaks.
- Unified persistence SQL cores across outbox and consent grant tables.
- Closed security advisory GHSA-3953-r994-fg4m (Transport gate).

## [1.4.0] - 2026-07-01

> Unsupported upgrade base. Oldest supported migration base: v1.5.0.

- Removed deprecated batch-relation and chunked-query pipelines.
- Implemented bucket-state refill rate probes for rate limiting.
- Hardened catalog invalidation precedence over in-flight version loading.
- Added automated Trivy filesystem container scans to continuous integration.
- Unified GraphQL error mapping and simulation test runners.
- Closed security advisory GHSA-wvwx-q6jg-44wv (CDC webhooks).

## [1.3.0] - 2026-06-01

> Unsupported upgrade base. Oldest supported migration base: v1.5.0.

- Added automatic registration of SQL data source tables via dbt synchronization.
- Introduced runtime-managed, app-scoped trusted GraphQL documents.
- Added support for gated WITH RECURSIVE CTE execution in WebSQL.
- Hardened GraphQL subscriptions validation for read-only access tokens.
- Upgraded runtime base image dependencies for vulnerability remediation.
- Closed security advisory GHSA-hw85-rqjh-cwxx (Token client allowlist).

## [1.2.0] - 2026-05-01

> Unsupported upgrade base. Oldest supported migration base: v1.5.0.

- Implemented typed aggregations in GraphQL queries for read-only tokens.
- Added same-source AST subtree partitioning in cross-source WebSQL.
- Added support for co-located data sources multi-database execution.
- Added dynamic key-set filters in cross-source WebSQL joins.
- Added bound parameterization in federated WebSQL queries.
- Closed security advisory GHSA-547h-29x4-m4ch (dbt sync).

## [1.1.0] - 2026-04-01

> Unsupported upgrade base. Oldest supported migration base: v1.5.0.

- Implemented AST target dialect generation across SQL backends.
- Added mandatory OData-Version 4.0 header for PowerQuery and Excel compatibility.
- Added active stored procedures documentation in OpenAPI schemas.
- Enhanced GraphQL schema introspection documentation.
- Closed security advisory GHSA-jcpx-6m27-2m97 (Error responses).

## [1.0.0] - 2026-03-01

> Unsupported upgrade base. Oldest supported migration base: v1.5.0.

- Initial release of Autheris Enterprise GraphQL Gateway.
- Zero-trust data-owner consent governance and access policy engine.
- High-throughput SQLite persistence with WAL mode and tiered audit batching.
- Multi-protocol gateway supporting GraphQL, WebSQL, and Envoy ext_authz.
- Built-in multi-tenant isolation and security controls.
- Closed security advisory GHSA-qfx6-4vp6-4hjv (Windows authentication).
