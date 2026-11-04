# REQ: Catalog-owned minimum group size for non-personal tables (issue #448)

Source: GitHub issue #448 (themulle/autheris). Phase 1 PRD. Owner of the decision: data owner (catalog), enforced by the operator ceiling.

## Context (current behaviour)
1. `GovernedAggregationService.ResolveEffectiveMinGroupSize` starts from `Analytics:Privacy:DefaultMinGroupSize` (default 5), lets only operator config (`Analytics:TableMinimumGroupSizes`, `Analytics:Privacy:Datasets[*].MinGroupSize`) replace it, then applies catalog (`Table.MinGroupSize`, `TableMetadata.EffectiveMinGroupSize`), manifest and request values with `Math.Max` (raise only; WP-0 / SEC-112-02).
2. `AnalyticsOptionsValidator` rejects any floor below 2 unless `Analytics:Privacy:AllowDisabled` is true (MISC-10).
3. Consequence: a master-data table (crane types) hides small groups from callers who may read every row anyway. Governance of one table is split between catalog and configuration.

## Requirements
- REQ-MGS-01: The catalog may declare a per-table minimum group size that can LOWER the effective floor (down to 1) for a table the data owner marks non-personal. Raising stays always allowed.
- REQ-MGS-02: Lowering is accepted only when ALL hold: (a) `non_personal=true` is set in the governance overlay (new, default false); dbt meta alone never lowers the floor; the operator option `AllowSourceDeclaredLowering` (default false) may additionally accept the dbt-declared flag; an overlay `non_personal=false` pins lowering off regardless of dbt (SEC-MGS-02); (b) the operator has enabled the capability (`Analytics:Privacy:AllowCatalogLowering`, default false); (c) the value is >= the operator ceiling `Analytics:Privacy:CatalogMinGroupSizeFloor` (default 1); (d) neither the table (table-level pii/sensitive tags) nor any of its columns is flagged `pii` or `sensitive` (SEC-MGS-03). Any failed condition falls back to the operator-resolved floor (fail-closed).
- REQ-MGS-03: Precedence of the resolved floor: operator per-table/dataset config is authoritative and always wins when set explicitly (including raising above the catalog value); otherwise a valid catalog lowering applies; otherwise `DefaultMinGroupSize`. Catalog raise, manifest and request values keep the `Math.Max` raise-only rule on top.
- REQ-MGS-04: Catalog sources: governance overlay (`min_group_size`, `non_personal`) and dbt `meta` (`autheris.min_group_size`, `autheris.non_personal`), parsed by one shared validator. Governance wins over dbt on conflict. dbt-declared `non_personal` counts only when `AllowSourceDeclaredLowering` is on and the overlay does not set it to false (SEC-MGS-02).
- REQ-MGS-13: Multi-table queries (joins, cross-table aggregation) use the MAXIMUM resolved floor across all touched tables and lower below the default only if every touched table is eligible under REQ-MGS-02 (SEC-MGS-11).
- REQ-MGS-05: Request-level and manifest-level overrides (`MinGroupSizeOverride`, metric/measure values) can still only raise; they never use the lowering path (no client-controlled thresholds).
- REQ-MGS-06: Setting `non_personal` or lowering below the operator default is an audited governance change (actor, table, old/new value) via the existing catalog audit trail; the aggregation audit/response metadata keeps `minimum_group_size` and adds `min_group_size_source` (`default|operator|catalog|request`).
- REQ-MGS-07: Flagging a column `pii`/`sensitive` after the table was marked non-personal silently reverts to the operator floor at query time (REQ-MGS-02d is evaluated per query) and logs a warning at ingestion/validation.
- REQ-MGS-08: A floor of 1 reached via catalog lowering does not require `AllowDisabled` (that opt-in remains for the global default and operator overrides); it requires REQ-MGS-02(b) instead. Differential-privacy noise and small-group strategy behaviour are unchanged; floor 1 only disables suppression for that table.
- REQ-MGS-09: Negation guard, cache keys and sensitivity math that consume the effective floor (`AggregationNegationGuard`, `NoisyCacheKey`, avg sensitivity) use the same resolved value; cache key must include it so lowered and default results never share an entry.
- REQ-MGS-10: Persistence: no group-size column exists today (neither `TABLES` nor `TABLE_CATALOG_SETTINGS` stores one, and no persistence code reads `Table.MinGroupSize`/`TableMetadata.MinGroupSize`). New storage is added in one forward-only migration step per dialect (v1.5.0 base frozen; later versions auto-migrate): dbt-owned `min_group_size` and `non_personal` on `TABLES`, and governance overrides of both in `TABLE_CATALOG_SETTINGS`; `non_personal` defaults to false, `min_group_size` to NULL. Repositories must map them onto the table model.
- REQ-MGS-11: Optional phase 2 (deferred): derive the default from column flags (suppress only when a grouped/aggregated column or the table is `pii`/`sensitive`). Not in this delivery.
- REQ-MGS-12: Config validation at startup rejects: catalog floor ceiling < 1, negative values in catalog metadata (ingestion error, table keeps operator floor).

## Acceptance criteria
1. Non-personal table, no pii/sensitive columns, capability on, catalog min 1: groups of size 1..4 are returned in full.
2. Same table with capability off (default): floor stays 5; no behaviour change versus today.
3. Table without governance-overlay `non_personal` but with catalog min 1: floor stays operator value. dbt-only `non_personal=true` lowers only with `AllowSourceDeclaredLowering=true`; overlay `non_personal=false` pins it off even then.
4. Table carrying a table-level pii/sensitive tag, or with one such column, and `non_personal=true`: floor stays operator value. Multi-table query with one ineligible table: maximum floor applies.
5. Operator `TableMinimumGroupSizes[table]=10` and catalog min 1: effective 10.
6. Request `MinGroupSizeOverride=1` on a default table: effective stays 5; override 8: effective 8.
7. Catalog value below `CatalogMinGroupSizeFloor`: ignored, floor stays operator value, warning logged.
8. Cache: same query before/after lowering yields distinct cache keys.
9. Audit event emitted on `non_personal`/min-size change; response metadata shows `min_group_size_source`.
10. Migration test: fresh install and upgrade from the previous version both end with the new columns on `TABLES` and `TABLE_CATALOG_SETTINGS` on all dialects, and a round-trip persists and reads back both values.
11. Existing SEC-112-02 tests stay green (no client or manifest lowering).

## Non-goals
- Aggregate-only consent level (longer-term item in the issue; separate PRD).
- Changing noise, small-group strategy or negation-guard algorithms.
- Column-flag-derived defaults (REQ-MGS-11, deferred).
- Per-caller or per-role thresholds.

## Rejected anti-patterns
1. Lowering suppression for tables with personal data or any pii/sensitive column.
2. Client-controlled thresholds (request, GraphQL argument, manifest) that can lower the floor.
3. Silent catalog lowering without an operator opt-in or audit trail (data owner alone must not disable a protection).
4. Reusing `AllowDisabled` as the catalog switch (it is a global kill switch, too coarse).
5. Editing the frozen v1.5.0 base DDL instead of a forward-only migration.
6. A second per-table list in operator configuration as the only way to express this (the issue's governance-split problem).

## Open questions (default taken)
1. Flag name: `non_personal` boolean vs. a classification enum (`public|internal|personal`). Default taken: boolean `non_personal`; enum is a later extension.
2. Should the operator opt-in be global or per data source? Default taken: global `AllowCatalogLowering`, default off.
3. Does operator per-table config beat a lower catalog value? Default taken: yes (operator authoritative).
4. Should floor 1 via catalog also disable noise for that table? Default taken: no, noise is independent.
5. Audit of lowering: reuse catalog audit trail vs. new event type. Default taken: reuse catalog audit with a distinct action name.
6. Derived defaults from column flags (issue proposal 2). Default taken: deferred (REQ-MGS-11).
