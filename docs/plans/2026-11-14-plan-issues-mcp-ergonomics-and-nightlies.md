# PLAN-OI-2026-11-14: open issues not yet planned (MCP result ergonomics, discovery artifacts, nightlies)

- Date: 2026-11-14. Base: `origin/main` @ `c4b0078a`. Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) rank 22.
- Scope: the 12 open issues that no plan referenced on 2026-11-14 (#703, #704, #705, #707, #709, #710, #712, #713, #714,
  #741, #745, #746), plus #711 part B. All other open issues are already planned; the index in section 5 lists where.
- Status: **Phase 2 draft.** Each work package needs Phase 3 before Phase 4. All of it falls under the feature freeze
  (PRD 5.1) except where a package is a bug fix; the owner decides freeze exceptions (open questions, section 6).

## 1. Shared design: the catalog tools' size control (#703, #704, #705, #709, #714)

One problem: the MCP catalog tools answer with more text than a client can use. `describe_dataset` for a 213-column table is
129,877 characters; one `list_datasets` page of 100 datasets is 172,646 characters; under the default
`Mcp:MaxTokensPerCall` (4096) both are replaced by a notice that names parameters the tool does not have. #702 (planned, PR #723)
removes empty members and is the first step. The remaining packages share one parameter vocabulary, so they are planned
together and built in this order:

| WP | Issue | Change | Acceptance (from the issue) |
|---|---|---|---|
| MCP-1 | #703 | `list_datasets` items and `search_catalog` hits carry a summary of `description` (first sentence or fixed length, marked when cut); `describe_dataset` keeps `description` and `longDescription`. | 100 datasets at most about 40,000 characters; 10 hits at most about 4,000. |
| MCP-2 | #704, #709 | `describe_dataset` takes `include` (`columns`, `keys`, `indexes`, `relations`, `examples`, `graphql`, `access`), `columns` (named) or `columnOffset`/`columnLimit`, and `relations` (term or target filter). `brief` stays as a shorthand. Parameters are declared in the tool schema because `PersistedToolValidator` rejects undeclared ones. | Keys, indexes and GraphQL block without columns at most about 2,000 characters; any single answer for the 213-column table under 20,000 characters. |
| MCP-3 | #705 | Budget truncation cuts at structure boundaries (whole datasets, whole columns), keeps `_meta.truncated` and names only parameters that exist, taken from MCP-2. | No catalog tool answers with only the notice. |
| MCP-4 | #714 | `longDescription` on a column, served only when `include` asks for it. Depends on MCP-2. | Absent by default; present with `include`. |

Constraints that apply to every package: no result may contain a dataset, column or relation the caller may not see
(the filters narrow an already authorized result, never widen it); `include` names are an allow-list, unknown values are
rejected with the same error as other invalid parameters; summaries are cut on a character boundary that never splits a
surrogate pair; the summary of a restricted scope's description is subject to the same governance decision as #571.
MCP-1 and MCP-2 touch `McpDatasetCatalog.cs` and `McpToolExecutionHandler.cs` like #702 and #711: build in sequence after #702 (#732) and #711 A (#725), which are
already on `main`, one PR at a time, to avoid conflicts in the same files.

## 2. Small catalog additions (#710, #712, #713, #711 part B)

| WP | Issue | Change | Notes |
|---|---|---|---|
| MCP-5 | #712 | `tag` parameter on `search_catalog`, same exact-match semantics as on `list_datasets`. | Smallest package; applied after the visibility filter. |
| MCP-6 | #710 | `sqlName` (three parts as WebSQL accepts it) on datasets and columns in `list_datasets`, `describe_dataset` and `search_catalog`; `describe_dataset` accepts it for lookup. | Must use the resolver that WebSQL uses (`SqlDataSourceResolver`), not a second spelling rule. Related to #666 and #668, which decide the physical name. Lookup goes through the same visibility check as the id. |
| MCP-7 | #711 B | `get_table_schema` adds `graphQlField` per table and column and the caller's column state. Part A is PLAN-OI-711. | Column state is the caller's own, never another principal's. |
| MCP-8 | #713 | Query guide for agents as an MCP resource, built from the enabled features like `McpAccessHints` (#579), so it names no switched-off path. | Static text plus feature switches; no catalog data, so no data-leak surface. Needs an owner decision on whether a resource is wanted (Q-MCP-2). |

## 3. Discovery artifacts: what a restricted scope receives (#707)

WP-SIMPL-41 PR 4 plans to serve uploaded samples and statistics "as uploaded". The statistics carry values (`MinValue`, `MaxValue`,
`TopValues`), while the value profile today withholds values of columns that are not clear for the caller. A token with a
restricted scope (`Governance:RestrictedScopes`, for example `Agent.Read`) would therefore see top values of columns masked
for exactly that scope, and an agent forwards them to a model provider.

**Decision recorded for PR 4 (proposal of the issue, adopted as the plan; needs the owner's confirmation, Q-MCP-1):** for
tokens whose scopes are restricted, value-bearing members (min, max, top values, samples) are served only for columns whose
state is `Clear` for that caller. Statistics without values (null ratio, distinct count, lengths, size class) are always served.

Binding for the PR 4 brief: fail closed (an unknown column state withholds the values); one code path decides, shared with
`ColumnValueProfileService`; mandatory Security-trait test: a canary in the top values of a column masked for the
restricted scope appears on no channel for that scope (MCP result, REST, export). PR 4 must not start before this is decided;
it also depends on WP-SIMPL-41 PR 3 as already planned. #571 (catalog text of masked columns) decides the same question for
descriptions; PR 4 and #571 should use one rule.

## 4. Nightly failures (#741, #745, #746)

These are issues the nightly workflows file themselves. They are CI health, not features, and are not frozen.

| Issue | Workflow | Failed jobs | Status / next step |
|---|---|---|---|
| #741 | Parity nightly (run 43577249271) | `parity-sqlite`, `parity-dialects` | Test execution failed after the #717/#731 changes; a local commit (`ea700208`, "repair parity-nightly fixture drift") exists but is not pushed. Push it through a PR, then re-run the nightly and close the issue only when the run is green. |
| #745 | SQL Compiler Gate nightly/cutover/G9 (run 43577693605) | `sql-compiler-gate-nightly`, `sql-compiler-gate-g7-aggregate` | Not analysed. Read `gh run view 43577693605 --log-failed`, decide drift in tests versus a real regression; a real compiler regression is a bug, file it with the Security trait if it concerns filter or mask handling. |
| #746 | Simulation nightly (run 43591120161) | `sim-parser-fuzz` | Only the SharpFuzz/AFL++ job fails; the API fuzz job is fixed by #737. The same local commit refreshes the fuzz seed corpus. Verify with a dispatch run after the PR. |

Rule for all three: an automatic issue is closed by the PR that makes the next scheduled or dispatched run green, never earlier.

## 5. Index: where every open issue is planned (65 open on 2026-11-14)

| Cluster | Issues | Plan |
|---|---|---|
| Catalog search | #624, #625, #626, #627, #628, #675, #676 | [`2026-11-13-plan-issues-catalog-search.md`](2026-11-13-plan-issues-catalog-search.md) |
| Catalog scale | #672, #673, #674, #677, #678, #679, #681 | [`2026-11-13-plan-issues-catalog-scale.md`](2026-11-13-plan-issues-catalog-scale.md) (#628c, #672, #677 back with the architect) |
| Consent correctness | #666, #667, #670, #671, #612 | [`2026-11-13-plan-issues-catalog-consent-correctness.md`](2026-11-13-plan-issues-catalog-consent-correctness.md) |
| WebSQL / MCP lookup | #668, #669, #682 | [`2026-11-13-plan-issues-websql-mcp.md`](2026-11-13-plan-issues-websql-mcp.md) |
| MCP result shape | #702, #711 A | PLAN-702 (merged as #732), PLAN-OI-711 (#725) |
| MCP size control, small additions | #703, #704, #705, #709, #710, #712, #713, #714, #711 B | this document, sections 1 and 2 |
| Discovery artifacts | #707, #571, #580 | this document, section 3; [`2026-11-05-plan-project-simplification.md`](2026-11-05-plan-project-simplification.md) |
| Authz / SQL hardening | #733, #730 | `WP-SIMPL-43` (#733 with WP-46 PR 6, #730 hardening issue) |
| Security (identity, transport, data paths) | #472, #473, #475, #476, #483, #487, #489, #491, #503, #508 | `2026-11-13-plan-issues-security-identity-transport.md`, `...-security-data-paths.md` |
| SQL engine and dialects | #478, #479, #486, #492 | [`...-plan-issues-sql-engine-dialects.md`](2026-11-13-plan-issues-sql-engine-dialects.md) (#492 deferred) |
| Tests and CI | #504, #505, #519, #520, #522, #523, #609 | `plan-issues-ci`, `plan-issues-code-quality` |
| Code quality | #613 | `plan-issues-code-quality` |
| Misc | #459, #485, #501 | `2026-11-05-plan-project-simplification.md` |
| Nightly failures | #741, #745, #746 | this document, section 4 |

(Cluster plan file names without a link are listed in the master plan rank 22 row; the owner decisions for them are in
[`2026-11-14-owner-decisions-sec-groups-1-9.md`](2026-11-14-owner-decisions-sec-groups-1-9.md).)

## 6. Open questions for the owner (defaults in bold; apply the default if unanswered)

| Id | Question | Default |
|---|---|---|
| Q-MCP-1 | Restricted scopes receive value-bearing discovery statistics only for `Clear` columns (#707)? | **Yes** |
| Q-MCP-2 | Serve the agent query guide as an MCP resource (#713), or only as a documentation topic? | **Documentation topic first; resource after MCP-1..4** |
| Q-MCP-3 | MCP-1..7 are new parameters and fields: freeze exception as a group, as for #702, or defer until the freeze ends? | **One group exception** (they are context-budget fixes for agents) |
| Q-CI-1 | Close #741/#745/#746 only when the next nightly is green? | **Yes** |

## 7. Order and dependencies

1. Nightly repair PR (#741, #746; #745 after analysis). No dependency.
2. MCP-1, then MCP-2, MCP-3, MCP-4 (shared vocabulary), MCP-5 anytime.
3. MCP-6 after #666 and #668 decide the physical name; MCP-7 after PLAN-OI-711 part A.
4. #707 decision before WP-SIMPL-41 PR 4 is briefed.
5. MCP-8 last.

Each package: Phase 3 review (the `include` allow-list and the visibility rule are the binding conditions to confirm),
then Phase 4 with filtered unit tests only; integration and parity run on GitHub Actions.
