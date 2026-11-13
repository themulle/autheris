# WP-SIMPL-45 PR 3 addendum: CI repair after the engine write permit (#653)

Parent spec: [WP-SIMPL-45-governed-write-access.md](WP-SIMPL-45-governed-write-access.md) (steps 5 and 7, sections 9, 10 and 12). Base: `main` at `10400ec4`.
Scope: test code only. The engine stays as merged: a null `GovernancePolicy.Write` means every DML statement is a `SecurityException`, MERGE is always rejected, and a column outside `WritableColumns` is rejected (SEC-SIMPL-120, -121, -124). There is no `src/` change, no skipped test and no deleted test without a replacement assertion. In production every DML statement stays denied until WP-45 PR 4 and WP-46 wire the decision-point write decision.

## Decisions

**R-1 Default test permit (all three helpers use the same rule).** `For(sql, policy)` finds the DML target and builds `DmlWritePermit(op, writable, protected)`:
- `op`: the operation of the statement (INSERT, UPDATE or DELETE). For MERGE, use `Update`. That permit only exists so the test reaches the specified MERGE rejection.
- `writable`: every catalog column of the target where `policy.Masks.HasMask(entry.Identity, column)` is false. The set is case-insensitive and includes the tenant and policy columns, so the existing tenant and SEC-ADG-06 checks keep their own messages.
- `protected`: an empty set.
- The helper returns `null` for SELECT and for unparseable SQL, so the engine reports its own error. If the operation is known but the target does not resolve, it returns a permit with an empty writable set, so unknown-target tests keep the unknown-target message.

An optional `alsoWritable` parameter adds named masked columns. Tests that assert a masked write succeeds use it, which is the SEC-SIMPL-121 case where a masked column is writable only when named.

The helper never replaces a non-null `Policy.Write`. It is applied only inside a seam's `Compile(sql, …)`, never inside a `Request(…)` method that does not see the SQL. A test that calls `Engine.Compile(sql, env.Request())` and needs a permit calls `.WithTestWritePermit(sql)` explicitly.

**R-2 Where the default must not be used:**
- `AstSecurityVisitorWritePermitTests` and the explicit permits in `CompileApiTests`.
- Every test that asserts the null-permit rejection.
- The new production-default tests in R-6.
- `GovernancePolicyFactoryTests`.

If a DML-rejection test that passes today turns red once the helper is applied, it was passing only because of the permit check. Report it as a finding. Do not work around it by removing the helper.

**R-3 One helper per test project:**

| Project (WP) | File | Signature | Target detection |
|---|---|---|---|
| TrinoSqlEngine.Tests (A) | `tests/TrinoSqlEngine.Tests/Support/TestWritePermits.cs` | `internal static DmlWritePermit? For(string sql, GovernancePolicy policy, IEnumerable<string>? alsoWritable = null)`; `internal static CompileRequest WithTestWritePermit(this CompileRequest r, string sql, IEnumerable<string>? alsoWritable = null)` | AST: `FastSqlEngine.Parse` plus `new SqlAstBuilder(new AstBuilderOptions { EnforceReadOnlyQueries = false }).BuildStatement(tree)`, as in `ConformanceRunner.Prepare`, then read `Insert/Update/Delete/MergeStatement.TargetTable` |
| Autheris.Tests.Unit (C) | `tests/Autheris.Tests.Unit/TestDoubles/TestWritePermits.cs` and `TestDoubles/TestWritePermitSqlEngine.cs` | Same as A, plus `sealed class TestWritePermitSqlEngine(FastSqlEngine inner) : ISqlEngine`, which delegates every member to `inner` and applies `WithTestWritePermit` in `Compile`; it exposes `Inner` for the cache-stats tests | AST (same as A; `InternalsVisibleTo` exists) |
| Autheris.Tests.Integration (B) | `tests/Autheris.Tests.Integration/TestWritePermits.cs` | `public static DmlWritePermit? For(string sql, GovernancePolicy policy, IEnumerable<string>? alsoWritable = null)` | Lexical: strip `--` and `/* */` comments and string literals, then take the first `\b(INSERT\s+INTO\|UPDATE\|DELETE\s+FROM\|MERGE\s+INTO)\s+<qualified name>` match (this also handles `WITH … DELETE`), unquote the parts and call `policy.Catalog.Resolve(new SqlQualifiedName(parts))` |
| Autheris.Tests.SqlCompilerGate (B) | `tests/Autheris.Tests.SqlCompilerGate/Harness/GateWritePermits.cs` | `public static DmlWritePermit? For(string statementClass, string sql, GovernancePolicy policy)` | `op` comes from `CorpusStatement.Class`; the target is detected lexically, as in Integration |

The helpers are duplicated deliberately (about 40 lines each), so that no file is shared between the work packages.

**R-4 MERGE migration (D-45-4, SEC-SIMPL-124).** Every test or fixture that asserted a MERGE rewrite, emission, guard message, `UnfilteredDmlException`, typed MERGE error or executed MERGE row counts now asserts the specified rejection:
- With the default permit: `SecurityException` with the exact message `MERGE is not supported for governed writes.`
- With a null permit: `A write permit is required for DML operations.`
- `SqlCompileNotSupportedException` stays only where the statement-class gate fires first: `Statements` lacks `Merge`, the dialect's `DmlStatements` lacks it, or `SupportsMerge` is false (only SQLite). Generator-level MERGE errors (for example Oracle `MERGE … THEN DELETE`) can no longer be reached and become `SecurityException`.

Keep the original SQL inputs as theory data, so every former MERGE variant now proves the rejection. Rename each test to `…_IsRejected_MergeNotSupported`. Integration MERGE tests also assert that the table snapshot is unchanged, reusing the contract's existing "nothing changes" assertions.

Conformance:
- In `Fixtures/{PostgreSql,SqlServer,Databricks,DuckDb,Oracle}.json`, change every `dml-merge-*` case that is not a parse rejection to `"Outcome":"rejected"`, `"ExceptionType":"SecurityException"` and `"Parameters":[]`, and drop `"Expected"`. That is 11 cases in each of the first four files and 11 in Oracle (9 `ok` and 2 not-supported). `Sqlite.json` is unchanged.
- In `Fixtures/rejected-nodes.json`, move `MergeStatement`, `MergeUpdateClause`, `MergeDeleteClause` and `MergeInsertClause` to `"*"` with the reason "MERGE is rejected for governed writes (WP-SIMPL-45 D-45-4)". Delete the SQLite and Oracle `Merge*` entries.
- Edit the fixtures by hand (a one-off script is fine but is not committed), because TrinoSqlEngine.Tests does not run locally.
- The exact message is pinned by the new theory `DmlCompileTests.Merge_IsRejected_ForGovernedWrites_InEveryMergeCapableDialect`, over the `dml-merge-*` corpus inputs.

The SqlCompilerGate needs no `accepted-differences.json` change: legacy never accepted MERGE, so MERGE is classified as `BothRejected`.

**R-5 Expected-message changes.** The engine checks DML in this order:
1. null permit
2. operation mismatch
3. target
4. INSERT columns / UPDATE assignments: duplicate, tenant (UPDATE), not covered, write-protected, masked, policy column
5. masked reads
6. subquery
7. unfiltered

As a result:
- An INSERT list or UPDATE assignment that targets a masked column now gets `A column is not covered by a write grant.` instead of `A masked column must not be written.`. This includes "permitted when the switch is off" tests. To keep that coverage, they pass `alsoWritable` and still compile.
- The verifier message `A masked column is written by the statement.` (DmlCoverageTests mutants) stays, because the default permit excludes masked columns.
- `UnfilteredDmlException` expectations on UPDATE and DELETE stay unchanged once a permit is supplied.

Low follow-up, outside this repair: delete the code paths that can no longer be reached:
- the `masked && !writable` branches in `ResolveInsertColumns` and `SecureAssignments`;
- MERGE securing in `AstSecurityVisitor`, `MergeAliasGuard` and the MERGE branch of `SecurityCoverageVerifier.Dml.cs`;
- dialect MERGE emission;
- the dialect capability entries `SupportsMerge`.

Delete them in one cleanup PR after CI is green. Stryker may report survivors there until then.

**R-6 The 25 Autheris.Tests.Unit pipeline tests use option (a), a test-only seam with no `src/` change.**

The seam: `GovernedSqlExecutionServiceFactory.Create(…, sqlEngine:)` and the `GovernedSqlRewriter(…, sqlEngine:)` constructor parameter already accept `ISqlEngine`. The test fixtures pass `new TestWritePermitSqlEngine(new FastSqlEngine())`:
- `BypassSemanticsAndDmlGuardrailTests.DmlFixture` (`:544`) and `ClientListFixture` (`:152`)
- `GovernedSqlExecutorX1Tests` (`:100`, `:155`, `:352`, `:601`, including `CompileAsync`, which `DmlWriteAheadAuditTests` uses)
- `WebSqlExecutorAndConnectorSecurityTests` (`:227`)
- `GovernedPipelineCachePoisoningTests` (wrap `_engine`; read the cache stats from `Inner`)
- `TestCompileEnv.Compile`, used by `SecurityReviewRemediationCoverageTests`, which applies `WithTestWritePermit`

Why production stays fail-closed:
- DI registers `FastSqlEngine` directly (`GatewaySqlEngineServiceExtensions.cs:72`).
- `GovernancePolicyFactory` never sets `Write`.
- The decorator exists only in the test assembly.

So nothing derives a permit from roles (SEC-SIMPL-120), and enforcement stays in the shared pipeline (SEC-SIMPL-125).

Why not option (b): (b) would turn the audit, rollback and row-limit coverage into denial-only tests, and that coverage would be lost until WP-46. The permit is part of the compile-cache key (`CompileCacheKey.Material` reflects over `Policy.Write`), so the decorator cannot poison the cache.

New mandatory tests (`[Trait("Category","Security")]`):
- `GovernedSqlExecutorX1Tests.WebSqlDml_WithTheProductionEngine_IsDeniedForLackOfAWritePermit_AndNothingIsExecuted`: factory with the default engine and a writer role; the statement is rejected through the existing `SecurityException` mapping, and no command reaches the connection.
- `GovernancePolicyFactoryTests.Create_NeverSetsAWritePermit_UntilWriteGrantsAreWired`.
- `GovernedPipelineCachePoisoningTests.CachePoisoning_PermittedWriterThenUnpermittedWriter_IsDenied`: same SQL and statements, the second compile without a permit throws the permit message, and the hit count is unchanged.
- `ArchitectureAndCleanCodeTests.DmlWritePermit_IsConstructedOnlyInTheEngine_UntilTheDecisionPointIsWired`: a source scan of `src/**/*.cs` for `new DmlWritePermit(` outside `src/TrinoSqlEngine/`, with an empty allow-list. WP-45 PR 4 adds the decision-point mapping as the only allowed entry.

## Work packages (no shared files)

**WP-A, `tests/TrinoSqlEngine.Tests/**`.** Add R-3 A. Apply it in:
- `ProbeEnv.Compile` (`Security/SecurityProbeSupport.cs:154`)
- `ConformanceRunner.Run` (`DmlRequest(dialect).WithTestWritePermit(input)`)
- `TimeZoneTestSupport`, `ClientListHarness` and `Properties/GateHarness.cs`, in their SQL-taking compile methods
- the class-local compile helpers of `DmlCompileTests`, `TypedDmlMessageMutationTests`, `DmlCoverageTests`, `TypedGuardMutationTests`, `LiteralBindTypeTests` and `BindLimitProductionProbeTests`

Then apply the R-4/R-5 assertion changes:

| Failing class | Fix |
|---|---|
| DmlCompileTests (44) | Seam. `Merge_*` and `OracleMerge_*` follow R-4. Add the R-4 theory. |
| TypedDmlMessageMutationTests (14) | Seam. `InsertColumns_MustBeListedOnceAndNotMasked` and `MaskedReads_…SwitchIsOff` follow R-5 (`alsoWritable`). `Merge_*` follow R-4. |
| DmlCoverageTests (10) | Seam. `Merge_*` and `FaultyEmitter_…MergeTerminator` follow R-4 (the compile throws before the verifier). The rest stay unchanged. |
| AstSecurityVisitorDmlTests, AstSecurityDmlPrecedenceTests, SecurityRemediationTests, SecurityReviewMediumSqlEngineTests, SqlParserLexerAndRewriterDifferentialTests, AstSimplificationVisitorTests, AstSecurityRegressionTests, DmlLimitAndAliasSql78Tests | Seam (`ProbeEnv`, the local `Compile`, or an explicit `.WithTestWritePermit(sql)` at each `Engine.Compile(sql, Request())` call). Messages follow R-5. |
| TypedGuardMutationTests, ClientListMutationTests, ClientListBinderTests, TimeZoneBinderTests, LiteralBindTypeTests, BindLimitProductionProbeTests | Seam only. |
| ConformanceMatrixTests (`dml-*` cases) | Runner seam plus the R-4 fixture and `rejected-nodes.json` edits. |

**WP-B, `tests/Autheris.Tests.Integration/**` and `tests/Autheris.Tests.SqlCompilerGate/**`.**
- Integration: change `AstCompilerDmlContract.Request(string tenant)` to `Request(string tenant, string sql)` with the `sql` parameter required, setting `Write = TestWritePermits.For(sql, policy)`. Making it required means the compiler finds every caller: `:94`, `:111`, `:502`, `ExecAsync`, `ExecCheckedAsync`, and the dialect subclasses (`Sqlite:128`, `PostgreSql:134`, `SqlServer:133` (MERGE), `DatabricksSpark:224`).
- Integration `Merge_*` tests (all 6 dialect classes) follow R-4, including `:690`, `:693`, `:704`, `:751`, `:761`, `:823` and `Merge_InsertOfAnotherTenantsKey…`, which currently fails with a NullReferenceException. `:678` stays as it is.
- Gate: change `GatePolicies.AstRequest(StatementPermissions permissions)` to `AstRequest(StatementPermissions permissions, string? statementClass = null, string? sql = null)`. `GateRunner.cs:106` passes `statement.Class` and `statement.Sql`; the `SELECT` probe at `:47` stays as it is. `Corpus/dml.sql` and the golden results are unchanged.

**WP-C, `tests/Autheris.Tests.Unit/**`.** R-3 C, R-6 wiring and the R-6 new tests. The 25 failing tests keep their assertions, except where R-5 applies.

## Verification

Run one build at a time and check `df` before each build. Each WP must pass `dotnet build Autheris.sln -c Release -p:AutherisIncludeTestAuth=true -warnaserror`.

WP-C additionally runs `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj --filter` with these classes: `DmlWriteAheadAuditTests`, `GovernedSqlExecutorX1Tests`, `BypassSemanticsAndDmlGuardrailTests`, `ClientListWebSqlTests`, `WebSqlDmlGuardTests`, `GovernedPipelineCachePoisoningTests`, `SecurityReviewRemediationCoverageTests`, `WebSqlExecutorAndConnectorSecurityTests`, `GovernancePolicyFactoryTests` and `ArchitectureAndCleanCodeTests`.

WP-A and WP-B are verified by CI only: Build & Test All Suites, SQL Compiler Gate steps 1 and 2, Integration shards 1-3, and Spark.

Done when CI is fully green, `git diff --stat origin/main -- src` is empty, and every class in the failing lists maps to a row above.

## Owner question (default taken)

Q-1: Developers cannot run TrinoSqlEngine.Tests locally, so they cannot regenerate the conformance fixtures with `CONFORMANCE_UPDATE=1`. Default taken: hand-edit the fixtures as R-4 specifies and let CI verify them. Alternative: allow one local regeneration run (`--filter ConformanceFixtureRegenerator`) as an exception.

## Phase 3 conditions (binding)

Verdict: APPROVED WITH CONDITIONS. Verified facts: only `src/TrinoSqlEngine` references `DmlWritePermit`; `CompileCacheKey.Material` reflects over the request and skips only `RowFilters`, `Masks` and `Catalog` of the policy, so `Policy.Write` is part of the key; DI registers `FastSqlEngine` directly. The plan keeps the permit fail-closed and the seam test-only.

- **SEC-CIFIX-1 (no src change).** The repair must leave `git diff origin/main -- src` empty. The test helpers, `TestWritePermitSqlEngine` and the `GateWritePermits` class must not be linked into any `src/` project (no `Compile Include`, no `InternalsVisibleTo` added, no shared project).
- **SEC-CIFIX-2 (seam stays out of DI).** No registration of `TestWritePermitSqlEngine` or `ISqlEngine` decoration may appear in `src/` or in a production composition root. Test-host fixtures that boot the real app (Integration `WebApplicationFactory`) must not override `ISqlEngine` with the decorator. The R-6 decorator is used only through the explicit factory or constructor `sqlEngine:` parameter.
- **SEC-CIFIX-3 (cache key covers the permit).** Add a unit test that the compile-cache key differs for permits that differ in operation, in writable set, in protected set and in null versus non-null. The R-6 poisoning test alone is insufficient. If any permit member is not part of `Material` (for example a set whose type the reflection does not enumerate), stop and report it as a blocking finding. Do not fix it inside this repair.
- **SEC-CIFIX-4 (helper never widens).** The helper must never replace a non-null `Policy.Write`, must return null for non-DML, and must return a permit with an empty writable set when the target does not resolve. Never place any column in `protected` (D-45-4 and SEC-SIMPL-121 stay tested by explicit permits).
- **SEC-CIFIX-5 (rejection tests are not weakened).** Every existing DML-rejection test (tenant column, SEC-ADG-06 policy column, masked write, unfiltered, subquery, cross-tenant, unknown target) must be checked under the default permit. A test that is red after the helper but was green before is a finding, reported in the PR description. It must not be fixed by widening the permit or by loosening the asserted message. Message changes are limited to the R-5 list.
- **SEC-CIFIX-6 (masked and protected explicit).** `alsoWritable` is allowed only in tests that assert that a masked column is writable when named. Each such test needs a sibling assertion that the same statement without `alsoWritable` is rejected with `A column is not covered by a write grant.`.
- **SEC-CIFIX-7 (MERGE rejection is exact).** Each migrated MERGE test asserts the exact `SecurityException` message and, in Integration, an unchanged table snapshot. No MERGE test may assert success, and no `try/catch` that accepts several exception types. `rejected-nodes.json` edits must not remove non-MERGE entries.
- **SEC-CIFIX-8 (lexical helpers fail closed).** The lexical target detection (Integration, Gate) must return an empty writable set (not all columns) when no unambiguous target is found, and must ignore matches inside comments and string literals.
- **SEC-CIFIX-9 (no skipped or deleted coverage).** No `Skip`, `[Fact(Skip=...)]`, `#if` or deleted test without a replacement assertion. Reviewer checks the count of renamed MERGE tests against the failing list.

Mandatory tests (all `[Trait("Category","Security")]`, all runnable in `Autheris.Tests.Unit`):
1. `GovernedSqlExecutorX1Tests.WebSqlDml_WithTheProductionEngine_IsDeniedForLackOfAWritePermit_AndNothingIsExecuted` (from R-6).
2. `GovernancePolicyFactoryTests.Create_NeverSetsAWritePermit_UntilWriteGrantsAreWired` (from R-6).
3. `GovernedPipelineCachePoisoningTests.CachePoisoning_PermittedWriterThenUnpermittedWriter_IsDenied` (from R-6).
4. `ArchitectureAndCleanCodeTests.DmlWritePermit_IsConstructedOnlyInTheEngine_UntilTheDecisionPointIsWired` (from R-6). Extend the scan to `src/**/*.cs` including object-initializer and target-typed `new(` forms for `DmlWritePermit`.
5. `ArchitectureAndCleanCodeTests.TestWritePermitSqlEngine_IsReferencedOnlyByTests` (new): no file under `src/` mentions `TestWritePermit`.
6. `CompileCacheKeyTests.Key_DiffersByWritePermitOperationWritableAndProtectedColumns` (new, SEC-CIFIX-3).
7. `GovernedSqlExecutorX1Tests.WebSqlDml_MaskedColumnWrite_WithoutExplicitGrant_IsRejected` (new, SEC-CIFIX-5 and -6: through the decorator, a masked column write is rejected with the not-covered message).

CI-only (no local run required): TrinoSqlEngine.Tests, Integration, Spark and SqlCompilerGate follow SEC-CIFIX-4, -5, -7 and -8 by review.
