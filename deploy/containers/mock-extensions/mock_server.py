#!/usr/bin/env python3
"""
Lightweight Mock Server for GqlGateway.Extensions
Provides high-performance, low-memory mock APIs for external extensions:
- OpenMetadata REST API (v1 tables, entities, webhooks)
- dbt manifest distribution, contract validation, proposals lifecycle & exposures
- Azure Blob Storage & Iceberg metadata endpoint emulation
Runs on standard Python without third-party dependencies (< 20MB RAM).
"""

import http.server
import json
import os
import sys
import urllib.parse
from pathlib import Path

PORT = int(os.environ.get("MOCK_PORT", 8585))
MANIFEST_PATH = Path(__file__).resolve().parent / "sample_dbt_manifest.json"

MOCK_TABLES = [
    {
        "id": "11111111-2222-3333-4444-555555555555",
        "name": "invoices",
        "fullyQualifiedName": "finance.public.invoices",
        "displayName": "Finance Invoices",
        "description": "PostgreSQL Invoices Table with PII and Sensitive GDPR Fields",
        "columns": [
            {"name": "id", "dataType": "INT", "tags": []},
            {"name": "email", "dataType": "VARCHAR", "tags": [{"tagFQN": "PII.Email"}]},
            {"name": "iban", "dataType": "VARCHAR", "tags": [{"tagFQN": "PII.Sensitive"}]},
            {"name": "salary", "dataType": "DECIMAL", "tags": [{"tagFQN": "PersonalData.Personal"}]}
        ],
        "tags": [{"tagFQN": "PII.Sensitive"}, {"tagFQN": "GDPR.Art9.Sensitive"}]
    },
    {
        "id": "77777777-7777-7777-7777-777777777777",
        "name": "orders",
        "fullyQualifiedName": "crm.dbo.orders",
        "displayName": "CRM Sales Orders",
        "description": "SQL Server CRM orders table",
        "columns": [
            {"name": "id", "dataType": "INT", "tags": []},
            {"name": "customer_name", "dataType": "VARCHAR", "tags": []},
            {"name": "email", "dataType": "VARCHAR", "tags": [{"tagFQN": "PII.Email"}]}
        ],
        "tags": [{"tagFQN": "PII.Sensitive"}]
    }
]

DBT_PROPOSALS = [
    {
        "id": "prop-1001",
        "modelName": "stg_customers",
        "status": "PENDING_APPROVAL",
        "proposedChanges": "Enforce column masking on email_address (MASK_EMAIL) and require four-eyes approval."
    }
]

class MockExtensionsHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        # Concise logging
        sys.stdout.write(f"[mock-extensions] {args[0]} {args[1]} -> {args[2]}\n")
        sys.stdout.flush()

    def send_json(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_HEAD(self):
        # Support Azure Blob and S3 HEAD existence probes
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", "1024")
        self.end_headers()

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path.rstrip("/")
        query = urllib.parse.parse_qs(parsed.query)

        # Healthcheck
        if path in ("", "/health", "/health/live", "/health/ready"):
            self.send_json(200, {
                "status": "healthy",
                "service": "mock-extensions",
                "modules": ["OpenMetadata", "dbt", "AzureBlob"]
            })
            return

        # 1. OpenMetadata: Tables & Entities
        if path == "/api/v1/tables":
            self.send_json(200, {
                "data": MOCK_TABLES,
                "paging": {"total": len(MOCK_TABLES), "after": None}
            })
            return

        if path.startswith("/api/v1/tables/name/"):
            fqn = urllib.parse.unquote(path[len("/api/v1/tables/name/"):])
            matched = next((t for t in MOCK_TABLES if t["fullyQualifiedName"] == fqn or t["name"] == fqn), None)
            self.send_json(200, matched if matched else MOCK_TABLES[0])
            return

        if path in ("/api/v1/policies", "/api/v1/roles", "/api/v1/teams", "/api/v1/users"):
            self.send_json(200, {"data": [], "paging": {"total": 0, "after": None}})
            return

        # 2. dbt: Manifest download
        if path == "/dbt/manifest.json":
            if MANIFEST_PATH.exists():
                with open(MANIFEST_PATH, "r", encoding="utf-8") as f:
                    manifest_data = json.load(f)
                self.send_json(200, manifest_data)
            else:
                self.send_json(404, {"error": "manifest.json not found"})
            return

        # 6. dbt: Exposures Endpoint
        if path == "/api/extensions/dbt/exposures":
            exposures_yaml = """version: 2
exposures:
  - name: executive_sales_dashboard
    type: dashboard
    maturity: high
    url: https://bi.corp.local/dashboards/sales
    description: Executive Sales & Invoices KPI dashboard powered by GqlGateway
    owner:
      name: BI Team
      email: bi-team@corp.local
    depends_on:
      - ref('fct_invoices')
      - ref('stg_customers')
"""
            body = exposures_yaml.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/x-yaml; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        # 7. dbt: Proposals Endpoint
        if path == "/api/extensions/dbt/proposals":
            self.send_json(200, DBT_PROPOSALS)
            return

        # 8. Azure Blob Storage Mock Emulation
        if path.startswith("/iceberg/"):
            dummy_json = {"status": "ok", "blob": path}
            self.send_json(200, dummy_json)
            return

        # Fallback for unknown GET
        self.send_json(200, {"message": "mock-extensions default response", "path": path})

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path.rstrip("/")
        content_length = int(self.headers.get("Content-Length", 0))
        post_body = self.rfile.read(content_length) if content_length > 0 else b"{}"

        try:
            payload = json.loads(post_body.decode("utf-8")) if post_body else {}
        except Exception:
            payload = {}

        # 1. ServiceNow: Change Request & Table API
        if path.startswith("/api/now/table/"):
            ticket_id = f"CHG{os.urandom(3).hex().upper()}"
            sys_id = f"sys_{os.urandom(8).hex()}"
            self.send_json(201, {
                "result": {
                    "sys_id": sys_id,
                    "number": ticket_id,
                    "state": "-5",
                    "short_description": "GqlGateway Access Request",
                    "table": payload.get("table", "unknown")
                }
            })
            return

        # 2. Jira: Issue Creation API
        if path == "/rest/api/2/issue":
            issue_key = f"SEC-{int.from_bytes(os.urandom(2), 'big') % 9000 + 1000}"
            self.send_json(201, {
                "id": str(int.from_bytes(os.urandom(3), 'big')),
                "key": issue_key,
                "self": f"http://mock-extensions:8585/rest/api/2/issue/{issue_key}"
            })
            return

        # 3. dbt: Contract Validation Gate
        if path == "/api/extensions/dbt/validate-contract":
            self.send_json(200, {
                "isCompatible": True,
                "violations": [],
                "breakingChangesCount": 0,
                "message": "All dbt model contracts are compatible with GqlGateway schema."
            })
            return

        # 4. dbt: Sync Manifest Endpoint
        if path == "/api/extensions/dbt/sync":
            self.send_json(200, {
                "modelsIngested": 2,
                "proposalsCreated": 1,
                "status": "success"
            })
            return

        # 5. dbt: Proposal Approval
        if path.startswith("/api/extensions/dbt/proposals/") and path.endswith("/approve"):
            prop_id = path.split("/")[-2]
            self.send_json(200, {
                "id": prop_id,
                "status": "APPROVED",
                "approvedBy": "SecurityOfficer",
                "epochIncremented": True
            })
            return

        # 6. Trigger Webhook directly to GqlGateway
        if path == "/trigger/webhook":
            import urllib.request
            target_url = payload.get("target_url", "http://gqlgateway-api:5050/api/webhooks/catalog")
            headers = payload.get("headers", {"Content-Type": "application/json"})
            data = json.dumps(payload.get("data", {})).encode("utf-8")
            req = urllib.request.Request(target_url, data=data, headers=headers, method="POST")
            try:
                with urllib.request.urlopen(req, timeout=5) as response:
                    self.send_json(response.status, {"status": "forwarded", "gateway_code": response.status})
            except Exception as ex:
                self.send_json(502, {"error": str(ex)})
            return

        # Default POST response
        self.send_json(200, {"status": "accepted", "path": path})

def run():
    server_address = ("", PORT)
    httpd = http.server.ThreadingHTTPServer(server_address, MockExtensionsHandler)
    print(f"[mock-extensions] Starting Enterprise Mock Server on port {PORT}...")
    print(f"[mock-extensions] Serving OpenMetadata, dbt & AzureBlob...")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()

if __name__ == "__main__":
    run()
