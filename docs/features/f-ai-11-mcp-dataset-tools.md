# F-AI-11: MCP Dataset Tools (Catalog Discovery and GraphQL Queries for AI Agents)

**Status:** [Done]  
**Components:** [`McpDatasetCatalog.cs`](../../src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs), [`McpDatasetTools.cs`](../../src/Autheris.Application/Mcp/Services/McpDatasetTools.cs), [`CatalogGraphQlMap.cs`](../../src/Autheris.GraphQL/Mcp/CatalogGraphQlMap.cs), [`GatewayMcpQueryExecutor.cs`](../../src/Autheris.GraphQL/Mcp/GatewayMcpQueryExecutor.cs)

---

## 1. Overview

An agent connects to `/mcp` and finds everything it may use with a few generic tools instead of one tool per table. **GraphQL is the preferred query path**: the tools point the agent to the GraphQL field of each dataset and run GraphQL queries for it.

1. `list_datasets` – which datasets exist for me, and which protocols does the gateway offer?
2. `describe_dataset` – what do they contain, and how do I query them with GraphQL?
3. `query_graphql` – run the query.
4. `sample_rows` – a quick look at a few rows.

The `initialize` response carries the same guidance as MCP `instructions`. The tool list stays the same size however large the catalog is.

The dataset description is also available as an MCP resource: the resource template `autheris://datasets/{dataset}` returns the same JSON as `describe_dataset` (and passes the same guardrails). The server runs on the official MCP C# SDK at `/mcp` (Streamable HTTP, current protocol revisions, OAuth protected resource metadata, see [ADR-014](../adr/ADR-014-enterprise-model-context-protocol-and-ai-data-guardrails.md)).

## 2. Tools

| Tool | Arguments | Returns |
| :--- | :--- | :--- |
| `list_datasets` | `search?`, `domain?`, `offset?`, `limit?` | Paginated datasets (default 50, max 100): `id` (`domain.schema.table`), description, sensitivity, number of visible columns and the `graphQlField`. `total` and `truncated` indicate whether further items exist. `guidance` instructs using GraphQL; `endpoints` lists all enabled protocols, GraphQL marked `preferred`. |
| `describe_dataset` | `dataset` | Visible columns with database type, GraphQL field and GraphQL type, `sensitive` and `primaryKey`; table description, sensitivity, `requiresApproval` (four-eyes); `graphQl` with endpoint, query field, filter and sort types, argument syntax, example query and relations to visible datasets; `otherAccess` (OData, OpenAPI, `sample_rows`, Arrow export); curated golden queries as `examples`. |
| `query_graphql` | `query`, `variables?` | The GraphQL response (`data`, or `errors` as an error result). Exactly one query operation per call; mutations and subscriptions are rejected. |
| `sample_rows` | `dataset`, `count?` (1–20, default 5) | Rows read through the governed table path. Works for datasets outside the GraphQL schema too (HTTP and lakehouse sources). |
| `query_data_catalog` | `tableName?` | Enterprise data catalog assets, governance classifications, tags, and designated data stewards (`governance.catalog.assets`). |

`endpoints` contains, depending on the configuration: GraphQL, OData v4, OpenAPI, WebSQL (`WebSql:Enabled`), saved SQL queries (`SqlEndpoints:Enabled`), stored procedures (`SqlEndpoints:Procedures:Enabled`), DuckDB OLAP (`DuckDbOlap:Enabled`) and Arrow IPC export (`Arrow:Enabled`).

## 3. Security

- **Visibility** is the same as for the GraphQL `catalog` query and the MCP resources: a table is listed when the caller has an active Allow consent in its tenant and no unconditional Deny; only granted columns are shown, and relations only to datasets the caller can see. `GovernanceAdmin` and `ClusterAdmin` see the whole catalog; anonymous callers see nothing.
- **No enumeration oracle:** a dataset the caller may not see returns the same `NOT_FOUND` error as one that does not exist.
- **Same enforcement as the HTTP APIs:** `query_graphql` runs on the gateway's GraphQL executor, `sample_rows` on the governed table path. Consent, ReBAC, Casbin, row filters, Virtual Filters ([`F-GOV-09`](f-gov-09-virtual-filters.md)), and column masking apply with the caller's identity exactly as for `POST /graphql` and REST. Over the stdio transport there is no HTTP caller, so GraphQL queries are denied.
- **Guardrails:** like all MCP tools, the calls pass the prompt-injection guardrail, PII scrubbing, the token budget and the audit log.
  - Casbin ABAC (when `Casbin:Enabled`) runs on the requested table (`describe_dataset`, `sample_rows`), on every table a GraphQL document reads (root fields and nested relations, fragments expanded), or on `governance.catalog.datasets` (`list_datasets`, pure introspection).
  - A GraphQL document that is not one query on catalog fields is denied before execution (fail-closed).
  - The `dataset` and `query` arguments must occur exactly once (case-insensitive), so the guardrail always checks what is executed.
  - Truncated results return valid, parseable JSON payloads with `truncated: true`.
- **Four-eyes:** `sample_rows` and `query_graphql` on a four-eyes table need step-up approval ([F-AI-05](f-ai-05-hitl-step-up-approval.md)); `describe_dataset` returns no rows and needs none.
- **Complexity:** GraphQL queries are subject to the gateway's complexity budget; agents should always pass a small `first`.

## 4. Example

```json
{ "jsonrpc": "2.0", "id": 1, "method": "tools/call",
  "params": { "name": "query_graphql", "arguments": {
    "query": "query Q($type: String) { default_main_cranes(where: { crane_type: { eq: $type } }, orderBy: [{ tonnage: DESC }], first: 10) { serial_number tonnage } }",
    "variables": { "type": "Mobilkran" } } } }
```
