# Delivery Tracker: Priority Waves 2 and 3

**Document ID:** `PLAN-DELIVERY-W23`
**Date:** 2026-10-16
**Status:** IN DELIVERY - gate G-PLAN passed 2026-10-16; security bugfixes B-0..B-3 delivered (section 3.5); next step I-1
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

Legend: PRD = Phase 1; P2 = architect plan; P3 = security review (final verdict = last "Phase 3 ..." section of the plan file). AWC = APPROVED WITH CONDITIONS. All plan files are in this directory.

| Rank | Item | PRD | Phase 2 plan | Phase 3 final verdict | Implementation steps | Status |
|---:|---|---|---|---|---|---|
| 2.1 | #107 row filter key-set cache (WP-A/B/C) | [`REQ-GOV-107`](2026-10-15-req-issue-107-row-filter-key-set-cache.md) | done: [`PLAN-GOV-107`](2026-10-15-plan-issue-107-row-filter-key-set-cache.md) | AWC (section 12.5, `SEC-RF107-01..16`) | I-1..I-3 (contracts: PR #108 merged) | Planning complete; Phase 4 next (I-1) |
| 2.2 | #115 read-path switches | [`REQ-SEC-115`](2026-10-16-req-issues-111-115-catalog-access-analytics.md) | done: [`plan-issue-115`](2026-10-16-plan-issue-115-read-path-switches.md) | AWC (`C-115-1..12`, `ST-115-01..24`) | I-4 | Planning complete; Phase 4 pending |
| 2.3 | #128 list parameters | [`REQ-SQL-128`](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md) | done: [`plan-issue-128`](2026-10-16-plan-issue-128-list-parameters.md) | AWC (section 14, `SEC-128-01..14`, C-1..C-16, M-1..M-18) | I-5 | Planning complete; Phase 4 pending |
| 2.4 | #130 governed time zone conversion | `REQ-SQL-130` (same PRD) | done: [`plan-issue-130`](2026-10-16-plan-issue-130-convert-timezone.md) | AWC (`BC-130-1..14`, 18 security tests) | I-6 | Planning complete; Phase 4 pending |
| 2.5 | #111 dataset tags and catalog visibility | `REQ-GOV-111` (bundle 111-115 PRD) | done: [`plan-issue-111`](2026-10-16-plan-issue-111-dataset-tags-catalog-visibility.md) (rev. 3) | narrow re-review rev. 3: **WP-0 AWC** (`W0-C1..C11`), **#111 WP-1..6 AWC** (`F-C1..C10`) | B-1 (WP-0 security fix), I-7 | B-1 done (WP-0); I-7 pending |
| 2.6 | #112 typed aggregations in GraphQL | `REQ-DATA-112` (bundle 111-115 PRD) | done: [`plan-issue-112`](2026-10-16-plan-issue-112-typed-aggregations-graphql.md) | AWC (`SEC-112-01..12`, C-1..C-11, B-1..B-8, M-1..M-6); standalone WP-0 required | B-2 (WP-0 = tracker label I-6b), I-8 | B-2 done (WP-0); I-8 pending |
| 3.1 | REQ-FED-01 INC-6: #122 / #124 / #123 (`WP-FED-11..14`) | [`REQ-FED-01`](2026-10-16-req-cross-source-all-consumers.md) section 7.1 | done: `PLAN-FED-01` 4.11 plus delta | AWC (section 12.6 `SEC-FED-19..34`; delta review `DELTA-13`) | I-14..I-17 | Planning complete; Phase 4 pending |
| 3.2 | REQ-FED-01 INC-1..INC-5 (`WP-FED-01..10`) | `REQ-FED-01` | done: `PLAN-FED-01` plus [`PLAN-FED-01-DELTA`](2026-10-16-plan-fed-01-delta-after-wave-1.md) | AWC (section 12 `SEC-FED-01..18`; delta review `SEC-FED-DELTA-01..14`, D-1..D-5) | B-0 (S-0 bugfix), I-9..I-13, I-18..I-21 | B-0 done; I-9.. pending |
| 3.3 | #113 access requests, WP-0 then A then B | `REQ-GOV-113` (bundle 111-115 PRD) | done: [`plan-issue-113`](2026-10-16-plan-issue-113-self-service-access-requests.md) (rev. 2) | narrow re-review rev. 2: **WP-0 AWC**, **A AWC**, **B AWC** (`SEC-113-R2-01..11`, W-9..W-14, T-S31..T-S36) | B-3 (WP-0), I-22 (A), I-24 (B) | B-3 done (WP-0); I-22, I-24 pending |
| 3.4 | #114 consumers per calling application | `REQ-OBS-114` (bundle 111-115 PRD) | done: [`plan-issue-114`](2026-10-16-plan-issue-114-consumers-per-application.md) | AWC (`SEC-114-01..12`, C-114-1..12) | I-23 | Planning complete; Phase 4 pending |
| 3.5 | #129 row filters on caller attributes (replaces `WP-SC-04`) | `REQ-GOV-129` (128-130 PRD) | done: [`plan-issue-129`](2026-10-16-plan-issue-129-caller-attribute-row-filters.md) | AWC, dedicated review (`BC-129-01..16`, `ST-129-01..24`) | I-25 | Planning complete; Phase 4 pending |
| 3.6 | #131 bound parameters in federated queries (`WP-FED-15`) | `REQ-FED-01` section 7.2 | done: [`plan-issue-131`](2026-10-16-plan-issue-131-federated-bound-parameters.md) | AWC (section 14, `SEC-131-01..`, B-15, M-15); `SEC-131-04` split out as B-0 | I-26 | Planning complete; Phase 4 pending |
| 3.7 | BL-06 OpenTelemetry | [`REQ-OPS-BL06`](2026-10-16-req-bl-06-open-telemetry.md) | done: [`PLAN-OPS-BL06`](2026-10-16-plan-bl-06-open-telemetry.md) | AWC (section 10.5, `SEC-OT06-01..21`) | I-27..I-30 | Planning complete; Phase 4 pending |

Out of scope here: `WP-FED-16` (#132) is rank 4.3 (wave 4); the #131 plan must not depend on it.

### 3.1 One plan per issue, or combined

- One plan per issue for #115, #111, #112, #114, #128, #129, #130, #131: the issues have separate files, separate security conditions and separate PRs.
- #113 is **one** plan with two increments (A allowlisted request/withdraw, B approver-chosen filters): the design of the request record and the allowlist is shared; the plan states a separate PR and a separate review section per increment.
- #131 gets its own plan file rather than extending `PLAN-FED-01` further (that file is already very large); `PLAN-FED-01` 4.12 and 12.7 get a pointer sentence from the new plan (the architect adds it).
- #122/#123/#124 stay one planned unit (`WP-FED-11..14`, already reviewed together).

### 3.2 Gate G-PLAN result (2026-10-16): PASSED

Every item has a Phase 2 plan and a final Phase 3 verdict of APPROVED WITH CONDITIONS. No item is CHANGES REQUIRED any more (the earlier CHANGES REQUIRED verdicts of #111 rev. 1/2 and #113 B rev. 1 were closed by revision 3 / revision 2 and the narrow re-reviews). Binding conditions are carried either by a plan revision or verbatim in the Phase 4 brief; the Phase 5 reviewer checks them line by line.

| Item / increment | Phase 2 | Final Phase 3 verdict |
|---|---|---|
| #107 (WP-A/B/C) | yes | AWC (`SEC-RF107-01..07` release-blocking) |
| #115 | yes | AWC |
| #128 | yes | AWC |
| #130 | yes | AWC |
| #111 WP-0 (`fix/catalog-schema-disclosure`) | yes (rev. 3) | AWC (re-review rev. 3) |
| #111 WP-1..WP-6 | yes (rev. 3) | AWC; starts only after WP-0 merged and after #115 |
| #112 WP-0 (aggregation privacy bugfixes, `I-6b`) | yes | AWC (P3.3, W0-1..W0-8) |
| #112 feature | yes | AWC; only after WP-0 merged |
| `PLAN-FED-01` INC-1..INC-6 plus delta | yes | AWC (delta review, binding points 1-4 in P3.10) |
| #113 WP-0 | yes (rev. 2) | AWC (narrow re-review) |
| #113 A | yes | AWC; after WP-0 and #111 |
| #113 B | yes | AWC; after #107 complete and A |
| #114 | yes | AWC |
| #129 | yes | AWC |
| #131 | yes | AWC |
| BL-06 | yes | AWC |

Not blocking Phase 4 (recorded, default taken; owner confirmation is due at the step named):

- Merge of **B-1** (#111 WP-0) needs the owner's acknowledgement of the behaviour changes O-1, O-5, O-6, O-7 including the single-tenant migration instruction (`Gateway:Tenancy:Mode=Single`, default is `Multi`; claim-less GovernanceAdmins are demoted otherwise).
- **B-3** (#113 WP-0): confirm O-9 (app-only tokens can no longer decide consent requests); **I-22/I-24**: confirm O-8 and the O-3 deferral of group templates.
- **I-8** (#112): owner answers O-4 (drop `suppressedRowsCount` from the GraphQL result, recommended) and O-5 (`GraphQL:Aggregation:MaxFieldsPerOperation`, default 3). Follow-up issue recommended for typed filters on REST/MCP (O-2).
- **I-23** (#114): open a separate tracked item for the audit retention purge job (O-4/O-9); #114 does not depend on it.
- #130 owner flags F-1..F-4 are accepted as recommended in the review (section 7); Q-130-1/2 fixed.
- Several plans (#115, #128, #130, #131, #129, #112, #114) are not re-issued as a revision 2: the architect folds the binding conditions in at the start of the step or the developer receives them verbatim (decision of the review verdicts); the lead includes the condition list in the Phase 4 brief.

Q-6 (both table owners approve `AllowCrossDataSource`, `SEC-FED-DELTA-12`): cannot be implemented on the current model. `TableMetadata` has only `OwnerTenantId` (a tenant), `DataOwner` is a global role, and `AllowCrossDataSource` is init-only and not persisted, so every table currently counts as "without owner" and the flag cannot be enabled (fail closed, safe). The review makes the per-table owner model a binding part of `WP-FED-07` (step **I-18**): tenant-scoped per-table owner designation (principal SID or group) set through an audited path; approval per (relation id, parent epoch, child epoch); distinct principals; `GovernanceAdmin` plus both owner approvals; unreadable store evaluates `false`; epoch bump invalidates; audit, schema cache invalidation. Consequences: I-18 grows from M to L (DDL x3 plus shapes, owner admin path); before I-18 starts the architect adds a short addendum to the `WP-FED-07` spec (check whether the existing table owner/delegate approver records used by #113/#114 can be reused). It does **not** block Phase 4 start nor I-9..I-17 (SQL-text federation does not use the relation flag). It **does** gate GraphQL cross-source relations: `WP-FED-08` (I-19) depends on I-18, and relations stay hidden (PR #139 gating) and unusable until the owner model is merged. No owner decision is needed, only the architect addendum.

## 3.5 Delivery progress and handoff (updated 2026-10-17)

Delivery mode (owner): one developer, strictly sequential; branches are **stacked** (each step branches from the previous step) and pushed when a step is done; PRs target the previous step's branch. Subagent models: architects on Opus, all other roles on Sonnet.

| Step | Branch | PR | Base | Phase 4 | Phase 5 code review | State |
|---|---|---|---|:---:|:---:|---|
| Planning | `docs/waves-2-3-planning` | #144 | `main` | – | – | pushed, ready to merge |
| B-0 | `fix/federated-audit-error-text` | #145 | `main` | ✅ | ✅ (findings addressed) | pushed, done |
| B-1 | `fix/catalog-schema-disclosure` | #147 | B-0 | ✅ | ✅ (findings addressed) | pushed, done |
| B-2 | `fix/aggregation-privacy-hardening` | #148 | B-1 | ✅ | ✅ (CHANGES REQUIRED, findings addressed, re-review APPROVED WITH FINDINGS, last Low finding fixed) | pushed, done (head `ca7dfa2d`) |
| B-3 | `fix/issue-113-wp0-decision-hardening` | – (owner opens; proposed base B-2) | B-2 | ✅ | ✅ (APPROVED WITH FINDINGS, findings F-1..F-8 addressed, re-review APPROVED WITH FINDINGS) | pushed, done |
| I-1 | `feat/issue-107-wp-a` | – (owner opens; proposed base B-3) | B-3 | – | – | **in progress** (chunk 1) |
| I-2.. | see section 4 | – | – | – | – | not started |

**Resume on another machine (next run starts with I-1):** `git fetch`, check out `fix/issue-113-wp0-decision-hardening` (B-3, it contains B-0..B-2 and this tracker), create `feat/issue-107-wp-a` from it and start step I-1 (#107 WP-A, `PLAN-GOV-107` with the binding conditions `SEC-RF107-*` of its section 12). Owner rule: before Phase 4 of any feature the owner sees a lines-of-code estimate per work package (production and tests separately); for #107 and BL-06 the estimates already exist in [`2026-10-16-loc-estimates.md`](2026-10-16-loc-estimates.md). Build like CI before every push: `dotnet build Autheris.sln -c Release -p:AutherisIncludeTestAuth=true -warnaserror`; tests with `-c Release -p:AutherisIncludeTestAuth=true`, integration tests with `DOTNET_USE_POLLING_FILE_WATCHER=true` and, in the podman dev container, `TESTCONTAINERS_HOST_OVERRIDE=10.88.0.1` (otherwise every container test fails in about 1 ms). The full integration suite takes about 25 minutes; do not pipe it through `grep` without `--blame-hang-timeout` (a buffered run looks hung).

**Notes:**
- `DistributedLeaseTests.LastConfirmedAt_IsTheTimeTheExtendWasSent` is a known timing flake (seen on several branches); unrelated to these steps.
- B-2 residuals (to fold into I-8 or a follow-up): MCP `list_metrics`/`describe_metric` and the GraphQL metric list still read the legacy semantic partition; `queryMetrics` results still include full metric definitions.
- Pending CI change `ci/release-reuse-ci-result` (release workflow reuses the CI result) cannot be pushed with the current token (needs the `workflow` scope). It is saved as `implementation/pending/ci-release-reuse-ci-result.patch`; apply with `git am` on a branch from `main` and push with a token that has `workflow` scope.
- Test results at hand-off (Release, `AutherisIncludeTestAuth=true`): B-2 unit 8,268, integration 1,232, architecture 114 (+1 skipped), extensions 253, TrinoSqlEngine 8,112, SQL compiler gate 177, all green; B-3 unit 8,358, integration 1,250, architecture 114 (+1 skipped), extensions 253, TrinoSqlEngine 8,112, SQL compiler gate 177, all green. Build with warnings as errors clean on both.
- The tracker and all wave 2/3 plans were brought into the step stack by merging `docs/waves-2-3-planning` (PR #144) into the B-3 branch (merge commit `94b5ad89`); the B-3 PR therefore shows the planning documents until #144 is merged. From B-3 on, the tracker is maintained on the step branches.
- B-0 and B-1 (PRs #145, #147) fail `SecurityRegressionTraceabilityTests.EveryLegacyId_HasSameOrStricterAstTest` on their own (`SEC-131` from B-0 had no catalog row); the catalog rows for `SEC-131`, `SEC-112` (B-2) and `SEC-113` (B-3) were added on B-2/B-3. Merging the stack in order makes `main` green; B-0/B-1 alone are red on that one test.
- B-3 decisions taken by the lead (default taken, owner to confirm at PR): `ErrorSanitizingFilter` whitelists `DECISION_NOT_PERMITTED` and `ACCESS_REQUEST_NOT_PENDING` (outside the R2.1 file list; without it clients see `INTERNAL_SERVER_ERROR`); the ITSM webhook acknowledges a lost status race without effect (R2.1 said "untouched"); recorded in the `PLAN-GOV-113` changelog. The `INDIVIDUAL_GRANT_*` codes are still masked by the sanitizer outside Development (pre-existing; candidate for #113 A).
- B-3 owner confirmation O-9 (application and certificate tokens can no longer decide consent requests): default taken (accepted as recommended by the review), owner to confirm at PR.
- Owner confirmations still due: see section 3.2 (B-1 O-1/O-5/O-6/O-7 acknowledged by the owner for O-7; B-3 O-9; I-8 O-4/O-5; I-22/I-24 O-8, O-3).

### 3.6 Release notes of the bugfix steps (for the PR texts)

- **B-2 (#112 WP-0, aggregation privacy hardening):** manifest or request `min_group_size` can only raise the operator floor, and a request cannot weaken `Suppress`; a request `where` on a metric with a filter is checked against denied and masked columns; in-memory aggregation fails with `AGGREGATION_ROW_CAP_EXCEEDED` at 100,000 rows instead of truncating; unknown and denied columns answer one `AGGREGATION_COLUMN_UNAVAILABLE` error; `suppressedRowsCount` is reported only when exposed and noise is off; the merged `OTHER` row returns `null` for `MIN`/`MAX`/`COUNT_DISTINCT`/`AVG`; with noise on, duplicate aggregates and `MIN`/`MAX` are rejected, the budget is charged per noised aggregate plus the row count, and order/limit use noised values; a denied and a missing backing table of a metric give the same `FORBIDDEN` answer; the semantic metric catalog is tenant-scoped (legacy partition only in `Gateway:Tenancy:Mode=Single`); non-admins get a reduced `GET /dbt/metrics` projection.
- **B-3 (#113 WP-0, consent decision hardening):** application tokens can no longer approve or reject consent requests (`DECISION_NOT_PERMITTED`), also with `GovernanceAdmin` and `AppOnlyTokens=ReadWrite`; certificate (mTLS) subjects can no longer decide; delegated tokens with a scope claim and an oid/SID may decide even without a human-name claim; an operator-enabled `ReadWrite` administrative application keeps its governance admin REST access (not a request decision). The individual grant policy is evaluated before the approval step, so a refused approval leaves the request pending and rejectable. Status changes use a guarded update; a concurrent change answers `ACCESS_REQUEST_NOT_PENDING`, and an ITSM callback that loses such a race is acknowledged without effect.

## 4. Implementation sequence (final; Phase 4 to 6, strictly one at a time)

Rules: one branch and one PR per step; every branch is created from the `main` that contains the previous step (G-SHARED). Owner rule "security bugs first": steps B-0..B-3 are standalone bugfix PRs without feature dependencies and run before any feature. Then #107 first among features (owner priority), then the rest. Plan labels in brackets are the names used inside the reviews. Size S/M/L as in the plans.

### 4.1 Security bugfixes first

| Step | Branch | Content (source) | Depends on | Size |
|---|---|---|---|:---:|
| B-0 | `fix/federated-audit-error-text` | `SEC-FED-DELTA-01` = `SEC-131-04`: the federated failure audit stores `ex.Message` for every non-gateway exception (`FederatedDuckDbExecutionService.cs:630`); fixed text from the reason code only, plus test. Live when `CrossSource:Enabled=true`; audit cannot be redacted afterwards, so it goes first. Add the statement-level checks "table functions, session properties, inline functions, zero tables, NATURAL/LATERAL" (delta P3.7, about 40 LOC) only if trivial, otherwise they stay in I-10. | none | S (about 30 prod / 80 test LOC) |
| B-1 | `fix/catalog-schema-disclosure` | #111 WP-0 [tracker label I-6a]: REST/MCP/search/`datasources`/GraphQL-flag catalog disclosure across tenants and consent, `CallerScopeResolver`, `Gateway:Tenancy:Mode` (default `Multi`), conditions `W0-C1..C11`, tests `T-W0-1..16`. Widest live exposure (default configuration). Merge needs owner acknowledgement (section 3.2). | none | M |
| B-2 | `fix/aggregation-privacy-hardening` | #112 WP-0 [tracker label I-6b]: `W0-1..W0-8`: `min_group_size` override may only raise (SEC-112-02), request `where` column check with metric filters (SEC-112-01), row-cap error, uniform column errors, `suppressedRowsCount`, `OTHER` row nulls, DP budget rules, reduced `GET /dbt/metrics` projection. | B-1 (`Gateway:Tenancy:Mode`, tenant passing) | M |
| B-3 | `fix/issue-113-wp0-decision-hardening` | #113 WP-0: app-only token approval bypass of the person four-eyes, `IGrantPolicy` before the approval step, `status` guards x3 engines (conditions `SEC-113-R2-01..03`, W-1..W-14). Opt-in precondition (`AppOnlyTokens=ReadWrite`), hence after B-1/B-2. | none | S (about 230 prod / 480 test LOC) |

Not split out as standalone (decided by the reviews, so they sit inside the named feature step): default spill `MaxTempDirectorySize=0B` ships with I-10 (it affects a working OLAP feature; owner is told); pre-authorization denials and the rewriter's `Enabled` branching ship inside I-9 (before any widening). If the owner enables `CrossSource` in any environment before I-9 merges, the router/rewriter part must become an extra bugfix before I-9. Wave-2 plan-level defects found in the reviews that are not live today (for example SEC-128-01 empty-list DML hazard) are feature conditions, not bugfixes.

### 4.2 Features

| Step | Branch (suggested) | Item (package) | Depends on | Shared files / conflict note | Size |
|---:|---|---|---|---|:---:|
| I-1 | `feat/issue-107-wp-a` | #107 WP-A: settings, persistence, admin (contracts merged) | PR #108, B-1 merged | baseline DDL x3 + shapes, repositories, `DatabaseOptions.cs`, `ConsentModels.cs` | M |
| I-2 | `feat/issue-107-wp-b` | #107 WP-B: key-query compiler and substitution | I-1 | `src/TrinoSqlEngine/Governance`, `GovernancePolicyFactory` | M |
| I-3 | `feat/issue-107-wp-c` | #107 WP-C: runtime, cache, epochs, integration | I-2 | epochs, `RlsOptions.cs` | M |
| I-4 | `feat/issue-115-read-path-switches` | #115 read-path switches (conditions `C-115-1..12`; the options class lives in `RestApiOptions.cs`, which holds `WebSqlOptions` etc.) | none | `RestApiOptions.cs`, MCP tool registry, `DatasetDataEndpoints.cs` | S |
| I-5 | `feat/issue-128-list-parameters` | #128 list parameters (C-1..C-16 incl. running-total cap in the shared expander, `FederatedDuckDbExecutionService` guard) | none | `WebSqlParameterValues.cs`, `SqlEndpointLoader.cs`, `SqlEmitterContext.cs`, `CompiledSql.cs` | M |
| I-6 | `feat/issue-130-convert-timezone` | #130 `convert_timezone` (BC-130-1..14); additional shared files: `RowPolicyFactory.cs`, `PolicyParserSetup.cs`, `RestApiOptions` validator, `FederatedDuckDbExecutionService.cs` (read-only check) | none | `SqlFunctionAllowlists.cs`, dialect generators, `DialectCapabilityTable.cs` | S-M |
| I-7 | `feat/issue-111-dataset-tags-visibility` | #111 WP-1..WP-6 dataset tags and catalog visibility (F-C1..F-C10) | B-1, I-4 | baseline DDL x3 + shapes, repositories, `CatalogVisibility.cs`, MCP catalog | M |
| I-8 | `feat/issue-112-typed-aggregations` | #112 typed aggregations and typed `queryMetrics` (rev. 2 conditions C-2..C-11, B-1..B-8, M-1..M-6) | B-2, I-7 | `CatalogSchemaModel.cs`, baseline DDL x3 (semantic manifest) | M |
| I-9 | `feat/fed-inc1-gate-and-error-contract` | `PLAN-FED-01` INC-1: `WP-FED-01` remainder plus `WP-FED-02` plus gate wiring after `AuthorizeAllTablesAsync`; `FederationDeniedException` / `FederationSourceException` split, one code and one text for disabled/not allowed, option validator (explicit `AsyncJob` without protection fails startup), pre-authorization denials moved behind authorization | B-0, I-4 | `RestApiOptions.cs`, error mappers across protocols | M |
| I-10 | `feat/fed-inc2a-sql-federation-hardening` | `WP-FED-03`: shared per-tenant and global lease (after authorization and gate, released in `finally`), spill default `MaxTempDirectorySize=0B`, `FEDERATION_SOURCE_ERROR` without driver text, federated-path checks (table functions, NATURAL/LATERAL, `MaxSqlLength`), `Rest` consumer inventory test; default list widens to `Mcp`, `SqlEndpoints`, `FlightSql`, `ArrowExport` only at the end of the PR; `Rest` only after the inventory test | I-9 | `CrossSourcePlanner.cs`, `FederatedStagingService.cs`, `GovernedSqlExecutionService.cs` | M |
| I-11 | `feat/fed-inc2c-olap-facade` | `WP-FED-05` OLAP facade (swapped with at-rest per the delta); "port first, delete last": the `SEC-FED-DELTA-05` control table is the merge gate, `ValidateUserSql` kept as generated-text verifier, `Olap` added to the default list in the last commit | I-9, I-10 | `DuckDbOlapEndpoints.cs`, `OlapStagingService.cs` | M |
| I-12 | `feat/fed-inc2b-staging-at-rest` | `WP-FED-04` staging and results at rest (encrypted spill or disabled, `ProtectedStagingEnabled`, then `AsyncJob` in the default list) | I-9 | `DuckDbOlapEngine.cs`, `AsyncQueryJobBackgroundWorker.cs` | M |
| I-13 | `feat/fed-inc3a-governed-source-reader` | `WP-FED-06` governed source reader, resolver rewrite, single `IsSameExecutionSource` | I-9, I-10 | `src/Autheris.Application/Data/Relations/*` | L |
| I-14 | `feat/fed-inc6a-pushdown` | `WP-FED-11` (#122 pushdown) | I-10 | `CrossSourcePlanner.cs` | L |
| I-15 | `feat/fed-inc6b-options-hints-audit` | `WP-FED-12` options, hints, audit, analysis (adds `DELTA-13`: `allow_key_set_import` default false) | I-14 | baseline DDL x3 + shapes, `TableModels.cs`, `QueryAnalysisService.Sql.cs` | L |
| I-16 | `feat/fed-inc6c-key-set-filters` | `WP-FED-14` (#124 key-set filters) | I-13, I-15 | `FederatedStagingService.cs`, `KeyPredicate.cs` | M |
| I-17 | `feat/fed-inc6d-same-source-subtrees` | `WP-FED-13` (#123 same-source subtrees; #130/#131 hooks) | I-15, I-16 | `CrossSourcePlanner.cs`, `GovernedSqlExecutionService.cs` | L |
| I-18 | `feat/fed-inc3b-relation-declaration` | `WP-FED-07` relation declaration and admin **including the per-table owner model for Q-6** (`SEC-FED-DELTA-12`); needs the architect addendum first | none (placed here to keep DDL changes serial) | baseline DDL x3 + shapes, `TableModels.cs`, repositories | L |
| I-19 | `feat/fed-inc3c-graphql-splitter` | `WP-FED-08` GraphQL splitter and schema gating (extend `RelationExecutability`; `GraphQl` in the default list only after the drift test is green) | I-13, I-18 | `CatalogSchemaModel.cs`, `CatalogGraphQlTypeModule.cs` | L |
| I-20 | `feat/fed-inc4-http-sources` | `WP-FED-09` HTTP sources and relation filters in GraphQL | I-10, I-19 | `FederatedStagingService.cs`, `CatalogSchemaModel.cs` | M |
| I-21 | `feat/fed-inc5-integration-parity` | `WP-FED-10` remaining integration and parity tests, Phase 6 doc corrections (#125) | I-20 | `tests/Autheris.Tests.Integration/Federation/*` | M |
| I-22 | `feat/issue-113-a-self-service-requests` | #113 increment A (conditions `SEC-113-01..13`, R2-04..06) | B-3, I-7 | `ConsentModels.cs`, access request endpoints, MCP tools | M |
| I-23 | `feat/issue-114-consumers-per-application` | #114 consumers per application (C-114-1..12) | I-22 (order only; no code dependency) | audit schema (baseline DDL x3), `CallerContext` | M |
| I-24 | `feat/issue-113-b-approval-configuration` | #113 increment B (`SEC-113-R2-07..11`, T-S33..T-S36) | I-3, I-22 | `ConsentModels.cs`, consent approval flow | L |
| I-25 | `feat/issue-129-caller-attribute-row-filters` | #129 (replaces `WP-SC-04`; `BC-129-01..16`) | I-5, I-3 | `ConsentModels.cs`, `RlsOptions.cs`, `DatabaseOptions.cs`, SQL emitter context | L |
| I-26 | `feat/issue-131-federated-bound-parameters` | #131 `WP-FED-15` (conditions of section 14 incl. validation after authorization and before key-source reads) | I-5, I-14, I-15 (I-17 for the subtree hand-off; `WP-1/2` may be pulled forward after I-10 by the owner) | `CrossSourcePlanner.cs`, `PushedClauseBuilder.cs`, `FederatedDuckDbExecutionService.cs` | S-M |
| I-27 | `feat/bl06-wp0-contracts` | BL-06 `WP-0` contracts | none; last on purpose | `Program.cs`, logging, metrics | M |
| I-28 | `feat/bl06-a-composition-logs-deploy` | BL-06 stream A | I-27 | as above | L |
| I-29 | `feat/bl06-b-policy-hook-spans` | BL-06 stream B | I-28 | gateway command path | M |
| I-30 | `feat/bl06-c-metrics-async` | BL-06 stream C (CDC links stay with BL-01) | I-29 | `GatewayDiagnostics`, async workers | M |

Execution order: B-0, B-1, B-2, B-3, I-1, I-2, ... I-30 (numeric). The corrected FED order of the delta review applies: B-0 -> I-9 -> I-10 (lease, spill `0B`, `Rest` inventory) -> I-11 facade -> I-12 at rest.

Ordering notes:

- #107 is the first feature step because of the owner priority; the four bugfixes are small, independent of it and do not touch its files except B-1 (catalog) which has no shared file with I-1.
- Ranks 3.1 and 3.2 stay interleaved: `WP-FED-14` needs the key-batching contract of `WP-FED-06`; `WP-FED-11..14` follow `WP-FED-03` (same files); INC-6 sits between INC-3a and INC-3b/c (confirmed by the delta review).
- The INC-1..INC-5 split into separate PRs (I-9..I-21) stands; no PR may combine two increments. The staged transport widening follows `SEC-FED-DELTA-02`: each transport is added only in the step named above.
- #113 B and #114 follow the PRD order (A, #114, B); #113 B waits for #107 being complete.
- #129 sits after #128 and #107; #131 after INC-6 and #128.
- BL-06 runs as four sequential PRs; the PRD's parallel streams become serial.
- Every step: Phase 4 (single TDD developer), lead builds and tests (Release, warnings as errors, `DOTNET_USE_POLLING_FILE_WATCHER=true` for integration tests in WSL), Phase 5 reviewer, Phase 6 documentation (feature doc, README, master plan row, this tracker), then the next step. Bugfix steps B-0..B-3 need Phase 5 and a short Phase 6 note (release notes), no new feature doc.

### 4.3 Thematic delivery chunks (owner, 2026-10-17)

The owner re-grouped the remaining delivery into thematic chunks; one project-manager agent runs one chunk, so related context stays together. **This chunk order replaces the numeric execution order of section 4.2.** Inside a chunk the steps run in the order listed; branches stay stacked (each step branches from the previous step's branch, across chunk borders too), so every dependency of section 4.2 remains satisfied by the stack.

| Chunk | Theme | Steps (in order) |
|---:|---|---|
| 1 | Row filters and WebSQL parameters | I-1, I-2, I-3, I-5, I-6, I-25 |
| 2 | Catalog, access and analytics | I-4, I-7, I-8, I-22, I-23, I-24 |
| 3 | Federation gate, hardening, execution | I-9, I-10, I-11, I-12, I-13 |
| 4 | Federation query planning | I-14, I-15, I-16, I-17, I-26 |
| 5 | Federation relations and GraphQL | I-18, I-19, I-20, I-21 |
| 6 | Observability (BL-06) | I-27, I-28, I-29, I-30 |
| 7 | Wave 4 | planning, then implementation, also chunked by theme |

Dependency check: I-25 needs I-3 and I-5 (both in chunk 1, earlier); I-7 needs I-4 (chunk 2, earlier); I-24 needs I-3 (chunk 1) and I-22 (chunk 2, earlier); I-9 needs B-0 and I-4 (chunk 2); I-16 needs I-13 (chunk 3); I-26 needs I-5 (chunk 1), I-14, I-15, I-17 (chunk 4, earlier); I-19 needs I-13 (chunk 3) and I-18. Owner waiver (2026-10-17): no lines-of-code estimate is required before Phase 4 for the rest of waves 2/3 and for wave 4.

## 5. Planning sequence (Phase 2 then Phase 3, strictly sequential) - COMPLETED 2026-10-16

All of P-1..P-10 and S-1..S-10 are done (including revisions and narrow re-reviews of #111, #113 and the delta); results in section 3.

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

- **No blocker for Phase 4.** No plan is CHANGES REQUIRED; no open owner decision blocks the start of B-0.
- Owner confirmations are due at merge or step start (section 3.2): B-1 behaviour changes (O-1, O-5, O-6, O-7, single-tenant migration), B-3 O-9, I-8 O-4/O-5, I-22/I-24 O-8/O-3.
- Q-6 owner model (`SEC-FED-DELTA-12`) is a design gap that lands in I-18 (L); needs an architect addendum before I-18 and gates GraphQL cross-source relations (I-19..I-21), not the SQL federation steps.
- Live bugs are only reachable under conditions (B-0: `CrossSource:Enabled=true`; B-3: `AppOnlyTokens=ReadWrite`); B-1 and B-2 are live in default configuration.
- If `CrossSource` is enabled in any environment before I-9 merges, split the pre-authorization denial fix out of I-9 as an extra bugfix.
- Wave 4 `#132` (`WP-FED-16`) shares `SubtreeExtractor.cs` with `WP-FED-13` and must not be planned into this delivery.
- Long single-agent chain: 4 bugfix PRs plus 30 feature steps. Progress of every step is tracked in the section 3 table; update it after each step.

## 7. Changelog

- 2026-10-16: Tracker created (gate verification, planning and implementation sequences).
- 2026-10-17: B-2 Phase 5 closed and B-3 delivered (sections 3.5, 3.6); tracker moved into the step stack; resume point I-1.
- 2026-10-17: Section 4.3 thematic delivery chunks (owner) replaces the numeric order; LOC estimates waived; chunk 1 started on `feat/issue-107-wp-a`.
- 2026-10-16: Planning complete. Section 3 holds final Phase 2/3 verdicts, section 3.2 the G-PLAN result (passed), section 4 the final implementation sequence (security bugfixes B-0..B-3 first, I-9..I-12 re-ordered per the FED delta review, I-18 enlarged by the Q-6 owner model).
