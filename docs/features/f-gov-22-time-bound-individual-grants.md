# F-GOV-22: Time-Bound Individual Grants (Grant Policy, Class Maxima, Renewal & Read-Time Clamp)

**Components:** [`GrantPolicy.cs`](../../src/Autheris.Application/Governance/Grants/GrantPolicy.cs), [`GrantModels.cs`](../../src/Autheris.Application/Governance/Grants/GrantModels.cs), [`GrantClassifier.cs`](../../src/Autheris.Application/Governance/Grants/GrantClassifier.cs), [`IndividualGrantServiceCollectionExtensions.cs`](../../src/Autheris.Application/Governance/Grants/IndividualGrantServiceCollectionExtensions.cs), [`IndividualGrantOptions.cs`](../../src/Autheris.Domain/Options/IndividualGrantOptions.cs), [`IndividualGrantLimits.cs`](../../src/Autheris.Domain/Model/IndividualGrantLimits.cs), [`AccessProfileAssignment.cs`](../../src/Autheris.Domain/Model/AccessProfileAssignment.cs), [`ConsentPageReader.cs`](../../src/Autheris.Application/Governance/Paging/ConsentPageReader.cs)

---

## 1. Overview

Standing access in Autheris is granted to **groups and roles**. A grant to an **individual** principal (`GranteeType.User` or `GranteeType.ServicePrincipal`, or an access profile assignment to a single user) is allowed, but it is always **time-bound**:

1. Every individual grant needs an explicit expiry (`ValidTo` / `validUntil` / `expires_at`).
2. The expiry may not exceed the maximum of the grant's class (regular, sensitive, unmasked, privileged, break-glass, service principal, customer realm).
3. An extension is a renewal: a new approval by someone other than the grantee, again capped by the class maximum.
4. Expiry is enforced at read time, and legacy individual grants without a bounded expiry are clamped at read time (nothing is rewritten in storage).

Group and role grants are standing and are not affected by this policy (they are recertified through the consent recertification workflow).

All rules are implemented in one stateless component, `IGrantPolicy` (`GrantPolicy`), that every grant write path calls. An architecture test (`IndividualGrantPolicyArchitectureTests`) enforces that the grant writers depend on it. Any unexpected state while evaluating a grant denies it (`INDIVIDUAL_GRANT_INDETERMINATE`, fail closed).

---

## 2. Grant Classes and Maxima

| Class (`GrantClass`) | Applies to | Built-in maximum | Four-eyes (approver differs from requester and grantee) | Justification required | Recertification class |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `Regular` | Individual user, regular data (no sensitive column, no PII tag) | 180 days | no (data owner approval as before) | no | 365-day campaign |
| `Sensitive` | Individual user, sensitive/PII tables or clear text on a sensitive column | 90 days | yes, two independent approvers | no | 90-day campaign |
| `IndividualUnmasked` | Individual `Unmasked` access profile | 30 days | yes, two independent approvers | yes | 90-day campaign |
| `IndividualPrivileged` | Individual privileged functional right (just-in-time elevation) | 7 days | yes, two independent approvers | yes | 90-day campaign |
| `BreakGlass` | Break-glass exception | 72 hours | yes, at least one independent approver; the approval may follow within 24 hours | yes | listed in the next 90-day campaign |
| `ServicePrincipal` | Service principal as direct grantee | 365 days | one independent approver when approvals are presented | no | 90-day campaign |
| `CustomerRegular` | Regular data for a user of a customer realm | 90 days | no | no | customer admin attestation |
| `Standing` | Group or role grantee | none | — | — | campaign of the group/role grant |

**Classification.** `GrantClassifier` derives the sensitivity from catalog metadata: a table is sensitive when it is marked highly sensitive, carries a tag containing `pii` or `gdpr`, or has a column that is sensitive or carries `meta.pii = true`. Any gap in the metadata (table not found, no metadata) classifies the grant as **sensitive**, so the shorter maximum applies (fail closed).

**Customer realms.** Individual `Unmasked` and individual privileged grants are disabled for customer-realm users (`INDIVIDUAL_GRANT_DISABLED_FOR_REALM`) unless `AllowCustomerUnmasked` / `AllowCustomerPrivileged` is set.

**Four-eyes timing.** The approver count is checked when approvals are presented (activation or approval step). At request time only expiry, maximum and justification are checked.

---

## 3. Configuration (`Gateway:Governance:IndividualGrants`)

A realm profile can only **shorten** the built-in maxima. Values are validated at startup (`ValidateDataAnnotations` + `ValidateOnStart`): a value above the ceiling or below 1 aborts startup.

| Key | Default (= ceiling) | Range | Description |
| :--- | :--- | :--- | :--- |
| `RegularMaxDays` | `180` | 1..180 | Maximum of `Regular` individual grants. Also the read-time clamp of individual user consents. |
| `SensitiveMaxDays` | `90` | 1..90 | Maximum of `Sensitive` individual grants. |
| `UnmaskedMaxDays` | `30` | 1..30 | Maximum of individual `Unmasked` grants. Also the read-time clamp of legacy `Unmasked` profiles without typed assignments. |
| `PrivilegedMaxDays` | `7` | 1..7 | Maximum of individual privileged functional rights. |
| `BreakGlassMaxHours` | `72` | 1..72 | Maximum of break-glass exceptions. |
| `ServicePrincipalMaxDays` | `365` | 1..365 | Maximum of service-principal grants. Also the read-time clamp of service-principal consents. |
| `CustomerRegularMaxDays` | `90` | 1..90 | Maximum of regular grants to customer-realm users. |
| `AllowCustomerUnmasked` | `false` | bool | Allows individual `Unmasked` grants in customer realms. |
| `AllowCustomerPrivileged` | `false` | bool | Allows individual privileged grants in customer realms. |
| `RoleConversionHintAfterRenewals` | `2` | 1..10 | From this many previous renewals onwards, the decision carries a hint to convert the grant into a group/role mapping. |
| `CampaignRequiredAfterRenewals` | `3` | 1..10 | From this many previous renewals onwards, a direct extension is refused (`INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN`); with the default, the 4th renewal needs a campaign decision. |

```json
{
  "Gateway": {
    "Governance": {
      "IndividualGrants": {
        "RegularMaxDays": 120,
        "SensitiveMaxDays": 60,
        "UnmaskedMaxDays": 14,
        "BreakGlassMaxHours": 24
      }
    }
  }
}
```

---

## 4. Grant Paths

Every path that creates or extends an individual grant evaluates `IGrantPolicy` with a `GrantKind`:

| Path | `GrantKind` | Expiry input |
| :--- | :--- | :--- |
| `POST /api/v1/consents/bulk` | `BulkGrant` | `validDays` (converted to an absolute expiry); the resulting access profile assignment is stored through the strict assignment API (section 5 of [F-GOV-11](f-gov-11-declarative-access-profiles.md)). |
| Consent request activation and approval (`ConsentRequestService`, GraphQL approval mutation) | `Consent` | Requested `ValidTo` of the request, evaluated at activation time (`now + max`). |
| Bundled consent requests (`BundledConsentRequestService`) | `Consent` | Requested expiry per table. |
| Declarative consent manifest (`POST /api/v1/governance/consents/apply`) | `ManifestPolicy` | Absolute `validUntil` of the policy (see [F-GOV-15](f-gov-15-declarative-consents-scopes.md)). Evaluated in dry-run as well. |
| MCP `admin_plan_access` / `admin_apply_access` | `McpAdminGrant` | `validUntil` per user principal; evaluated at planning time and again at apply time. ReBAC tuples written for the grant carry `ExpiresAt`. |
| Consent expiry extension and recertification extension (`ConsentRecertificationWorkflowService`) | `Renewal` | New expiry, capped at `now + max` of the class. |
| Access profile assignment (`IAccessProfileRepository.AssignProfileAsync` / `RenewAssignmentAsync`) | — | `ExpiresAt`, validated by `AccessProfileAssignmentRules` (same maxima for users: 180 days regular, 30 days `Unmasked`, 72 hours break-glass). |

`revoke-all` and other revocation paths are not affected.

### Error responses

A refused grant answers `400` with a stable machine-readable code and, where known, the latest allowed expiry:

```json
{ "error": "INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM", "message": "The requested expiry exceeds the maximum of 90 days for this grant class.", "maxValidTo": "2027-01-13T09:00:00+00:00" }
```

| Code | Meaning |
| :--- | :--- |
| `INDIVIDUAL_GRANT_REQUIRES_EXPIRY` | No expiry given for an individual grant. There is no silent default for individual grants. |
| `INDIVIDUAL_GRANT_EXPIRY_IN_PAST` | The expiry is not in the future. |
| `INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM` | The expiry is later than `now + max` of the class. |
| `INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES` | Not enough independent approvers for the class. |
| `INDIVIDUAL_GRANT_REQUIRES_JUSTIFICATION` | `IndividualUnmasked`, `IndividualPrivileged` or `BreakGlass` without justification. |
| `INDIVIDUAL_GRANT_RENEWAL_BY_GRANTEE` | The grantee (or nobody) tried to renew. |
| `INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN` | Too many renewals; a campaign decision is required. |
| `INDIVIDUAL_GRANT_DISABLED_FOR_REALM` | The class is disabled for customer realms. |
| `INDIVIDUAL_GRANT_INDETERMINATE` | The grant could not be evaluated (unknown grantee type or internal error); denied. |

Access profile assignments use their own codes (`INDIVIDUAL_GRANT_REQUIRES_EXPIRY`, `GRANT_EXPIRY_IN_THE_PAST`, `GRANT_EXCEEDS_MAXIMUM_DURATION`, `UNMASKED_REQUIRES_FOUR_EYES`, `BREAK_GLASS_REQUIRES_JUSTIFICATION`, `APPROVER_MUST_DIFFER`, `RENEWAL_BY_GRANTEE_FORBIDDEN`, `BREAK_GLASS_NOT_RENEWABLE`, `ASSIGNMENT_KIND_CONFLICT`), see [F-GOV-11](f-gov-11-declarative-access-profiles.md).

---

## 5. Renewal

- A renewal is a new approval by an owner or delegate, **never by the grantee**.
- The new expiry is at most `now + max` of the class; four-eyes applies when the class requires it.
- `CONSENTS.renewal_count` and `access_profile_assignments.renewal_count` count the approved renewals. A renewed assignment restarts its grant period (`assigned_at`).
- From `RoleConversionHintAfterRenewals` previous renewals the decision suggests converting the recurring need into a group/role mapping; from `CampaignRequiredAfterRenewals` previous renewals a direct extension is refused and a campaign decision is required.
- Break-glass exceptions are not renewable.

---

## 6. Read-Time Expiry and Clamp of Legacy Grants

Expiry is evaluated on every read; nothing in storage is rewritten:

| Object | Effective expiry |
| :--- | :--- |
| Consent of a `User` grantee | `min(ValidTo, ValidFrom + RegularMaxDays)` (`Consent.EffectiveValidTo`) |
| Consent of a `ServicePrincipal` grantee | `min(ValidTo, ValidFrom + ServicePrincipalMaxDays)` |
| Consent of a `Group` / `Role` grantee | `ValidTo` (not clamped) |
| Access profile assignment to a user | `min(expires_at, assigned_at + max)` with max 180 days (regular) or 30 days (`Unmasked`); an assignment without `expires_at` is clamped, never permanent |
| Break-glass assignment | `min(expires_at, assigned_at + 72 h)`; without approval it is effective only for 24 hours after `assigned_at` |
| Group / role assignment | `expires_at` (standing when null) |
| `Unmasked` profile without typed assignments (legacy) | `min(ValidTo, CreatedAt + UnmaskedMaxDays)` |

The class of a stored consent is not known at read time, so the consent clamp uses the largest user maximum (`RegularMaxDays`) as a backstop; the write paths enforce the exact class maximum. A grant past its clamped expiry is inactive (fail closed). The consent decision cache TTL (`ComputeDecisionCacheTtl`) is bounded by the stored `ValidTo` of the active consents and by the base TTL (10 minutes, 60 seconds for highly sensitive tables), so a cached decision built from a clamped grant ends at the latest one base TTL after the clamped expiry.

### Report of clamped grants

`GET /api/v1/governance/grants?clamped=true` lists the individual grants whose effective expiry is earlier than the stored one, with `validTo`, `effectiveValidTo`, `clamped=true` and the message `individual grant without expiry/over limit, treated as expiring at <instant>`. Clamped grants are re-approved through the normal renewal path. The endpoint is described in [F-GOV-11, section 7](f-gov-11-declarative-access-profiles.md#7-paged-grant-listing-get-apiv1governancegrants).

---

## 7. Behaviour Changes

These changes apply to every deployment that runs this version:

1. **Mandatory expiry for individual grants.** Requests that create a consent, bulk grant, manifest policy or MCP grant for a `User` or `ServicePrincipal` without an expiry, or with an expiry beyond the class maximum, are rejected with `400`. The manifest default of 365 days (`validityDays`) applies to group and role grantees only; individual manifest policies need `validUntil`.
2. **Clamping of existing permanent grants.** Existing individual consents and access profile assignments with no or an over-long expiry are treated as expiring at the clamped instant. Grants created more than 180 days ago (365 days for service principals, 30 days for legacy `Unmasked` profiles) become **inactive immediately** after the upgrade. Use the clamped-grant report before the upgrade window to plan re-approvals.
3. **Unmasked assignment rules.** An `Unmasked` profile takes effect only through an approved group/role mapping, a four-eyes individual grant of at most 30 days, or a break-glass exception (see [F-GOV-11](f-gov-11-declarative-access-profiles.md)). Previously assigned `Unmasked` profiles that do not meet these rules stop unmasking data.
4. **Renewals.** Expiry extensions are capped per class (no fixed 30-day suggestion) and the grantee can no longer extend its own grant.
