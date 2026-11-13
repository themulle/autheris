# PLAN-702: MCP tool results omit null and empty members (#702)

- Date: 2026-11-13. Base: `origin/main` @ `a67797a6`. Issue: [#702](https://github.com/themulle/autheris/issues/702).
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md). Program rules: [`simplification/README.md`](simplification/README.md).
- Kind: bug fix (feature freeze, PRD 5.1). No parameter, field, endpoint, option or switch is added; whether the PR needs
  `freeze-exception` is an open question to the owner in the PR (Q-702-1).

## 1. Requirement (Phase 1)

A `describe_dataset` result spends 32 % to 48 % of its characters on members whose value is `null`, `[]` or `{}` (per
column about 300 characters: `valueProfile` with ten `null` members, `semantics` with only `null` members, `deprecation`,
`access`, `displayName`). For an AI client this is context budget without information. `valueProfile.emptyShare` is `0`
when no profile exists, which reads as "no empty values" although nothing is known.

Acceptance:

| Id | Criterion |
|---|---|
| R1 | Results of the MCP query executor and of the MCP tool execution handler contain no member whose value is `null`, and no member whose value is an object without members (`{}`), except the members in R2. |
| R2 | The lists a client iterates stay present: `datasets`, `columns`, `domains`, `relations` (also when empty; `domains` keeps today's `null` outside the overview mode), the members `field` and `dataset` of a relation, and `graphQlType` of a column (also when `null`). |
| R3 | Arrays are never omitted, also when empty (absence and emptiness must stay distinguishable for clients that index them). |
| R4 | Data values are untouched: a `null` cell of a row (`sample_rows`, `query_sql`, `query_dataset`) stays `null`. Dictionary entries are not members. |
| R5 | `describe_dataset` emits no `valueProfile` for a column when nothing is known about its values (no stored profile snapshot, no declared `empty_share`, no other profile member set). |
| R6 | `describe_dataset` for a 10-column table without profiles is at most about 4,500 characters. |
| R7 | Other paths (REST catalog endpoints, GraphQL catalog, other MCP tool families with their own serializer options) are unchanged. |

Rejected alternative: dropping every empty array (`tags: []`, `examples: []`). Some clients index those lists without a
presence check; the gain is small (a few members per result, not per column).

## 2. Architecture (Phase 2)

- New `McpResultJson` (`src/Autheris.Application/Mcp/Services/McpResultJson.cs`): the shared `JsonSerializerOptions` of
  MCP tool results: camelCase, relaxed escaping (as today), plus a contract modifier on the default resolver. For every
  member without a `[JsonIgnore]` attribute it sets `ShouldSerialize` to: the previous predicate (if any) and the value is
  not `null` and the value is not an empty object. An empty object is a value whose contract is an object with no member
  that would be written, or a dictionary without entries. Collections, strings and value types are never empty objects.
  The check runs at write time on the runtime type, so it also covers anonymous result types and `object`-typed members.
- `GatewayMcpQueryExecutor.CamelCaseJsonOptions` and `McpToolExecutionHandler.JsonOpts` use `McpResultJson.Options`.
- R2: `McpDatasetList.Domains` and `McpDatasetColumn.GraphQlType` carry `[JsonIgnore(Condition = Never)]`. The attribute
  is a no-op for the default options of the REST endpoints (R7).
- R5: `McpDatasetCatalog.DescribeDatasetAsync` drops a profile that carries no information before it is used for the
  semantics restriction and the column (`KnownProfileOrNull`): known means a stored snapshot exists, the column declares
  `empty_share`, or any other profile member is set. `ColumnValueProfile` and the profile service stay unchanged
  (`valueProfile` is removed altogether by WP-SIMPL-41 PR 4; no rebuild here).

Files: the new options class, the two serializer fields, two attributes in `McpDatasetModels.cs`, one helper in
`McpDatasetCatalog.cs`. Estimate: < 80 net `src/` lines.

## 3. Security (Phase 3)

- Omitting members never adds information: no new data reaches a caller. The profile is dropped only when it would show
  nothing; the semantics restriction (`RestrictSemantics`) receives `null` in exactly those cases, and with a `null`
  profile it already returns the descriptive meta only (fail closed, unchanged).
- Error payloads (`isError`, `error.code`, `error.message`) have no `null` members; the optional `graphQL` element is
  omitted when absent, as before it was `null`.
- The presence of an empty profile was never a reliable access signal for clients (a caller without `GRANTED` and without
  the catalog-viewer release gets no profile for any column); after this change a missing profile means "nothing known or
  not visible", never more access.
- Row data: dictionaries are serialized entry by entry and are not affected by `ShouldSerialize` (R4, tested).
- No new security trait test is needed; the existing MCP security suites run unchanged.

## 4. Tests (Phase 4, TDD)

`tests/Autheris.Tests.Unit/Mcp/McpResultEmptyMembersTests.cs`, red before the fix:

| Test | Criterion |
|---|---|
| `DescribeDataset_TenColumnsWithoutProfile_StaysWithinBudget_WithoutNullOrEmptyMembers` | R1, R2, R6 (real catalog and executor) |
| `DescribeDataset_ColumnWithoutKnownValues_HasNoValueProfile` | R5 |
| `DescribeDataset_ColumnWithStoredSnapshot_KeepsItsValueProfile` | R5 (no over-removal) |
| `ListDatasets_EmptyResult_KeepsTheListsPresent` | R2, R3 |
| `SampleRows_NullCell_StaysNull` | R4 |
| `QuerySql_NullCell_StaysNull_AndNullMembersAreOmitted` | R1, R4 (tool execution handler) |
| `Options_OmitNestedEmptyObjectsAndEmptyDictionaries_ButKeepArrays` | R1, R3 (options directly) |
| `Options_CyclicGraph_FailsWithTheSerializerError_NotAStackOverflow` (Security) | the emptiness check recurses; beyond `MaxDepth` a value counts as not empty, so a cyclic graph ends in the serializer's `JsonException` and never in a process-ending stack overflow |

Fixture change: `ScopeLevelTestKit` (used by `ColumnLevelParityTests` and `McpDatasetCatalogScopeLevelTests`) reads the
presence of a value profile as "not Deny" (Mask is a profile without values). Its columns had no known values, so after R5
they had no profile in any case. Both columns now declare `empty_share`, which keeps the profile channel meaningful; no
assertion of the parity or scope-level tests changes.

## 5. Owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-702-1 | Is this a bug fix under the freeze, or does the PR need `freeze-exception`? | Bug fix: no field, parameter, option or switch is added; the result shape only loses `null` and `{}` members |
