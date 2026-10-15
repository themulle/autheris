# Plan: SqlGate Accepted Differences Review & Phase 3 Cutover

**Document ID:** `PLAN-SQLGATE-CUTOVER-03`  
**Date:** 2026-10-15  
**Status:** PROPOSED  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Related Plans:** [security.md](security.md), [implementation/00-implementation-roadmap.md](implementation/00-implementation-roadmap.md)  
**Author / Lead:** Application Security Engineer & Solution Architect  

---

## 1. Executive Summary & Context

### 1.1 Context
In Phase 1 and 2 of the AST SQL compiler migration (WP-X1), the legacy string/token-stream rewriter (`RewriteRls`) was removed from the production codebase (`src/**`). Architectural IL scanning tests ([`CutoverArchitectureTests`](file:///root/autheris/tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs)) enforce that no legacy rewriter types, methods, or options exist in production assemblies.

To prevent silent security regressions during the cutover, the differential compiler gate (Gate G4 in [`Autheris.Tests.SqlCompilerGate`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate)) evaluates live AST compilation outcomes against frozen baseline results ([`golden-results/legacy-<Dialect>.json`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/golden-results/legacy-PostgreSql.json)).

### 1.2 Current State & Problem
Currently, **18 triage entries** (covering **57 unique SQL statements** / 148 dialect×statement combinations) in [accepted-differences.json](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json) are marked with `"approval": "pending"`, alongside **2 approved `legacyDefects` entries** (4 statements).
While `"pending"` is valid for pull-request builds (`SQLGATE_PROFILE=pr`), it blocks the final cutover profile (`SQLGATE_PROFILE=cutover`), which strictly requires:
```csharp
accepted.HasPendingEntries.ShouldBeFalse("accepted-differences.json still has pending entries: the security reviewers have not signed off");
```
Furthermore, 15 of the 18 entries still carry the generic auto-generated placeholder justification (`"Triage proposal of the gate run: ... Needs security review before approval."`) from [`AcceptedDifferencesWriter`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/Classification/AcceptedDifferencesWriter.cs) and need concrete security justifications before approval.

### 1.3 Scope & Objectives
1. **Replace Generic Triage Justifications & Sign Off:** Replace the auto-generated triage text in all 18 entries of `accepted-differences.json` with the verified root-cause justifications documented below, assign a reviewer (`@security-lead`) and issue reference, and transition `"approval"` from `"pending"` to `"approved"`.
2. **Phase 3 Cutover Verification:** Execute the full gate in `SQLGATE_PROFILE=cutover` mode locally and via the GitHub Actions workflow.
3. **Decommissioning Roadmap:** Establish the criteria and procedure for retiring `FrozenLegacy` golden baselines after cutover sign-off.

---

## 2. Statement-by-Statement Security & Completeness Evaluation

### 2.1 Overview of the 18 Pending Entries (57 Unique Statements)

| Reason (`ast-stricter`) | Dialects | Entries | Unique Stmts | Dialect×Stmt Pairs | Root Cause & Security Verdict |
|---|---|:---:|:---:|:---:|---|
| `FewerRows` | Oracle | 1 | 2 | 2 | **Critical Legacy Bug Fixed (`CR-ADG-01`):** Quoted CTE `WITH "orders" AS (SELECT 1 AS id)` vs. unquoted `FROM orders` / `FROM ORDERS`. Legacy skipped tenant filtering and let Oracle bind unquoted `ORDERS` to the physical table, leaking all 12 rows across tenants. AST binds to the CTE and returns `[1]`. |
| `UnfilteredDmlException` | Oracle, PG, MSSQL, SQLite | 4 | 6 | 24 | **DML Tautology Guard:** Rejects `DELETE FROM orders` with constant or self-comparison predicates (`WHERE 1=0`, `WHERE 1<2`, `WHERE 100+200=300`, `WHERE id IN (id)`, `WHERE id BETWEEN id AND id`, `WHERE EXISTS (SELECT 1)`). |
| `MaskedAggregationException` | PG, MSSQL, SQLite | 3 | 4 | 12 | **Frequency/Cardinality Leak Prevention:** Under `TenantConsentMask`, rejects `GROUP BY email` on a masked column. Legacy masked only `SELECT` while grouping on raw values. |
| `SqlCompileNotSupportedException` | Oracle, MSSQL, SQLite | 3 | 9 | 13 | **Type-Coercion & Side-Channel Guard (`SEC-ADG-16`):** All 9 statements compare `WHERE tenant_id = 42` (integer literal) against text column `tenant_id`. AST rejects numeric-to-text column comparisons to prevent `ORA-01722` / conversion-error side channels and SQLite affinity quirks. |
| `SecurityException` | Oracle, PG, MSSQL, SQLite | 4 | 31 | 86 | **4 Hardening Subgroups:**<br/>1. Masked column `email` in `WHERE`/`ORDER BY` (10 stmts)<br/>2. `UPDATE`/`INSERT` touching policy column `region`, masked column `email`, or duplicate `SET` column (10 stmts)<br/>3. CTE table-shadowing, case-collision, or unlabelled recursion (7 stmts)<br/>4. Uncataloged `pg_indexes`, quoted function/type names, `current_time` (4 stmts). |
| `AstBuildException` | PG, MSSQL, SQLite | 3 | 9 | 11 | **2 Subgroups:**<br/>1. Intentional security/portability rejections (6 stmts: `CAST(... AS xml)`, `current_user`, subquery in window frame, `GROUPS` frame, `current_timestamp(3)`, Databricks-only `TIMESTAMP_NTZ` on SQLite)<br/>2. Unimplemented ANSI/Trino syntax in `SqlAstBuilder` (3 stmts on PG: `OVERLAY(... PLACING ...)`, `TIMESTAMP '... UTC' AT TIME ZONE ...`). |
| **Total** | **All 4 Dialects** | **18** | **57** | **148** | **0 regressions; 54 intentional security hardenings, 3 benign unimplemented Trino expressions (`OVERLAY`, `AT TIME ZONE`).** |

### 2.2 Approved Legacy Defects (2 Entries, 4 Unique Statements)
* **PostgreSQL (`582b869c9140`, `5730f78b10ab`, `d119663e7737`):** `WITH RECURSIVE chain ...` — Legacy failed to iterate recursive CTE members (returning 8 anchor rows instead of 24). The AST compiler returns all 24 tenant-scoped rows with zero `PolicyOracle` violations.
* **Oracle (`16eb9fc1d7da`):** `SELECT EXISTS(SELECT 1)` — Legacy emitted Oracle native boolean (`Boolean:True`); AST emits portable `1`/`0` (`Int64:1`).

---

## 3. Implementation Steps & Checklist

### Phase 1: Security Triage & Approval
- [ ] **Task 1.1:** Replace the generic `"Triage proposal of the gate run..."` justification strings in [accepted-differences.json](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json) with specific security justifications per entry (see Section 2.1).
- [ ] **Task 1.2:** Set `"approval": "approved"`, `"reviewer": "@security-lead"`, and `"issue"` reference on all 18 entries in `accepted-differences.json`.
- [ ] **Task 1.3:** Decide whether `OVERLAY(... PLACING ...)` (`23208521524f`, `f41a2db71b5b`) or `AT TIME ZONE` (`80d5942dea95`) are needed for production workloads; if not, keep them fail-closed under `AstBuildException`.

### Phase 2: Cutover Verification
- [ ] **Task 2.1:** Run local verification under the cutover profile:
  ```bash
  SQLGATE_PROFILE=cutover dotnet test tests/Autheris.Tests.SqlCompilerGate/Autheris.Tests.SqlCompilerGate.csproj
  ```
- [ ] **Task 2.2:** Verify that [`CutoverPrecondition_EveryAcceptedDifferenceIsApproved`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/AcceptedDifferencesTests.cs#L104) passes.
- [ ] **Task 2.3:** Trigger `.github/workflows/sql-compiler-gate-nightly.yml` with `profile: cutover`.

### Phase 3: Legacy Baseline Retirement (Post-Cutover)
- [ ] **Task 3.1:** Keep `FrozenLegacy` (`golden-results/legacy-*.json`) active until the Phase 3 cutover sign-off is merged to `main`.
- [ ] **Task 3.2:** After sign-off, either snapshot the governed AST outcomes as the permanent regression baseline or retire `FrozenLegacy` in favor of `PolicyOracle` + `ConsumerParityTests` + `RendererInvariantTests`.

---

## 4. Verification & Testing

```bash
# 1. Verify schema and approval validity
dotnet test tests/Autheris.Tests.SqlCompilerGate --filter "FullyQualifiedName~AcceptedDifferencesTests"

# 2. Verify all gate tests under cutover profile
SQLGATE_PROFILE=cutover dotnet test tests/Autheris.Tests.SqlCompilerGate

# 3. Verify architecture guards against legacy types
dotnet test tests/Autheris.Tests.Architecture --filter "FullyQualifiedName~CutoverArchitectureTests"
```

---

## 5. Changelog & Refinement Notes
- **2026-10-15:** Initial draft created and refined with complete statement-by-statement audit of all 18 pending entries (57 unique statements) and 2 legacy defect entries.
