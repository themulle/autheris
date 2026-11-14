# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### BREAKING
- OpenSchema removal (OD-19): The `Gateway:OpenSchema` and `Gateway:Catalog:OpenSchema` keys were removed and setting either key triggers startup failure via `LegacySwitchGuard`. Anonymous catalog access is not supported. Authenticated users discover the catalog by default ("visible unless hidden"); objects and columns can be explicitly hidden using `PUT /api/v1/catalog/datasets/{id}/discovery`, and the legacy `visibility` tier is ignored.
- MCP `search_catalog` and `find_relevant_tables` name a table hit and its join targets with the `list_datasets` id `domain.schema.table` instead of `domain.table` (`table`, `join_relations[].target_table`, `candidates[].dataset`, `candidates[].relations[].targetTable`), so a hit can be passed to `describe_dataset` and tables with the same name in two schemas are distinct hits (#627). `join_condition` keeps the table names.
- An unsupported governance provider fails startup: `Gateway:GovernanceDb:Provider` accepts only `PostgreSql` (default) and `SqlServer`; `Sqlite` and SQLite connection strings are rejected in every environment, and an empty connection string fails startup. There is no migration tool from SQLite governance files. The deploy stack (compose, Helm, benchmark) uses PostgreSQL and the `governance-seed` container is removed. See [Governance store migration](docs/operations/migration-simplification.md#3-governance-store-migration-postgresql-and-sql-server).

### Removed
- Anonymous `OpenSchema` options (`Gateway:OpenSchema`, `Gateway:Catalog:OpenSchema`, `OpenApiDocumentOptions.OpenSchema`) and branches across GraphQL, OData, WebSQL, SQL endpoints, and stored procedures.
- SQLite governance store (repositories, migration runner, baseline, steps), the `governance-seed` compose container and `tools/migrations/upgrade_governance_v1_1_to_v1_2.sql`. SQLite stays supported as a data source.

### Moved to modules
- None yet.

### Fixed
- dbt sync registers sources under their `identifier` and models and seeds under their `alias` (falling back to `name`), so catalog entries, relations and generated SQL endpoints point to the physical table; new entries show the dbt name as display name. A catalog entry records the dbt node that owns it (governance step 0012); a node whose physical name is taken by an entry of another origin, and all nodes of one manifest that map to the same table, are skipped with `PHYSICAL_NAME_COLLISION` (before, the second of two such nodes was skipped as `DUPLICATE`). Entries registered under the logical name are left unchanged and reported with the warning `PHYSICAL_NAME_CHANGED` (#666).
- Catalog search hits (MCP and `GET /api/v1/catalog/search`) suggest the real GraphQL root field of the table (`domain_schema_table`, sanitized) from the catalog GraphQL schema, or none when the table has no root field, instead of a non-existent `domain_table` (#682).

### Security
- None yet.

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
