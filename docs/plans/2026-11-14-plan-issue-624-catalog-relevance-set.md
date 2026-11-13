# PLAN-OI-624: catalog search relevance set

- Date: 2026-11-14 · Base: `origin/main` @ `7a4df959` (written on `0cb36516`) · Issue: [#624](https://github.com/themulle/autheris/issues/624)
- Phase 1 and 2: [`2026-11-13-plan-issues-catalog-search.md`](2026-11-13-plan-issues-catalog-search.md), section WP-OI-SRCH-624. This file records the delivery and where it deviates from that plan.
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (plan registry).
- Freeze (PRD 5.1): tests only, 0 lines in `src/`. No Phase 3 review (cluster plan, "Phase 3 review scope").

## Delivered

- `tests/Autheris.Tests.Unit/Catalog/Relevance/catalog-relevance-fixture.json`: neutral sample catalog of 40 tables (fleet, logistics, sales, finance, HR) with German default texts, English translations on most tables, German-only tables, German compounds, a key column described as "Fahrzeug-ID" in 16 tables, and the table name `orders` in two schemas.
- `relevance-cases.json`: 21 cases with query, expected dataset ids, maximum rank, issue row, the feature they need (`baseline`, `field-weights`, `partial-words`, `word-forms`, `embeddings`, `full-identifier`) and the issue that builds it. Every row of the issue table has at least two queries.
- `CatalogSearchRelevanceTests`:
  - `Case_ExpectedTablesWithinMaxRank(caseId, mode)` runs every case in keyword mode and in hybrid mode and writes the rank of each expected table to the test output.
  - `AllCases_ReportRankPerExpectedTable` writes the rank of every case, including the skipped ones, as `MET` / `NOT MET`.
  - Fixture and case checks: `Fixture_ContainsSameNameInTwoSchemas`, `Fixture_HasTheShapeAskedForInTheIssue`, `EveryCaseNeedingAFeature_LinksAnIssue`, `EveryIssueRow_HasAtLeastTwoQueries`.
  - Security (`[Trait("Category","Security")]`): `RestrictedCaller_FixtureHiddenColumnText_NeverProducesHit` (a term that occurs only in a column the caller may not see yields no hit in keyword and hybrid mode; an unrestricted administrator finds the table by the same term).

## Switching a case on

xUnit 2 cannot skip a single row of a theory at run time. The cases of one feature are supplied by one `[RelevanceCases("<need>", Skip = "... (#issue)")]` attribute; the PR that builds the feature removes the `Skip` of its attribute in the same PR. Cases that need embeddings stay skipped in both modes, because the only embedding generator in the tests (`LocalDeterministicEmbeddingGenerator`) hashes tokens and is lexical, not multilingual.

## Deviations from the cluster plan

- **Full identifier (#627):** the engine and `CatalogDiscoveryService` already return the full `TableIdentifier`; the defect of #627 is in the `search_catalog` projection (`table = domain.table`). The `full-identifier` cases therefore also run `search_catalog` over the same hits and require distinct `table` values that equal the expected dataset ids. On `0cb36516` (before #627) both cases failed with `search_catalog tables: sales.orders, sales.orders` while the engine ranks were met. #627 merged in #729 before this PR, so the cases are switched on here and pass.
- **Hybrid mode:** runs with the deterministic test generator and a provider other than `None` (with the default `None` the engine silently runs keyword search). It checks the rank fusion of the cases that are switched on; it is not evidence for #628.
- **Word forms:** no stemming issue exists (owner decision Q-SRCH-6); these cases link #624.
- **Case corrections after the first run:** two cases did not test their feature because the query matched a whole word of a column name: "Lieferant" (column `lieferant_nr`) is now a baseline case, and "maintenance plan" (column `plan_id`) is now "maintenance". No fixture data was changed.

## Ranks on the base (keyword mode, before #625 and #626)

| Need | Case | Rank on base |
|---|---|---|
| baseline | 5 cases | all rank 1 |
| field-weights | "Fahrzeug", "vehicle" -> vehicle master | 16, 16 |
| field-weights | "Lager" -> warehouse master | 5 |
| partial-words | "Betriebsstunden", "Fahrzeug" (compounds only), "Verbrauch" | not found |
| word-forms | "Aufträge", "Rechnungen", "Fahrten" | not found |
| embeddings | "invoice", "maintenance", "supplier", "Standort", "Personal" | not found |
| full-identifier | "Bestellungen", "orders" | met (on `0cb36516`: engine ranks met, `search_catalog` named both `sales.orders`) |

## Phase 4 and 5

- Tests only: no red-green cycle on `src/`. The report test runs the skipped cases too and shows them `NOT MET` on the base (table above); the `full-identifier` cases were run unskipped once (see deviations).
- Delivery: UNDER REVIEW (PR to be linked).
