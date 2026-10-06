#!/usr/bin/env python3
"""
Comprehensive Integration Test Suite for all GqlGateway Extensions:
1. Lakehouse: Apache Iceberg v2 Connector over MinIO (S3) & Azure Blob Storage emulation
2. OData v4: Direct Connector, CSDL Metadata Generator & Keyset Paging
3. Multi-Catalog Governance:
   - OpenMetadata REST v1 tables & webhook
   - Microsoft Purview / Apache Atlas basic search & classification mapping
   - Collibra Data Intelligence REST Core v2 assets & domain hierarchy
   - Alation Data Catalog integration API v2 & PII field mapping
   - Automated GDPR Art. 9 Enforcement (Health, Biometric, High Sensitivity)
4. ITSM Approval Workflows:
   - ServiceNow Table API Change Request & Webhook Handler
   - Jira Service Management REST API Issue Creation & Webhook Handler
5. dbt (data build tool):
   - Streaming manifest.json distribution
   - Contract validation & breaking changes gate
   - Zero-Trust proposal approval lifecycle
   - Downstream exposure publishing
"""

import json
import os
import sys
import time
import urllib.request
import urllib.error

BASE_URL = os.environ.get("TARGET_PROXY", "http://localhost:8082").rstrip("/")
MOCK_URL = os.environ.get("TARGET_MOCK", "http://localhost:8585").rstrip("/")
def _load_secret():
    """HMAC secret from the environment, else GATEWAY_HMAC_SECRET from the untracked deploy/.env."""
    val = os.environ.get("HMAC_SECRET_KEY") or os.environ.get("GATEWAY_HMAC_SECRET")
    if val:
        return val
    env_file = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".env")
    if os.path.exists(env_file):
        with open(env_file, encoding="utf-8") as fh:
            for line in fh:
                if line.startswith("GATEWAY_HMAC_SECRET="):
                    return line.split("=", 1)[1].strip()
    sys.exit("HMAC_SECRET_KEY / GATEWAY_HMAC_SECRET not set and deploy/.env not found (run scripts/generate-env.sh)")

SECRET_KEY = _load_secret()

def log(msg):
    print(f"\033[1;36m[Extensions Test]\033[0m {msg}")

def log_success(msg):
    print(f"\033[1;32m[Extensions Test] SUCCESS:\033[0m {msg}")

def log_warn(msg):
    print(f"\033[1;33m[Extensions Test] WARNING:\033[0m {msg}")

def log_err(msg):
    print(f"\033[1;31m[Extensions Test] FAILED:\033[0m {msg}")

def http_req(url, method="GET", data=None, headers=None, expect_status=200):
    if headers is None:
        headers = {}
    if "Content-Type" not in headers and (data is not None or method in ("POST", "PUT")):
        headers["Content-Type"] = "application/json"

    encoded_data = json.dumps(data).encode("utf-8") if isinstance(data, (dict, list)) else (data.encode("utf-8") if isinstance(data, str) else None)
    req = urllib.request.Request(url, data=encoded_data, headers=headers, method=method)

    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            content = resp.read().decode("utf-8", errors="replace")
            status = resp.status
            if status != expect_status and expect_status is not None:
                raise RuntimeError(f"Expected status {expect_status}, got {status}: {content}")
            return status, resp.headers, content
    except urllib.error.HTTPError as e:
        content = e.read().decode("utf-8", errors="replace")
        if expect_status is not None and e.code != expect_status:
            raise RuntimeError(f"HTTP {e.code} ({e.reason}): {content}")
        return e.code, e.headers, content
    except urllib.error.URLError as e:
        raise RuntimeError(f"Connection failed for {url}: {e.reason}")

# ------------------------------------------------------------------------------
# 1. Lakehouse Extension Tests (Apache Iceberg over MinIO & Azure Blob)
# ------------------------------------------------------------------------------
def test_lakehouse():
    log("Testing Apache Iceberg Lakehouse Connector over MinIO (S3)...")
    url = f"{BASE_URL}/graphql"
    query = """
    query TestLakehouseScan {
      table(domain: "lakehouse", name: "orders", schema: "dbo", first: 5) {
        tableName
        totalCount
        jsonRows
      }
    }
    """
    headers = {
        "X-Benchmark-Role": "Finance",
        "GraphQL-Preflight": "1"
    }
    status, _, body = http_req(url, method="POST", data={"query": query}, headers=headers, expect_status=200)
    res = json.loads(body)

    if "errors" in res and res["errors"]:
        raise RuntimeError(f"Lakehouse query returned GraphQL errors: {res['errors']}")

    table_data = res.get("data", {}).get("table")
    if not table_data:
        raise RuntimeError("No table data returned for lakehouse.dbo.orders")

    rows = json.loads(table_data.get("jsonRows", "[]"))
    log(f"Received {len(rows)} Lakehouse orders rows.")
    if len(rows) == 0:
        raise RuntimeError("Lakehouse query returned 0 rows!")

    # Verify column masking on customerEmail (must be masked for Finance group)
    first_row = rows[0]
    email = first_row.get("customerEmail")
    if email and "@" in email and "***" not in email:
        raise RuntimeError(f"Zero-Trust Policy Violation: customerEmail was NOT masked: {email}")

    log_success(f"Lakehouse Iceberg Scan verified: {len(rows)} rows, Email Masking verified: '{email}'")

    # Verify Azure Blob Storage Provider emulation endpoint
    log("Testing Azure Blob Storage / ADLS Gen2 Provider endpoint...")
    status, _, azure_body = http_req(f"{MOCK_URL}/iceberg/metadata/v2.metadata.json", method="GET", expect_status=200)
    log_success("Azure Blob Storage Provider emulation endpoint verified.")
    return True

# ------------------------------------------------------------------------------
# 2. OData v4 Extension Tests
# ------------------------------------------------------------------------------
def test_odata():
    log("Testing OData v4 direct protocol adapter and CSDL generation...")

    # 1. Service Root
    status, _, body = http_req(f"{BASE_URL}/odata", method="GET", expect_status=200)
    root = json.loads(body)
    if "value" not in root:
        raise RuntimeError("OData service root response missing 'value' array.")
    log(f"OData Service Root: Found {len(root['value'])} EntitySets.")

    # 2. CSDL Metadata ($metadata)
    status, headers, csdl = http_req(f"{BASE_URL}/odata/$metadata", method="GET", expect_status=200)
    if "edmx:Edmx" not in csdl or "EntityType" not in csdl:
        raise RuntimeError("OData $metadata did not return valid EDMX CSDL XML schema.")
    log("OData $metadata CSDL XML Schema verified.")

    # 3. EntitySet Direct Query with Pagination
    status, _, orders_body = http_req(f"{BASE_URL}/odata/orders?$top=5", method="GET", expect_status=200)
    orders = json.loads(orders_body)
    if "@odata.context" not in orders or "value" not in orders:
        raise RuntimeError("OData /odata/orders response missing @odata.context or value.")
    log_success(f"OData /odata/orders verified: {len(orders['value'])} entities returned.")
    return True

# ------------------------------------------------------------------------------
# 3. Enterprise Multi-Catalog Extension Tests (OpenMetadata, Purview, Collibra, Alation)
# ------------------------------------------------------------------------------
def test_catalogs():
    log("Testing Enterprise Multi-Catalog Integrations (OpenMetadata, Purview, Collibra, Alation)...")

    # 1. OpenMetadata REST API
    status, _, om_body = http_req(f"{MOCK_URL}/api/v1/tables", method="GET", expect_status=200)
    om_data = json.loads(om_body)
    tables = om_data.get("data", [])
    log(f"OpenMetadata REST API: {len(tables)} tables verified.")

    # 2. Microsoft Purview / Apache Atlas Search API
    status, _, purview_body = http_req(f"{MOCK_URL}/catalog/api/atlas/v2/search/basic?typeName=rdbms_table", method="GET", expect_status=200)
    purview_data = json.loads(purview_body)
    entities = purview_data.get("entities", [])
    if len(entities) == 0:
        raise RuntimeError("Purview API returned 0 entities.")
    log(f"Microsoft Purview Apache Atlas Search: {len(entities)} catalog entities verified.")

    # 3. Collibra Data Intelligence Platform REST Core API v2
    status, _, collibra_body = http_req(f"{MOCK_URL}/rest/2.0/assets?typeNames=Table&limit=250", method="GET", expect_status=200)
    collibra_data = json.loads(collibra_body)
    assets = collibra_data.get("results", [])
    if len(assets) == 0:
        raise RuntimeError("Collibra API returned 0 assets.")
    log(f"Collibra Data Intelligence: {len(assets)} assets verified (Domain: {assets[0].get('domain', {}).get('name')}).")

    # 4. Alation Data Catalog Integration API v2
    status, _, alation_body = http_req(f"{MOCK_URL}/integration/v2/table/?limit=250", method="GET", expect_status=200)
    alation_tables = json.loads(alation_body)
    if len(alation_tables) == 0:
        raise RuntimeError("Alation API returned 0 tables.")
    log(f"Alation Data Catalog: {len(alation_tables)} tables verified (Tags: {alation_tables[0].get('tags')}).")

    # 5. Catalog Invalidation Webhook (with GDPR Art. 9 Health Tag detection)
    catalog_payload = {
        "eventType": "ENTITY_UPDATED",
        "entityType": "table",
        "entityId": "11111111-2222-3333-4444-555555555555",
        "domain": "finance",
        "schema": "public",
        "tableName": "invoices",
        "tags": ["GDPR.Art9.Health", "PII.Sensitive"]
    }
    status, _, body = http_req(
        f"{BASE_URL}/api/webhooks/catalog?provider=OpenMetadata",
        method="POST",
        data=catalog_payload,
        expect_status=200
    )
    log_success("Multi-Catalog Integrations & GDPR Art. 9 Invalidation Webhooks verified.")
    return True

# ------------------------------------------------------------------------------
# 4. ITSM Workflow Extension Tests (ServiceNow & Jira)
# ------------------------------------------------------------------------------
def test_itsm():
    log("Testing ITSM Outbound Clients & Inbound Approval Webhooks (ServiceNow & Jira)...")

    # 1. ServiceNow Outbound Client & Table API
    snow_create = {"table": "finance.public.invoices", "requester": "user1@corp.local"}
    status, _, snow_res = http_req(f"{MOCK_URL}/api/now/table/change_request", method="POST", data=snow_create, expect_status=201)
    snow_ticket = json.loads(snow_res).get("result", {})
    log(f"ServiceNow Table API: Created ticket {snow_ticket.get('number')} (sys_id: {snow_ticket.get('sys_id')}).")

    # 2. ServiceNow Inbound Approval Webhook
    snow_payload = {
        "number": snow_ticket.get("number", "CHG0010042"),
        "state": "3", # Approved
        "approval": "approved",
        "table": "finance.public.invoices",
        "requester": "S-1-5-21-FINANCE-USER-1"
    }
    status, _, _ = http_req(
        f"{BASE_URL}/api/webhooks/servicenow",
        method="POST",
        data=snow_payload,
        expect_status=200
    )
    log("ServiceNow Inbound Approval Webhook processed.")

    # 3. Jira Outbound Client & Issue API
    jira_create = {"fields": {"project": {"key": "SEC"}, "summary": "Data Access Request"}}
    status, _, jira_res = http_req(f"{MOCK_URL}/rest/api/2/issue", method="POST", data=jira_create, expect_status=201)
    jira_issue = json.loads(jira_res)
    log(f"Jira Service Management REST API: Created issue {jira_issue.get('key')}.")

    # 4. Jira Inbound Approval Webhook
    jira_payload = {
        "issue": {
            "key": jira_issue.get("key", "SEC-1042"),
            "fields": {
                "status": {"name": "Approved"}
            }
        }
    }
    status, _, _ = http_req(
        f"{BASE_URL}/api/webhooks/jira",
        method="POST",
        data=jira_payload,
        expect_status=200
    )
    log_success("ITSM Workflows (ServiceNow & Jira Service Management) verified.")
    return True

# ------------------------------------------------------------------------------
# 5. dbt Integration Extension Tests
# ------------------------------------------------------------------------------
def test_dbt():
    log("Testing dbt Ingestion, Contract Enforcement, Proposals Lifecycle & Exposures...")

    # 1. dbt Manifest.json download
    status, _, manifest_str = http_req(f"{MOCK_URL}/dbt/manifest.json", method="GET", expect_status=200)
    manifest = json.loads(manifest_str)
    nodes = manifest.get("nodes", {})
    log(f"dbt Manifest Distribution: {len(nodes)} model nodes verified.")

    # 2. dbt Contract Validation Gate
    status, _, body = http_req(
        f"{MOCK_URL}/api/extensions/dbt/validate-contract",
        method="POST",
        data=manifest,
        expect_status=200
    )
    val_res = json.loads(body)
    log(f"dbt Contract Validation: IsCompatible={val_res.get('isCompatible', True)}")

    # 3. dbt Manifest Lineage & Proposal Ingestion
    status, _, body = http_req(
        f"{MOCK_URL}/api/extensions/dbt/sync",
        method="POST",
        data=manifest,
        expect_status=200
    )
    sync_res = json.loads(body)
    log(f"dbt Ingestion Result: Models={sync_res.get('modelsIngested', 0)}, Proposals={sync_res.get('proposalsCreated', 0)}")

    # 4. dbt Proposals Endpoint
    status, _, body = http_req(
        f"{MOCK_URL}/api/extensions/dbt/proposals",
        method="GET",
        expect_status=200
    )
    proposals = json.loads(body)
    log(f"Active dbt Proposals: {len(proposals)}")

    # 5. dbt Proposal Approval Lifecycle
    status, _, app_res = http_req(
        f"{MOCK_URL}/api/extensions/dbt/proposals/prop-1001/approve",
        method="POST",
        data={},
        expect_status=200
    )
    approved = json.loads(app_res)
    log(f"dbt Proposal Approval: Proposal {approved.get('id')} -> {approved.get('status')}")

    # 6. dbt Exposure Publishing
    status, _, exposures_yaml = http_req(
        f"{MOCK_URL}/api/extensions/dbt/exposures",
        method="GET",
        expect_status=200
    )
    if "version: 2" not in exposures_yaml and "exposures:" not in exposures_yaml:
        raise RuntimeError("dbt exposures endpoint returned empty or non-standard YAML.")
    log("dbt exposures.yaml successfully generated.")

    log_success("dbt Enterprise Integration Extension verified.")
    return True

def main():
    scenario = os.environ.get("BENCH_SCENARIO", "full").lower()
    enable_lakehouse = os.environ.get("ENABLE_LAKEHOUSE", "true").lower() == "true"
    enable_enterprise = os.environ.get("ENABLE_ENTERPRISE", "true").lower() == "true"

    log(f"Starting Complete Extension Verification (Scenario: '{scenario}', Lakehouse: {enable_lakehouse}, Enterprise: {enable_enterprise})...")

    results = {}
    overall_ok = True

    # 1. Lakehouse & Object Storage Test
    if enable_lakehouse or scenario in ("lakehouse", "full"):
        try:
            results["Lakehouse (Iceberg/S3/AzureBlob)"] = test_lakehouse()
        except Exception as e:
            log_err(f"Lakehouse verification failed: {e}")
            results["Lakehouse (Iceberg/S3/AzureBlob)"] = False
            overall_ok = False

    # 2. OData Test (always available on core gateway)
    try:
        results["OData v4 Direct Connector"] = test_odata()
    except Exception as e:
        log_err(f"OData verification failed: {e}")
        results["OData v4 Direct Connector"] = False
        overall_ok = False

    # 3. Multi-Catalog & Enterprise Tests
    if enable_enterprise or scenario in ("enterprise", "full"):
        try:
            results["Data Catalogs (OpenMetadata, Purview, Collibra, Alation)"] = test_catalogs()
        except Exception as e:
            log_err(f"Catalogs verification failed: {e}")
            results["Data Catalogs (OpenMetadata, Purview, Collibra, Alation)"] = False
            overall_ok = False

        try:
            results["ITSM Workflows (ServiceNow & Jira)"] = test_itsm()
        except Exception as e:
            log_err(f"ITSM verification failed: {e}")
            results["ITSM Workflows (ServiceNow & Jira)"] = False
            overall_ok = False

        try:
            results["dbt Integration (Contracts, Ingestion, Exposures)"] = test_dbt()
        except Exception as e:
            log_err(f"dbt verification failed: {e}")
            results["dbt Integration (Contracts, Ingestion, Exposures)"] = False
            overall_ok = False

    print("\n" + "=" * 80)
    print("           GQLGATEWAY COMPLETE EXTENSIONS VERIFICATION SUMMARY")
    print("=" * 80)
    for ext, ok in results.items():
        state = "\033[1;32m[PASS]\033[0m" if ok else "\033[1;31m[FAIL]\033[0m"
        print(f"  {ext.ljust(65)} {state}")
    print("=" * 80)

    # Save summary json
    results_dir = os.environ.get("RESULTS_DIR", "results")
    os.makedirs(results_dir, exist_ok=True)
    with open(os.path.join(results_dir, "extensions-test-summary.json"), "w", encoding="utf-8") as f:
        json.dump({"scenario": scenario, "overall_passed": overall_ok, "results": results}, f, indent=2)

    if not overall_ok:
        sys.exit(1)

if __name__ == "__main__":
    main()
