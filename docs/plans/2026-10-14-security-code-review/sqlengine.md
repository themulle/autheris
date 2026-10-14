# TrinoSqlEngine security review — src/TrinoSqlEngine

Scope: Analysis/, Ast/ (builder, rewriter, dialect generators, security visitors), RlsListener.cs, FastSqlEngine*.cs,
SqlFunctionAllowlists.cs/SqlFunctionPolicy.cs, SqlIdentifierHelper.cs, RowFilterAliases.cs, UnfilteredDmlException.cs,
ZeroCopyCaseInsensitiveStream.cs, plus reachability through Autheris.Application/Sql (GovernedSqlRewriter) and
Autheris.Application/Services/AdvancedRlsFilterGenerator / VirtualFilters/StructuredFilterSqlBuilder.

Method: static review + a throwaway console harness
(a throwaway local console project) referencing
`TrinoSqlEngine.csproj` directly, driving both `RewriteRls` engines (`LegacyTokenStream` / `AstCompiler`) and
`Analyze`, plus a second project stress-testing nesting/size limits. Outputs were additionally replayed against
SQLite (`python3 -I` + `sqlite3`) to confirm result-set differences where feasible.

---

### Medium — Correlated row-filter / consent-subquery can be shadowed by a user-supplied CTE of the same name
- **File:** `src/TrinoSqlEngine/RlsListener.cs:629-668` (`BuildReplacement`, verbatim policy-filter splice), `src/Autheris.Application/Services/AdvancedRlsFilterGenerator.cs:60-150`, `src/Autheris.Application/VirtualFilters/StructuredFilterSqlBuilder.cs:60-107`
- **CWE:** CWE-863 (Incorrect Authorization) / CWE-694 (identifier/name resolution ambiguity)
- **Confidence:** Confirmed (harness-verified) for the mechanism; reachability in production depends on configuration (see below)
- **Issue:** The RLS/consent row filter the policy provider returns (`GetPolicyFilter`) is spliced into the rewritten SQL as **raw text**, correlated through the reserved alias `autheris_target` (`RowFilterAliases.Target`). Neither `RlsListener` nor the AST compiler re-parses or re-validates that filter text against the *user's* CTE scope. If the filter references its dependent/consent table by an **unqualified** (single-part) name, a user-supplied `WITH <thatName> AS (...)` in the outer query shadows the real physical table for every reference inside that scope — including inside the verbatim-spliced `EXISTS (...)` predicate, because CTE name resolution happens entirely inside the backend engine (PostgreSQL/SQL Server/SQLite/etc.), which always prefers a CTE over a physical table of the same unqualified name within the query it is defined in.
- **Exploit scenario (harness-confirmed, SQLite):** Policy filter for table `secret` is `tenant_id = 'T1' AND EXISTS (SELECT 1 FROM "consents" AS d WHERE d."subject"='u1' AND d."row_id" = autheris_target."id")`. Compiling
  ```sql
  WITH consents(subject,row_id) AS (VALUES ('u1',1),('u1',2),('u1',3))
  SELECT * FROM secret
  ```
  produces (both `LegacyTokenStream` and `AstCompiler`):
  ```sql
  WITH consents(subject,row_id) AS (VALUES ('u1',1),('u1',2),('u1',3))
  SELECT * FROM (SELECT "id","v" FROM secret AS autheris_target
                 WHERE tenant_id='T1' AND EXISTS (SELECT 1 FROM "consents" AS d
                       WHERE d."subject"='u1' AND d."row_id" = autheris_target."id")) AS secret
  ```
  Executed against a real engine, the inner `"consents"` resolves to the attacker's CTE, not the real consent table, so every row with `id` 1, 2 or 3 passes the consent check regardless of the real grants — confirmed by running the equivalent two queries directly against SQLite: without the CTE only row `id=1` is returned (correct), with the attacker's CTE in scope all three rows are returned.
- **Production reachability:** `AdvancedRlsFilterGenerator.FormatTableIdentifier` and `StructuredFilterSqlParts`/`StructuredFilterSqlBuilder` always schema-qualify the dependent/consent table (`"schema"."table"` / `[schema].[table]` / `` `schema`.`table` ``), and a CTE can only be referenced unqualified, so the *current* generators are not shadowable this way. The gap is in the underlying mechanism (`RlsListener`/AST compiler never check whether a policy-provider-supplied filter's table references collide with the caller's CTE names), not in the current callers. It becomes exploitable the moment any `IRlsPolicyProvider`/`IPolicyPredicateProvider` implementation (current or future, including test/ops tooling, `SqlFilterCompiler`, or a bespoke integrator) returns a filter with an **unqualified** table reference, which the public API does not forbid or validate anywhere in the engine.
- **Remediation:** Validate (fail closed) in the compiler itself — not only by convention in callers — that any table name appearing inside a spliced/typed row-filter or consent predicate is schema-qualified relative to the catalog, or alternatively walk the policy-filter AST (it is already parsed as `Governance/PolicyExpressionParser.cs` for the typed path) and reject/qualify any unqualified table reference that collides with a CTE name visible at the injection point. For the legacy text-splice path (`RlsListener.BuildReplacement`), either stop accepting unqualified table names in `PolicyFiltersAreTargetDialectSql` mode, or defensively rename the outer CTE scope so dependent-table names used in generated filters can never be shadowed (e.g. prefix every spliced filter's physical table references with the gateway's reserved schema).

### Medium — INSERT tenant `WITH CHECK OPTION` only validates the *first* occurrence of the tenant column in a duplicated column list (`RlsListener`, legacy engine)
- **File:** `src/TrinoSqlEngine/RlsListener.cs:381-399` (`EnterInsertInto`, tenant-index lookup)
- **CWE:** CWE-863 (Incorrect Authorization)
- **Confidence:** Confirmed (harness-verified against SQLite; PostgreSQL/SQL Server reject duplicate INSERT column names outright and are not affected)
- **Issue:** `EnterInsertInto` finds `tenantIndex` by scanning the column-alias list and **breaking on the first match** (`if (col.Equals(tenantColumn,...)) { tenantIndex = i; break; }`). If the statement's column list names the tenant column twice (once as written, once in a different case, e.g. `tenant_id` and `TENANT_ID`, both normalized case-insensitively to the same column), only the **first** occurrence's value is checked by `EnsureTenantLiteral`; the second occurrence's value is never inspected and is passed through unchanged.
- **Exploit scenario:**
  ```sql
  INSERT INTO orders (id, tenant_id, TENANT_ID) VALUES (1, 'T1', 'T2')
  ```
  The rewriter accepts this unchanged (confirmed: `OUT:` equals `IN:` verbatim, no rewrite, no exception) because it only validated the `tenant_id` at index 1 (`'T1'`, matches the expected tenant). On a backend that tolerates duplicate column names in `INSERT` with “last value wins” semantics (verified with SQLite: the row ends up with `tenant_id = 'T2'`), this inserts a row into another tenant's partition — a tenant-hopping write despite `WITH CHECK OPTION`/`RequireTenantColumnInInsert` being enabled. PostgreSQL and SQL Server reject duplicate INSERT columns with a parse-time error, so the exposure is backend-dependent (SQLite and any MySQL-compatible target that tolerates duplicate column lists).
- **Remediation:** When scanning the column-alias list for the tenant column, reject the statement outright (fail closed) if the tenant column name appears **more than once** (case-insensitively, after identifier normalization), instead of silently taking the first match. Apply the same duplicate-detection to every WITH-CHECK-relevant column, not just the tenant column, since the same first-match pattern could be reused for masked/denied columns in the future.

### Low — `SELECT *` with a masked column still exposes the mask via `FOR ... *` dereference and whole-tuple SELECT shapes that evade the simple column-name walk
- **File:** `src/TrinoSqlEngine/RlsListener.cs:646-666` (`BuildReplacement`, column projection), `src/TrinoSqlEngine/Analysis/SqlQueryAnalyzer.cs` (no finding here; informational)
- **CWE:** CWE-200
- **Confidence:** Likely (not harness-confirmed against a real masking/consent integrator; verified only that the engine itself does not reject it)
- **Issue:** The masked projection the legacy engine builds only replaces masked **columns**; a bare whole-row reference to the replaced subquery's alias (`SELECT orders.* FROM orders` or `SELECT orders FROM orders` in a SELECT, as opposed to DML) is **not** blocked by `RejectWholeRowReferencesInDml` (that guard is DML-only; see `EnsureNoMaskedColumnReferences`, which is only invoked from `EnterDelete`/`EnterUpdate`). For `SELECT`, `orders.*`/`orders` resolves against the already-masked subquery, so the mask itself is *not* bypassed by this construct (confirmed by harness: `SELECT orders FROM orders` and `SELECT o.* FROM orders o` both correctly surface `'***' AS "email"`). This is listed as a verified non-issue after testing, but is recorded here because it is adjacent to SEC-HIGH-15 (masked column oracles) and the apparent asymmetry (DML guard vs. no equivalent SELECT guard) could look like a gap to a future reviewer; no exploit was found. Downgraded to informational/Low because no masked value was observed to leak in any of the whole-row/whole-table projections tested in either engine.
- **Remediation:** None required functionally. Consider adding a short code comment at `EnsureNoMaskedColumnReferences`'s call sites (`EnterDelete`/`EnterUpdate`) noting explicitly that `SELECT` whole-row references are safe only because masking is applied at the projection stage before the name resolves, so a future refactor of `BuildReplacement` does not accidentally make `SELECT *`/`SELECT tbl.*` bypass masking.

---

## Verified strengths

- RLS injection is applied consistently across every tested table-reference shape in both engines: plain `FROM`, qualified `schema.table`, `JOIN ... USING`, `NATURAL JOIN`, scalar/`IN`/`EXISTS`/`ANY` subqueries, `LATERAL`, set operations (`UNION ALL`), `TABLE` shorthand, and non-recursive/recursive CTEs that do *not* collide with a physical table name.
- A CTE is prevented from silently shadowing (and thus unsecuring) a physical table of the same name at the AST layer: `WITH RECURSIVE orders AS (...) SELECT * FROM orders` is rejected outright (`AstCompiler`: "A recursive CTE must not have the name of a catalog table").
- Masked columns are consistently replaced with their mask expression in every projected-column shape tested (`SELECT *`, explicit column, qualified `alias.*`, bare table-name projection) in both engines, and DML referencing masked columns in `SET`/`WHERE`/`ORDER BY`/`FILTER` is rejected fail-closed (`SEC H-15`/`R-53` guards all fired correctly in the harness).
- Statement-class gating is fail-closed: `MERGE`, `CALL`, `SET SESSION`, `EXPLAIN`, `EXECUTE` are all rejected by the legacy engine's `EnterSingleStatement` allowlist; the governed `GovernedSqlCompiler` independently fails closed on any statement-class flag it does not recognize.
- Function allowlist/denylist checks are quote- and case-insensitive on both sides (`"pg_read_file"`, `PG_SLEEP`, `pg_catalog.pg_read_file`, `pg_catalog.upper` were all evaluated correctly — the two legitimate `upper` variants passed, every other probed function on the deny side was rejected).
- INSERT tenant verification correctly rejects non-literal, concatenation (`'T1' || 'x'`), wildcard-projection, and every-set-operation-branch INSERT…SELECT shapes, and verifies **every** VALUES row and every UNION branch individually.
- Statement/token guards reject multi-statement injection (`;`), nested block comments, backslash-escaped strings, dollar-quoting (when configured), and non-ASCII identifiers outside string/quoted-identifier tokens.
- Parser/AST has multiple independent depth limits (token nesting depth, parse-tree depth, dedicated-thread stack guard, separate AST nesting-depth check in the governed compiler) that all fired well before any stack overflow in deliberately pathological inputs (2900-deep arithmetic/`OR` chains, 2000-way cross joins, 500-level nested nested CTEs/subqueries); no crash or hang was reproduced.
- `ZeroCopyCaseInsensitiveStream.LA` ASCII-only fast-fold is safe: non-ASCII and Turkish-İ/ı characters are passed through unfolded (not miscompared), consistent with the lexer rejecting non-ASCII identifiers by default in governed mode.

## Areas not covered

- Databricks/Snowflake/Oracle/Datbricks-specific dialect generator quoting/escaping corner cases (only PostgreSQL/SQL Server/SQLite dialects were exercised with the harness; no live Oracle/Databricks/Snowflake instance was available to differentially verify generated SQL).
- `TreeSqlCompiler` / `GovernedTreeQueryService` (the tree-based compile path) was out of scope per the assignment and not analyzed.
- Live round-trip execution against real PostgreSQL/SQL Server/DuckDB backends (no such servers were available in this sandbox); SQLite was used as a stand-in for backend-semantics checks (duplicate INSERT columns, CTE shadowing), which is dialect-accurate for those two findings but cannot confirm/refute dialect-specific behavior on the actual production targets.
- `SecurityCoverageVerifier` / `DialectCapabilityValidator` / `EmittedSqlInvariantChecker` (the governed `Compile` pipeline's post-emission proof stages) were read but not adversarially fuzzed against a faulty injector; the DEBUG-only `CompilerTestSeams.FaultyInjector` hook exists for that purpose and already has dedicated tests (`SecurityMutantGateTests.cs`) that were not independently re-run here.
- Full differential fuzzing of `SqlDialectGeneratorBase.Expressions.cs` string-literal escaping across all eight `TargetSqlDialect` values (only spot checks were performed for PostgreSQL/SQL Server/SQLite/DuckDB-adjacent paths).

---

**Summary:** This review examined RLS/table-security, function allowlists, DML guards, CTE/subquery handling, dialect quoting, and parser DoS limits in `src/TrinoSqlEngine`, using a live harness exercising both the legacy token-stream rewriter and the AST compiler. Findings: 0 Critical, 0 High, 2 Medium (a CTE-name-shadowing gap in the correlated row-filter/consent-subquery splice mechanism — not reachable through current production callers because they schema-qualify dependent table names, but unguarded in the engine itself; and an INSERT tenant-check bypass via a duplicated tenant-column name in the column list, exploitable only against backends that tolerate duplicate INSERT columns such as SQLite), 1 Low (a non-exploitable but noteworthy asymmetry between DML and SELECT whole-row-reference guards around masked columns), 0 additional Critical/High regressions of prior SEC-CRIT/SEC-HIGH findings were found. No new DML/DDL detection gaps, no new function-allowlist bypass, and no new parser-DoS vector were confirmed beyond what the existing layered depth limits already catch.
