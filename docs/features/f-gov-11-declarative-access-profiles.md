# F-GOV-11: Declarative Access Profiles & Person-Based Plaintext Exceptions (R-52, R-50, R-20)

**Components:** [`AccessProfile.cs`](../../src/Autheris.Domain/Model/AccessProfile.cs), [`AccessProfileAssignment.cs`](../../src/Autheris.Domain/Model/AccessProfileAssignment.cs), [`IAccessProfileRepository.cs`](../../src/Autheris.Domain/Interfaces/IAccessProfileRepository.cs), [`AccessProfileStore.cs`](../../src/Autheris.Infrastructure/Persistence/AccessProfileStore.cs), [`AccessProfileCache.cs`](../../src/Autheris.Application/Policy/Services/AccessProfileCache.cs), [`GovernanceEndpoints.Access.cs`](../../src/Autheris.Api/Endpoints/GovernanceEndpoints.Access.cs), [`GovernanceEndpoints.Audit.cs`](../../src/Autheris.Api/Endpoints/GovernanceEndpoints.Audit.cs), [`ConsentPageReader.cs`](../../src/Autheris.Application/Governance/Paging/ConsentPageReader.cs)

## Executive Summary

Enterprise access governance often requires individual or team-specific deviations from the standard masking rules without elevating users to administrative bypass roles.

Autheris implements **access profiles**: a named set of target table patterns with a masking mode (`Strict`, `Default` or `Unmasked`) and an optional row filter, assigned to **users, groups or roles**. For example, user `philipp` sees pseudonymized customer data by default, while the fraud team group can be mapped to an approved `Unmasked` profile for designated schemas, and an individual investigator `david` can receive a four-eyes, time-bound `Unmasked` grant of at most 30 days.

Access profiles are persisted on every governance engine (SQLite, PostgreSQL, SQL Server). The in-memory repository is a test double only and is never registered by the gateway, so profiles are consistent across replicas.

---

## 1. Data Model

```sql
CREATE TABLE access_profiles (
    profile_id VARCHAR(128) NOT NULL,
    tenant_id VARCHAR(64) NOT NULL,
    name VARCHAR(256) NOT NULL,
    masking_mode VARCHAR(32) NOT NULL DEFAULT 'Default',   -- Strict | Default | Unmasked
    target_tables TEXT NOT NULL DEFAULT '["*.*"]',          -- JSON array of table patterns, e.g. ["tem.gps_position", "md.*"]
    row_filter_predicate TEXT NULL,
    justification TEXT NULL,
    created_by VARCHAR(256) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    valid_to TIMESTAMPTZ NULL,
    PRIMARY KEY (tenant_id, profile_id)
);

CREATE TABLE access_profile_assignments (
    profile_id VARCHAR(128) NOT NULL,
    tenant_id VARCHAR(64) NOT NULL,
    subject VARCHAR(256) NOT NULL,
    subject_type VARCHAR(32) NOT NULL DEFAULT 'User',       -- User | Group | Role
    subject_norm VARCHAR(256) NULL,                         -- upper-invariant subject for case-insensitive lookup
    assigned_at TIMESTAMPTZ NOT NULL,                       -- start of the current grant period (a renewal restarts it)
    expires_at TIMESTAMPTZ NULL,
    created_by VARCHAR(256) NULL,                           -- requester
    approved_by VARCHAR(256) NULL,                          -- second person (four-eyes)
    approved_at TIMESTAMPTZ NULL,
    justification TEXT NULL,
    kind VARCHAR(32) NOT NULL DEFAULT 'Standard',           -- Standard | BreakGlass
    renewal_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, profile_id, subject_type, subject),
    FOREIGN KEY (tenant_id, profile_id) REFERENCES access_profiles (tenant_id, profile_id) ON DELETE CASCADE
);
-- lookup index on (tenant_id, subject_type, subject_norm)
```

The SQL Server and SQLite baselines use the equivalent types. `AccessProfile.Assignments` carries the typed assignments (users, groups, roles) with their approvals and expiry; it is the only source of who holds a profile. There is no plain subject list: a bulk-consent request that carries `assignedSubjects` is answered with 400 `ACCESS_PROFILE_PLAIN_SUBJECTS_REMOVED` before any store call.

---

## 2. Assignment Rules

`AccessProfileAssignmentRules` validates every write and decides at read time whether an assignment is effective. Anything doubtful is not effective (fail closed).

### 2.1 Who can be assigned

| Assignment | Expiry | Approval | Maximum |
| :--- | :--- | :--- | :--- |
| Group or role mapping (`Standard`) | optional (standing) | `Strict`/`Default`: none. `Unmasked`: a second administrator (`approved_by` ≠ `created_by`) | none; recertified |
| Individual user, `Strict` or `Default` profile | **mandatory** | none | 180 days from `assigned_at` |
| Individual user, `Unmasked` profile | **mandatory** | four-eyes: requester, approver and grantee all different, justification required | 30 days from `assigned_at` |
| Break-glass exception (`kind = BreakGlass`, any subject) | **mandatory** | requester and justification at creation; an independent approver within 24 hours | 72 hours from `assigned_at` |

### 2.2 When `Unmasked` takes effect

`Strict` and `Default` profiles can only tighten masking, so their assignments are effective while unexpired. An `Unmasked` profile unmasks data only through one of three routes:

1. A **group/role mapping** approved by a second administrator (`approved_by` set and different from `created_by`). The mapping is standing and recertified.
2. An **individual four-eyes grant** of at most 30 days with an explicit expiry.
3. An **active break-glass exception**: effective for at most 72 hours, and without an approval only during the first 24 hours after `assigned_at`.

An `Unmasked` assignment that matches none of these routes (for example an unapproved group mapping or a legacy user assignment without approver) is ignored.

### 2.3 Precedence

When several profiles match a table, the most restrictive applies: `Strict` before `Default` before `Unmasked`; ties are broken by `profile_id` (ordinal). An unknown masking mode is treated as `Strict`. The repository returns effective profiles already in this order, so taking the first matching profile is deterministic on every replica.

### 2.4 Read-time expiry

The effective expiry of an assignment is `min(expires_at, assigned_at + maximum)` for individual and break-glass assignments; an individual assignment without `expires_at` is clamped and never permanent. A profile with no typed assignments (legacy) and mode `Unmasked` expires at `min(valid_to, created_at + 30 days)`. A profile past its own `valid_to` is not effective for any assignment. See [F-GOV-22](f-gov-22-time-bound-individual-grants.md) for the time-bound grant policy and the clamp of legacy grants.

---

## 3. Repository Contract (`IAccessProfileRepository`)

| Method | Behaviour |
| :--- | :--- |
| `GetProfilesForSubjectAsync(tenant, subject)` | Effective profiles of one user subject (SID or alias), ordered by precedence. Used on the request path (`TableAccessPolicy`, query analysis, `AccessProfileCache`). |
| `GetProfilesForSubjectsAsync(tenant, users, groups, roles)` | Effective profiles found through the caller's user ids, groups and roles, ordered by precedence. Expired, unapproved `Unmasked` and over-limit assignments are not returned. A store failure throws; it never returns an empty list. |
| `UpsertProfileAsync(profile)` | Stores the profile and reconciles its assignments in one transaction. Typed assignments are authoritative: they are validated, their period restarts now, and every stored assignment that is not in the list is removed (an empty list clears them all). |
| `AssignProfileAsync(tenant, profileId, assignment)` | Adds or replaces one assignment (period starts now). Throws `AccessProfileAssignmentRejectedException` and stores nothing when a rule is violated. |
| `ApproveAssignmentAsync(tenant, profileId, type, subject, approver)` | Records the second person. The grantee never approves; for `Unmasked` and break-glass the requester does not either. An assignment can be approved once (`ALREADY_APPROVED`). |
| `RenewAssignmentAsync(tenant, profileId, type, subject, newExpiresAt, approver)` | Renewal with a new approval (never by the grantee), restarts the period and increments `renewal_count`. Break-glass is not renewable. |
| `RemoveAssignmentAsync(...)` / `DeleteProfileAsync(...)` | Removes one assignment / the profile and all its assignments. |

Rejection codes (`AccessProfileAssignmentRejectedException.Code`): `INDIVIDUAL_GRANT_REQUIRES_EXPIRY`, `GRANT_EXPIRY_IN_THE_PAST`, `GRANT_EXCEEDS_MAXIMUM_DURATION`, `UNMASKED_REQUIRES_FOUR_EYES`, `BREAK_GLASS_REQUIRES_JUSTIFICATION`, `APPROVER_MUST_DIFFER`, `RENEWAL_BY_GRANTEE_FORBIDDEN`, `BREAK_GLASS_NOT_RENEWABLE`, `ASSIGNMENT_KIND_CONFLICT`, `ALREADY_APPROVED`.

---

## 4. Integration into `TableAccessPolicy`

- For every governed table access the gateway loads the effective profiles of the caller (through `AccessProfileCache`) and applies the first profile whose target patterns match the table.
- An effective `Unmasked` profile bypasses column masking for that table without granting administrative privileges; `Strict` applies the strictest masking; `Default` keeps the standard masking rules (`GEO_JITTER`, `PARTIAL_MASK`, `HASH`, `REDACT`).
- An optional `row_filter_predicate` of the profile is added to the row filters.

### Caching

- Profiles are cached in the shared L1 memory cache per tenant, policy epoch and subject with a 5-minute TTL. Entries are sized in estimated bytes (256 bytes plus 1 KB per profile plus the key), so they count correctly against `Caching:L1MemoryCache:SizeLimitMb`.
- A profile change through the API invalidates the tenant's entries cluster-wide (`IAccessProfileCache.InvalidateTenantAsync`).

---

## 5. Bulk Grant API (`POST /api/v1/consents/bulk`)

Creates one access profile for one subject and assigns it in one request.

```http
POST /api/v1/consents/bulk
Content-Type: application/json
Authorization: Bearer <DataOwner-or-GovernanceAdmin-Token>

{
  "subject": "S-1-5-21-ANALYST-7",
  "subjectType": "User",
  "maskingMode": "Default",
  "tables": [ "billing.invoices", "billing.payments" ],
  "rowFilter": "region = 'EU'",
  "validDays": 30,
  "justification": "Quarter-end reconciliation INC-8921"
}
```

| Field | Rules |
| :--- | :--- |
| `subject` | Required; self-grants are rejected (Segregation of Duties, compared against all caller identifiers and groups). |
| `subjectType` | `User`, `Group`, `Role` or `ServicePrincipal`; anything else is `400`. |
| `maskingMode` | `Default` or `Strict`. `Unmasked` is refused with `403`, because clear-text access needs the four-eyes consent request workflow. |
| `tables` | At least one pattern. |
| `rowFilter` | Optional; parsed and sandboxed (no `;` or comments). |
| `validDays` | 1 to 180. For individual subjects the resulting expiry is additionally checked by the grant policy against the class maximum (for example 90 days when a matched table is sensitive or cannot be classified). |
| `justification` | At least 15 characters. |

The profile is stored first, then the assignment is created through `AssignProfileAsync`. When the grant policy or the assignment rules reject the request, the profile is deleted again (no unassigned profile remains) and the response is `400` with the error code (`{ "error": "...", "message": "...", "maxValidTo": "..." }`). A successful grant writes a required `CONSENT_GRANTED` audit event and invalidates the tenant's profile cache.

---

## 6. Behaviour Changes

- Access profiles are persisted on PostgreSQL and SQL Server as well (previously in memory on these engines), so they survive restarts and are shared by all replicas.
- `Unmasked` assignments follow the rules of section 2.2; assignments that do not meet them no longer unmask data.
- Individual user assignments are time-bound and clamped at read time (section 2.4).

---

## 7. Paged Grant Listing (`GET /api/v1/governance/grants`)

Keyset-paged listing of consents/grants of the caller's tenant for administration and entitlement reviews.

| Query parameter | Description |
| :--- | :--- |
| `cursor` | Opaque continuation token from `nextCursor`. A malformed cursor is `400 INVALID_CURSOR` (never a silent restart). |
| `pageSize` | Default 100, clamped to 1..500. |
| `table` | Restrict to one table (`domain.schema.table`); malformed is `400 INVALID_TABLE`. |
| `granteeType` | `User`, `Group`, `Role` or `ServicePrincipal`; otherwise `400 INVALID_GRANTEE_TYPE`. |
| `grantee` | Grantee SID or role name. |
| `includeInactive` | `true` also lists revoked and expired grants. |
| `clamped` | `true` lists only individual grants whose effective expiry was clamped ([F-GOV-22](f-gov-22-time-bound-individual-grants.md)). |

**Authorization:** `GovernanceAdmin`, `TenantAdmin`, `SecurityAuditor` and `PrivacyAdmin` read their tenant; a `DataOwner` reads only the grants of one table they own (with `table`). Otherwise `403`. Every read writes a summarized `AUDIT_READ` audit event.

Response items carry `id`, `table`, `granteeType`, `grantee`, `effect`, `validFrom`, `validTo`, `effectiveValidTo`, `clamped`, `isActive`, `renewalCount` and, for clamped grants, a `message`; the page carries `nextCursor` (null on the last page) and the effective `pageSize`.

The audit trail has the same paging model: see `GET /api/v1/governance/audit` in [F-AUDIT-01](f-audit-01-hardened-cryptographic-audit.md#paged-audit-listing-get-apiv1governanceaudit).

## 8. Runtime Resolution, Approval and Expiry

- **Resolution.** `TableAccessPolicy` resolves profiles with ONE `GetProfilesForSubjectsAsync` call that carries every identity of the caller (SIDs and aliases), its groups and its roles, so group and role mappings take effect at runtime and the profile of the first SID cannot hide the profiles of the others. `IAccessProfileCache.GetProfilesForSubjectsAsync` caches by tenant, epoch and a SHA-256 hash of the normalised subject set (typed, upper-case, ordered). Among all matching, active profiles the most restrictive wins (Strict, Default, Unmasked).
- **Cached profiles are re-checked.** `AccessProfile.IsActive` stays true only while at least one loaded assignment is effective: its clamped expiry, the 24 h approval window of an unapproved break-glass exception and the recertification below are evaluated at use, not only when the profile is loaded.
- **Recertification.** A standing `Unmasked` group/role mapping lapses 90 days after `ApprovedAt`; a mapping without an approval time is not effective. A renewal sets a new `ApprovedAt`.
- **Approval endpoint.** `POST /api/v1/profiles/{id}/assignments/approve` with `{ "subject", "subjectType" }` (GovernanceAdmin or ClusterAdmin). The approver must differ from the requester (`CreatedBy`) and the grantee; for an individual `Unmasked` grant the approver must be data owner or delegate of every matched table (`IsAuthorizedApproverForTableAsync`; an unavailable ownership source is a 503). The approval is a Tier-A `CONSENT_APPROVED` audit event and bumps the profile epoch (an unavailable epoch store is a 503 plus `PROFILE_INVALIDATION_FAILED`).
