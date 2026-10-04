# F-DBT-2: dbt Model Contract Enforcement & Breaking-Change CI Gate

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`IDbtContractValidator.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Dbt/Interfaces/IDbtContractValidator.cs), [`DbtContractValidator.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Dbt/Services/DbtContractValidator.cs)

---

## 1. Overview & Problem Statement

Data teams frequently deploy dbt transformations that rename, drop, or alter the data types of columns, unintentionally breaking downstream mobile apps, GraphQL clients, and BI dashboards. F-DBT-2 provides a proactive CI/CD validation gate (`autheris-schema-check`). By comparing new dbt model contracts against active gateway schemas, breaking changes are rejected in Git pull requests before deployment.

---

## 2. Business Value

- **Zero Production API Breakages**: Blocks incompatible schema changes in CI before they reach production databases.
- **Data Contract Reliability**: Enforces the contract between data producers (analytics engineers) and data consumers (app developers).
- **Automated Pull Request Verification**: Integrates directly into GitHub Actions, GitLab CI, or Azure DevOps pipelines.

---

## 3. Architecture & Capabilities

- Validates `contract: { enforced: true }` blocks in dbt `manifest.json` against active gateway catalogs.
- Detects dropped fields, altered data types, and violated non-nullability constraints.
- Emits detailed GitHub Markdown summary tables in pull request comments.

---

## 4. Usage Example

```bash
# Execute CLI contract validator in CI pipeline
dotnet run --project tools/autheris-schema-check/autheris-schema-check.csproj -- \
  --manifest ./target/manifest.json \
  --gateway-schema http://gateway.corp.local/graphql \
  --fail-on-breaking

# Output:
# [ERROR] Breaking change detected in model 'dim_customers':
#   - Column 'account_status' dropped (breaking 14 downstream GraphQL operations)
# CI check failed with exit code 1.
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Dbt": {
      "ContractEnforcement": {
        "Enabled": true,
        "AllowCompatibleWidening": true,
        "BlockDroppedColumns": true,
        "RequireContractOnPublicModels": true
      }
    }
  }
}
```
