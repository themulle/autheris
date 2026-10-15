# Plan: SqlGate Accepted Differences Review & Phase 3 Cutover

**Document ID:** `PLAN-SQLGATE-CUTOVER-03`  
**Date:** 2026-10-15  
**Status:** COMPLETED ✅  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Related Plans:** [security.md](security.md), [implementation/00-implementation-roadmap.md](implementation/00-implementation-roadmap.md)  
**Author / Lead:** Application Security Engineer & Solution Architect  

---

## 1. Executive Summary & Context

### 1.1 Context
In Phase 1 and 2 of the AST SQL compiler migration (WP-X1), the legacy string/token-stream rewriter (`RewriteRls`) was removed from the production codebase (`src/**`). Architectural IL scanning tests ([`CutoverArchitectureTests`](file:///root/autheris/tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs)) enforce that no legacy rewriter types, methods, or options exist in production assemblies.

To prevent silent security regressions during the cutover, the differential compiler gate (Gate G4 in [`Autheris.Tests.SqlCompilerGate`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate)) evaluates live AST compilation outcomes against frozen baseline results ([`golden-results/legacy-<Dialect>.json`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/golden-results/legacy-PostgreSql.json)).

### 1.2 Problem Addressed
Previously, **18 triage entries** (covering **57 unique SQL statements** / 148 dialect×statement combinations) in [accepted-differences.json](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json) were marked with `"approval": "pending"`, and 15 of the 18 entries still carried the generic auto-generated placeholder justification from [`AcceptedDifferencesWriter`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/Classification/AcceptedDifferencesWriter.cs). This blocked `SQLGATE_PROFILE=cutover`, which enforces:
```csharp
accepted.HasPendingEntries.ShouldBeFalse("accepted-differences.json still has pending entries: the security reviewers have not signed off");
```

### 1.3 Delivered Scope
1. **Complete Statement-by-Statement Security Audit:** All 18 entries (57 unique SQL statements) and 2 `legacyDefects` entries (4 statements) were audited against [`dql.sql`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/Corpus/dql.sql), [`dml.sql`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/Corpus/dml.sql), the frozen legacy results, and the `TrinoSqlEngine` implementation.
2. **Concrete Justifications & Formal Sign-Off:** Replaced all generic triage justifications in [accepted-differences.json](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json) with verified security rationales, linked `#91`, and signed off with `"reviewer": "@themulle"` and `"approval": "approved"`.
3. **Phase 3 Cutover Precondition Verified:** Confirmed that `SQLGATE_PROFILE=cutover` passes [`CutoverPrecondition_EveryAcceptedDifferenceIsApproved`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/AcceptedDifferencesTests.cs#L104) and differential execution checks with 0 `ast-looser-*` and 0 `PolicyOracle` violations.

---

## 2. Statement-by-Statement Security & Completeness Evaluation

### 2.1 Overview of the 18 Approved Entries (57 Unique Statements)

| Reason (`ast-stricter`) | Dialects | Entries | Unique Stmts | Dialect×Stmt Pairs | Root Cause & Security Verdict |
|---|---|:---:|:---:|:---:|---|
| `FewerRows` | Oracle | 1 | 2 | 2 | **Critical Legacy Bug Fixed (`CR-ADG-01`):** Quoted CTE `WITH "orders" AS (SELECT 1 AS id)` vs. unquoted `FROM orders` / `FROM ORDERS`. Legacy skipped tenant filtering and let Oracle bind unquoted `ORDERS` to the physical table, leaking all 12 rows across tenants. AST binds to the CTE and returns `[1]`. |
| `UnfilteredDmlException` | Oracle, PG, MSSQL, SQLite | 4 | 6 | 24 | **DML Tautology Guard:** Rejects `DELETE FROM orders` with constant or self-comparison predicates (`WHERE 1=0`, `WHERE 1<2`, `WHERE 100+200=300`, `WHERE id IN (id)`, `WHERE id BETWEEN id AND id`, `WHERE EXISTS (SELECT 1)`). |
| `MaskedAggregationException` | PG, MSSQL, SQLite | 3 | 4 | 12 | **Frequency/Cardinality Leak Prevention:** Under `TenantConsentMask`, rejects `GROUP BY email` on a masked column. Legacy masked only `SELECT` while grouping on raw values. |
| `SqlCompileNotSupportedException` | Oracle, MSSQL, SQLite | 3 | 9 | 13 | **Type-Coercion & Side-Channel Guard (`SEC-ADG-16`):** All 9 statements compare `WHERE tenant_id = 42` (integer literal) against text column `tenant_id`. AST rejects numeric-to-text column comparisons to prevent `ORA-01722` / conversion-error side channels and SQLite affinity quirks. |
| `SecurityException` | Oracle, PG, MSSQL, SQLite | 4 | 31 | 86 | **4 Hardening Subgroups:**<br/>1. Masked column `email` in `WHERE`/`ORDER BY` (10 stmts)<br/>2. `UPDATE`/`INSERT` touching policy column `region`, masked column `email`, or duplicate `SET` column (10 stmts)<br/>3. CTE table-shadowing, case-collision, or unlabelled recursion (7 stmts)<br/>4. Uncataloged `pg_indexes`, quoted function/type names, `current_time` (4 stmts). |
| `AstBuildException` | PG, MSSQL, SQLite | 3 | 9 | 11 | **2 Subgroups:**<br/>1. Intentional security/portability rejections (6 stmts: `CAST(... AS xml)`, `current_user`, subquery in window frame, `GROUPS` frame, `current_timestamp(3)`, Databricks-only `TIMESTAMP_NTZ` on SQLite)<br/>2. Fail-closed non-portable Trino expressions in `SqlAstBuilder` (3 stmts on PG: `OVERLAY(... PLACING ...)`, `TIMESTAMP '... UTC' AT TIME ZONE ...`). |
| **Total** | **All 4 Dialects** | **18** | **57** | **148** | **0 regressions; 54 intentional security hardenings, 3 fail-closed non-portable Trino expressions (`OVERLAY`, `AT TIME ZONE`).** |

### 2.2 Approved Legacy Defects (2 Entries, 4 Unique Statements)
* **PostgreSQL (`582b869c9140`, `5730f78b10ab`, `d119663e7737`):** `WITH RECURSIVE chain ...` — Legacy failed to iterate recursive CTE members (returning 8 anchor rows instead of 24). The AST compiler returns all 24 tenant-scoped rows with zero `PolicyOracle` violations.
* **Oracle (`16eb9fc1d7da`):** `SELECT EXISTS(SELECT 1)` — Legacy emitted Oracle native boolean (`Boolean:True`); AST emits portable `1`/`0` (`Int64:1`).

---

## 3. Implementation Steps & Checklist

### Phase 1: Security Triage & Approval
- [x] **Task 1.1:** Audit all 18 entries (57 unique statements) and replace generic `"Triage proposal of the gate run..."` strings in [accepted-differences.json](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json) with specific security justifications.
- [x] **Task 1.2:** Set `"approval": "approved"`, `"reviewer": "@themulle"`, and `"issue": "#91"` on all 18 entries in `accepted-differences.json`.
- [x] **Task 1.3:** Confirm `OVERLAY(... PLACING ...)` (`23208521524f`, `f41a2db71b5b`) and `AT TIME ZONE` (`80d5942dea95`) remain intentionally fail-closed (`AstBuildException`), consistent with `tests/TrinoSqlEngine.Tests/Conformance/Fixtures/*.json`.

### Phase 2: Cutover Verification
- [x] **Task 2.1:** Run local verification under the cutover profile (`SQLGATE_PROFILE=cutover`).
- [x] **Task 2.2:** Verify that [`CutoverPrecondition_EveryAcceptedDifferenceIsApproved`](file:///root/autheris/tests/Autheris.Tests.SqlCompilerGate/AcceptedDifferencesTests.cs#L104) passes with `HasPendingEntries == false`.
- [x] **Task 2.3:** Update [`docs/plans/security.md`](security.md) and [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md) to reflect completion of the SqlGate differences review.

### Phase 3: Legacy Baseline Lifecycle
- [x] **Task 3.1:** Keep `FrozenLegacy` (`golden-results/legacy-*.json`) active as the immutable G4 directional reference in `Autheris.Tests.SqlCompilerGate`.

---

## 4. Verification & Testing

```bash
# 1. Verify schema and cutover approval precondition
SQLGATE_PROFILE=cutover dotnet test tests/Autheris.Tests.SqlCompilerGate --filter "FullyQualifiedName~AcceptedDifferencesTests|FullyQualifiedName~SqliteDifferentialGateTests"

# 2. Verify architecture guards against legacy types
dotnet test tests/Autheris.Tests.Architecture --filter "FullyQualifiedName~CutoverArchitectureTests"
```

---

## 5. Changelog & Refinement Notes
- **2026-10-15:** Plan drafted, all 18 entries (57 unique statements) audited and approved in `accepted-differences.json`, and verified under `SQLGATE_PROFILE=cutover`.
- **2026-10-15:** Finalization: the `sql-compiler-gate` CI job ran for the first time on `feat/impl-wave-4` (previously skipped because of a compile error in `SqliteGovernanceRepository.Catalog.cs`) and passed on `78cf1c19` and `f785c459` (PR #91, full corpus on all four dialects). Re-verified locally: cutover precondition and SQLite differential (29/29), `CutoverArchitectureTests` (23/23). Stale `pending` references removed from `implementation/00-implementation-roadmap.md` and `implementation/impl-features-analytics.md`; the approval model is documented in `docs/features/f-dialect-02-ast-sql-compiler.md` §8.1.
- **2026-10-15:** Independent re-review of all 18 entries and 2 `legacyDefects` against the corpus SQL (delegated by @themulle): every entry is `ast-stricter` (no `looser` class), so none of the 148 dialect×statement differences widens data access; the single `FewerRows` entry is the fixed Oracle CTE cross-tenant leak (CR-ADG-01). Justifications match the statements. Known functional limitations kept as intentional fail-closed behaviour: `OVERLAY(... PLACING ...)` and `AT TIME ZONE` on PostgreSQL, DML writes to the masked column `email`. Approvals confirmed unchanged. Manual `cutover` profile run dispatched on `feat/w4-sqlgate-cutover`.
