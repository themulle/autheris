# Plan: Issues #800 and #801 (consents/apply documentation)

Status: Done (documentation only, no code change).

## Problem

- #800: F-GOV-15 shows a `Role` grantee manifest and describes role grants and `validityDays`; since 1.8.0 `POST /api/v1/governance/consents/apply` rejects role grantees and every policy needs `validUntil`. The 1.8.0 changelog does not mention it.
- #801: the four-eyes refusal of individual manifest grants (`INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES`) is undocumented; F-GOV-22 explains the code only as "not enough approvers".

## Verified behaviour (main)

- `DeclarativeConsentApplyService.cs:184` rejects any `Role` grantee (Allow and Deny) with `ArgumentException`; the endpoint (`GovernanceEndpoints.Catalog.cs`) answers `400 BAD_REQUEST`.
- `DeclarativeConsentApplyService.cs:220` calls `GrantValidityPolicy.ValidateDeclarativeValidTo` for every policy: `validUntil` required, in the future, at most `now + Gateway:Governance:Grants:DeclarativeMaxDuration` (defaults to `MaxDuration`). `ValidityDays` is bound but never read.
- `DeclarativeConsentApplyService.cs:222-251`: User/ServicePrincipal policies run `GrantPolicy` (`GrantKind.ManifestPolicy`, class limit `Gateway:Governance:IndividualGrants:*`), then an `Allow` with a `Clear` column rule, on a sensitive table (`GrantClassifier`), or with a four-eyes class is refused with `INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES` (also in dry-run). Groups are not affected.
- Self-grant and degraded group resolution also answer `INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES` (lines 204-218).
- Migration step `0011_role_grantee_and_profile_assignment_retirement` (in v1.8.0) revokes non-Deny role consents (`ADR020_ROLE_GRANTEE_UNSUPPORTED`); role Deny consents stay and keep restricting (`ConsentResolutionService.IsSubjectMatch`); `GET /api/v1/admin/authz/migration-report` lists the revoked consents.

## Work packages

1. F-GOV-15: group and user example, apply semantics, four-eyes section, seeding of test/demo environments.
2. F-GOV-22: manifest path row, error code meaning, behaviour-change item on `validityDays`.
3. Migration guide (`docs/operations/migration-simplification.md`): role grantee section.
4. CHANGELOG: 1.8.0 BREAKING note, `[Unreleased]` docs line.

## Security

No behaviour change. The docs must not suggest bypassing four-eyes (no auto-approve switch); recommended seeding paths are group grantees and the consent request/approval workflow.

## Out of scope / follow-ups

- Stale code comment on `DeclarativeConsentPolicy` (`ValidityDays` default 365) and on line 199 of the service.
- F-GOV-22 section 2/3 still list compiled-in class maxima that no longer exist.
