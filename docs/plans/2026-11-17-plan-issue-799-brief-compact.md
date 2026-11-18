# PLAN-OI-799: describe_dataset brief and list_datasets compact save little

- Date: 2026-11-17 · Issue: [#799](https://github.com/themulle/autheris/issues/799)
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (entry maintained by the orchestrator).
- Branch: `fix/799-brief-compact`, stacked on `fix/805-807-describe-keys-filters` (same describe code path, and brief carries the
  keys and required filters restored there).

## Phase 1: Requirement

- **Problem:** measured on v1.8.4, `describe_dataset(brief: true)` on a 287-column dataset saves 9.6 % (91,043 to 82,315 characters)
  and `list_datasets(format: "compact")` saves 1.4 % (51,354 to 50,641). Brief columns still carry `description`, `sensitive`,
  `primaryKey: false`, `graphQlType: null`, `classifications`, `clearRequestable`; compact entries are identical to full entries.
- **REQ-799-1 (brief):** each column is a signature `{name, type, access, primaryKey}` (`primaryKey` only on key columns, `access`
  only when known). The answer keeps id, display name, short description, sensitivity, approval flag, access status, column total,
  size and view, and adds what a correct query needs: `primaryKeyOrder`, `indexes`, `requiredFilterColumns(Groups)` (#805/#807).
  It drops long description, examples, GraphQL block, other access paths, owners, tags and the per-column prose. `columns` /
  `columnOffset` / `columnLimit` / `include` (#704) keep working (narrowing runs before the brief projection).
- **REQ-799-2 (compact):** a list entry is `{id, displayName (only when it differs from the table name), description (first
  sentence, at most 120 characters), sensitivity, accessStatus, kind (only when not "table")}`; `graphQlField`, `columnCount`, `tags`,
  `size` and the endpoints are dropped. Paging members and `domains` stay.
- **REQ-799-3:** the tool descriptions state exactly this.
- **Anti-patterns rejected:** dropping `access` or the key from brief (an agent then writes queries that fail on masked or required
  columns); a third verbosity switch (the existing `brief`/`format` arguments are sufficient).

## Phase 2: Design

- `McpCatalogWire` (Application, MCP services): pure projections `Brief(McpDatasetDescription)` and `Compact(McpDatasetList)` to the
  wire records `McpBriefDatasetDescription` / `McpColumnSignature` and `McpCompactDatasetList` / `McpCompactDatasetSummary`;
  `ForTool(result, compactList)` is the one hook the executor calls after `McpDescribeNarrowing.Apply`.
- Both describe paths (discovery and legacy) mark the brief answer `View: "Brief"` and fill key order, indexes and required filters
  in brief too. The internal `McpDatasetDescription` / `McpDatasetList` models stay unchanged for other consumers.
- `GatewayMcpQueryExecutor` serializes `McpCatalogWire.ForTool(result, format == "compact")`.

## Phase 3: Security

- **SEC-799-1:** the projections only remove members or shorten text of an already authorized answer (no new data source, no
  widening); keys, indexes and required filters come from the caller-specific projection of #805/#807 (SEC-805-1, SEC-807-1).
- **SEC-799-2:** brief keeps `access` per column so a masked column is never presented as clear; denied columns stay absent.
- **SEC-799-3:** the guardrail treats brief as value-free (`IsValueFreeDescribeAsync`); brief still carries no value profiles,
  samples or examples, so that classification stays true.

## Phase 4: Test plan

`Issue799BriefAndCompactTests`: wide-table fixture (287 columns, descriptions, a classification per column) measured on the wire
shape (`ForTool` + `McpResultJson`); brief keeps signatures, key order with direction, index, required filter; brief + `columns`
narrowing; masked key column (Security); compact list of 100 datasets with two-sentence descriptions; compact keeps `kind` for
procedures and a differing display name. Legacy-path brief carries the key order.

## Measurement (unit fixture, serialized MCP tool result with `McpResultJson`)

| Call | Default | With option | Saving |
|---|---|---|---|
| `describe_dataset`, 287 columns with description and classification | 92,541 | `brief: true`: 15,757 | 83.0 % |
| `list_datasets`, 100 datasets with two-sentence descriptions | 38,624 | `format: "compact"`: 18,924 | 51.0 % |

## Phase 5: Review follow-up

- The legacy brief also projects the required filters onto its listed columns (SEC-807-1).
- Legacy access label = the caller's effective level (an explicit Clear on a sensitive column is `clear`; the administrator view keeps a
  sensitive column `mask`), and the key/index projection uses the same labels, so a key column never shows `access: "mask"`.
- `Summarize` ends a sentence only before an upper-case letter and, after a period, only when the word before it is longer than two
  characters ("z. B.", "Nr. 184" no longer cut); a long sentence is cut at the last word boundary.
- The `list_datasets` tool description and the access guidance say what `compact` and `brief` leave out.
- The REST `?brief=true` detail also carries `primaryKeyDirections`, `indexes` and `requiredFilterColumns(Groups)`.
- Executor-level tests prove the wire shapes are applied by `GatewayMcpQueryExecutor`.
