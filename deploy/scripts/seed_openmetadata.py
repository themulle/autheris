#!/usr/bin/env python3
"""
Seed script for OpenMetadata:
Creates realistic Enterprise database services, databases, schemas, tables, columns,
comments, and PII tags in the running OpenMetadata container (http://172.30.152.15:8585 or localhost).
"""

import base64
import json
import os
import sys
import urllib.request
import urllib.error

OM_URL = os.environ.get("OPENMETADATA_URL", "http://172.30.152.15:8585").rstrip("/")
ADMIN_EMAIL = os.environ.get("OPENMETADATA_EMAIL", "admin@openmetadata.org")
ADMIN_PASSWORD = os.environ.get("OPENMETADATA_PASSWORD", "admin")

def log(msg):
    print(f"\033[1;36m[OpenMetadata Seed]\033[0m {msg}")

def log_success(msg):
    print(f"\033[1;32m[OpenMetadata Seed] SUCCESS:\033[0m {msg}")

def log_err(msg):
    print(f"\033[1;31m[OpenMetadata Seed] ERROR:\033[0m {msg}")

def api_request(path, method="GET", data=None, token=None):
    url = f"{OM_URL}{path}"
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"

    encoded_data = json.dumps(data).encode("utf-8") if data is not None else None
    req = urllib.request.Request(url, data=encoded_data, headers=headers, method=method)

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            content = resp.read().decode("utf-8")
            return resp.status, json.loads(content) if content else {}
    except urllib.error.HTTPError as e:
        err_body = e.read().decode("utf-8", errors="replace")
        try:
            return e.code, json.loads(err_body)
        except Exception:
            return e.code, {"error": err_body}
    except Exception as ex:
        log_err(f"Request to {url} failed: {ex}")
        sys.exit(1)

def get_auth_token():
    log("Authenticating as admin...")
    b64_pass = base64.b64encode(ADMIN_PASSWORD.encode("utf-8")).decode("utf-8")
    payload = {
        "email": ADMIN_EMAIL,
        "password": b64_pass
    }
    status, res = api_request("/api/v1/users/login", method="POST", data=payload)
    if status != 200 or "accessToken" not in res:
        log_err(f"Failed to authenticate with OpenMetadata: {res}")
        sys.exit(1)
    log_success("Authenticated successfully.")
    return res["accessToken"]

def seed_classifications_and_tags(token):
    log("Ensuring PII and PersonalData tags exist...")

    # 1. PII Classification
    status, res = api_request("/api/v1/classifications/name/PII", method="GET", token=token)
    if status == 404:
        log("Creating PII classification...")
        status, res = api_request("/api/v1/classifications", method="POST", token=token, data={
            "name": "PII",
            "displayName": "PII (Personally Identifiable Information)",
            "description": "Classification for Personally Identifiable Information"
        })

    # PII Tags
    for tag_name, desc in [("Sensitive", "Sensitive PII requiring zero-trust encryption or masking"),
                           ("Email", "Email address subject to masking rules")]:
        tag_status, _ = api_request(f"/api/v1/tags/name/PII.{tag_name}", method="GET", token=token)
        if tag_status == 404:
            log(f"Creating tag PII.{tag_name}...")
            api_request("/api/v1/tags", method="POST", token=token, data={
                "name": tag_name,
                "displayName": f"PII {tag_name}",
                "description": desc,
                "classification": "PII"
            })

    # 2. PersonalData Classification
    status, _ = api_request("/api/v1/classifications/name/PersonalData", method="GET", token=token)
    if status == 404:
        log("Creating PersonalData classification...")
        api_request("/api/v1/classifications", method="POST", token=token, data={
            "name": "PersonalData",
            "displayName": "Personal Data",
            "description": "General GDPR Personal Data"
        })

    tag_status, _ = api_request("/api/v1/tags/name/PersonalData.Personal", method="GET", token=token)
    if tag_status == 404:
        log("Creating tag PersonalData.Personal...")
        api_request("/api/v1/tags", method="POST", token=token, data={
            "name": "Personal",
            "displayName": "Personal",
            "description": "Personal identification metric",
            "classification": "PersonalData"
        })

def seed_services_and_databases(token):
    log("Creating Database Services, Databases, Schemas and Tables...")

    # 1. Database Service: finance (Postgres)
    status, _ = api_request("/api/v1/services/databaseServices/name/finance", method="GET", token=token)
    if status == 404:
        log("Registering Database Service 'finance' (Postgres)...")
        status, _ = api_request("/api/v1/services/databaseServices", method="POST", token=token, data={
            "name": "finance",
            "serviceType": "Postgres",
            "description": "Corporate Enterprise Financial PostgreSQL Database Cluster",
            "connection": {
                "config": {
                    "type": "Postgres",
                    "scheme": "postgresql+psycopg2",
                    "hostPort": "postgres:5432",
                    "username": "gqluser",
                    "authType": {"password": os.environ["GQL_PG_PASSWORD"]},
                    "database": "governancedb"
                }
            }
        })

    # 2. Database Service: crm
    status, _ = api_request("/api/v1/services/databaseServices/name/crm", method="GET", token=token)
    if status == 404:
        log("Registering Database Service 'crm'...")
        status, _ = api_request("/api/v1/services/databaseServices", method="POST", token=token, data={
            "name": "crm",
            "serviceType": "Postgres",
            "description": "Customer Relationship Management Sales Database",
            "connection": {
                "config": {
                    "type": "Postgres",
                    "scheme": "postgresql+psycopg2",
                    "hostPort": "sqlserver:1433",
                    "username": "sa",
                    "authType": {"password": "Password123!"},
                    "database": "crm"
                }
            }
        })

    # 3. Databases
    # 3a. finance.governancedb
    status, _ = api_request("/api/v1/databases/name/finance.governancedb", method="GET", token=token)
    if status == 404:
        log("Creating Database 'governancedb' under 'finance'...")
        api_request("/api/v1/databases", method="POST", token=token, data={
            "name": "governancedb",
            "displayName": "Enterprise Governance DB",
            "description": "Primary transactional database for invoices, consent policies, and financial data.",
            "service": "finance"
        })

    # 3b. crm.crm_db
    status, _ = api_request("/api/v1/databases/name/crm.crm_db", method="GET", token=token)
    if status == 404:
        log("Creating Database 'crm_db' under 'crm'...")
        api_request("/api/v1/databases", method="POST", token=token, data={
            "name": "crm_db",
            "displayName": "CRM Sales Orders Database",
            "description": "Sales orders and transactional customer relationship database.",
            "service": "crm"
        })

    # 4. Schemas
    # 4a. finance.governancedb.public
    status, _ = api_request("/api/v1/databaseSchemas/name/finance.governancedb.public", method="GET", token=token)
    if status == 404:
        log("Creating Schema 'public' under 'finance.governancedb'...")
        api_request("/api/v1/databaseSchemas", method="POST", token=token, data={
            "name": "public",
            "displayName": "Public Financial Ledger",
            "description": "Public schema containing invoice records, line items, and audit data.",
            "database": "finance.governancedb"
        })

    # 4b. crm.crm_db.dbo
    status, _ = api_request("/api/v1/databaseSchemas/name/crm.crm_db.dbo", method="GET", token=token)
    if status == 404:
        log("Creating Schema 'dbo' under 'crm.crm_db'...")
        api_request("/api/v1/databaseSchemas", method="POST", token=token, data={
            "name": "dbo",
            "displayName": "CRM DBO Schema",
            "description": "Standard dbo schema for customer sales orders.",
            "database": "crm.crm_db"
        })

    # 5. Tables
    # 5a. Table: finance.governancedb.public.invoices
    invoices_payload = {
        "name": "invoices",
        "displayName": "Customer Invoices Ledger",
        "description": "Enterprise PostgreSQL Invoices table. Contains billing details, department allocations, and PII columns subject to zero-trust masking policies.",
        "databaseSchema": "finance.governancedb.public",
        "columns": [
            {
                "name": "id",
                "dataType": "INT",
                "description": "Unique auto-incrementing invoice identifier (Primary Key)."
            },
            {
                "name": "name",
                "dataType": "VARCHAR",
                "dataLength": 255,
                "description": "Customer legal name or business trade name."
            },
            {
                "name": "amount",
                "dataType": "NUMERIC",
                "description": "Total invoice billing amount in EUR, excluding tax."
            },
            {
                "name": "email",
                "dataType": "VARCHAR",
                "dataLength": 255,
                "description": "Customer primary billing email address. Masked via HMAC-SHA256 for non-privileged roles.",
                "tags": [
                    {"tagFQN": "PII.Email", "source": "Classification"}
                ]
            },
            {
                "name": "iban",
                "dataType": "VARCHAR",
                "dataLength": 64,
                "description": "International Bank Account Number (IBAN). Redacted for unverified third parties.",
                "tags": [
                    {"tagFQN": "PII.Sensitive", "source": "Classification"}
                ]
            },
            {
                "name": "salary",
                "dataType": "NUMERIC",
                "description": "Internal employee salary / compensation rate. Restricted to HR and C-Level executives.",
                "tags": [
                    {"tagFQN": "PersonalData.Personal", "source": "Classification"}
                ]
            },
            {
                "name": "department",
                "dataType": "VARCHAR",
                "dataLength": 64,
                "description": "Allocated corporate cost center (e.g. IT, Finance, HR, Sales)."
            },
            {
                "name": "vendor",
                "dataType": "VARCHAR",
                "dataLength": 255,
                "description": "Supplying vendor organization or service provider."
            },
            {
                "name": "status",
                "dataType": "VARCHAR",
                "dataLength": 32,
                "description": "Invoice processing state: DRAFT, PENDING_APPROVAL, APPROVED, or PAID."
            },
            {
                "name": "created_at",
                "dataType": "TIMESTAMP",
                "description": "System creation timestamp in UTC."
            }
        ],
        "tags": [
            {"tagFQN": "PII.Sensitive", "source": "Classification"}
        ]
    }

    status, _ = api_request("/api/v1/tables/name/finance.governancedb.public.invoices", method="GET", token=token)
    if status == 404:
        log("Creating Table 'finance.governancedb.public.invoices'...")
        status, res = api_request("/api/v1/tables", method="POST", token=token, data=invoices_payload)
        if status in (200, 201):
            log_success("Created table 'invoices' with columns and PII tags.")
        else:
            log_err(f"Failed to create table 'invoices': {res}")
    else:
        log("Table 'invoices' already exists; updating metadata...")
        api_request("/api/v1/tables", method="PUT", token=token, data=invoices_payload)

    # 5b. Table: finance.governancedb.public.finance_items
    items_payload = {
        "name": "finance_items",
        "displayName": "Invoice Line Items",
        "description": "Detailed line item breakdown associated with invoice parent records.",
        "databaseSchema": "finance.governancedb.public",
        "columns": [
            {
                "name": "id",
                "dataType": "VARCHAR",
                "dataLength": 64,
                "description": "Primary key UUID for line item."
            },
            {
                "name": "parent_id",
                "dataType": "VARCHAR",
                "dataLength": 64,
                "description": "Foreign key reference to invoices.id."
            },
            {
                "name": "product_name",
                "dataType": "VARCHAR",
                "dataLength": 255,
                "description": "Product or service description."
            },
            {
                "name": "price",
                "dataType": "NUMERIC",
                "description": "Unit line price."
            },
            {
                "name": "sensitive_note",
                "dataType": "VARCHAR",
                "dataLength": 255,
                "description": "Internal procurement and compliance note.",
                "tags": [
                    {"tagFQN": "PII.Sensitive", "source": "Classification"}
                ]
            }
        ]
    }
    status, _ = api_request("/api/v1/tables/name/finance.governancedb.public.finance_items", method="GET", token=token)
    if status == 404:
        log("Creating Table 'finance.governancedb.public.finance_items'...")
        api_request("/api/v1/tables", method="POST", token=token, data=items_payload)
        log_success("Created table 'finance_items'.")

    # 5c. Table: crm.crm_db.dbo.orders
    orders_payload = {
        "name": "orders",
        "displayName": "CRM Sales Orders",
        "description": "CRM customer order records stored in SQL Server. Tracks order totals, fulfillment stages, and buyer email.",
        "databaseSchema": "crm.crm_db.dbo",
        "columns": [
            {
                "name": "id",
                "dataType": "INT",
                "description": "Order sequence number."
            },
            {
                "name": "customer_name",
                "dataType": "VARCHAR",
                "dataLength": 255,
                "description": "Customer contact name."
            },
            {
                "name": "email",
                "dataType": "VARCHAR",
                "dataLength": 255,
                "description": "Customer purchase notification email address.",
                "tags": [
                    {"tagFQN": "PII.Email", "source": "Classification"}
                ]
            },
            {
                "name": "total_price",
                "dataType": "NUMERIC",
                "description": "Order sum total."
            }
        ]
    }
    status, _ = api_request("/api/v1/tables/name/crm.crm_db.dbo.orders", method="GET", token=token)
    if status == 404:
        log("Creating Table 'crm.crm_db.dbo.orders'...")
        api_request("/api/v1/tables", method="POST", token=token, data=orders_payload)
        log_success("Created table 'orders'.")

def seed_comments(token):
    log("Adding Governance & Compliance discussions/comments on tables and columns...")

    comments = [
        {
            "about": "<#E::table::finance.governancedb.public.invoices>",
            "from": "admin",
            "message": "Zero-Trust Audit: Verified column-level masking rules for finance.governancedb.public.invoices with GqlGateway governance policy."
        },
        {
            "about": "<#E::table::finance.governancedb.public.invoices::columns::email>",
            "from": "admin",
            "message": "Compliance Notice: Email column requires mandatory HMAC-SHA256 masking according to GDPR Art. 9 policy."
        },
        {
            "about": "<#E::table::finance.governancedb.public.invoices::columns::salary>",
            "from": "admin",
            "message": "Security Policy: Salary column restricted to HR-Executive roles. Non-HR roles receive [REDACTED]."
        },
        {
            "about": "<#E::table::crm.crm_db.dbo.orders>",
            "from": "admin",
            "message": "Integration Sync: Cross-database joins enabled with finance invoices via customer reference."
        }
    ]

    for c in comments:
        api_request("/api/v1/feed", method="POST", token=token, data=c)
    log_success("Comments and discussion threads added successfully.")

def verify_seeded_tables(token):
    log("Verifying seeded tables in OpenMetadata REST API...")
    status, res = api_request("/api/v1/tables?limit=50&fields=columns,tags,database,databaseSchema,service", method="GET", token=token)
    tables = res.get("data", [])
    print("\n" + "=" * 80)
    print("                    OPENMETADATA SEEDED TABLES REPORT")
    print("=" * 80)
    for t in tables:
        fqn = t.get("fullyQualifiedName", "unknown")
        desc = t.get("description", "No description")
        cols = t.get("columns", [])
        tags = [tag.get("tagFQN") for tag in t.get("tags", [])]
        print(f"Table:       {fqn}")
        print(f"Description: {desc[:90]}...")
        print(f"Columns:     {len(cols)} columns")
        print(f"Tags:        {', '.join(tags) if tags else 'None'}")
        print("-" * 80)

def main():
    log(f"Connecting to OpenMetadata instance at {OM_URL}...")
    token = get_auth_token()
    seed_classifications_and_tags(token)
    seed_services_and_databases(token)
    seed_comments(token)
    verify_seeded_tables(token)
    log_success("OpenMetadata seeding successfully completed!")

if __name__ == "__main__":
    main()

