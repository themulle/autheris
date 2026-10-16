# F-AI-12: Official Hybrid MCP Gateway (Query Tools, Native Resources & Prompts)

## 1. Overview & Business Value

Autheris exposes an **Enterprise Model Context Protocol (MCP)** server built directly upon the official ModelContextProtocol SDK (`/mcp`). It equips autonomous AI agents (Claude Desktop, Cursor, Custom Agent Workflows) with governed tools and resources to safely explore schemas and query enterprise datasets without hallucinating tables or bypassing security policies.

### Hybrid Query Execution:
- **`query_sql`:** High-performance, read-only SQL queries validated by the ANTLR4 AST parser and executed against the Governed WebSQL engine.
- **`query_dataset`:** Structured queries against declared catalog tables with filtering, projection, and pagination.
- **`search_catalog` & `list_datasources`:** Semantic catalog discovery allowing agents to find relevant tables by domain, keyword, or business tag. `search_catalog` returns only datasets and columns the calling principal may see (see the catalog visibility rules in F-API-08). `list_datasources` exposes endpoint base URLs and authentication state and is therefore administrator-only: other callers receive a `Forbidden` answer (the `autheris://catalog/datasources` resource is empty for them, and `invoke_api` answers `403`). A `GovernanceAdmin` without tenant claim in `Gateway:Tenancy:Mode = Multi` is not an administrator and receives a `Forbidden` answer that names the option.
- **Caller class:** MCP resources, tools and `invoke_api` derive the caller class from the principal built from the session (never from the session role strings). A tenant-scoped `GovernanceAdmin` sees only its tenant's datasets, relations and synthetic sample rows; a forward-auth identity claiming `ClusterAdmin` is never a cluster administrator.
- **`get_my_permissions`:** Allows the AI agent to introspect what tables, columns, and rows the calling user is permitted to see before issuing queries.
- **`describe_api` & `invoke_api`:** Introspect and invoke declarative HTTP/REST endpoints with zero-leakage credentials.

### Native Resources & Prompts:
- **`autheris://catalog/tables`:** Direct JSON resource of all accessible tables.
- **`autheris://catalog/schema/{domain}/{table}`:** Detailed column definitions, data types, and sensitivity tags.
- **`autheris://governance/effective-permissions`:** Live permission snapshot of the authenticated session.
- **Prompts:** Pre-engineered, security-compliant agent prompts:
  - `explore_dataset`: Guided data discovery template with schema grounding and query safety guardrails.
  - `audit_access_compliance`: Compliance inspection template examining effective consents, delegations, and access logs.

### Federated Query Transport Opt-In (`WebSql:CrossSource:AllowedTransports`)

Federated queries that span several data sources are only available when `Gateway:WebSql:CrossSource:Enabled` is true, and only for transports that are explicitly listed in `Gateway:WebSql:CrossSource:AllowedTransports`. Valid names are the constants of `SqlTransports`: `WebSql`, `Trino`, `Rest`, `Mcp`, `ArrowExport`, `FlightSql`, `SqlEndpoints` and `AsyncJob`. When nothing is configured, only `WebSql` and `Trino` are allowed. Every other channel is rejected on federated queries (generic "not supported" response) unless its name is listed. MCP query tools use the transport name `Mcp`. An explicitly empty list allows no transport at all (fail-closed). A configured list replaces the default; it is not merged with it.

```json
{
  "Gateway": {
    "WebSql": {
      "CrossSource": {
        "Enabled": true,
        "AllowedTransports": [ "WebSql", "Trino", "Mcp" ]
      }
    }
  }
}
```

## 2. Protocol Integration

- **Transport:** Streamable HTTP with Server-Sent Events (SSE) and stateless request handling at `/mcp`.
- **Discovery Endpoint:** `/.well-known/oauth-protected-resource/mcp`.
- **Native resources:** rendered by `IMcpResourceProvider` (`src/Autheris.Application/Mcp/Services/McpResourceProvider.cs`); `src/Autheris.Api/Mcp/GatewayMcpServer.cs` only maps the result to the MCP SDK types.
- **Implementation:** `src/Autheris.Application/Mcp/Server/GatewayMcpServer.cs` and `src/Autheris.Application/Mcp/Tools/HybridMcpTools.cs`.
