# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### BREAKING
- None yet.

### Removed
- None yet.

### Moved to modules
- None yet.

### Fixed
- None yet.

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
