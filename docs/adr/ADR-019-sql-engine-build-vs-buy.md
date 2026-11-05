# ADR-019: SQL Engine Build vs. Buy (Parser, Security Visitor, Dialect Generators)

## Status

Proposed (pending owner acceptance). Date: 2026-11-05. Revised the same day for binding owner decisions OD-12 (Oracle and Databricks kept as governed data sources) , OD-14 (Snowflake generator deleted, Ansi kept generator-only) and OD-15 (cross-source federation and DuckDB stay core); they override earlier text of this ADR where they conflict.

Supplements ADR-017 §2 and its amendments of 2026-10-11 and 2026-10-13. ADR-017 introduced the AST dialect generator without an alternatives assessment; this ADR records that assessment after the fact and narrows the dialect scope. On acceptance it supersedes the tier list in the 2026-10-11 amendment (item 5) and the admission list in the 2026-10-13 amendment (item 4).

## Context

The project simplification PRD (`docs/plans/2026-11-05-req-project-simplification.md`, REQ-SIMPL-07, decision D5, OD-1, OD-2/D14) asks whether Autheris should keep building its own SQL engine or adopt an existing one. The question is asked now because:

- The engine is a large, permanent maintenance cost. Measured on 2026-11-05: `src/TrinoSqlEngine` 25,698 lines; `TrinoSqlEngine.Tests` 33,692; `Autheris.Tests.SqlCompilerGate` 5,772; plus `Autheris.Tests.Fuzz` and 3 Stryker configurations (the engine config breaks below 72%). Engine plus engine tests: 59,390 lines.
- The dialect surface is wider than the evidence. `TargetSqlDialect` has 8 values (Ansi, PostgreSql, SqlServer, Sqlite, DuckDb, Snowflake, Oracle, Databricks) and there are 6 generator files (SqlServer 693, Oracle 559, Sqlite 433, PostgreSql 375, Databricks 364, Analytical for DuckDb/Snowflake/Ansi 453 lines) on a shared base of about 1,900 lines. The parity suite is SQLite-centric (33 SQLite cases vs. 1 DuckDB), and the master plan still lists the SQL Server, PostgreSQL and DuckDB parity baselines as open.
- OD-1 (binding) removes SQLite as the governance store; OD-2 (default taken) removes SQLite as a data source as well, staged last.

Not all of the engine was written in-house. Parsing uses ANTLR4 (`Antlr4.Runtime.Standard` 4.13.1) with a grammar derived from Trino (`SqlBase.g4`, 1,573 lines). The in-house parts are the typed AST and `SqlAstBuilder` (1,673 lines), the `AstSecurityVisitor` (13 partials, 3,234 lines), capability validation, and the dialect generators and binders. The engine package has 3 package references in total.

## Decision Drivers

1. **AST-level policy injection.** Row filters, column masks and consent rules must be added as typed AST nodes before emission, and coverage must be checked (ADR-017 amendment 2026-10-11, items 1 and 3). The enforcement point must be in-process and under Autheris control.
2. **Fail-closed rejection.** Any construct the security visitor does not fully understand must be rejected with a typed error. It must never be passed through. A permissive parser fails this driver unless an allowlist is put in front of it.
3. **Dialect coverage with evidence.** Each supported dialect needs a parity baseline against a real engine. A generator without parity evidence is a liability.
4. **Effort.** Both the one-time migration cost and the ongoing maintenance cost.
5. **Operational footprint.** No extra runtime (JVM, Python) and no extra network hop on the query path.
6. **Sunk vs. ongoing test investment.** The existing tests count only if they keep protecting the code that remains. Their past cost alone does not decide anything.
7. **Supply chain.** Number, maintenance status and licences of dependencies on the security-critical path.

## Considered Options

1. **Keep and shrink** the in-house engine to the dialects that have parity tests.
2. **Replace the parser** with an existing .NET parser and keep the security visitor. Candidates: `Microsoft.SqlServer.TransactSql.ScriptDom` (MIT, maintained by Microsoft, T-SQL only), community multi-dialect parsers such as the C# port of sqlparser-rs, and native `libpg_query` wrappers for PostgreSQL.
3. **Sidecar engine** (sqlglot in Python, or Apache Calcite on the JVM) for parsing and transpiling, called over IPC.
4. **Delegate to a query engine** (Trino or DuckDB) and push policy down into it, for example through Trino's system access control row filters and column masks.
5. **No free-form SQL.** Clients get only parameterised or allowlisted query shapes, generated from GraphQL/REST.

## Assessment

| Driver | (1) Keep + shrink | (2) .NET parser swap | (3) Sidecar (sqlglot/Calcite) | (4) Delegate (Trino/DuckDB) | (5) No free-form SQL |
|---|---|---|---|---|---|
| AST-level RLS/mask/consent injection | Yes, existing typed path | Yes, but the visitor needs a mapping layer for each parser AST, or must be rewritten for each one | Only if the visitor is rewritten in Python/Java, or the AST is serialised in both directions | Enforcement moves into the engine; Autheris can only configure it | Yes; there is nothing to parse |
| Fail-closed on unsupported constructs | Yes; the grammar and visitor allowlist are ours (G1-G10 gate) | Weaker: ScriptDom and sqlparser ports are built to accept broadly, so an allowlist must be rebuilt in front of the visitor | Weak by default: sqlglot transpiles on a best-effort basis and needs a strict error level; Calcite is stricter but has its own dialect | Depends on the engine's access-control semantics | Strongest: only known shapes exist |
| Dialect coverage | PostgreSQL, SQL Server, Oracle, Databricks, with parity | T-SQL only (ScriptDom); PostgreSQL through native wrappers of uncertain maintenance; two ASTs | Broad on paper; fidelity is not proven per dialect | Broad, but on the engine's terms | Not applicable |
| One-time effort | S-M (deletion and parity work) | L (new parser per dialect, mapping, gate re-baseline) | L (sidecar, protocol, visitor port) | XL (product re-architecture) | L (Web SQL, async jobs and MCP SQL tools removed or redesigned) |
| Ongoing effort | Medium, bounded by 4 dialects | Medium-high (track upstream AST changes for each parser) | High (two runtimes, two languages) | High (cluster operations) | Low for SQL; product loss |
| Operational footprint | None added | Native library for PostgreSQL wrappers | Extra container, IPC on hot path, new CVE stream | JVM cluster (Trino) or embedded engine without native SQL Server federation (DuckDB) | Lowest |
| Test investment | ~45k lines stay relevant; generator tests for dropped dialects go | Parser and builder tests lost; visitor tests partly kept | Most engine tests lost | Most engine tests lost | Engine tests lost; GraphQL/REST compile tests stay |
| Supply chain | 3 packages (ANTLR runtime, build tasks, ObjectPool) | +1-3 parsers, at least one single-maintainer | Python or JVM dependency tree | Full engine distribution | Smallest |

Notes:

- Option 2 replaces only the grammar and `SqlAstBuilder` (about 3,200 lines). The visitor and generators stay. The saving is small and two dialect-specific ASTs are added, which the current single typed AST avoids.
- The GraphQL tree compiler emits SQL through the same generators. So option 5 does not remove the generators; it removes only the parser, builder and part of the visitor.
- Option 4 goes against the product positioning in ADR-015 (no external warehouse cluster, enforcement in the gateway).

## Decision

**Option 1: keep the in-house engine and shrink it to the dialects that have parity tests.**

1. **Core dialect set = PostgreSQL, SQL Server, Oracle and Databricks** (D5 as amended by binding owner decision OD-12, 2026-11-05: Oracle database support and Databricks as data source are kept). These are the only `TargetSqlDialect` values that the core accepts once the shrink is done, plus SQLite until OD-2 stage 3. Oracle keeps its `Production` status from the ADR-017 amendment of 2026-10-11; Databricks keeps `DatabricksOboTokenProvider`, the statement-execution executor and its connection types. The governance store stays PostgreSQL and SQL Server only (OD-1); Oracle and Databricks are governed data sources, never governance stores.
2. **SQLite is removed in OD-2 stages.** First the governance store (OD-1, binding). Then the tests and the parity suite move to PostgreSQL Testcontainers and fakes. Last, `SqliteDialectGenerator`, `SqliteTypeAffinity` and the SQLite enum value are removed. The owner may stop after any stage. Until the last stage, SQLite stays a supported dialect and must meet the same parity rule.
3. **Ansi stays a generator-only dialect; the Snowflake generator is deleted** (binding owner decision OD-14, revised 2026-11-05). Ansi emits SQL as today but is not admitted as a data source (`SqlDialectMapper`, `DialectAdmissionTests`). **DuckDB stays core** (binding OD-15: cross-source joins are core): the DuckDB generator emits the federated staging SQL for `FederatedDuckDbExecutionService`/`CrossSourcePlanner`; it is an internal federation target, not an admitted data source.
4. **A configured dialect outside the core set fails closed at startup** through configuration validation. Compile-time rejection stays as a second line of defence.
5. **No new parser dependency.** ANTLR4 and the Trino-derived grammar stay. Grammar productions that only the deleted dialects used may be pruned when an allowlist test proves them unreachable.
6. **The rationale.** Option 1 is the only option that meets drivers 1 and 2 without new runtimes or a mapping layer. It also has the smallest one-time effort. The real problem was unproven dialect breadth, not the decision to build. Shrinking fixes that directly.

## Implementation Staging

The detailed work packages are in the implementation plan. This ADR fixes only the order:

1. **Parity first.** PostgreSQL and SQL Server parity baselines are built against Testcontainers and turned green on every PR. Oracle runs a security-tagged smoke subset on PR (Testcontainers.Oracle, pre-pulled image) and the full suite nightly. Databricks runs contract tests on PR (generated-SQL snapshots plus an executor contract against a fake statement-execution API) and the full suite nightly against a real workspace when credentials exist, otherwise against the Spark container. Pending cells for the four core dialects are closed before any generator is deleted.
2. **Non-core generators.** Only the Snowflake generator and enum value are deleted (OD-14). Ansi stays generator-only and DuckDB stays the core federation staging target (OD-15); `DialectAdmissionTests` keeps both out of data-source admission. Federation/DuckDB paths get core parity and integration coverage. Oracle and Databricks generators, connectivity and packages stay (OD-12).
3. **Startup validation.** An out-of-set dialect in data-source configuration fails the start with a typed configuration error.
4. **SQLite stages per OD-2.** The governance store goes first, then tests and parity, then the dialect. The SQLite parity suite is removed only after step 1 is green.
5. **Gate trim.** Stryker configurations are merged to at most 2, and the engine `break` threshold stays at 72 or higher.

## Consequences

### Positive

- The code to maintain shrinks to four generators on a shared base (plus SQLite until OD-2 stage 3). Every remaining generator has parity evidence: PostgreSQL, SQL Server and Oracle against a real engine, Databricks by contract on PR and against a real workspace or the Spark container nightly.
- The security-critical path keeps a single typed AST and a single fail-closed visitor. There is no IPC hop and no second runtime.
- The supply chain stays at 3 engine packages. `Oracle.ManagedDataAccess.Core` stays in Infrastructure (OD-12); Databricks needs no driver package (HTTP statement-execution API).
- The engine tests that remain protect code that remains.

### Negative / Risks

- Oracle and Databricks stay, so the engine shrinks less (about 2,200 more engine lines than a two-dialect core) and CI carries an Oracle container and a Databricks/Spark nightly lane. Mitigation: full Oracle and Databricks suites run nightly; PRs run only the security-tagged Oracle smoke subset and the Databricks contract tests.
- The zero-dependency SQLite quickstart goes away after OD-2 stage 3, and DB-backed tests get slower (Testcontainers).
- A parity gap exists between SQLite parity removal and full PostgreSQL/SQL Server parity. Mitigation: the SQLite parity suite is not deleted until both new baselines are green.
- The team still owns grammar and generator maintenance (ongoing driver 4). The revisit triggers below bound this cost.

## Measurable Targets

| Metric | Baseline (2026-11-05) | Target |
|---|---|---|
| `TargetSqlDialect` values accepted by the core | 8 | 4 as data sources (PostgreSql, SqlServer, Oracle, Databricks); 5 while SQLite stage 3 is pending; Ansi and DuckDb stay as non-admitted generator values (OD-14, OD-15); Snowflake removed |
| Generator files in `src/TrinoSqlEngine/Ast/Generators` (excluding base/factory) | 6 | 5 (PostgreSql, SqlServer, Oracle, Databricks, Analytical with Ansi + DuckDb) (+1 SQLite until stage 3) |
| TrinoSqlEngine + TrinoSqlEngine.Tests lines | 59,390 | <= 48,500 (previous 45,000 + kept Oracle/Databricks generators 923 and engine tests 1,284 + kept Ansi/DuckDb generators about 360 and DuckDb engine tests 279, rounded up) |
| Parity cells per supported dialect, in CI | SQLite-centric; SQL Server/PostgreSQL baselines open | 100% of matrix cells, 0 `Pending`/`KnownGap` entries for core dialects; PG/SS on PR, Oracle smoke on PR + full nightly, Databricks contract on PR + real/Spark nightly |
| `sql-compiler-gate` (G1-G10) | green | green for each core dialect |
| Engine mutation score (Stryker `break`) | 72 | >= 72; not lowered |
| Stryker configurations | 3 | <= 2 |
| Package references in `TrinoSqlEngine.csproj` | 3 | <= 3 |
| Startup with an out-of-set dialect | accepted at config, rejected at compile/admission | fails at startup validation (typed error) |

Verification: line counts with `wc -l` over the two projects; the generator count from the folder listing; parity from the parity CI results file; mutation from the Stryker report of the engine config.

## Revisit Triggers

Re-open this ADR when any of the following happens:

1. A named customer or design partner commits to a third dialect. Use the module route from Decision 3; do not change the core.
2. A maintained .NET parser offers both PostgreSQL and T-SQL fidelity together with a strict (reject-unknown) mode. Re-assess option 2.
3. The product drops free-form SQL (Web SQL, async SQL jobs, MCP SQL tools). Re-assess option 5.
4. The engine's mutation score stays below the floor for two consecutive releases, or engine-originated security advisories exceed one per quarter for two quarters.
5. The ANTLR4 C# runtime becomes unmaintained, or has an unfixed CVE on the parse path.
6. The product strategy moves federation to an external lakehouse engine (would amend ADR-015). Re-assess option 4.

## References

- ADR-017 §2 and its amendments 2026-10-11 and 2026-10-13 (`docs/adr/ADR-017-distributed-state-ast-generator-and-rbac.md`)
- ADR-015 (positioning without an external warehouse cluster)
- PRD `docs/plans/2026-11-05-req-project-simplification.md` (section 3.3, REQ-SIMPL-07, REQ-SIMPL-12, REQ-SIMPL-13, D5, D14, OD-1, OD-2)
- Implementation plan `docs/plans/2026-11-05-plan-project-simplification.md`
- `docs/plans/2026-10-10-plan-ast-dialect-generator.md` (gate G1-G10)
