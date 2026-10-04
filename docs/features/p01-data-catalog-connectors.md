# P1: Enterprise Data Catalog Connectors (Purview, Collibra, OpenMetadata)

**Status:** [Done] (100% GA – Core Foundation)  
**Components:** [`PurviewDataCatalogClient.cs`](file:///root/lis-git/autheris/src/Autheris.Extensions/DataCatalog/PurviewDataCatalogClient.cs), [`CollibraDataCatalogClient.cs`](file:///root/lis-git/autheris/src/Autheris.Extensions/DataCatalog/CollibraDataCatalogClient.cs), [`OpenMetadataDataCatalogClient.cs`](file:///root/lis-git/autheris/src/Autheris.Extensions/DataCatalog/OpenMetadataDataCatalogClient.cs), [`DataCatalogSyncService.cs`](file:///root/lis-git/autheris/src/Autheris.Application/DataCatalog/Services/DataCatalogSyncService.cs)

---

## 1. Overview & Problem Statement

Manual double-maintenance of classification tags, sensitivity tiers, and masking rules between enterprise data catalogs and API gateways leads to discrepancies, audit failures, and compliance breaches. P1 delivers native REST connectors with Polly 8 resilience and OAuth for Microsoft Purview, Collibra, Alation, and OpenMetadata. Classifications and GDPR Art. 9 tags synchronize directly into gateway masking rules and consent policies.

---

## 2. Business Value

- **100% Governance Consistency**: The enterprise data catalog remains the definitive single source of truth; policies propagate automatically into API access enforcement.
- **Elimination of Audit Findings**: Guarantees that columns marked as PII or confidential in the catalog are immediately masked at the gateway.
- **Automated Regulatory Compliance**: Instantly identifies and shields GDPR Article 9 special categories (health, biometric, religious data) with mandatory Four-Eyes approval.

---

## 3. Architecture & Capabilities

- Mirror Mode (local persistence with cache epoch invalidation) and Reference Mode (on-demand live lookup).
- Tag-to-masking rule mappings (`PII_EMAIL` -> `MASK_EMAIL`, `SSN` -> `REDACT`).
- Scheduled periodic synchronization and on-demand administrative GraphQL mutation (`syncDataCatalog`).

---

## 4. Usage Example

```graphql
# Trigger an on-demand catalog synchronization dry-run
mutation TriggerCatalogSync {
  syncDataCatalog(dryRun: false) {
    success
    syncedTablesCount
    syncedColumnsCount
    maskedColumnsCount
    art9ProtectedTablesCount
    affectedTables {
      domain
      schema
      tableName
    }
    warnings
  }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Catalog": {
      "Enabled": true,
      "Provider": "MicrosoftPurview",
      "SyncMode": "Mirror",
      "SyncIntervalMinutes": 60,
      "Purview": {
        "Endpoint": "https://corp-purview.purview.azure.com",
        "TenantId": "72f988bf-86f1-41af-91ab-2d7cd011db47",
        "ClientId": "a820c78a-f326-4d1d-91b4-2195f1342618"
      },
      "TagToMaskingRuleMap": {
        "PII.Email": "MASK_EMAIL",
        "PII.CreditCard": "REDACT",
        "PII.SSN": "REDACT"
      }
    }
  }
}
```
