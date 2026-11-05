# WP-SIMPL-04 Docs describing non-existent components
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-04-docs-describing-non-existent-components-512) | Issues: #512 | Owner decisions: OD-17 (accepted defaults), OD-30 (ReBAC removed; ADR-020) | Security conditions: none specific (SEC-SIMPL-04 applies to any config sample touched)

## 1. Goal and outcome
Feature docs describe only code that exists. Measurable: 0 feature docs for unimplemented features (f-ai-09, f-ai-10, f-gov-06, f-perf-10 deleted), 0 feature docs for removed features (f-sec-04 ReBAC deleted: OD-30 removes ReBAC, executed by WP-SIMPL-46; no replacement doc), and 0 occurrences of "JDBC", "ODBC", "Flight SQL" claims in `README.md` and `docs/` outside `docs/plans/` except allow-listed lines.

## 2. Preconditions and dependencies
- After WP-SIMPL-03 (it creates `tests/Autheris.Tests.Architecture/DocsClaimsArchitectureTests.cs`, which this WP extends). Parallel with WP-SIMPL-01/02/06.
- WP-SIMPL-05 merges after this WP (it adds the link/path gates that cover these edits).
- WP-SIMPL-16 later deletes the Arrow "Flight" endpoints; this WP only fixes wording.

## 3. Scope
### In scope
- Delete `docs/features/f-sec-04-rebac-openfga.md` and its index row (OD-30: ReBAC is removed by WP-SIMPL-46; no replacement doc).
- Delete `f-ai-09-native-vector-database-rag-egress.md`, `f-ai-10-semantic-cache-policy-recommendation.md`, `f-gov-06-cross-domain-joins.md`, `f-perf-10-streaming-pipelining.md` and their index rows.
- Replace JDBC/ODBC/Flight SQL claims with "HTTP endpoints returning Arrow IPC".
- New test methods in the existing `DocsClaimsArchitectureTests` (created by WP-SIMPL-03): `Protocol_claims_require_interop_test`, `Feature_index_rows_point_to_existing_files`.
### Out of scope
- Deleting `ArrowFlightSqlEndpoints.cs` or `f-data-04-arrow-flight-sql.md` (WP-SIMPL-16; this WP only rewords f-data-04).
- Link rewriting, status tags, German doc (WP-SIMPL-05). Performance claims (WP-SIMPL-03).
- Removing `RebacOptions.OpenFgaApiUrl/OpenFgaStoreId` (WP-SIMPL-06) and the ReBAC code itself (WP-SIMPL-46, OD-30).

## 4. Current state (verified on 63cbb893)
- `docs/features/README.md:22` F-AI-09, `:23` F-AI-10, `:70` F-GOV-06, `:94` F-PERF-10 (all marked "(not implemented)"), `:98` F-SEC-04 "ReBAC via OpenFGA / Zanzibar", `:50` F-DATA-04 "Native Apache Arrow Flight SQL Egress".
- ReBAC code (removed later by WP-SIMPL-46, OD-30): `src/Autheris.Application/Security/Rebac/Services/InMemoryRebacStore.cs`, `.../ZanzibarRebacEvaluator.cs`; no OpenFGA client exists; only unconsumed `RebacOptions.OpenFgaApiUrl/OpenFgaStoreId` (`src/Autheris.Domain/Options/Security/RlsOptions.cs:47-48`).
- Files with JDBC/ODBC/Flight SQL words outside docs/plans (11): `README.md`, `docs/comparisons/autheris-vs-immuta.md`, `docs/configuration-guide.md`, `docs/endpoints-and-testing.md`, `docs/features/README.md`, `docs/features/f-api-03-odata-openapi.md`, `docs/features/f-data-04-arrow-flight-sql.md`, `docs/features/f-dialect-02-ast-sql-compiler.md`, `docs/features/f-gov-09-virtual-filters.md`, `docs/features/f-sec-05-gateway-hardening-defense.md`, `docs/operations-runbook.md`.
- Endpoint file `src/Autheris.Api/Endpoints/ArrowFlightSqlEndpoints.cs` serves Arrow IPC over HTTP (not the Flight gRPC protocol).
- Test helper `RepositoryScan` exists in `tests/Autheris.Tests.Architecture` (used by `OptionsConsumptionInventoryTests.cs:35`).
- Links to `f-sec-04-rebac-openfga.md` outside docs/plans: `README.md:164`, `docs/features/README.md:98`.

## 5. Implementation steps (TDD order)
1. RED: extend `tests/Autheris.Tests.Architecture/DocsClaimsArchitectureTests.cs` (created by WP-SIMPL-03) with `Protocol_claims_require_interop_test` and `Feature_index_rows_point_to_existing_files` (see section 6). Scan set: `README.md`, root `*.md`, `docs/**/*.md` excluding `docs/plans/**` and `docs/adr/**` (ADRs are historical decisions). Regex (case-insensitive): `\b(JDBC|ODBC|gRPC|Flight SQL|Arrow Flight)\b`. Allow-list file `tests/Autheris.Tests.Architecture/Baselines/protocol-claim-allowlist.txt`, format `relative/path.md|<substring of the line>|<TestClass.TestMethod proving it>`; each allow-listed test name must exist as a method in the test assemblies (reflection over `Autheris.Tests.*` assemblies referenced by the architecture project, or a text grep of `tests/**/*.cs` for `void <Method>(`). Start with an empty allow-list. Test fails listing file:line.
2. GREEN, docs: `git rm docs/features/f-sec-04-rebac-openfga.md` and delete its index row `docs/features/README.md:98` (ReBAC is removed by OD-30 / WP-SIMPL-46; no replacement doc). Remove the ReBAC bullet at root `README.md:164` (keep no link to the deleted doc).
3. `git rm` the four unimplemented feature docs; delete `docs/features/README.md` rows 22, 23, 70, 94; grep the repo (`git grep -n 'f-ai-09\|f-ai-10\|f-gov-06\|f-perf-10\|f-sec-04-rebac-openfga'`) and fix every remaining link outside `docs/plans/` (inside docs/plans leave text as is).
4. Reword the 11 files: "Arrow Flight SQL" -> "Arrow IPC over HTTP"; remove JDBC/ODBC client claims entirely (no driver exists); `docs/features/f-data-04-arrow-flight-sql.md` keeps its filename until WP-SIMPL-16 but its title/text says "HTTP endpoints returning Arrow IPC"; `docs/features/README.md:50` index title likewise. Keep the literal route names if they contain "flight" only inside backticks; the test ignores backticked spans (strip `` `...` `` before matching).
5. Run the test; until green either reword or (only for true statements like "no JDBC driver is provided") add an allow-list line pointing to an existing test (e.g. a negative statement may cite `DocsClaimsArchitectureTests.Protocol_claims_require_interop_test` itself). Prefer rewording; target allow-list size 0.

## 6. Tests
Project `tests/Autheris.Tests.Architecture`:
- `DocsClaimsArchitectureTests.Protocol_claims_require_interop_test` - asserts no non-allow-listed, non-backticked protocol word in the scan set; asserts every allow-list entry's file exists, its substring still occurs, and its test method exists (stale entries fail).
- `DocsClaimsArchitectureTests.Feature_index_rows_point_to_existing_files` - every `](f-*.md)` link in `docs/features/README.md` resolves (cheap guard until WP-SIMPL-05 lands the general link test).
No security-trait tests (docs only). No tests deleted.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build --filter "FullyQualifiedName~DocsClaimsArchitectureTests"`
- `git grep -n -i -E 'jdbc|odbc|flight sql' -- README.md 'docs/*.md' ':!docs/plans' ':!docs/adr'` -> only backticked or allow-listed hits.
- `git grep -n 'f-sec-04-rebac-openfga\|f-ai-09\|f-ai-10\|f-gov-06\|f-perf-10' -- ':!docs/plans'` -> empty.

## 8. Acceptance criteria
- [ ] Five feature docs deleted (f-ai-09, f-ai-10, f-gov-06, f-perf-10, f-sec-04); no index rows or links to them outside docs/plans.
- [ ] Both new tests green; allow-list file exists (may be empty).
- [ ] Release build with `-warnaserror` green.

## 9. PR slicing
One PR (docs + one test file, < 200 src-equivalent lines). Title `docs: remove docs of non-existent components (#512)`.

## 10. Risks, rollback, must NOT do
- Risk: dangling links in docs that WP-SIMPL-05 would flag; mitigated by step 3 grep. Rollback: revert the PR.
- Must NOT delete `ArrowFlightSqlEndpoints.cs`, options, or any src code. Must NOT edit `docs/plans/` history text. Must NOT add status tags or WIP notes to docs.

## 11. Estimate
+~120 test lines, docs -~1,350 lines net (5 deleted docs), 1.25 person-days.

## Decisions taken in this spec
- ADRs (`docs/adr/`) excluded from the protocol scan: they record decisions, not current claims.
- f-sec-04 is deleted, not rewritten (OD-30 removes ReBAC; a doc for code about to be deleted is not written). Supersedes the plan's "rewrite and rename".
- Backticked spans are exempt so route/class names (`ArrowFlightSqlEndpoints`) do not need allow-listing before WP-SIMPL-16.
