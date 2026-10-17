# Issue Intake Assessment: #157, #158

**Document ID:** `INTAKE-2026-10-17B`
**Date:** 2026-10-17
**Phase:** 1 (product qualification), consolidated by the Delivery Project Manager
**Code base verified:** `origin/main` at `fa7e29c0` (v1.1.16 plus waves 2/3 part 1 and the wave-1 intake plans, PR #156)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md) (section 0, "Issue intake 2026-10-17 (second batch)")
**Previous intake:** [`INTAKE-2026-10-17`](2026-10-17-issue-intake-assessment.md)
**Child PRDs:**
[`REQ-BUG-157`](2026-10-17-req-issue-157-columnless-table-graphql-startup.md),
[`REQ-GOV-158`](2026-10-17-req-issue-158-consent-description.md)

---

## 1. Summary

Neither issue is rejected or a duplicate. Both are **sensible with changes**. #157 is an availability bug with the
root cause correctly identified in the issue, but the trigger is wider than stated. #158 is a small feature; verifying it
uncovered a separate bug in the same code (the access explanation reads consents the caller does not hold), which is
split out as a wave-1 increment.

| Issue | Verdict | Recommended solution (one line) | Size | Proposed wave / rank |
|---|---|---|:---:|---|
| #157 column-less table stops the gateway | Sensible with changes (bug confirmed) | `CatalogSchemaModel` omits tables with no **queryable** columns before reserving names, reason-coded `OmittedTables` list, warning log, list in the role-gated system health endpoint; no write-time rejection | S | Wave 1, rank 1.9 |
| #158 consent description | Sensible with changes, split | INC-0: explanation uses only consents the caller matches (description and `EffectiveValidTo`); INC-1: nullable `CONSENTS.description` (baseline x3), persisted from the existing manifest `description`, approval argument, bulk API, `setConsentDescription`; `null` when absent; `role_name` never read | S + M | INC-0 wave 1, rank 1.10; INC-1 wave 2, rank 2.11 |

## 2. Cross-cutting findings

1. **Both items touch G-SHARED files** of `PLAN-DELIVERY-W23`: #157 `CatalogSchemaModel.cs` (also #138 INC-4), #158
   INC-1 `ConsentModels.cs` and the `0001` baseline DDL (also #151, #138 INC-2, #146 B). Sequential merging applies;
   #157 has no shared file with the running wave-1 items 1.6-1.8 and can start at once.
2. **No upgrade migrations** (owner rule 2026-10-15): #158's column is a baseline edit plus shape snapshots.
3. **Untrusted text.** #158 descriptions are read by AI agents. They use the same normaliser as `justification` and
   `context` in #113 A (`UntrustedText.Normalize`, SEC-113-08); whichever step lands first creates it.

## 3. Per-issue assessment

### 3.1 #157 A catalog table without columns stops the gateway at startup

**Verdict:** sensible with changes; P1 availability bug.

**Root cause.** `CatalogSchemaModel.BuildAsync` adds every active, tree-compilable SQL table to the model
(`src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs:155-216`) even when its column list is empty.
`CatalogGraphQlTypeModule.CreateTypesAsync` then emits `<type>_order_by` with column fields only
(`CatalogGraphQlTypeModule.cs:207-215`) and the entity `ObjectType` with column and relation fields only (`:217-255`).
HotChocolate 16.6.8 rejects input and object types without fields during type initialisation; the eager executor
warm-up throws `SchemaException` and the host stops. The `try/catch` in `CreateTypesAsync` (`:134-151`) covers only the
model build, not HotChocolate's validation, so nothing degrades gracefully. (`_filter` is never empty because it always
has `and`/`or`/`not`, `:197-199`.)

**Claim check:**

| Claim | Result |
|---|---|
| `_filter` / `_order_by` created for every active SQL table | Confirmed (`CatalogSchemaModel.cs:155-160`) |
| Zero columns give an input object without fields | Confirmed for `_order_by`; also the entity type when there are no relations |
| Host stops from the executor warm-up | Confirmed by code path |
| Collisions already omitted with a log entry | Confirmed (`:162-172`) |
| Column-less tables not omitted | Confirmed (`:216`) |

**Additional findings:** (1) a table whose columns all fail the plain-identifier rule (`:188-192`, R-GQL-13) crashes the
same way; (2) names are reserved (`:176-178`) before columns are known, so the guard must move the reservation after the
column computation; (3) the 60 s runtime refresh (`CatalogGraphQlTypeModule.cs:63-104`) would break the executor rebuild
the same way when such a row appears at runtime; (4) every other read path already treats a column-less table as
unknown (`GovernedSqlRewriter.cs:394`, `StoredProcedureCatalogValidator.cs:476`,
`GovernedProcedureExecutionService.cs:352`).

**Recommended solution:** omit tables with no queryable columns (reason `NoQueryableColumns`), log a warning, record
all omissions (also collisions and unsupported dialects) in a reason-coded `OmittedTables` list on the model and show
it in `/api/governance/system/health` (already role-gated). Relations to omitted tables disappear automatically. An
invariant test asserts that no generated catalog type has zero fields.
**Deviations:** no rejection at import or write time (many writers, legitimate intermediate state, other paths already
robust); no placeholder fields; no "start without catalog" fallback as the fix.
**Size / wave:** S, wave 1 rank 1.9, standalone step. **Dependencies:** none; merge before #138 INC-4.
**Security notes:** availability only; fail closed (omitted table is an unknown field); table names only to the
system-health roles; no column data in logs.

### 3.2 #158 Description field for consents

**Verdict:** sensible with changes; split into INC-0 (bug) and INC-1 (feature).

**Claim check:**

| Claim | Result |
|---|---|
| `describe_my_access` returns `consentDescription` | Confirmed, also GraphQL `myAccess` (`EffectiveTableAccessDto.cs:16`, `McpDatasetCatalog.cs:575`, `QueryTypes.cs:74-98`) |
| No store column | Confirmed (`CONSENTS` in all three baselines, `PostgreSqlBaselineSchema.cs:97-115`; `ConsentModels.cs:73-104`) |
| Value from `role_name` | Confirmed (`EffectiveAccessExplanationService.cs:154`) |
| Generic text otherwise | Confirmed: `"Active grant for subject {sid}"` (`:155`) |
| Manifest has no description field | **Wrong:** `DeclarativeConsentPolicy.Description` exists (`DeclarativeConsentModels.cs:27`), used only as grant-policy justification and audit text (`DeclarativeConsentApplyService.cs:128`, `:248`), not stored |
| Access request flow has no description field | Confirmed (`approveConsentRequest(requestId, idempotencyKey)`, `MutationTypes.cs:285`) |

**Bug found (INC-0).** `GetActiveConsentsForSubjectsAsync` returns every role grant on the table
(`SubjectPredicate`, `PostgreSqlGovernanceRepository.Consent.cs:25-26`). The decision is correct
(`ConsentResolutionService.cs:313-320` matches grantees), but `ExplainMyAccessAsync` selects `applicableConsents` by
effect and activity only (`EffectiveAccessExplanationService.cs:146-148`). An allowed caller can therefore receive the
**name of a role grant they do not hold** as `consentDescription`, and `validTo` is the minimum over grants that do not
apply (also computed from `ValidTo` instead of `EffectiveValidTo`, `:151`). Low-severity disclosure plus wrong expiry;
denied and unknown paths are unaffected (#117 parity holds).

**Recommended solution:**
- INC-0 (S, wave 1): filter by `GranteeMatcher.Matches` with the decision's inputs; `validTo` from `EffectiveValidTo`.
- INC-1 (M, wave 2): nullable `CONSENTS.description` (baseline x3 plus shapes), `Consent.Description`; written by the
  manifest (existing `description`, description-only changes update in place), the activating approval step
  (`consentDescription` argument), the bulk API and a new `setConsentDescription` mutation (admins, table owner or
  delegate); read by `describe_my_access`/`myAccess` (one value by grantee specificity, then earliest expiry, plus an
  additive `consentDescriptions` list) and `ConsentDto`. No description means `null`; `role_name` is never read for it.
**Deviations:** reuse of the manifest field; in-place update instead of revoke/recreate; `null` instead of a default
text; per-language variants deferred to after #146 step B.
**Dependencies:** INC-1 after INC-0; DDL slot under G-SHARED; approval argument after #113 I-22 (same mutation);
`UntrustedText.Normalize` shared with #113 A.
**Security notes:** only admins, owners/delegates and manifest appliers write descriptions, never requesters (requester
justification is not copied); text normalised (NFC, no control, bidi or zero-width characters, 500-character cap); text
never logged, audit stores length and hash prefix; returned as plain data, documented as owner-authored and not an
instruction; Phase 3 review required.

## 4. Placement (confirmed by the owner 2026-10-17)

| Wave | Rank | Item | Size |
|---|---|---|:---:|
| 1 | 1.9 | `REQ-BUG-157`: omit tables without queryable columns from the GraphQL schema | S |
| 1 | 1.10 | `REQ-GOV-158` INC-0: access explanation uses only matching consents (description, `EffectiveValidTo`) | S |
| 2 | 2.11 | `REQ-GOV-158` INC-1: consent description field, write paths, read surfaces | M |
| 3 | – | `REQ-GOV-158` later: per-language descriptions after #146 step B | S |

The wave 2 item does not enter the running `PLAN-DELIVERY-W23` sequence until that delivery reaches it, so gate G-PLAN
is not reopened. Wave 1 items need Phase 2 and Phase 3 before Phase 4. Both wave 1 items are small and share no files
with 1.6-1.8 or with each other; they can run in parallel with the current step under the relaxed G-ONE rule.

## 5. Owner questions and decisions

**Decision 2026-10-17:** the owner accepted every recommended default below (OQ-16..OQ-25).

| ID | Question | Recommended default |
|---|---|---|
| OQ-16 | Place #157 in wave 1 (rank 1.9) and #158 INC-0 in wave 1 (rank 1.10)? | Yes |
| OQ-17 (Q-157-1) | Also reject column-less tables at import or write time? | No |
| OQ-18 (Q-157-2) | Diagnostics: warning log plus reason-coded list in `/api/governance/system/health`? | Yes |
| OQ-19 (Q-157-3) | Last-resort fallback to a catalog-free GraphQL schema on any other schema-validation error? | Not now |
| OQ-20 (Q-158-1) | Persist the manifest's existing `description` as the consent description? | Yes |
| OQ-21 (Q-158-2) | Missing description: `null` instead of a default text? | `null` |
| OQ-22 (Q-158-3) | Several grants: one value by specificity plus `consentDescriptions` list? | Yes |
| OQ-23 (Q-158-4) | Who may edit a description later? | Governance admins and the table's data owner or active delegate |
| OQ-24 (Q-158-5) | Maximum length | 500 (configurable 50-2000) |
| OQ-25 (Q-158-7) | Per-language variants now? | No, after #146 step B |
