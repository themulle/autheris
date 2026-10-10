# F-AI-12: Official Hybrid MCP Gateway (Query Tools, Native Resources & Prompts)

## 1. Overview & Business Value

Autheris exposes an **Enterprise Model Context Protocol (MCP)** server built directly upon the official ModelContextProtocol SDK (`/mcp`). It equips autonomous AI agents (Claude Desktop, Cursor, Custom Agent Workflows) with governed tools and resources to safely explore schemas and query enterprise datasets without hallucinating tables or bypassing security policies.

### Hybrid Query Execution:
- **`query_sql`:** High-performance, read-only SQL queries validated by the ANTLR4 AST parser and executed against the Governed WebSQL engine.
- **`query_dataset`:** Structured queries against declared catalog tables with filtering, projection, and pagination.
- **`search_catalog` & `list_datasources`:** Semantic catalog discovery allowing agents to find relevant tables by domain, keyword, or business tag.
- **`get_my_permissions`:** Allows the AI agent to introspect what tables, columns, and rows the calling user is permitted to see before issuing queries.
- **`describe_api` & `invoke_api`:** Introspect and invoke declarative HTTP/REST endpoints with zero-leakage credentials.

### Native Resources & Prompts:
- **`autheris://catalog/tables`:** Direct JSON resource of all accessible tables.
- **`autheris://catalog/schema/{domain}/{table}`:** Detailed column definitions, data types, and sensitivity tags.
- **`autheris://governance/effective-permissions`:** Live permission snapshot of the authenticated session.
- **Prompts:** Pre-engineered, security-compliant agent prompts:
  - `explore_dataset`: Guided data discovery template with schema grounding and query safety guardrails.
  - `audit_access_compliance`: Compliance inspection template examining effective consents, delegations, and access logs.

## 2. Protocol Integration

- **Transport:** Streamable HTTP with Server-Sent Events (SSE) and stateless request handling at `/mcp`.
- **Discovery Endpoint:** `/.well-known/oauth-protected-resource/mcp`.
- **Implementation:** `src/Autheris.Application/Mcp/Server/GatewayMcpServer.cs` and `src/Autheris.Application/Mcp/Tools/HybridMcpTools.cs`.
