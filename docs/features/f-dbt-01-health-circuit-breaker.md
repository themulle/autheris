# F-DBT-1: dbt Data Health Circuit Breaker

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`IDbtHealthCircuitBreaker.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Dbt/Interfaces/IDbtHealthCircuitBreaker.cs), [`DbtHealthCircuitBreaker.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Dbt/Services/DbtHealthCircuitBreaker.cs)

---

## 1. Overview & Problem Statement

When upstream data pipelines or dbt transformations fail their automated test suites (e.g. `dbt test` uniqueness, not_null, or business validation tests), traditional API gateways remain unaware and continue serving corrupt or stale data to downstream consumers and AI agents. F-DBT-1 ingests dbt `run_results.json` and trips an automated Circuit Breaker on affected tables, immediately switching to `CircuitBreaker: Open` and returning descriptive HTTP 503 / GraphQL error responses until upstream health is restored.

---

## 2. Business Value

- **Prevention of Dirty Data Consumption**: Safeguards automated trading, billing, and reporting applications from making decisions on corrupted upstream data.
- **Protection for AI Reasoning**: Prevents LLMs from ingesting invalid numbers that cause severe analytical hallucinations.
- **Automated Incident Isolation**: Quarantines broken tables instantly without taking down the entire gateway or unrelated domains.

---

## 3. Architecture & Capabilities

- High-throughput parsing of dbt `run_results.json` test outcomes.
- Automated state machine: `Closed` (healthy), `Open` (quarantined), `HalfOpen` (probing on next run).
- Custom error payload indicating exact failed test names and upstream model dependencies.

---

## 4. Usage Example

```bash
# Push dbt test execution results from CI/CD pipeline
curl -X POST http://localhost:8080/api/v1/dbt/run-results \
  -H "Authorization: Bearer <dbt-webhook-secret>" \
  -H "Content-Type: application/json" \
  -d @target/run_results.json

# If a test fails, subsequent queries for that table return fail-closed response:
# {
#   "errors": [
#     {
#       "message": "Data source temporarily quarantined due to upstream dbt test failure: unique_customers_customer_id.",
#       "extensions": {
#         "code": "CIRCUIT_BREAKER_OPEN",
#         "table": "crm.customers"
#       }
#     }
#   ]
# }
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Dbt": {
      "CircuitBreaker": {
        "Enabled": true,
        "QuarantineOnSeverity": "Error",
        "AutoRecoverOnSuccessfulTest": true,
        "FailureResponseStatusCode": 503
      }
    }
  }
}
```
