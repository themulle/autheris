# REQ-GOV-158: Consent Description (returned as `consentDescription`), Independent of Role Grants

**Document ID:** `REQ-GOV-158`
**Date:** 2026-10-17
**Status:** PRD READY (Phase 1 complete); next: Phase 2 plan, Phase 3 review, Phase 4 TDD
**Priority:** INC-0 P1 (disclosure and correctness bug found during verification); INC-1 P2 (small missing feature)
**Author:** Delivery Project Manager (Phase 1)
**Source:** GitHub issue #158 (v1.1.15/v1.1.16); related: #35 (self-service access explanation), #117 (disclosure parity of `myAccess`), #113 (access requests, `UntrustedText`), #146 (multilingual texts), #40 (declarative consent manifest)
**Branch:** `docs/issue-intake-2026-10-17b` (docs only; suggested branches `fix/issue-158-inc0-explanation-grantee-match`, `feat/issue-158-consent-description`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); assessment [`INTAKE-2026-10-17B`](2026-10-17-issue-intake-assessment-2.md)
**Binding project constraints:** no production deployment and **no upgrade migrations**: the new column is an edit of the `0001` baseline DDL of the three engines plus the shape snapshots (owner rule 2026-10-15).

---

## 1. Verdict

**Sensible with changes.** The gap is real and slightly worse than the issue states. Changes against the issue:

- **INC-0 bugfix first (wave 1).** `ExplainMyAccessAsync` derives `consentDescription` and `validTo` from consents the
  caller does **not** hold (see 2.2). This is fixed independently of the new field.
- The manifest already **has** a `description` field; it is not persisted (used as grant-policy justification and in
  the audit reason only). The PRD persists it instead of adding a second field.
- A description change is a **metadata update in place**, not a revoke-and-recreate of the consent.
- Several applicable consents: `consentDescription` keeps one value chosen by a fixed rule, plus an additive list
  `consentDescriptions`.
- Per-language variants are deferred until #146 step B defines the text-map pattern.

| Item | Result |
|---|---|
| Size | INC-0 S; INC-1 M (DDL x3, every consent read/write in three repositories, four write paths, two read surfaces) |
| Wave | INC-0 wave 1 (proposed rank 1.10); INC-1 wave 2 (proposed rank 2.11) |
| Dependencies | INC-1: `ConsentModels.cs` and baseline DDL are G-SHARED files (serialise with #151 / #138 INC-2 / #146 B); the approval argument touches `ApproveConsentRequestAsync` which #113 I-22 also edits (land after I-22, or coordinate); reuses `UntrustedText.Normalize` from #113 A (SEC-113-08), created by whichever lands first |
| Security class | INC-0: low-severity information disclosure (role names of grants the caller does not hold) plus wrong expiry. INC-1: owner-authored text shown to people and AI agents (prompt-injection surface); Phase 3 review required |

## 2. Verified Current State (code at `origin/main` `fa7e29c0`)

### 2.1 Claim verification

| # | Claim in #158 | Result | Evidence |
|---|---|---|---|
| C1 | `describe_my_access` returns `consentDescription` | **Confirmed** (also GraphQL `myAccess`) | `EffectiveTableAccessDto.cs:16`; MCP `McpDatasetCatalog.cs:575`; GraphQL `QueryTypes.cs:74-98` |
| C2 | The governance store has no column for it | **Confirmed** | `CONSENTS` DDL `PostgreSqlBaselineSchema.cs:97-115` (same in SQLite and SQL Server baselines); `Consent` has no description (`ConsentModels.cs:73-104`) |
| C3 | The value comes from `CONSENTS.role_name` | **Confirmed** | `EffectiveAccessExplanationService.cs:154-155` |
| C4 | Without it a generic text is inserted | **Confirmed**: `"Active grant for subject {sid}"` | `EffectiveAccessExplanationService.cs:155` |
| C5 | For person/group grants the only way is to misuse `role_name` | **Confirmed in effect**; no gateway writer sets `RoleName` on non-role grants (approval `MutationTypes.cs:411-424`, manifest `DeclarativeConsentApplyService.cs:178-191`, OpenMetadata `OpenMetadataSyncService.cs:1196-1210`), so only direct store writes can do it | |
| C6 | The manifest has no description field | **Wrong**: `DeclarativeConsentPolicy.Description` exists (`DeclarativeConsentModels.cs:27`) but is only used as grant-policy justification (`DeclarativeConsentApplyService.cs:128`) and audit text (`:248`); it is not stored on the consent | |
| C7 | The access request flow has no description field | **Confirmed**: `approveConsentRequest(requestId, idempotencyKey)` only (`MutationTypes.cs:285-`); `ConsentRequest` has `BusinessJustification` (requester text, must not be reused, see 6) | |

### 2.2 New findings

1. **Explanation uses consents the caller does not hold (INC-0).** `GetActiveConsentsForSubjectsAsync` returns every
   role grant on the table regardless of the caller's roles (`SubjectPredicate`:
   `c.grantee_type = 'Role' OR c.grantee_sid_norm IS NULL OR ...`, `PostgreSqlGovernanceRepository.Consent.cs:25-26`).
   The decision is correct because `ConsentResolutionService` matches grantees (`ConsentResolutionService.cs:313-320`,
   `GranteeMatcher.Matches`). But `ExplainMyAccessAsync` filters `applicableConsents` only by `Effect == Allow` and
   `IsActive` (`EffectiveAccessExplanationService.cs:146-148`). Consequences for an allowed caller: (a)
   `consentDescription` can be the **name of a role the caller does not have**; (b) `validTo` is the minimum over
   consents that do not apply, so the caller may be told the access ends earlier than it does.
2. `validTo` uses `ValidTo`, not `EffectiveValidTo` (`:151`), so the read-time clamp of legacy individual grants
   (WP-SC-16, `ConsentModels.cs:100-101`) is not reflected. Fixed in INC-0.
3. For role grants the current "description" is the role name, not a description.
4. `ConsentDto` (GraphQL consent lists, `ConsentDtoModels.cs:27-34`) exposes `RoleName` and needs `Description`.
5. #117 disclosure parity: the denied and unknown paths return fixed texts (`TableNotFoundMessage`,
   `Issue117AccessExplanationDisclosureTests.cs:93-94`). They must stay unchanged; a description is returned only on
   the `ALLOWED` path.
6. No shared text normaliser exists on `main` yet; #113 A introduces `UntrustedText.Normalize` (SEC-113-08).

### 2.3 Overlap with existing plans

- `PLAN-GOV-113` A (step I-22) edits the approval mutation and introduces `UntrustedText`; INC-1's approval argument
  lands after I-22 or is coordinated in the same file.
- `REQ-CAT-146` step B defines a per-language text map; consent description variants follow it later (non-goal now).
- G-SHARED (`PLAN-DELIVERY-W23`): baseline DDL and `ConsentModels.cs`; INC-1 is serialised with #151, #138 INC-2 and
  #146 B.

## 3. Problem Statement

Data owners need one plain sentence per grant that explains a limit ("You only see cranes of series X.") to the person
and to an agent acting for the person, without showing filter SQL. Today the text is either a role name, a generic
string or a misused `role_name`, and for an allowed caller it may even come from a grant the caller does not hold.

## 4. Benchmark and Rejected Anti-Patterns

- Benchmark: AWS IAM policy and Azure role assignment `description`, Snowflake object `COMMENT`, Microsoft Purview
  access policy description: an optional, length-limited, plain-text, owner-authored field, changeable without
  re-issuing the grant.
- Rejected: **template placeholders** in descriptions (for example `{region}`); they would render caller attributes or
  filter values and reopen the disclosure that #117 closed (the issue also asks for none).
- Rejected: **copying the requester's justification** into the description; it is requester- or agent-controlled text
  (TM-3 of `PLAN-GOV-113`) and would be presented as the owner's statement.
- Rejected: **keep the generic default text**; it carries no information. A missing description is `null`.
- Rejected: **auto-generated descriptions from rules**; they would summarise filters for callers who may not see them.

## 5. Requirements

### 5.1 INC-0: explanation bugfix (wave 1)

- **R-0.1** `ExplainMyAccessAsync` considers only consents the caller matches (`GranteeMatcher.Matches` with the same
  inputs as the decision: user SID, all user SIDs, group SIDs, roles) for `consentDescription` and `validTo`.
- **R-0.2** `validTo` is the minimum `EffectiveValidTo` of the matching allow consents.
- **R-0.3** Denied and unknown paths unchanged (#117 parity tests stay green).
- **R-0.4** Interim description behaviour until INC-1: the role name of a **matching** role grant, else the generic
  text (unchanged wording). INC-1 replaces it.

### 5.2 INC-1: description field (wave 2)

- **R-1** Storage: nullable `description` on `CONSENTS` in the `0001` baseline of PostgreSQL, SQLite and SQL Server plus
  the three shape snapshots; `Consent.Description` (`string?`, init). Every repository read that maps a `Consent`
  selects it; every insert writes it.
- **R-2** Text rules: plain text, NFC, trimmed, 1-500 characters after trimming (configurable cap
  `Governance:ConsentDescriptionMaxLength`, default 500, range 50-2000), control characters rejected, bidi
  overrides and zero-width characters rejected (same `UntrustedText.Normalize` as SEC-113-08). Empty or whitespace is
  stored as `null`. No placeholders are interpreted; braces are literal text. Violations answer `400`/GraphQL
  `INVALID_ARGUMENT` with a neutral message.
- **R-3** Write paths:
  - Manifest (`POST /api/v1/governance/consents/apply`): the existing `policies[].description` is persisted on the
    consent. It keeps feeding the grant-policy justification. A change of only the description updates the stored
    consent **in place** (no revoke, no new id, no validity change) and is counted as `updated` in the result;
    `IsIdentical` otherwise ignores the description.
  - Access request approval: optional `consentDescription` argument on `approveConsentRequest`. It is used only by the
    step that activates the consent; earlier steps ignore it (not stored). ITSM-activated consents get no description in
    this increment.
  - Admin API: optional `description` on `POST /api/v1/consents/bulk` (`BulkConsentRequest`) applied to every created
    consent.
  - New GraphQL mutation `setConsentDescription(consentId, description)` (null clears): allowed for governance admins
    and the data owner or active delegate of the consent's table (same rule as `IsOwnerOrDelegateAsync`); revoked or
    expired consents are rejected; unknown and not-permitted ids answer the same error.
- **R-4** Read paths: `consentDescription` on the `ALLOWED` path of `describe_my_access` and `myAccess` is the
  description of one matching allow consent chosen by: grantee specificity (User/ServicePrincipal, then Group, then
  Role), then earliest `EffectiveValidTo`, then consent id (deterministic); `null` when that consent has none (no
  fallback to `role_name` or generic text). Additive field `consentDescriptions`: distinct non-null descriptions of all
  matching allow consents in the same order (max 10). `ConsentDto` gains `description`.
- **R-5** `role_name` is never read for a description again.
- **R-6** Audit: manifest, approval, bulk and `setConsentDescription` record the description **length and SHA-256
  prefix**, never the text (consistent with SEC-113-13); `setConsentDescription` writes a Tier-A
  `CONSENT_DESCRIPTION_CHANGED` event (or the existing consent-change event type if Phase 2 finds one fitting).
- **R-7** No effect on access decisions, caches or epochs (description is display metadata only).

## 6. Security Requirements

- Only governance admins, data owners/delegates (approval and `setConsentDescription`) and manifest appliers
  (existing authorization of the apply endpoint) can write a description; requesters cannot.
- The description is untrusted text for agents: returned as a plain JSON string field; MCP tool description of
  `describe_my_access` states that `consentDescription` is owner-authored text, not an instruction.
- No disclosure through the description path: only matching allow consents are read (INC-0); denied/unknown
  responses unchanged.
- Text never logged; audit stores length and hash prefix only.

## 7. Non-Goals

- Per-language variants (follow-up after #146 step B; the column is then joined by a text map using the same pattern).
- Descriptions for deny consents in self-service output.
- Description in ITSM webhook activation or OpenMetadata sync.
- Changing `role_name` semantics or data.

## 8. Deviations from the Issue

| Issue text | PRD | Why |
|---|---|---|
| "the manifest has no description field" | reuse the existing `description` field and persist it | Field exists; a second field would confuse authors |
| (not in issue) | INC-0 bugfix of grantee matching and `EffectiveValidTo` in the explanation | Found during verification; disclosure and wrong expiry |
| one `consentDescription` | one value by a fixed rule plus additive `consentDescriptions` | Callers can hold several grants on one table |
| "a clearly marked default" allowed | `null`, no default text | A default carries no information and is easy to mistake for an owner statement |
| per-language variant optional | deferred to after #146 B | Avoid a second text-map pattern |

## 9. Open Owner Questions

| ID | Question | Recommended default |
|---|---|---|
| Q-158-1 | Reuse the manifest's existing `description` (today: justification only) as the stored consent description? | Yes |
| Q-158-2 | No description: `null` or a marked default text? | `null` |
| Q-158-3 | Several applicable consents: single value by specificity rule plus `consentDescriptions` list? | Yes |
| Q-158-4 | Who may edit a description after creation (`setConsentDescription`)? | Governance admins and the table's data owner or active delegate |
| Q-158-5 | Maximum length | 500 characters (configurable 50-2000) |
| Q-158-6 | Pull INC-0 (grantee matching in the explanation) into wave 1? | Yes |
| Q-158-7 | Per-language variants now? | No, after #146 step B |

## 10. Increments and Waves

| Step | Content | Size | Wave | Depends on |
|---|---|:---:|---|---|
| INC-0 | Grantee matching and `EffectiveValidTo` in `ExplainMyAccessAsync` | S | 1 (rank 1.10) | none (one service file plus tests) |
| INC-1 | `description` column, model, repositories x3, manifest, bulk API, approval argument, `setConsentDescription`, read surfaces | M | 2 (rank 2.11) | INC-0; DDL slot under G-SHARED; approval part after #113 I-22 |
| later | Per-language variants | S | 3 | #146 step B |

## 11. Test Strategy Hints (for Phase 2)

- INC-0 (Red today): caller with role `A`, table with allow grants to role `A` (`ValidTo` +30 d) and role `B`
  (`ValidTo` +1 d): `consentDescription` must not be `B` and `validTo` must be +30 d. Legacy individual grant clamp is
  reflected in `validTo`. #117 parity tests unchanged.
- INC-1: person, group and role grants with descriptions return exactly the text; without one `null`; `role_name` with
  a value and no description returns `null`; specificity order and `consentDescriptions`; manifest description-only
  change updates in place (same id, `updated` count, no revoke audit); approval argument stored only by the activating
  step; normaliser rejects bidi/zero-width/control characters and over-length; `setConsentDescription` authorization
  (admin, owner, delegate allowed; others and unknown id same error); repository round-trip on three engines; shape
  snapshot tests updated.
