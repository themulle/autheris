# Implementation Plan: Issue #236 Unified Required Filter Column Groups

**Document ID:** `PLAN-GOV-236`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 2 & Phase 3 Complete)  
**Author:** Solution Architect & Lead Delivery PM (`csharp-architect`, `project-manager`)  
**Parent PRD:** [`docs/plans/2026-10-19-req-issue-236-unified-required-filter-column-groups.md`](2026-10-19-req-issue-236-unified-required-filter-column-groups.md) (`REQ-GOV-236`)  
**Branch:** `feat/issues-236-237-governance-hotfix`  
**Target Milestone:** v1.2.5 (Immediate Hotfix)  

---

## 1. Architectural Architecture & Work Packages

To permanently resolve the fail-open defect where `RequiredFilterColumnGroups` (#221) are ignored by REST single-table reads, typed aggregations, bulk streams, and federated staging, this plan unifies evaluation into a shared, zero-allocation helper logic and enforces it across all read paths.

```
                      +-----------------------------------+
                      |   Incoming Query Read Request     |
                      +-----------------+-----------------+
                                        |
                 +----------------------+----------------------+
                 |                      |                      |
                 v                      v                      v
        [ REST Table Read ]    [ Keyset Bulk Stream ]   [ Typed Aggregate ]
        [ PlanRead/Count  ]    [ /stream & /bulk    ]   [ PlanAggregate   ]
                 |                      |                      |
                 +----------------------+----------------------+
                                        |
                                        v
                 +---------------------------------------------+
                 |  RequiredFilterEvaluationEngine.Validate    |
                 |  - RequiredFilterColumns (all must hold)    |
                 |  - RequiredFilterColumnGroups (>=1 group)   |
                 |  - Tenant column auto-satisfaction          |
                 |  - Active RLS predicate satisfaction        |
                 |  - Query / Pushdown filter satisfaction     |
                 +----------------------+----------------------+
                                        |
                    +-------------------+-------------------+
                    | Fail                                  | Pass
                    v                                       v
      [ Throw GovernedSqlException ]               [ Construct & Execute ]
      [ REQUIRED_FILTER_COLUMN_MISSING ]           [ Governed Query SQL  ]
```

### WP-236-1: Shared Required Filter Evaluation Engine
- Implement `RequiredFilterEvaluationEngine` in `Autheris.Application.Data.Services` (or shared internal static):
  - Evaluates both `RequiredFilterColumns` and `RequiredFilterColumnGroups`.
  - For `RequiredFilterColumns`: ALL must be satisfied.
  - For `RequiredFilterColumnGroups`: AT LEAST ONE group must have ALL its columns satisfied.
  - Returns `bool IsSatisfied(string col)` evaluating:
    1. Tenant column match (if not in aggregate mode without tenant injection).
    2. Active RLS predicate referenced columns (e.g. `PolicyPredicateRowPolicy` or `CombinedRowFilterSql`).
    3. Query filter predicate referenced columns (`TableFilterClause`, `TypedFilterNode`, or pushdown filter).
    4. Explicit joined child columns (if joined on key).
  - Throws `RequiredFilterColumnMissingException(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing))` fail-closed.

### WP-236-2: GovernedTableReadPlanner Integration
- In `GovernedTableReadPlanner.cs`:
  - Update `EnsureRequiredFilterColumnsSatisfied` for `Plan`, `PlanCount`, and `PlanAggregate`.
  - Inspect `metadata.RequiredFilterColumnGroups`: if present and no group is satisfied, throw `RequiredFilterColumnMissingException`.
- In `GovernedTableReadPlanner.Keyset.cs`:
  - In `PlanKeysetStream`, replace step 3 with full column and column group verification.

### WP-236-3: FederatedStagingService Integration
- In `FederatedStagingService.cs`:
  - After line 94, evaluate `req.Metadata.RequiredFilterColumnGroups`.
  - If column groups exist, check whether at least one group has all columns constrained by tenant, RLS, `CombinedRowFilterSql`, or `PushdownFilter`.
  - If unconstrained, reject with `GatewaySecurityException(GovernedSqlErrorCodes.RequiredFilterColumnMissing)`.

### WP-236-4: Comprehensive Parity Verification Test
- Author end-to-end parity test `Issue236_RequiredFilterColumnGroups_AllReadPaths_ParityTests.cs`:
  - Tests a table with `RequiredFilterColumnGroups = [["tenant_id"], ["region", "event_date"]]`.
  - Verifies rejection across:
    1. REST `PlanRead`
    2. REST `PlanCount`
    3. GraphQL `PlanAggregate`
    4. Keyset `PlanKeysetStream` (/stream and /bulk)
    5. WebSQL / AST visitor
    6. Federated staging (`FederatedStagingService.StageAsync`)
  - Verifies success when either `tenant_id` is supplied OR both `region` and `event_date` are supplied.

---

## 2. Phase 3 Security & Compliance Review (Binding Conditions)

- **SEC-RF236-01 (Strict Fail-Closed)**: If `RequiredFilterColumnGroups` is non-empty, and zero groups are fully satisfied, the query MUST be aborted before any database connection or cursor allocation.
- **SEC-RF236-02 (Dual Requirement Conjunctive Safety)**: If BOTH `RequiredFilterColumns` and `RequiredFilterColumnGroups` are defined, BOTH must be satisfied.
- **SEC-RF236-03 (No Oracle Disclosure)**: Missing filter exceptions must emit the fixed message from `GovernedSqlErrorCodes.FixedMessage` without disclosing tenant values, database connection details, or schema internals.
- **SEC-RF236-04 (RLS & Pushdown Parity)**: RLS predicates must only satisfy a required filter column if the predicate strictly references the exact required column.

---

## 3. Work Package Execution Order
1. WP-236-1 & WP-236-2: `GovernedTableReadPlanner` and `GovernedTableReadPlanner.Keyset`
2. WP-236-3: `FederatedStagingService`
3. WP-236-4: Parity Unit Tests
