# Product Requirements Document (PRD): Wave 3 Catalog Transparency, Data Ownership & Response Enhancements

**Document ID:** `REQ-WAVE3-CATALOG-OWNERSHIP-2026-10-12`  
**Date:** 2026-10-12  
**Status:** APPROVED 📝  
**Author:** Enterprise Product Manager & Delivery Project Manager  
**Target Branch:** `feat/catalog-ownership-wave3`  
**Target Path:** `docs/plans/2026-10-12-req-catalog-ownership-wave3.md`  
**Parent Roadmap:** [PLAN-GH-ISSUES-ROADMAP-2026-10-12](file:///root/autheris/docs/plans/2026-10-12-plan-github-issues-roadmap.md)  
**Origin Master Plan:** [00-master-plan-overview.md](file:///root/autheris/docs/plans/00-master-plan-overview.md)  

---

## 1. Executive Summary & Strategic Fit

Enterprise data platforms connecting autonomous AI agents (via MCP) and enterprise portal consumers (via GraphQL and Spotify Backstage) require high-fidelity catalog metadata, clear ownership accountability, self-service access discoverability, and response transparency:

1. **Tag-Based Discovery for AI Agents & Search (Issue #43):**
   - Tables carry rich tags in the metadata catalog (e.g. `[finance, tier-1, pii]`), but MCP agents calling `list_datasets` and `describe_dataset` cannot inspect or filter by tags. Agents need tag filtering to constrain their exploration to relevant business domains.
2. **Data Ownership Transparency & Backstage Alignment (Issue #33):**
   - Data owners and designated delegates are recorded in the governance database (`DATA_OWNERS`, `TABLE_OWNERS`, `DATA_OWNER_DELEGATIONS`), but catalog consumers cannot identify the responsible steward, and Spotify Backstage entity exports fall back to a static `DefaultOwner`.
3. **Catalog Discoverability with Access Status (Issue #32):**
   - Users and agents exploring available datasets need to know their current access posture: `GRANTED` (active consent), `PENDING` (request submitted), or `NONE` (no access). At the same time, datasets marked with `HIDDEN` visibility must remain invisible to unauthorized callers without leaking existence oracles.
4. **Distinguishing Masked Values in GraphQL Responses (Issue #27):**
   - When non-string columns (e.g. salary, credit score) are masked to `null` or standard redaction values, client applications cannot tell whether a field is genuinely `null` in the source database or masked by gateway policy. The gateway must populate `extensions.maskedFields` in the GraphQL response.

---

## 2. Industry Benchmarks & Standards Alignment

- **OWASP API Security Top 10 (2023):**
  - *API1:2023 (Broken Object Level Authorization):* Catalog discovery must not act as an existence oracle for confidential or restricted datasets.
  - *API3:2023 (Broken Object Property Level Authorization):* Response masking metadata must indicate applied masking without revealing the underlying sensitive values.
- **Spotify Backstage Entity Format (v1alpha1):**
  - Entity `spec.owner` must reflect the primary table data owner (or email/UPN) resolved from `IDataOwnershipRepository`, falling back to `DefaultOwner` only when no owner is configured.
- **GraphQL Foundation & Hot Chocolate Best Practices:**
  - Response extensions (`extensions`) provide standardized machine-readable execution metadata (`maskedFields`) without altering the GraphQL schema contract.

---

## 3. Scope & Detailed Requirements

### Requirement 1 (Issue #43): Catalog Tags in MCP & GraphQL
- **R1.1:** Add `IReadOnlyList<string> Tags` to `McpDatasetSummary` and `McpDatasetDescription`.
- **R1.2:** In `list_datasets`, support an optional `tag` argument: `list_datasets(tag: "product")`. Only datasets matching the tag (case-insensitive) are returned.
- **R1.3:** Expose `tags: [String!]!` on the GraphQL `catalog` query / `TableMetadataDto`.

### Requirement 2 (Issue #33): Data Owners in Catalog, `describe_dataset`, and Backstage
- **R2.1:** In `McpDatasetDescription`, include `string? DataOwner` (e.g., `Alice Smith <alice@corp.local>`) and `IReadOnlyList<string>? DataOwnerDelegates`.
- **R2.2:** Expose `dataOwner: DataOwnerDto` and `delegates: [DataOwnerDelegationDto!]!` on GraphQL `TableMetadataDto`.
- **R2.3:** In `BackstageCatalogExportService`, resolve the active table owner via `IDataOwnershipRepository` and populate `tableEntity.Spec.Owner` with the owner's email or account.

### Requirement 3 (Issue #32): Catalog Discovery with Access Status & Visibility
- **R3.1:** Introduce `DatasetVisibility` enum: `Hidden` (default), `Summary`, `Full`.
- **R3.2:** Introduce `AccessStatus` enum / string: `GRANTED`, `PENDING`, `NONE`.
- **R3.3:** When querying the catalog:
  - If caller has active allow consent -> `AccessStatus: GRANTED` (visible in full).
  - If caller has a pending consent request -> `AccessStatus: PENDING` (visible if `Summary` or `Full`).
  - If caller has no consent -> `AccessStatus: NONE` (visible only if `Summary` or `Full`; strictly invisible if `Hidden`).
- **R3.4:** Tables with `Hidden` visibility and `NONE` access status fail closed and are excluded from all catalog queries, MCP lists, and search lookups.

### Requirement 4 (Issue #27): Mark Masked Values in GraphQL Responses
- **R4.1:** When any field resolver in GraphQL applies a column mask (e.g., `ColumnAccessLevel.Mask`), the field path (e.g., `orders.notes` or `customers[0].salary`) is recorded into request-scoped execution state.
- **R4.2:** On response generation, if any fields were masked, attach `extensions.maskedFields: [string]` containing the distinct list of masked field paths.
- **R4.3:** Genuine database `null` values without an applied mask are NOT included in `extensions.maskedFields`.

---

## 4. Security & Compliance Invariants

1. **Zero Existence Oracle:**
   - Datasets with `Hidden` visibility cannot be enumerated or probed. Lookups against hidden unconsented tables return `TableNotFoundException` (identical to non-existent tables).
2. **Zero-Trust Ownership Lookup:**
   - Data owner email/UPN addresses are exposed only for discoverable tables. Inactive data owners (`IsActive == false`) are ignored.
3. **Fail-Closed Default Visibility:**
   - Tables without an explicit visibility setting default to `Hidden`.
4. **Tamper-Proof Response Extensions:**
   - `extensions.maskedFields` contains only path strings. It never leaks HMAC salt IDs, hashing keys, or masked clear-text values.

---

## 5. Success Metrics & Acceptance Criteria

- [ ] All 4 issues (#43, #33, #32, #27) covered by dedicated unit and integration tests.
- [ ] `dotnet build Autheris.sln` compiles with **0 warnings and 0 errors**.
- [ ] All 3,500+ unit and integration tests pass 100% green.
- [ ] Feature documentation authored in [`docs/features/f-gov-16-catalog-ownership-transparency.md`](file:///root/autheris/docs/features/f-gov-16-catalog-ownership-transparency.md).
