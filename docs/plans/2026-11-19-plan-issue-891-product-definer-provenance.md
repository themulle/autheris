# Plan: own provenance flag for product definer decisions (Issue #891)

Phase 2 (solution architect), 2026-11-19. Code facts verified on `origin/main` `e3e32b401`. Implementation starts after the v1.9.0 tag.

## 1. Problem and evidence

- Issue: [#891](https://github.com/themulle/autheris/issues/891).
- `src/Autheris.Application/Sql/Services/FederatedDuckDbExecutionService.cs:640` (`AuthorizeDefinerTables`, declared at line 592 and called at line 459) builds an unconstrained `TableAccessDecision.Allowed(...)` with `EvaluatedByPdp = true`. It then runs `GovernedSqlExecutionService.EnforceMaskedColumnGuardrails`.
- `EvaluatedByPdp` is `internal bool` on `src/Autheris.Domain/Interfaces/TableAccessDecision.cs:70`. Its documented meaning (MISC-06) is "set only by the PDP after the full pipeline ran".
- The only reader is `src/Autheris.Application/Analytics/GovernedAggregationService.ValidationAndExecution.cs:35`, which trusts a `PreEvaluatedDecision` only with that flag. The only producer of `PreEvaluatedDecision` is `SemanticMetricQueryService.cs:291`, which uses PDP decisions. Definer decisions never reach it.
- The ratchet `Architecture_PdpProvenance_IsStampedOnlyInsideThePdpNamespace` (`tests/Autheris.Tests.Architecture/AuthorizationArchitectureTests.cs:107-118`) allowlists `FederatedDuckDbExecutionService.cs`.

## 2. Options

| Option | Description | Verdict |
|---|---|---|
| A | Remove the stamp from the definer path and add nothing else. | Possible, because no reader needs it. But it loses the explicit statement of who decided. |
| B | Add `internal bool EvaluatedAsProductDefiner { get; init; }`, set only in `AuthorizeDefinerTables`. `EvaluatedByPdp` is set only in `Policy/Pdp`. Readers that trust provenance keep accepting `EvaluatedByPdp` only. | **Recommended** (as the issue expects). |
| C | Replace both booleans with an enum `DecisionProvenance { None, Pdp, ProductDefiner }`. | Rejected for now: it touches 25 PDP stamp sites for no functional gain. |

## 3. Decisions

1. D-891-1: Option B. The definer path sets `EvaluatedAsProductDefiner = true` and no longer sets `EvaluatedByPdp`.
2. D-891-2: `GovernedAggregationService` keeps trusting only `EvaluatedByPdp`. A definer decision handed in as `PreEvaluatedDecision` is evaluated again (fail closed). A unit test pins this.
3. D-891-3: Both flags may not be set on one decision. The `TableAccessDecision` init accessors do not enforce this. The ratchet in WP-891-1 does.

## 4. Work package

**WP-891-1 (S, about 80 LOC): flag, stamp and ratchets**
- `src/Autheris.Domain/Interfaces/TableAccessDecision.cs`: add `internal bool EvaluatedAsProductDefiner { get; init; }` with a doc comment. Confirm the `InternalsVisibleTo` list covers the Application assembly (it already does for `EvaluatedByPdp`).
- `FederatedDuckDbExecutionService.cs:640`: change `with { EvaluatedByPdp = true }` to `with { EvaluatedAsProductDefiner = true }`.
- `tests/Autheris.Tests.Architecture/AuthorizationArchitectureTests.cs:107`: remove the `FederatedDuckDbExecutionService.cs` allowlist line and its comment. Add `Architecture_ProductDefinerProvenance_IsStampedOnlyByTheDefinerPath`: `EvaluatedAsProductDefiner = true` may appear only in `FederatedDuckDbExecutionService.cs`, and never in `Policy/Pdp/`. Add the new test name to `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt`.
- Unit tests, `[Trait("Category","Security")]`:
  - `tests/Autheris.Tests.Unit/Products/ProductDefinerTests.cs`: `DefinerDecision_CarriesDefinerProvenance_NotPdpProvenance`.
  - The existing `GovernedAggregationService` pre-evaluated decision test class (find it with `git grep -ln PreEvaluatedDecision tests/Autheris.Tests.Unit`): `PreEvaluatedDecision_WithDefinerProvenance_IsReEvaluated`.
- Run `ProductDefinerTests`, `ProductDefinerBoundaryTests` and `AuthorizationArchitectureTests`.

No DDL. No dependencies.

## 5. Integration test impact

- None expected. Product definer integration paths (`tests/Autheris.Tests.Integration`, `git grep -ln Definer`) only check results, not provenance. Hydrogen confirms this with the compile check.

## 6. Phase 3

**Required (light).** It changes the trust marker on authorization decisions. No SEC conditions are set here.

## 7. Owner questions

None. All defaults are taken from the issue text.
