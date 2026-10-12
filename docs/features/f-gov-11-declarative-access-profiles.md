# F-GOV-11: Declarative Access Profiles & Person-Based Plaintext Exceptions (R-52, R-50, R-20)

## Executive Summary

Enterprise access governance often requires individualized or team-specific exemptions from standard masking rules without elevating users to full administrative bypass roles.

Autheris implements **Declarative Access Profiles** (`ACCESS_PROFILES`), enabling policy administrators to configure deterministic, time-bounded, or purpose-specific masking exemptions. For example, while user `philipp` observes pseudonymized or masked customer data by default, an authorized compliance officer or fraud investigator `david` can be assigned an `Unmasked` access profile for designated schemas or tables.

---

## Architectural Implementation

### 1. Database Schema (`ACCESS_PROFILES`)
The access profile schema is natively provisioned across all supported governance databases (SQLite, PostgreSQL, and Microsoft SQL Server):

```sql
CREATE TABLE ACCESS_PROFILES (
    id NVARCHAR(64) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(64) NOT NULL,
    subject NVARCHAR(256) NOT NULL,
    profile_type NVARCHAR(64) NOT NULL, -- e.g. 'Unmasked', 'StandardMasked', 'StrictMasked'
    target_pattern NVARCHAR(512) NOT NULL, -- e.g. 'finance.invoices.*', 'customers.pii'
    exemption_purpose NVARCHAR(256) NULL,
    valid_from DATETIMEOFFSET NOT NULL,
    valid_until DATETIMEOFFSET NULL,
    created_by NVARCHAR(256) NOT NULL,
    created_at DATETIMEOFFSET NOT NULL
);
CREATE INDEX IX_ACCESS_PROFILES_SUBJECT ON ACCESS_PROFILES (tenant_id, subject);
```

### 2. Integration into `TableAccessPolicy`
- When evaluating permissions in [`TableAccessPolicy`](../../src/Autheris.Application/Policy/TableAccessPolicy.cs), the gateway checks for matching active access profiles for the current subject (`TenantId` and `Subject`).
- If an `Unmasked` profile matches the queried table pattern within its validity window:
  - Column masking policies are bypassed for that specific table access without granting DBA or system privileges.
  - The access is tagged in the audit trail with the profile identifier and stated business purpose.
- If no matching profile exists, default masking policies (`GEO_JITTER`, `PARTIAL_MASK`, `HASH`, `REDACT`) are strictly enforced.

### 3. High-Performance Caching & Synchronous Eviction
- Access profiles are cached in an L1 memory cache keyed by `access_profile:{tenant}:{subject}` with a short TTL.
- When profiles are modified via API or GitOps synchronization, [`TableAccessPolicy.InvalidateCache`](../../src/Autheris.Application/Policy/TableAccessPolicy.cs) synchronously purges both local memory cache entries and triggers cluster-wide invalidation events.

### 4. Bulk Consent API (`POST /api/v1/consents/bulk`)
- Allows administrative tooling to grant or revoke batch consents and profile associations in a single atomic transaction.
- Emits transactional `CONSENT_GRANTED` / `PROFILE_ASSIGNED` audit events tied directly to the mutation.

---

## Example Usage

### Granting a Plaintext Exemption Profile via API:

```http
POST /api/v1/governance/access-profiles
Content-Type: application/json
Authorization: Bearer <GovernanceAdmin-Token>

{
  "tenantId": "finance-eu",
  "subject": "david@corp.local",
  "profileType": "Unmasked",
  "targetPattern": "billing.payment_records.*",
  "exemptionPurpose": "Fraud Investigation Incident #INC-8921",
  "validUntil": "2026-10-10T18:00:00Z"
}
```
