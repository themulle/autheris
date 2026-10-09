# F-DBT-6: Policy & RLS Auto-Sync from dbt Metadata

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`DbtPolicySyncService.cs`](file:///root/autheris/src/Autheris.Application/Dbt/Services/DbtPolicySyncService.cs), [`CasbinRlsPushdownEngine.cs`](file:///root/autheris/src/Autheris.Application/Security/CasbinRlsPushdownEngine.cs)

---

## 1. Overview & Problem Statement

Defining security classifications and row-level filtering rules in multiple places creates policy drift. Analytics engineers already define metadata, tags (e.g. `meta: { sensitivity: high, rls_tenant_column: tenant_id }`), and access policies in dbt YAML files. F-DBT-6 automatically translates dbt model metadata tags into active gateway Row-Level Security filters and Casbin ABAC access policies.

---

## 2. Business Value

- **Security as Code in Data Engineering**: Manage data access policies directly within dbt version control alongside data transformations.
- **Elimination of Policy Drift**: Changes to security tags in dbt YAML immediately update runtime RLS and masking rules.
- **Streamlined Approvals**: Security teams review access policy PRs in Git rather than clicking through proprietary administrative consoles.

---

## 3. Architecture & Capabilities

- Ingests `meta.sensitivity` tags (`PUBLIC`, `INTERNAL`, `CONFIDENTIAL`, `RESTRICTED`).
- Automatically configures column masking based on `meta.masking_rule`.
- Generates dialect-aware SQL RLS filters mapped to `meta.tenant_column`.

---

## 4. Usage Example

```yaml
# dbt schema.yml definition
version: 2
models:
  - name: fct_customer_transactions
    meta:
      sensitivity: CONFIDENTIAL
      rls_tenant_column: tenant_id
    columns:
      - name: credit_card_number
        meta:
          masking_rule: REDACT
```

```bash
# Gateway automatically ingests and enforces:
# 1. RLS: WHERE tenant_id = @caller_tenant_id
# 2. Column Masking: credit_card_number -> [REDACTED]
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Dbt": {
      "PolicySync": {
        "Enabled": true,
        "SyncIntervalMinutes": 30,
        "DefaultTenantColumnName": "tenant_id",
        "EnforceStrictTagValidation": true
      }
    }
  }
}
```
