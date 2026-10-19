# F-GOV-19: GraphQL Relation Exposure and Data Source Boundaries

**Feature-ID:** F-GOV-19  
**Category:** GraphQL Governance  
**Components:**
- Application: [`RelationExecutability.cs`](../../src/Autheris.Application/Sql/Tree/Federation/RelationExecutability.cs) (shared predicate), [`GovernedTreeQueryService.cs`](../../src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs) (runtime enforcement)
- GraphQL: [`CatalogSchemaModel.cs`](../../src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs) (relation fields of the generated schema)
- Domain: [`TableModels.cs`](../../src/Autheris.Domain/Model/TableModels.cs) (`TableRelation`, `TableRelation.AllowCrossDataSource`)

---

## 1. Overview

### 1.1 Behavior
The generated GraphQL schema exposes a declared `TableRelation` as a field on both tables of the relation (forward field on the parent, reverse field `<parentTable>_by_<relation>` on the child) **only when the inner tree query service can run it as one governed statement**. A relation is exposed when both tables:

- have the same `DataSourceType` (SQL),
- name the same data source (`Table.SourceName`, compared case-insensitively, no trimming), and
- use the same SQL dialect (`Table.SourceType`, aliases such as `postgres` and `PostgreSql` count as the same dialect; an unparseable dialect is never executable).

Relations between tables of different data sources are **not part of the schema**: the field does not appear in the SDL or in introspection, and a query that selects it fails GraphQL validation (unknown field) before any data access. The MCP view of the schema (`search_catalog`, `query_graphql` document analysis) is derived from the same model and lists the same relations.

`TableRelation.AllowCrossDataSource` exists in the domain model but has no effect on the schema.

### 1.2 Same source, different domain
`TableIdentifier.Domain` is a catalog label, not a security boundary of the connection. Relations between tables of different domains that share one data source are exposed and run as one statement. Governance still applies per table: every table of the query is resolved and audited, a denial on any table fails the whole request with `Access denied.` before any SQL runs, and tenant filter, row filters and masks are compiled per table.

---

## 2. Architecture

`RelationExecutability.IsSameExecutionSource(parent, child)` is the single source of truth for the conditions above. It depends on catalog metadata only, never on the caller, so every authenticated caller sees the same schema.

- **Schema:** `CatalogSchemaModel.BuildAsync` calls the predicate for the (parent, child) pair of every relation in both directions and skips a failing relation before any field name is allocated. Names of the remaining relation fields therefore do not depend on omitted relations. The schema fingerprint covers the emitted relations only.
- **Runtime:** `GovernedTreeQueryService` uses `RelationExecutability.HasSameSourceName` for its data source check, so schema and runtime compare source names with one implementation. Any relation that reaches the service across data sources (for example from a stale schema) is rejected fail-closed with `INVALID_QUERY_OPTION` ("Relations across data sources are not supported in one query."). The runtime additionally compares the catalog dialect with the provider of the data source connection, which depends on configuration.

---

## 3. Observability

When a schema build omits relations, one Information line is logged per real rebuild: `GraphQL catalog schema: {Count} relation(s) between different data sources or dialects are not exposed; ...`. The line carries the count only, no table, relation or source names.

---

## 4. Limitations

- A tree query runs as one statement against one data source. Relations across data sources cannot be queried through GraphQL.
- Relation fields cannot be used in `where` or `orderBy` arguments; the aggregate fields (`<table>_aggregate`) take free-text `groupBy` and `where` arguments that accept only columns of the table, so a relation path is rejected.
- `CrossSourceRelationResolver` and `InMemoryCrossSourceRowReader` (batched key lookup building blocks) are registered but are not used by the GraphQL path.
- The schema lists all active SQL tables and their relations to every authenticated caller; access is enforced at query time.
