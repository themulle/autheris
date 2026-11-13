# PLAN-OI-706: MCP tool annotations (readOnlyHint, destructiveHint)

- Date: 2026-11-13 · Base: `origin/main` @ `f2afc418` · Issue: [#706](https://github.com/themulle/autheris/issues/706)
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (plan register).
- Freeze (PRD 5.1): adds the standard MCP `annotations` member to every entry of `tools/list`. No parameter, endpoint,
  option or switch; no ratchet baseline grows. It is still a new protocol field, so the PR asks the owner for
  `freeze-exception` or for a recorded decision in the PR description.
- Size: about 120 net lines in `src/`.

## Phase 1: Requirement

- **Problem:** `tools/list` returns `name`, `description` and `inputSchema` only (`GatewayMcpServer.ListToolsAsync`,
  `McpProtocolHandler.HandleToolsListAsync`). Clients and brokers that admit tools by `readOnlyHint` (MCP specification,
  `ToolAnnotations`) have nothing to decide on; the list of enabled tools is the only boundary.
- **REQ-706-1:** every tool in `tools/list` carries `annotations.readOnlyHint` on both MCP paths (SDK server and the
  JSON-RPC protocol handler).
- **REQ-706-2:** a tool that can change state reports `readOnlyHint: false` and an explicit `destructiveHint`
  (`true` when it can overwrite or remove existing state, `false` when it only adds).
- **REQ-706-3 (security):** no tool that can change state for the calling session reports `readOnlyHint: true`. A tool
  name without a classification is treated as state-changing and destructive (fail-safe), and a test fails CI when a
  built-in tool has no classification.
- **Out of scope:** `idempotentHint`, `openWorldHint` and `title` stay unset (spec defaults). The planned
  `request_access` tool (WP-SIMPL-41 PR 5) is classified when it is added; the completeness test forces that decision.

## Phase 2: Design

One closed, name-keyed effect table next to the group table (`McpToolGroups`), so no tool definition changes:
`src/Autheris.Application/Mcp/Services/McpToolAnnotations.cs`.

| Effect | Tools | Hints |
|---|---|---|
| Read | catalog, access, API description, documentation reads, `query_graphql` (queries only, mutations rejected), `query_dataset`, `sample_rows`, `query_metrics`, `simulate_query`, `query_data_catalog`, `get_golden_queries`, demo queries, `admin_resolve_principal` | `readOnlyHint: true` |
| Adds | `admin_plan_access`, `admin_plan_row_filter_evaluation` (store a pending plan), `admin_register_datasource` (new datasource, inactive) | `readOnlyHint: false`, `destructiveHint: false` |
| Overwrites | `admin_apply_access`, `admin_apply_row_filter_evaluation`, `admin_set_dataset_state`, `admin_set_dataset_catalog`, `deposit_documentation` (deposits or updates) | `readOnlyHint: false`, `destructiveHint: true` |
| SQL statement | `query_sql`, `invoke_api` (`POST /api/v1/statement` branch): DML runs when `WebSql:AllowDml` is on and the token is not read-only | read-only session or DML off: `readOnlyHint: true`; otherwise `false`, `destructiveHint: true` |
| Procedure | `call_procedure`: write procedures run unless the session is read-only | read-only session: `readOnlyHint: true`; otherwise `false`, `destructiveHint: true` |
| Unknown | dynamic tools (GraphQL discovery, `Mcp:AllowedOperations`) and any unclassified name | `readOnlyHint: false`, `destructiveHint: true` |

- `McpToolAnnotations.For(toolName, readOnlySession, sqlDmlAllowed)` returns the hints; both `tools/list` paths call it
  with `McpSessionContext.IsReadOnly` and `GatewayOptions.IsWebSqlDmlAllowed`.
- The SQL and procedure rows depend on the session only in the direction the gateway already enforces: the rewriter
  rejects DML when DML is off or the principal is read-only (`GovernedSqlRewriter`), and `call_procedure` rejects write
  procedures for read-only callers (`McpToolExecutionHandler`). A writable session without the write scope still sees
  `readOnlyHint: false` (conservative).

## Phase 3: Security review

| Check | Result |
|---|---|
| Fail-closed | Hints are advisory and never authorize anything; enforcement is unchanged. Wrong hints can only matter in one direction (a client admitting a write tool as read-only), so unknown and unclassified names are state-changing and destructive. |
| Truthfulness | Every "Read" entry was checked against its handler: `query_graphql` rejects non-query operations, catalog and access tools emit metadata only, admin plan tools are excluded because they persist a plan. Session-dependent rows only claim read-only where the gateway rejects the write path for that session. |
| Disclosure | The annotations reveal no data and no configuration beyond what a client can already observe (a read-only session already gets DML rejected). Admin tools stay hidden from non-cluster-admins as before. |
| Tests | `McpToolAnnotationsTests` (`[Trait("Category","Security")]`): every built-in name is classified; no state-changing tool reports `readOnlyHint: true` under any session and DML combination; unknown names are destructive; both `tools/list` paths emit annotations for every tool. |

## Phase 4 and 5

- Branch `fix/706-mcp-tool-annotations`; red tests first, then the table and the two call sites.
