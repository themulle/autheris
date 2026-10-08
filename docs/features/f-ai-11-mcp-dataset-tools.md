# F-AI-11: MCP Dataset Tools (Catalog Discovery for AI Agents)

**Status:** [Done]  
**Components:** [`McpDatasetCatalog.cs`](../../src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs), [`McpDatasetTools.cs`](../../src/Autheris.Application/Mcp/Services/McpDatasetTools.cs), [`GatewayMcpQueryExecutor.cs`](../../src/Autheris.GraphQL/Mcp/GatewayMcpQueryExecutor.cs)

---

## 1. Overview

An agent connects to `/mcp` and finds everything it may use with a few generic tools instead of one tool per table:

1. `list_datasets` – which datasets exist for me?
2. `describe_dataset` – what do they contain?
3. `sample_rows` – what does the data look like?

The tool list stays the same size however large the catalog is.

## 2. Tools

| Tool | Arguments | Returns |
| :--- | :--- | :--- |
| `list_datasets` | `search?`, `domain?` | Up to 200 datasets: `id` (`domain.schema.table`), description, sensitivity, number of visible columns. `total` and `truncated` tell whether more matched. `search` matches ids, descriptions and column names. |
| `describe_dataset` | `dataset` | Visible columns with type, description, `sensitive` and `primaryKey`; table description, sensitivity, `requiresApproval` (four-eyes) and the curated golden queries of the table as `examples`. |
| `sample_rows` | `dataset`, `count?` (1–20, default 5) | Rows read through the governed table path: consent, ReBAC, Casbin, row filters and column masking apply exactly as for REST and GraphQL. |

## 3. Security

- **Visibility** is the same as for the GraphQL `catalog` query and the MCP resources: a table is listed when the caller has an active Allow consent in its tenant and no unconditional Deny; only granted columns are shown. `GovernanceAdmin` and `ClusterAdmin` see the whole catalog; anonymous callers see nothing.
- **No enumeration oracle:** a dataset the caller may not see returns the same `NOT_FOUND` error as one that does not exist.
- **Guardrails:** like all MCP tools, the calls pass the prompt-injection guardrail, PII scrubbing, the token budget and the audit log. Casbin ABAC runs on the requested table (`describe_dataset`, `sample_rows`) or on `governance.catalog.datasets` (`list_datasets`). The `dataset` argument must occur exactly once (case-insensitive); otherwise the call is denied, so ABAC always checks the table that is read.
- **Four-eyes:** `sample_rows` on a four-eyes table needs step-up approval ([F-AI-05](f-ai-05-hitl-step-up-approval.md)); `describe_dataset` returns no rows and needs none.

## 4. Example

```json
{ "jsonrpc": "2.0", "id": 1, "method": "tools/call",
  "params": { "name": "describe_dataset", "arguments": { "dataset": "sales.public.orders" } } }
```
