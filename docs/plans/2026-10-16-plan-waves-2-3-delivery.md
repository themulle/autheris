# Delivery Tracker: Priority Waves 2 and 3

**Document ID:** `PLAN-DELIVERY-W23`
**Date:** 2026-10-16
**Status:** IN PROGRESS (planning sequence not started)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md) (section 0)
**Author / Lead:** Delivery Project Manager & Workflow Orchestrator

---

## 1. Owner instruction and gate rules

Owner instruction (2026-10-16): for Priority Waves 2 and 3, first complete **all** planning (Phase 2 architect plans and Phase 3 security reviews) for every item, then have **one** developer implement everything. Nothing is parallelised: one agent at a time, in sequence, across phases and across items.

Gate rules:

1. **G-PLAN:** Phase 4 (implementation) of any wave 2/3 item starts only after every Phase 2 plan (section 3) and every Phase 3 review (section 3) of waves 2 and 3 is written and carries a verdict. This includes items that already have a plan.
2. **G-ITEM:** Each implementation item (section 4) passes Phase 5 (code review, loop back until clean) and Phase 6 (feature docs, `docs/features/README.md`, master plan update) before the next item starts.
3. **G-ONE:** At any time exactly one agent works. Planning agents do not run in parallel; the developer does not run in parallel with a reviewer.
4. **G-BUILD:** The lead builds and tests once per item (Release build, warnings as errors, as in CI); the developer only implements (see memory rule on parallel agents; here the developer is single, so the lead compiles after each item).
5. **G-SHARED:** Baseline DDL (`0001` x3 engines plus shape snapshots), `DatabaseOptions.cs`, `ConsentModels.cs`, `CrossSourcePlanner.cs`, `FederatedStagingService.cs` and `CatalogSchemaModel.cs` are touched by several items. Sequential merging is the conflict strategy; every branch is created from the `main` that contains the previous item.
6. **G-PLAN-REBASE:** Each Phase 2 plan verifies code facts against current `main` (waves 1 merged: PRs #139-#142), not against the commit its PRD cites.
7. Owner decisions of 2026-10-16 (master plan section 0, table "Owner decisions 2026-10-16") are fixed inputs. Every other open question defaults to the PRD recommendation; the architect records the default taken.

## 2. Verification result: which gates are passed (read from the documents)

| Document | What it contains |
|---|---|
| [`2026-10-15-plan-issue-107-row-filter-key-set-cache.md`](2026-10-15-plan-issue-107-row-filter-key-set-cache.md) | Phase 2 and Phase 3 (section 12, approved with conditions `SEC-RF107-01..16`) complete |
| [`2026-10-16-plan-cross-source-all-consumers.md`](2026-10-16-plan-cross-source-all-consumers.md) (`PLAN-FED-01`) | Phase 2 and Phase 3 for `WP-FED-01..10` (section 12, `SEC-FED-01..18`) and for `WP-FED-11..14` (section 12.6, `SEC-FED-19..34`), both approved with conditions. Section 4.12 (`WP-FED-15`, #131) is only a **brief** Phase 2 design; section 12.7 is a **placeholder, not a review** |
| [`2026-10-16-plan-bl-06-open-telemetry.md`](2026-10-16-plan-bl-06-open-telemetry.md) | Phase 2 and Phase 3 (section 10, approved with conditions `SEC-OT06-01..21`) complete |
| [`2026-10-16-req-issues-111-115-catalog-access-analytics.md`](2026-10-16-req-issues-111-115-catalog-access-analytics.md) | PRD only; no plan, no review exists for #111, #112, #113, #114, #115 |
| [`2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md`](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md) | PRD only; section 6 states Phase 3 pending for all three; no plan exists for #128, #129, #130 |

Findings that matter for planning:

- #117 and #118 (dependencies of #111, #112, #113 A) are merged (PR #119). No dependency blocker remains.
- `PLAN-FED-01` pre-dates the merged wave 1: PR #140 (`PLAN-SEC-OLAP-01`) took over parts of `WP-FED-01`/`WP-FED-05` (gate with transport `Olap`, `AllowedTransports` validation, dead options removed) and PR #139 took over the schema-gating part of `WP-FED-08`. INC-1..INC-5 therefore need a short delta plan against current `main` before implementation (step P-10 below). This is the only "re-plan" item among the already planned ones.
- #107: contracts, settings and DDL are merged (PR #108); WP-A/B/C implementation remains.
- #129 realises and replaces `WP-SC-04` (owner decision Q-129-1); the scale plan keeps only a pointer. #129 must respect the `SEC-RF107-*` conditions where it touches the shared row-filter placeholder mechanism.
- Open question Q-114-4 depends on T-114-1 (do denied audit entries carry caller and category); the #114 architect answers it by code reading, no owner decision needed.

## 3. Status table

Legend: Phase 1 = PRD; P2 = architect plan; P3 = security review. "To write" means the planning sequence (section 5) produces it.

| Rank | Item | PRD | Phase 2 plan | Phase 3 review | Implementation branch / PR | Status |
|---:|---|---|---|---|---|---|
| 2.1 | #107 row filter key-set cache (WP-A/B/C) | [`REQ-GOV-107`](2026-10-15-req-issue-107-row-filter-key-set-cache.md) | done: [`PLAN-GOV-107`](2026-10-15-plan-issue-107-row-filter-key-set-cache.md) | done, approved with conditions `SEC-RF107-01..16` (section 12 of the plan) | contracts: PR #108 merged; WP-A/B/C: not started | Planning complete; implementation pending (I-1..I-3) |
| 2.2 | #115 read-path switches | [`REQ-SEC-115`](2026-10-16-req-issues-111-115-catalog-access-analytics.md) | to write: `2026-10-16-plan-issue-115-read-path-switches.md` | to write (same file) | not started | Planning pending |
| 2.3 | #128 list parameters | [`REQ-SQL-128`](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md) | to write: `2026-10-16-plan-issue-128-list-parameters.md` | to write (same file) | not started | Planning pending |
| 2.4 | #130 governed time zone conversion | `REQ-SQL-130` (same PRD) | to write: `2026-10-16-plan-issue-130-convert-timezone.md` | to write (same file) | not started | Planning pending |
| 2.5 | #111 dataset tags and catalog visibility | `REQ-GOV-111` (bundle 111-115 PRD) | to write: `2026-10-16-plan-issue-111-dataset-tags-catalog-visibility.md` | to write (same file) | not started | Planning pending |
| 2.6 | #112 typed aggregations in GraphQL | `REQ-DATA-112` (bundle 111-115 PRD) | to write: `2026-10-16-plan-issue-112-typed-aggregations-graphql.md` | to write (same file) | not started | Planning pending |
| 3.1 | REQ-FED-01 INC-6: #122 / #124 / #123 (`WP-FED-11..14`) | [`REQ-FED-01`](2026-10-16-req-cross-source-all-consumers.md) section 7.1 | done: `PLAN-FED-01` section 4.11 (delta check in P-10) | done: section 12.6, approved with conditions `SEC-FED-19..34` | not started | Planning complete; P-10 delta check pending |
| 3.2 | REQ-FED-01 INC-1..INC-5 (`WP-FED-01..10`) | `REQ-FED-01` | done: `PLAN-FED-01`; stale after PR #139/#140, delta plan to write: `2026-10-16-plan-fed-01-delta-after-wave-1.md` | done: section 12 (`SEC-FED-01..18`); delta review to write (same delta file) | not started | Planning complete; delta plan and review pending |
| 3.3 | #113 access requests, increment A then B | `REQ-GOV-113` (bundle 111-115 PRD) | to write: `2026-10-16-plan-issue-113-self-service-access-requests.md` (one plan, two increments) | to write (same file) | not started | Planning pending |
| 3.4 | #114 consumers per calling application | `REQ-OBS-114` (bundle 111-115 PRD) | to write: `2026-10-16-plan-issue-114-consumers-per-application.md` | to write (same file) | not started | Planning pending |
| 3.5 | #129 row filters on caller attributes (replaces `WP-SC-04`) | `REQ-GOV-129` (128-130 PRD) | to write: `2026-10-16-plan-issue-129-caller-attribute-row-filters.md` | to write (same file; dedicated review, required by the PRD) | not started | Planning pending |
| 3.6 | #131 bound parameters in federated queries (`WP-FED-15`) | `REQ-FED-01` section 7.2 (part #131) | brief only: `PLAN-FED-01` section 4.12; detailed plan to write: `2026-10-16-plan-issue-131-federated-bound-parameters.md` | placeholder only (12.7); review to write (same file) | not started | Planning pending |
| 3.7 | BL-06 OpenTelemetry | [`REQ-OPS-BL06`](2026-10-16-req-bl-06-open-telemetry.md) | done: [`PLAN-OPS-BL06`](2026-10-16-plan-bl-06-open-telemetry.md) | done: section 10, approved with conditions `SEC-OT06-01..21` | not started | Planning complete; implementation pending |

Out of scope here: `WP-FED-16` (#132) is rank 4.3 (wave 4); the #131 plan must not depend on it.

### 3.1 One plan per issue, or combined

- One plan per issue for #115, #111, #112, #114, #128, #129, #130, #131: the issues have separate files, separate security conditions and separate PRs.
- #113 is **one** plan with two increments (A allowlisted request/withdraw, B approver-chosen filters): the design of the request record and the allowlist is shared; the plan states a separate PR and a separate review section per increment.
- #131 gets its own plan file rather than extending `PLAN-FED-01` further (that file is already very large); `PLAN-FED-01` 4.12 and 12.7 get a pointer sentence from the new plan (the architect adds it).
- #122/#123/#124 stay one planned unit (`WP-FED-11..14`, already reviewed together).

## 4. Implementation sequence (Phase 4 to 6, strictly one at a time, only after G-PLAN)

Each line is one branch and one PR unless noted. "Shared" lists files that make the order matter.

| Step | Item (package) | Depends on | Shared files / conflict note | Size |
|---:|---|---|---|:---:|
| I-1 | #107 WP-A: settings, persistence, admin (contracts merged) | PR #108 | baseline DDL x3 + shapes, repositories, `DatabaseOptions.cs`, `ConsentModels.cs` | M |
| I-2 | #107 WP-B: engine key-query compiler and substitution | I-1 | `src/TrinoSqlEngine/Governance`, `GovernancePolicyFactory` | M |
| I-3 | #107 WP-C: runtime, cache, epochs, integration | I-2 | epochs, `RlsOptions.cs` | M |
| I-4 | #115 read-path switches | none | `RestApiOptions.cs`, MCP tool registry, `DatasetDataEndpoints.cs` | S |
| I-5 | #128 list parameters | none | `WebSqlParameterValues.cs`, `SqlEndpointLoader.cs`, `SqlEmitterContext.cs`, `CompiledSql.cs` | M |
| I-6 | #130 `convert_timezone` | none (interacts with #123 function scope later) | `SqlFunctionAllowlists.cs`, dialect generators, `DialectCapabilityTable.cs` | S-M |
| I-7 | #111 dataset tags and catalog visibility | #117/#118 (merged) | baseline DDL x3 + shapes, repositories, `CatalogVisibility.cs`, MCP catalog | M |
| I-8 | #112 typed aggregations and typed `queryMetrics` | I-7 (catalog persistence pattern; PRD says independent, order chosen to avoid DDL conflicts) | `CatalogSchemaModel.cs`, baseline DDL x3 (semantic manifest) | M |
| I-9 | `PLAN-FED-01` INC-1: `WP-FED-01`, `WP-FED-02` (gate remainder after PR #140, error contract) | I-4 (options), delta plan | `RestApiOptions.cs`, error mappers across protocols | M |
| I-10 | INC-2a: `WP-FED-03` SQL federation hardening | I-9 | `CrossSourcePlanner.cs`, `FederatedStagingService.cs`, `GovernedSqlExecutionService.cs` | M |
| I-11 | INC-2b: `WP-FED-04` staging and results at rest | I-9 | `DuckDbOlapEngine.cs`, `AsyncQueryJobBackgroundWorker.cs` | M |
| I-12 | INC-2c: `WP-FED-05` OLAP facade | I-10, I-11 | `DuckDbOlapEndpoints.cs`, `OlapStagingService.cs` | M |
| I-13 | INC-3a: `WP-FED-06` governed source reader and resolver rewrite | I-9, I-10 | `src/Autheris.Application/Data/Relations/*` | L |
| I-14 | INC-6a: `WP-FED-11` (#122 pushdown) | I-10 | `CrossSourcePlanner.cs` | L |
| I-15 | INC-6b: `WP-FED-12` options, hints, audit, analysis | I-14 | baseline DDL x3 + shapes, `TableModels.cs`, `QueryAnalysisService.Sql.cs` | L |
| I-16 | INC-6c: `WP-FED-14` (#124 key-set filters; merges before 13) | I-13, I-15 | `FederatedStagingService.cs`, `KeyPredicate.cs` | M |
| I-17 | INC-6d: `WP-FED-13` (#123 same-source subtrees) | I-15, I-16 | `CrossSourcePlanner.cs`, `GovernedSqlExecutionService.cs` | L |
| I-18 | INC-3b: `WP-FED-07` relation declaration and admin | none (placed here to keep DDL changes serial) | baseline DDL x3 + shapes, `TableModels.cs`, repositories | M |
| I-19 | INC-3c: `WP-FED-08` GraphQL splitter and schema gating | I-13, I-18 | `CatalogSchemaModel.cs`, `CatalogGraphQlTypeModule.cs` | L |
| I-20 | INC-4: `WP-FED-09` HTTP sources and relation filters in GraphQL | I-10, I-19 | `FederatedStagingService.cs`, `CatalogSchemaModel.cs` | M |
| I-21 | INC-5: `WP-FED-10` remaining integration and parity tests, Phase 6 doc corrections (#125) | I-20 | `tests/Autheris.Tests.Integration/Federation/*` | M |
| I-22 | #113 increment A | I-7 | `ConsentModels.cs`, access request endpoints, MCP tools | M |
| I-23 | #114 consumers per application | I-22 (shared normalisation helper) | audit schema (baseline DDL x3), `CallerContext` | M |
| I-24 | #113 increment B | I-3 (#107 typed filters), I-22 | `ConsentModels.cs`, consent approval flow | L |
| I-25 | #129 caller-attribute row filters (replaces `WP-SC-04`) | I-5 (#128), I-3 (#107) | `ConsentModels.cs`, `RlsOptions.cs`, `DatabaseOptions.cs`, SQL emitter context | L |
| I-26 | #131 `WP-FED-15` bound parameters in federated queries | I-5, I-14, I-15 | `CrossSourcePlanner.cs`, `PushedClauseBuilder.cs`, `FederatedDuckDbExecutionService.cs` | S-M |
| I-27 | BL-06 `WP-0` contracts | none; last on purpose (touches composition root, logging, metrics everywhere) | `Program.cs`, logging, metrics | M |
| I-28 | BL-06 stream A: composition root, logs, deploy | I-27 | as above | L |
| I-29 | BL-06 stream B: policy hook, spans | I-28 | gateway command path | M |
| I-30 | BL-06 stream C: metrics, async propagation (CDC links stay with BL-01) | I-29 | `GatewayDiagnostics`, async workers | M |

Ordering notes:

- Ranks 3.1 and 3.2 are interleaved on purpose: `WP-FED-14` needs the key-batching contract of `WP-FED-06` and `WP-FED-11..14` follow `WP-FED-03` (same files), so INC-6 sits between INC-3a and INC-3b/c. This follows `PLAN-FED-01` 6.1 and the owner preference for volume relief before GraphQL non-SQL relations. The delta plan (P-10) confirms or changes this order.
- INC-1..INC-5 are split into separate PRs per work package group (I-9 to I-21). Each PR carries its own integration cases from `WP-FED-10`; I-21 is the remainder. INC-1..5 is large (about 5,350 production and 8,000 test LOC plus INC-6 3,600 / 4,700); no PR is allowed to combine two increments.
- #113 B and #114 follow the PRD order (A, #114, B); #113 B waits for #107 being complete.
- #129 sits after #128 and #107 and before #131; #131 sits after INC-6 (needs `WP-FED-11`/`12`) and #128.
- BL-06 runs as four sequential PRs; the PRD's "Dev B / streams in parallel" becomes serial.
- Every step: Phase 4 (single TDD developer), lead builds and tests (Release, warnings as errors, `DOTNET_USE_POLLING_FILE_WATCHER=true` for integration tests in WSL), Phase 5 reviewer, Phase 6 documentation (feature doc, README, master plan row and status in this tracker), then the next step.

## 5. Planning sequence (Phase 2 then Phase 3, strictly sequential)

All Phase 2 plans first (P-1..P-10), then all Phase 3 reviews (S-1..S-10). Each review is appended to the plan file it reviews (convention of the existing plans). The architect and the security expert work on one file at a time.

### 5.1 Phase 2 (agent role `csharp-architect`)

| Step | Item | Output file | Notes |
|---:|---|---|---|
| P-1 | #128 list parameters | `docs/plans/2026-10-16-plan-issue-128-list-parameters.md` | First: #131 and #129 consume its list expander, bind-budget accounting and `ParameterOrigin`. Default Q-128-2..5 to the PRD recommendation; Q-128-1 fixed (1000, configurable). |
| P-2 | #131 federated bound parameters | `docs/plans/2026-10-16-plan-issue-131-federated-bound-parameters.md` | After P-1 (list contract). Detail of `WP-FED-15` against current `CrossSourcePlanner.cs`; add pointer in `PLAN-FED-01` 4.12/12.7. |
| P-3 | #130 `convert_timezone` | `docs/plans/2026-10-16-plan-issue-130-convert-timezone.md` | Argument order and result type are decided (Q-130-1/2). Check interplay with #123 function scope and the SqlGate corpus. |
| P-4 | #129 caller-attribute row filters | `docs/plans/2026-10-16-plan-issue-129-caller-attribute-row-filters.md` | After P-1. Defines `ICallerAttributeProvider`; reconcile with `WP-SC-05`/`WP-SC-07`, `PLAN-GOV-107` (shared files `ConsentModels.cs`, `RlsOptions.cs`, `DatabaseOptions.cs`), scale plan row 22 pointer. |
| P-5 | #115 read-path switches | `docs/plans/2026-10-16-plan-issue-115-read-path-switches.md` | Smallest; defines the options placement in `RestApiOptions.cs` and the MCP tool-group registry reused by #111 to #113. |
| P-6 | #111 tags and catalog visibility | `docs/plans/2026-10-16-plan-issue-111-dataset-tags-catalog-visibility.md` | Q-111-1 fixed (`Full` only by governance admins). |
| P-7 | #112 typed aggregations | `docs/plans/2026-10-16-plan-issue-112-typed-aggregations-graphql.md` | Q-112-1/2 fixed; check other GraphQL callers of `where: String` before removal. |
| P-8 | #113 self-service access requests (A and B) | `docs/plans/2026-10-16-plan-issue-113-self-service-access-requests.md` | Q-113-2 fixed (`data.access.request` / `data.access.review`). Increment B builds on #107 typed filter storage. |
| P-9 | #114 consumers per application | `docs/plans/2026-10-16-plan-issue-114-consumers-per-application.md` | Answers T-114-1 and Q-114-4 by code reading; reuse FinOps `CallerContext` normalisation; audit schema change in baseline `0001`. |
| P-10 | `PLAN-FED-01` delta (INC-1..INC-6) | `docs/plans/2026-10-16-plan-fed-01-delta-after-wave-1.md` | Short. Re-verify `WP-FED-01..14` against `main` after PRs #139/#140 (what is already delivered, what shrinks), confirm the INC-6-before-INC-3b/c order, split into per-PR packages for section 4, list changed security conditions. No redesign. |

Not re-planned (complete): #107 (`PLAN-GOV-107`) and BL-06 (`PLAN-OPS-BL06`). The developer re-verifies their code facts when the step starts; any material drift is recorded in the plan changelog first.

### 5.2 Phase 3 (agent role `csharp-security-expert`)

| Step | Reviews | Appended to | Notes |
|---:|---|---|---|
| S-1 | #128 | plan from P-1 | Conditions V-128-1..9 as input. |
| S-2 | #131 | plan from P-2 | Turns placeholders `SEC-FED-35..37` and the parameter parts into binding conditions; `SEC-FED-38..42` belong to `WP-FED-16` (wave 4) and stay a placeholder. |
| S-3 | #130 | plan from P-3 | Conditions V-130-1..9. |
| S-4 | #129 | plan from P-4 | **Dedicated review** (PRD requirement): provider trust, claim spoofing, fail closed, one generic outcome, cache keys, interplay with `SEC-RF107-*`. |
| S-5 | #115 | plan from P-5 | |
| S-6 | #111 | plan from P-6 | `Summary` never exposes columns; no existence oracle (#117). |
| S-7 | #112 | plan from P-7 | Privacy review of typed aggregates (minimum group size, DP). |
| S-8 | #113 A and B | plan from P-8 | Separate verdict per increment. |
| S-9 | #114 | plan from P-9 | Untrusted caller labels, cardinality cap, per-person view stays restricted. |
| S-10 | delta of INC-1..INC-6 | plan from P-10 | Short; only the changed or new conditions. |

After S-10 the tracker status of every planned item is set to "Planning complete" and G-PLAN is satisfied; the owner is told, then Phase 4 starts with I-1.

## 6. Blockers and risks

- **No owner decision blocks Phase 2.** All decisions needed are recorded in the master plan table; the remaining open questions (Q-128-2..5, Q-129-2..7, Q-114-4, Q-111/112/113 leftovers) default to the PRD recommendation.
- #131 is only blocked by P-1/S-1 (sequence, not decision).
- The `PLAN-FED-01` delta (P-10) is a risk for the estimate of INC-1..INC-5: after PR #140 some of `WP-FED-01`/`WP-FED-05` is delivered; sizes shrink, but the order may change.
- Wave 4 `#132` (`WP-FED-16`) shares `SubtreeExtractor.cs` with `WP-FED-13` and must not be planned into this delivery.
- Long single-agent chain: 10 plans + 10 reviews + 30 implementation steps. Progress of every step is tracked in the section 3 table (columns branch/PR and status); update it after each step.

## 7. Changelog

- 2026-10-16: Tracker created (gate verification, planning and implementation sequences).
