# PLAN-OI-820: describe_dataset is shortened at column boundaries when it exceeds the token budget

- Date: 2026-11-17 · Issue: [#820](https://github.com/themulle/autheris/issues/820) (related: #705, #704, #799)
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (entry maintained by the orchestrator).
- Branch: `fix/820-describe-token-budget`, stacked on the #799 PR (`fix/799-brief-compact`, itself on `fix/805-807-describe-keys-filters`):
  the shortening must keep the keys, indexes and required filters restored there and must also work on the brief wire shape.

## Phase 1: Requirement

- **Problem:** `McpBudgetShortening.AppliesTo` (`src/Autheris.Application/Mcp/Services/McpBudgetShortening.cs:15`) covers only
  `list_datasets` and `search_catalog`. Every other result above `Mcp:MaxTokensPerCall` is replaced whole by the
  `TOKEN_BUDGET_EXCEEDED` notice (`AiDataGuardrailService.Helpers.cs:330`, `else if` branch). With the default 4,096 tokens
  (characters / 4) a `describe_dataset` of a table with more than about 53 columns returns no columns, keys or relations.
- **Correction to the report:** `describe_dataset` does have column selection since #704 (`columns`, `columnOffset`, `columnLimit`,
  `include`); the notice just does not name it, and the agent gets no hint where to continue.
- **REQ-820-1:** an over-budget `describe_dataset` answer keeps every dataset-level member unchanged (id, names, descriptions, sensitivity,
  access status, key order, indexes, required filters, GraphQL block with relations, examples, other access paths, size) and keeps the
  longest prefix of `columns` (whole columns, catalog order) that fits.
- **REQ-820-2:** the shortened answer carries `columnTotal` and `_meta` `{truncated, reason: TOKEN_BUDGET_EXCEEDED, maxTokens, returned,
  omitted, nextColumnOffset, note}`. `nextColumnOffset` = requested `columnOffset` (default 0) + returned; the note names only existing
  parameters: call again with the same arguments and `columnOffset` = `nextColumnOffset`, or select columns by name with `columns`; on a
  full answer it also names `brief: true`. With a `columns` name selection (names win over paging) there is no `nextColumnOffset`; the
  note says to request the columns not returned by name.
- **REQ-820-3:** under-budget answers are byte-identical to today. If not even the answer without columns fits, the existing notice stays.
- **Decision (no automatic brief fallback):** #705 shortens the list without switching to `compact`; switching the shape silently would
  give the agent an answer it did not ask for and make pages of one table differ in shape. The note offers `brief: true` instead.
- **Decision (no new parameter):** `columnOffset` / `columnLimit` from #704 already are the deterministic column cursor (catalog column
  order over the caller's visible columns). Only their schema description gains the hint to `_meta.nextColumnOffset`.
- **Anti-patterns rejected:** cutting inside a column object or a string (breaks the JSON contract); dropping keys or relations first
  (an agent then writes wrong queries); raising the budget per tool.

## Phase 2: Design

- `McpBudgetShortening.AppliesTo` adds `describe_dataset`. A new overload `TryShorten(tool, json, argumentsJson, maxChars, maxTokens, out)`
  dispatches the describe result to `TryShortenColumns`: binary search over the number of kept `columns` (as for datasets), the envelope
  is the parsed answer itself, so the brief shape (`McpCatalogWire.Brief`) is handled without a second code path.
- The requested `columnOffset` and the presence of `columns` are read from the call arguments (already validated by the executor);
  unparsable arguments mean no shortening (notice).
- `ApplyTokenBudget` gains an optional `argumentsJson` that the dataset-tool call site passes (`AiDataGuardrailService.cs`). No change in
  `GatewayMcpQueryExecutor` or the wire models.
- The shortened JSON is written with the relaxed encoder of `McpResultJson`, so non-ASCII text keeps the size and form of the
  unshortened answer (this also applies to the shortened list).

## Phase 3: Security

- **SEC-820-1:** the shortening only removes trailing columns from the already authorized, scrubbed and inspected answer; it adds no
  data except counts that the answer already implies (`columnTotal` is the caller's visible column count, set by #704 paging too).
- **SEC-820-2:** the continuation is an ordinary new `describe_dataset` call: authorization, column visibility, masking and the
  narrowing run again; `columnOffset` indexes the caller's visible columns only, so hidden columns never appear on any page and never
  shift the cursor in a way that reveals them.
- **SEC-820-3:** fail closed: when the answer without any column does not fit, or the answer or arguments cannot be parsed, the
  existing `TOKEN_BUDGET_EXCEEDED` notice replaces the result.

## Phase 4: Test plan

`Issue820DescribeTokenBudgetTests` (wire shape from the real catalog + `McpDescribeNarrowing` + `McpCatalogWire.ForTool`):
300-column table under the default 4,096 tokens keeps dataset-level members, a column prefix and the marker; paging with
`nextColumnOffset` returns all columns without overlap or gap; brief + budget; hidden (denied) columns on no page (Security);
under-budget answer unchanged; `columns` name selection has no `nextColumnOffset`; budget below the envelope falls back; guardrail
end to end (`ExecuteToolWithGuardrailAsync` with a stub executor) shortens instead of replacing.

## Out of scope (same symptom, different mechanism)

- `get_table_schema`: a batch of tables (`tables[].columns`) without column paging; an over-budget answer is still replaced. Needs a
  column cursor per table; workaround: fewer datasets per call or `describe_dataset` with `columnOffset`.
- `describe_my_access`: column name lists only (about 25 characters per column), over budget only beyond about 600 columns; no paging.
