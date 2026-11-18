# F-GOV-22: Time-Bound Individual Grants (Grant Policy, Class Maxima, Renewal & Read-Time Clamp)

**Components:** [`GrantPolicy.cs`](../../src/Autheris.Application/Governance/Grants/GrantPolicy.cs), [`GrantModels.cs`](../../src/Autheris.Application/Governance/Grants/GrantModels.cs), [`GrantClassifier.cs`](../../src/Autheris.Application/Governance/Grants/GrantClassifier.cs), [`IndividualGrantServiceCollectionExtensions.cs`](../../src/Autheris.Application/Governance/Grants/IndividualGrantServiceCollectionExtensions.cs), [`IndividualGrantOptions.cs`](../../src/Autheris.Domain/Options/IndividualGrantOptions.cs), [`IndividualGrantLimits.cs`](../../src/Autheris.Domain/Model/IndividualGrantLimits.cs), [`AccessProfileAssignment.cs`](../../src/Autheris.Domain/Model/AccessProfileAssignment.cs), [`ConsentPageReader.cs`](../../src/Autheris.Application/Governance/Paging/ConsentPageReader.cs)

---

## 1. Overview

Standing access in Autheris is granted to **groups**. A grant to an **individual** principal (`GranteeType.User` or `GranteeType.ServicePrincipal`, or an access profile assignment to a single user) is allowed, but it is always **time-bound**:

1. Every individual grant needs an explicit expiry (`ValidTo` / `validUntil` / `expires_at`).
2. The expiry may not exceed the effective maximum of the grant's class (regular, sensitive, unmasked, privileged, service principal, customer realm): the shorter of the optional class limit (section 3) and the required tenant maximum `Gateway:Governance:Grants:MaxDuration` (`DeclarativeMaxDuration` for declarative manifests).
3. An extension is a renewal: a new approval by someone other than the grantee, again capped by the class maximum.
4. Expiry is enforced at read time; where a class limit is configured, individual grants with a longer stored expiry are clamped at read time (nothing is rewritten in storage).

Group grants are standing: they take no class limit, but an explicit expiry is bounded by the tenant maximum. They are recertified through the consent recertification workflow. A role never matches an `Allow` grant (ADR-020).

All rules are implemented in one stateless component, `IGrantPolicy` (`GrantPolicy`), that every grant write path calls. An architecture test (`IndividualGrantPolicyArchitectureTests`) enforces that the grant writers depend on it. Any unexpected state while evaluating a grant denies it (`INDIVIDUAL_GRANT_INDETERMINATE`, fail closed).

---

## 2. Grant Classes and Maxima

There are no compiled-in maxima. The effective maximum of an individual class is `min(class limit, tenant maximum)`, where the class limit is the optional key of section 3 (unset means no class-specific limit) and the tenant maximum is `Gateway:Governance:Grants:MaxDuration` (for declarative manifests `DeclarativeMaxDuration`, which defaults to `MaxDuration`).

| Class (`GrantClass`) | Applies to | Class limit key | Four-eyes (approvers differ from requester and grantee) | Justification required | Recertification class |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `Regular` | Individual user, regular data | `RegularMaxDays` | no | no | 365-day campaign |
| `Sensitive` | Individual user, sensitive table | `SensitiveMaxDays` | yes, two independent approvers | no | 90-day campaign |
| `IndividualUnmasked` | Individual unmasked access (`Unmasked` profile, or a `Clear` column rule on a manifest policy) | `UnmaskedMaxDays` | yes, two independent approvers | yes | 90-day campaign |
| `IndividualPrivileged` | Individual privileged functional right (just-in-time elevation) | `PrivilegedMaxDays` | yes, two independent approvers | yes | 90-day campaign |
| `ServicePrincipal` | Service principal as direct grantee | `ServicePrincipalMaxDays` | no | no | 90-day campaign |
| `CustomerRegular` | Regular data for a user of a customer realm | `CustomerRegularMaxDays` | no | no | customer admin attestation |
| `Standing` | Group grantee | none (tenant maximum only) | — | — | campaign of the group grant |

A `Deny` grant only restricts access: the grant policy sets no maximum for it (section 8).

**Classification.** `GrantClassifier` derives the sensitivity from catalog metadata: a table is sensitive when its sensitivity is `CONFIDENTIAL` or higher (or not recognised) or it requires four-eyes approval, carries a tag containing `pii` or `gdpr`, or has a column that carries a classification other than `PUBLIC`, is marked sensitive, carries `meta.pii = true` or has a column masking rule. Any gap in the metadata (table not found, no metadata) classifies the grant as **sensitive** (fail closed).

**Customer realms.** Individual `Unmasked` and individual privileged grants are disabled for customer-realm users (`INDIVIDUAL_GRANT_DISABLED_FOR_REALM`) unless `AllowCustomerUnmasked` / `AllowCustomerPrivileged` is set.

**Four-eyes timing.** The approver count is checked when approvals are presented (activation or approval step). At request time only expiry, maximum and justification are checked. Paths that carry no approvals (declarative manifests, MCP `admin_apply_access`) refuse a four-eyes class with `INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES`.

---

## 3. Configuration (`Gateway:Governance:IndividualGrants`)

Every class limit is an optional operator limit that can only **shorten** the tenant maximum for its class; unset (`null`) means no class-specific limit. Values are validated at startup (`ValidateDataAnnotations` + `ValidateOnStart`): a class limit below 1 aborts startup. The tenant maximum itself (`Gateway:Governance:Grants:MaxDuration`, required) is described in the [configuration guide](../configuration-guide.md).

| Key | Default | Range | Description |
| :--- | :--- | :--- | :--- |
| `RegularMaxDays` | unset | `>= 1` | Limit of `Regular` individual grants. Also the read-time clamp of individual user consents and of regular profile assignments when set. |
| `SensitiveMaxDays` | unset | `>= 1` | Limit of `Sensitive` individual grants. |
| `UnmaskedMaxDays` | unset | `>= 1` | Limit of individual `Unmasked` grants. Also the read-time clamp of `Unmasked` profiles and assignments when set. |
| `PrivilegedMaxDays` | unset | `>= 1` | Limit of individual privileged functional rights. |
| `ServicePrincipalMaxDays` | unset | `>= 1` | Limit of service-principal grants. Also the read-time clamp of service-principal consents when set. |
| `CustomerRegularMaxDays` | unset | `>= 1` | Limit of regular grants to customer-realm users. |
| `AllowCustomerUnmasked` | `false` | bool | Allows individual `Unmasked` grants in customer realms. |
| `AllowCustomerPrivileged` | `false` | bool | Allows individual privileged grants in customer realms. |
| `RoleConversionHintAfterRenewals` | `2` | 1..10 | From this many previous renewals onwards, the decision carries a hint to convert the grant into a group mapping. |
| `CampaignRequiredAfterRenewals` | `3` | 1..10 | From this many previous renewals onwards, a direct extension is refused (`INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN`); with the default, the 4th renewal needs a campaign decision. |

```json
{
  "Gateway": {
    "Governance": {
      "Grants": {
        "DefaultDuration": "P1Y",
        "MaxDuration": "P5Y"
      },
      "IndividualGrants": {
        "RegularMaxDays": 365,
        "SensitiveMaxDays": 90,
        "UnmaskedMaxDays": 30
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
| Declarative consent manifest (`POST /api/v1/governance/consents/apply`) | `ManifestPolicy` | Absolute `validUntil` of the policy, required for every grantee and bounded by `Gateway:Governance:Grants:DeclarativeMaxDuration` (see [F-GOV-15](f-gov-15-declarative-consents-scopes.md)). Evaluated in dry-run as well. A manifest carries no approvals, so an individual `Allow` of a four-eyes class (a `Clear` column rule or a sensitive table) is never applied: after the self-grant check and the expiry and class-limit checks (which answer with their own codes, for example `INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM`), it is refused with `INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES`; see [F-GOV-15, Four-Eyes Rule for Individual Grantees](f-gov-15-declarative-consents-scopes.md#four-eyes-rule-for-individual-grantees). |
| MCP `admin_plan_access` / `admin_apply_access` | `McpAdminGrant` | `validUntil` per user principal; evaluated at planning time and again at apply time, where a four-eyes class is refused (`INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES`). The apply writes `Allow` grant records (consents) with that expiry. |
| Consent expiry extension and recertification extension (`ConsentRecertificationWorkflowService`) | `Renewal` | New expiry, capped at `now + max` of the class. |
| Access profile assignment (`IAccessProfileRepository.AssignProfileAsync` / `RenewAssignmentAsync`) | — | `ExpiresAt`, validated by `AccessProfileAssignmentRules`: required for a user assignment and bounded by `UnmaskedMaxDays` (`Unmasked` profile) or `RegularMaxDays` (other profiles) when configured. Role assignees are refused (`ROLE_ASSIGNEE_UNSUPPORTED`). |

`revoke-all` and other revocation paths are not affected.

### Error responses

A refused grant answers `400` with a problem details body whose `code` is a stable machine-readable code and, where known, `maxValidTo` (the latest allowed expiry). Example from the declarative manifest path:

```json
{
  "type": "urn:autheris:error:INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM",
  "title": "Bad Request",
  "status": 400,
  "detail": "The grant violates the grant rules.",
  "instance": "/api/v1/governance/consents/apply",
  "code": "INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM",
  "correlationId": "0f8b6c2e9d4a4f1e",
  "traceId": "0f8b6c2e9d4a4f1e",
  "maxValidTo": "2027-01-13T09:00:00+00:00"
}
```

| Code | Meaning |
| :--- | :--- |
| `INDIVIDUAL_GRANT_REQUIRES_EXPIRY` | No expiry given for an individual grant. There is no silent default for individual grants. |
| `INDIVIDUAL_GRANT_EXPIRY_IN_PAST` | The expiry is not in the future. |
| `INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM` | The expiry is later than `now +` the effective maximum (class limit or tenant maximum). |
| `INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES` | The grant needs independent four-eyes approval that the request does not carry. On approval paths: not enough independent approvers (different from requester and grantee) for the class. On the declarative manifest path, which carries no approvals, it is returned for an individual (`User` / `ServicePrincipal`) `Allow` with a `Clear` column rule or on a sensitive table once the expiry and class-limit checks have passed (a `validUntil` beyond the class limit answers `INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM` first); use the consent request/approval workflow or a group grantee instead. The manifest path also returns it, before any expiry check, for a self-grant (a policy for the applying actor or for a group it belongs to) and for a group `Allow` while the actor's group memberships are not completely resolved. |
| `INDIVIDUAL_GRANT_REQUIRES_JUSTIFICATION` | `IndividualUnmasked` or `IndividualPrivileged` without justification. |
| `INDIVIDUAL_GRANT_RENEWAL_BY_GRANTEE` | The grantee (or nobody) tried to renew. |
| `INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN` | Too many renewals; a campaign decision is required. |
| `INDIVIDUAL_GRANT_DISABLED_FOR_REALM` | The class is disabled for customer realms. |
| `INDIVIDUAL_GRANT_INDETERMINATE` | The grant could not be evaluated (unknown grantee type or internal error); denied. |

Access profile assignments use their own codes (`INDIVIDUAL_GRANT_REQUIRES_EXPIRY`, `GRANT_EXPIRY_IN_THE_PAST`, `GRANT_EXCEEDS_MAXIMUM_DURATION`, `UNMASKED_REQUIRES_FOUR_EYES`, `APPROVER_MUST_DIFFER`, `RENEWAL_BY_GRANTEE_FORBIDDEN`, `ASSIGNMENT_KIND_CONFLICT`, `ROLE_ASSIGNEE_UNSUPPORTED`), see [F-GOV-11](f-gov-11-declarative-access-profiles.md).

---

## 5. Renewal

- A renewal is a new approval by an owner or delegate, **never by the grantee**.
- The new expiry is at most `now +` the effective maximum of the class; four-eyes applies when the class requires it.
- `CONSENTS.renewal_count` and `access_profile_assignments.renewal_count` count the approved renewals. A renewed assignment restarts its grant period (`assigned_at`).
- From `RoleConversionHintAfterRenewals` previous renewals the decision suggests converting the recurring need into a group mapping; from `CampaignRequiredAfterRenewals` previous renewals a direct extension is refused and a campaign decision is required.

---

## 6. Read-Time Expiry and Clamp of Legacy Grants

Expiry is evaluated on every read; nothing in storage is rewritten. A clamp applies only when the named class limit is configured; without it the stored expiry applies. `Deny` consents and owner grants are never clamped:

| Object | Effective expiry |
| :--- | :--- |
| Consent of a `User` grantee | `min(ValidTo, ValidFrom + RegularMaxDays)` (`Consent.EffectiveValidTo`) |
| Consent of a `ServicePrincipal` grantee | `min(ValidTo, ValidFrom + ServicePrincipalMaxDays)` |
| Consent of a `Group` grantee | `ValidTo` (not clamped) |
| Access profile assignment to a user | `min(expires_at, assigned_at + max)` with max `RegularMaxDays` or, for an `Unmasked` profile, `UnmaskedMaxDays`; a legacy assignment without `expires_at` expires at `assigned_at + max` |
| Group assignment | `expires_at` (standing when null); a standing `Unmasked` group mapping lapses 90 days after its approval (recertification) |
| `Unmasked` profile without typed assignments (legacy) | `min(ValidTo, CreatedAt + UnmaskedMaxDays)` |

The class of a stored consent is not known at read time, so the consent clamp uses `RegularMaxDays` for every user consent as a backstop; the write paths enforce the exact class maximum. A grant past its clamped expiry is inactive (fail closed). The consent decision cache TTL (`ComputeDecisionCacheTtl`) is bounded by the stored `ValidTo` of the active consents and by the base TTL (10 minutes, 60 seconds for highly sensitive tables), so a cached decision built from a clamped grant ends at the latest one base TTL after the clamped expiry.

### Report of clamped grants

`GET /api/v1/governance/grants?clamped=true` lists the individual grants whose effective expiry is earlier than the stored one, with `validTo`, `effectiveValidTo`, `clamped=true` and the message `individual grant without expiry/over limit, treated as expiring at <instant>`. Clamped grants are re-approved through the normal renewal path. The endpoint is described in [F-GOV-11, section 7](f-gov-11-declarative-access-profiles.md#7-paged-grant-listing-get-apiv1governancegrants).

---

## 7. Behaviour Changes

These changes apply to every deployment that runs this version:

1. **Mandatory expiry for individual grants.** Requests that create a consent, bulk grant, manifest policy or MCP grant for a `User` or `ServicePrincipal` without an expiry, or with an expiry beyond the class maximum, are rejected with `400`. Declarative manifest policies need an absolute `validUntil` for every grantee (`Group`, `User`, `ServicePrincipal`); `validityDays` has no effect and role grantees are not accepted (see [F-GOV-15](f-gov-15-declarative-consents-scopes.md#apply-semantics)).
2. **Clamping of existing grants.** When class limits are configured, existing individual consents and access profile assignments with an over-long expiry are treated as expiring at the clamped instant; grants whose clamped instant has passed (for example created more than `RegularMaxDays` ago) become **inactive immediately**. Use the clamped-grant report before configuring or lowering a limit to plan re-approvals.
3. **Unmasked assignment rules.** An `Unmasked` profile takes effect only through a group mapping approved by a second administrator (recertified every 90 days) or a four-eyes individual grant within `UnmaskedMaxDays` when configured (see [F-GOV-11](f-gov-11-declarative-access-profiles.md)). Previously assigned `Unmasked` profiles that do not meet these rules stop unmasking data.
4. **Renewals.** Expiry extensions are capped per class (no fixed 30-day suggestion) and the grantee can no longer extend its own grant.

## 8. Deny Grants, Renewal Period and Manifests

- **Deny is never limited.** A `Deny` consent needs no expiry, has no maximum and is never clamped at read time (`GrantRequest.Effect`, `Consent.EffectiveValidTo`); an expiring deny would silently re-open the data. Only `Allow` grants of individual users and service principals are bounded.
- **A renewal restarts the clamp period.** The clamp is `min(valid_to, valid_from + max)`. `ExtendConsentExpiryAsync` therefore also moves `valid_from` forward to the renewal time (never earlier than a future start), so a renewal at day 150 or after expiry is not cancelled by the original period start. No extra column is needed.
- **Manifests.** A `Deny` manifest policy also needs a `validUntil` within `Gateway:Governance:Grants:DeclarativeMaxDuration`: the manifest requires one for every policy, although the grant policy itself sets no maximum for deny grants. An earlier individual `validUntil` than the stored one is a change (the old grant is revoked, a shorter one created); a later one is not (a longer validity is a renewal that needs its own approval). The `GITOPS_CONSENT_APPLY` audit entries are written inside the replace transaction.
- **Decision cache.** The cache lifetime of a consent decision is bounded by the clamped `EffectiveValidTo`, not by the raw `ValidTo`.
