# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed
- `consents/apply` treats more differences as a change and replaces the stored consent (#806):
  - An active consent with write rules, row scopes or classification access (for example the write grant of an approved consent request) is now revoked and replaced by the declarative consent when a manifest policy for the same grantee and table is applied. The manifest never creates these, so such a consent no longer counts as `unchanged`.
  - An earlier `validUntil` is a change for group policies too (before: individual grants only). Group grants stored with a later expiry than the manifest's `validUntil` are therefore replaced once on the first apply after the upgrade. A later `validUntil` still never extends a grant.
  - A row filter `valueType` or filter group difference is a change (before: ignored).
- F-GOV-15 shows a group and a user manifest instead of a role grantee and describes the current apply rules (`validUntil` for every policy, no `validityDays`, no role grantees) and the four-eyes rule for individual grantees, including how to seed test and demo environments; F-GOV-22 explains `INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES` on the manifest path; the simplification migration guide describes what the upgrade does to role consents (#800, #801).

### Fixed
- `consents/apply` is idempotent for columns with several rules, for example a base rule plus a scope rule on the same column (#806). Column rules and row filters are compared as multisets independent of order, scopes as sets in their stored form. Re-applying an unchanged manifest reports `unchanged` and writes no revocation, consent, grant record, audit entry or epoch bump. Duplicated rules or scopes can no longer hide a difference.
- `consents/apply` ignores the grantee's pattern grants (#806). The subject read also returns the grantee's pattern grants of the whole domain. Before, a matching pattern grant, even one for another table, could count as the identical consent (no grant was created for the table), and a changed policy revoked the grantee's domain-wide pattern grants.
- Consent manifests (`consents/apply`) and consent request decisions (column masking input) reject a scope entry that is blank or contains a comma with `400` before anything is written. The store keeps a rule's scopes comma-separated and drops empty entries on read, so `applicableScopes: [" "]` silently became a base rule for every scope (for example `Clear` for all callers instead of one scope), and a comma split one entry into several scopes. An empty list still sets the base level (#806).

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
