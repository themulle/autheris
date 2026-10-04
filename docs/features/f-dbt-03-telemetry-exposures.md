# F-DBT-3: Live-Telemetry Exposure Publisher

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`IDbtExposurePublisher.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Dbt/Interfaces/IDbtExposurePublisher.cs), [`DbtExposurePublisher.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Dbt/Services/DbtExposurePublisher.cs)

---

## 1. Overview & Problem Statement

Data engineers often have no visibility into how models in the warehouse are consumed in production, making it difficult to assess the blast radius of maintenance or refactorings. F-DBT-3 continuously analyzes incoming GraphQL and REST query traffic, maps executed fields to upstream dbt models, and automatically generates and publishes updated dbt `exposures.yaml` files back to the repository.

---

## 2. Business Value

- **Full Visibility into Downstream Usage**: Data engineers see exact query frequencies, consuming services, and owner teams directly in the dbt documentation DAG.
- **Informed Deprecation Decisions**: Prevents keeping unused data pipelines alive or mistakenly deleting heavily used models.
- **Automated Governance Feedback Loop**: Bridges operational runtime traffic back to warehouse data engineering.

---

## 3. Architecture & Capabilities

- Live aggregation of query frequencies and consumer client types per model.
- Automated export of valid dbt `exposures.yaml` documents.
- Integration with GitHub / GitLab APIs to create automated exposure update PRs.

---

## 4. Usage Example

```bash
# Export generated dbt exposures YAML via CLI or REST endpoint
curl -X GET http://localhost:8080/api/v1/dbt/exposures \
  -H "Authorization: Bearer <admin-token>" \
  -o exposures.yaml

# Generated dbt exposure snippet:
# version: 2
# exposures:
#   - name: autheris_graphql_sales_api
#     type: application
#     maturity: high
#     owner:
#       name: Sales Engineering Team
#     depends_on:
#       - ref('fct_orders')
#       - ref('dim_customers')
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Dbt": {
      "Exposures": {
        "Enabled": true,
        "PublishIntervalHours": 24,
        "MinQueryThreshold": 10,
        "OutputPath": "dbt_project/models/exposures.yaml"
      }
    }
  }
}
```
