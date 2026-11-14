# PLAN-OI-669: SQL Server synonyms are registered but always denied

- Date: 2026-11-14 · Base: `origin/main` @ `7a4df959` · Issue: [#669](https://github.com/themulle/autheris/issues/669)
- Phase 1 and 2: [`2026-11-13-plan-issues-websql-mcp.md`](2026-11-13-plan-issues-websql-mcp.md), section #669 (WP-OI-WEB-669, Q-WEB-2: a synonym is governed as an independent catalog object, like a view). This file records the delivery, where `main` differs from that plan, and the short Phase 3 review of the one code change.
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (plan registry).
- Freeze (PRD 5.1): regression tests plus a bug fix of a log reason. No new option, field, endpoint or switch. 15 net lines in `src/`.

## Where `main` differs from the cluster plan

- The plan expects a synonym without declared columns to be denied with the explicit reason of `GovernedCatalogProvider.LoadAsync` ("The table has no columns defined in the governance catalog."). On the WebSQL path that check is never reached: `GovernedSqlRewriter` denies the table earlier and folded three cases into one log reason, "no catalog metadata registered or table is inactive", the same text as for an unknown table. The clear log reason the issue asks for ("instead of 'unknown table'") was therefore missing.
- The log reason is therefore fixed in this PR (bug fix under the freeze) instead of a tests-only PR.
- An undeclared column is not rejected by the compiler: the table is sent to SQL Server as a derived table of its declared columns (`SELECT [item_code], [internal_note] FROM (SELECT [item_code] AS [item_code], [item_name] AS [item_name] FROM [dbo].[items]) AS [items] ...`, captured with an Extended Events session during the analysis), so SQL Server answers "Invalid column name" and the caller receives `SQL_POLICY_DENIED`. No value of the undeclared column is read. The test pins this outcome.

## Change

`GovernedSqlRewriter` logs one of three reasons: "no catalog metadata is registered", "the catalog entry is inactive", "the catalog entry declares no columns". The exception, its message and the error code for the caller are unchanged.

## Phase 3: security review

| Check | Result |
|---|---|
| Disclosure to the caller | None. The three cases throw the same `WebSqlTableDeniedException` with the same message as before; the test `Query_SynonymWithoutColumnsOrInactive_AnswersLikeAnUnregisteredTable` compares the messages. The reason exists only in the server log. |
| Log content | Table name as written by the caller (as before) and a fixed reason; no connection details, no catalog data. |
| Fail-closed | Unchanged: every case still throws before the governance policy is assembled; the order of the checks only selects the log text. |
| Synonym governance (Q-WEB-2) | A synonym is an independent catalog object: only declared columns are readable (`Query_SynonymColumnNotDeclared_DeniedByCatalog`), the base object is reachable only when registered itself (`Query_BaseTableNotRegistered_DirectAccessDenied`). Registering a synonym of a governed table creates a second governed object with its own grants; this is the owner decision Q-WEB-2, not a widening by this PR. |
| OD-25 | No source metadata is read; the synonym is resolved by SQL Server at query time. |

## Phase 4 and 5

- Tests: `tests/Autheris.Tests.Integration/WebSqlSqlServerSynonymTests.cs` (SQL Server in Testcontainers, two databases on one server). Red commit: 3 of 7 failed (the three log reasons); green after the change, 7 of 7.
- Delivery: UNDER REVIEW.
