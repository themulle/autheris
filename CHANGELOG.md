# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
- Grant validity: grants may be valid for several years. There is no compiled-in maximum; `Gateway:Governance:Grants:MaxDuration` and `DeclarativeMaxDuration` are required configuration (startup fails without them), and `Gateway:Governance:IndividualGrants:*` are optional operator limits. The former per-class ceilings (180/90/30/7/365/90 days) and the group exemption are gone. REST requests without a duration use `DefaultDuration`.
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
