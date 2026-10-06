#!/usr/bin/env python3
"""
Seed script for SQLite Governance Database
Creates production-grade governance schema, tables, masking rules, data ownership,
four-eyes approvers, roles, and granular consents with RLS row filters.
"""

import os
import sys
import sqlite3
import uuid
import json
from datetime import datetime, timezone, timedelta

def get_db_path():
    db_path = os.environ.get("GOVERNANCE_DB_PATH", "/data/governance.db")
    os.makedirs(os.path.dirname(db_path), exist_ok=True)
    return db_path

def init_hr_sqlite_db():
    hr_db_path = os.environ.get("HR_DB_PATH", "/data/hr.db")
    print(f"[governance-seed] Initializing and seeding HR SQLite database at: {hr_db_path}")
    os.makedirs(os.path.dirname(hr_db_path), exist_ok=True)
    hr_conn = sqlite3.connect(hr_db_path)
    hr_cur = hr_conn.cursor()
    hr_cur.executescript("""
    PRAGMA journal_mode = WAL;
    CREATE TABLE IF NOT EXISTS hr_table_1 (
        id INTEGER PRIMARY KEY,
        name TEXT NOT NULL,
        email TEXT NOT NULL,
        department TEXT NOT NULL,
        role TEXT NOT NULL,
        salary REAL NOT NULL,
        created_at TEXT NOT NULL
    );
    CREATE VIEW IF NOT EXISTS employees AS SELECT * FROM hr_table_1;
    """)

    departments = ["Finance", "Engineering", "Marketing", "HR", "Sales", "Operations"]
    roles_list = ["Specialist", "Senior Specialist", "Team Lead", "Manager", "Director"]
    first_names = ["Anna", "Bernd", "Claudia", "Dirk", "Elena", "Felix", "Greta", "Hans", "Ines", "Jonas"]
    last_names = ["Mueller", "Schmidt", "Schneider", "Fischer", "Weber", "Meyer", "Wagner", "Becker", "Schulz", "Hoffmann"]

    records = []
    base_time = datetime(2025, 1, 1, tzinfo=timezone.utc)
    for i in range(1, 5001):
        fn = first_names[i % len(first_names)]
        ln = last_names[(i // len(first_names)) % len(last_names)]
        name = f"{fn} {ln}"
        email = f"{fn.lower()}.{ln.lower()}.{i}@corp.local"
        dept = departments[i % len(departments)]
        r = roles_list[i % len(roles_list)]
        sal = round(45000.0 + (i * 13.7) % 75000.0, 2)
        created = (base_time + timedelta(hours=i)).isoformat()
        records.append((i, name, email, dept, r, sal, created))

    hr_cur.executemany("INSERT OR REPLACE INTO hr_table_1 VALUES (?,?,?,?,?,?,?)", records)
    hr_conn.commit()
    hr_conn.close()
    print(f"[governance-seed] Successfully seeded 5,000 employee records into {hr_db_path}")

def init_governance_db(db_path):
    init_hr_sqlite_db()
    print(f"[governance-seed] Initializing SQLite database at: {db_path}")
    conn = sqlite3.connect(db_path)
    cur = conn.cursor()

    cur.executescript("""
    PRAGMA journal_mode = WAL;
    PRAGMA foreign_keys = ON;

    CREATE TABLE IF NOT EXISTS TABLES (
        id TEXT PRIMARY KEY,
        source_type TEXT NOT NULL,
        source_name TEXT NOT NULL,
        schema_name TEXT NOT NULL,
        table_name TEXT NOT NULL,
        display_name TEXT NOT NULL,
        sensitivity TEXT NOT NULL,
        requires_four_eyes INTEGER NOT NULL,
        is_active INTEGER NOT NULL,
        data_source_type INTEGER NOT NULL DEFAULT 0,
        http_endpoint_json TEXT,
        plugin_name TEXT
    );

    CREATE TABLE IF NOT EXISTS TABLE_COLUMNS (
        id TEXT PRIMARY KEY,
        table_id TEXT NOT NULL,
        column_name TEXT NOT NULL,
        data_type TEXT NOT NULL,
        is_sensitive INTEGER NOT NULL
    );

    CREATE TABLE IF NOT EXISTS COLUMN_MASKING_RULES (
        id TEXT PRIMARY KEY,
        table_column_id TEXT NOT NULL,
        rule_type TEXT NOT NULL,
        pattern_or_format TEXT,
        replacement TEXT,
        hmac_key_id TEXT
    );

    CREATE TABLE IF NOT EXISTS DATA_OWNERS (
        id TEXT PRIMARY KEY,
        ad_sid TEXT NOT NULL,
        ad_account TEXT NOT NULL,
        display_name TEXT NOT NULL,
        email TEXT NOT NULL,
        is_active INTEGER NOT NULL
    );

    CREATE TABLE IF NOT EXISTS TABLE_OWNERS (
        id TEXT PRIMARY KEY,
        table_id TEXT NOT NULL,
        data_owner_id TEXT NOT NULL,
        owner_role TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS DATA_OWNER_DELEGATIONS (
        id TEXT PRIMARY KEY,
        data_owner_id TEXT NOT NULL,
        delegate_sid TEXT NOT NULL,
        valid_from TEXT NOT NULL,
        valid_to TEXT NOT NULL,
        reason TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS ROLES (
        id TEXT PRIMARY KEY,
        role_name TEXT NOT NULL UNIQUE,
        description TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS ROLE_MEMBERS (
        id TEXT PRIMARY KEY,
        role_id TEXT NOT NULL,
        member_type TEXT NOT NULL,
        member_sid TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS CONSENTS (
        id TEXT PRIMARY KEY,
        table_id TEXT NOT NULL,
        consent_request_id TEXT,
        effect TEXT NOT NULL,
        grantee_type TEXT NOT NULL,
        grantee_sid TEXT,
        role_id TEXT,
        role_name TEXT,
        valid_from TEXT NOT NULL,
        valid_to TEXT NOT NULL,
        is_revoked INTEGER NOT NULL,
        revoked_by_sid TEXT,
        revoked_at TEXT,
        revoke_reason TEXT
    );

    CREATE TABLE IF NOT EXISTS CONSENT_COLUMN_RULES (
        id TEXT PRIMARY KEY,
        consent_id TEXT NOT NULL,
        table_column_id TEXT NOT NULL,
        column_name TEXT NOT NULL,
        access_level INTEGER NOT NULL
    );

    CREATE TABLE IF NOT EXISTS CONSENT_ROW_FILTERS (
        id TEXT PRIMARY KEY,
        consent_id TEXT NOT NULL,
        filter_group INTEGER NOT NULL,
        table_column_id TEXT NOT NULL,
        column_name TEXT NOT NULL,
        operator TEXT NOT NULL,
        value_type TEXT NOT NULL,
        value_json TEXT NOT NULL,
        value_source TEXT NOT NULL,
        user_attribute TEXT,
        filter_type INTEGER NOT NULL DEFAULT 0,
        dependent_table TEXT,
        dependent_table_alias TEXT,
        foreign_key_column TEXT,
        primary_key_column TEXT,
        subquery_predicate_json TEXT,
        target_temporal_column TEXT,
        dependent_valid_from_column TEXT,
        dependent_valid_to_column TEXT,
        target_table_alias TEXT,
        additional_hops_json TEXT
    );

    CREATE TABLE IF NOT EXISTS POLICY_EPOCHS (
        table_id TEXT PRIMARY KEY,
        domain TEXT NOT NULL,
        schema_name TEXT NOT NULL,
        table_name TEXT NOT NULL,
        epoch INTEGER NOT NULL,
        updated_at TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS AUDIT_LOG_ENTRIES (
        id TEXT PRIMARY KEY,
        occurred_at TEXT NOT NULL,
        event_type TEXT NOT NULL,
        actor_sid TEXT NOT NULL,
        target_table TEXT NOT NULL,
        target_column TEXT,
        decision TEXT NOT NULL,
        trace_id TEXT NOT NULL,
        details_json TEXT NOT NULL,
        prev_hash TEXT NOT NULL,
        entry_hash TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS CONSENT_REQUESTS (
        id TEXT PRIMARY KEY,
        table_id TEXT NOT NULL,
        requester_sid TEXT NOT NULL,
        requested_grantee_type TEXT NOT NULL,
        requested_grantee_ref TEXT NOT NULL,
        business_justification TEXT NOT NULL,
        status TEXT NOT NULL,
        requested_at TEXT NOT NULL,
        requested_valid_to TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS APPROVAL_STEPS (
        id TEXT PRIMARY KEY,
        consent_request_id TEXT NOT NULL,
        step_number INTEGER NOT NULL,
        approver_sid TEXT NOT NULL,
        decision TEXT NOT NULL,
        rejection_reason TEXT,
        decided_at TEXT
    );

    CREATE TABLE IF NOT EXISTS TABLE_RELATIONS (
        id TEXT PRIMARY KEY,
        parent_table_id TEXT NOT NULL,
        child_table_id TEXT NOT NULL,
        relation_name TEXT NOT NULL,
        join_key_parent TEXT NOT NULL,
        join_key_child TEXT NOT NULL,
        cardinality TEXT NOT NULL
    );

    CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLES_NATURAL
        ON TABLES (source_name, schema_name, table_name);

    CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_COLUMNS_NATURAL
        ON TABLE_COLUMNS (table_id, column_name);

    CREATE UNIQUE INDEX IF NOT EXISTS UX_DATA_OWNERS_SID
        ON DATA_OWNERS (ad_sid);

    CREATE UNIQUE INDEX IF NOT EXISTS UX_POLICY_EPOCHS_NATURAL
        ON POLICY_EPOCHS (domain, schema_name, table_name);
    """)

    conn.commit()
    seed_governance_data(conn)
    conn.close()
    print("[governance-seed] Seeding completed successfully.")

def seed_governance_data(conn):
    cur = conn.cursor()
    now_iso = datetime.now(timezone.utc).isoformat()
    far_future_iso = (datetime.now(timezone.utc) + timedelta(days=3650)).isoformat()

    print("[governance-seed] Inserting Data Owners...")
    data_owners = [
        (str(uuid.uuid4()), "S-1-5-21-DATAOWNER-FINANCE", "CORP\\cfo", "Chief Financial Officer", "cfo@corp.local", 1),
        (str(uuid.uuid4()), "S-1-5-21-APPROVER-A", "CORP\\approver_a", "Finance Compliance Officer A", "approver_a@corp.local", 1),
        (str(uuid.uuid4()), "S-1-5-21-APPROVER-B", "CORP\\approver_b", "Audit Director B", "approver_b@corp.local", 1)
    ]
    for row in data_owners:
        cur.execute("INSERT OR REPLACE INTO DATA_OWNERS VALUES (?,?,?,?,?,?)", row)

    print("[governance-seed] Inserting Roles & Members...")
    roles = [
        (str(uuid.uuid4()), "GovernanceAdmin", "Platform & Security Governance Admin"),
        (str(uuid.uuid4()), "FinanceManager", "Finance Department Executive Manager"),
        (str(uuid.uuid4()), "FinanceAuditor", "External & Internal Financial Auditor"),
        (str(uuid.uuid4()), "FinanceUser", "Standard Finance Operations User")
    ]
    role_map = {}
    for rid, rname, rdesc in roles:
        cur.execute("INSERT OR REPLACE INTO ROLES VALUES (?,?,?)", (rid, rname, rdesc))
        role_map[rname] = rid

    role_members = [
        (str(uuid.uuid4()), role_map["GovernanceAdmin"], "User", "S-1-5-21-FORWARD-ADMIN"),
        (str(uuid.uuid4()), role_map["FinanceManager"], "User", "S-1-5-21-FORWARD-USER_MANAGER"),
        (str(uuid.uuid4()), role_map["FinanceAuditor"], "User", "S-1-5-21-FORWARD-USER_AUDITOR"),
        (str(uuid.uuid4()), role_map["FinanceUser"], "User", "S-1-5-21-FORWARD-USER_FINANCE"),
        (str(uuid.uuid4()), role_map["FinanceUser"], "Group", "S-1-5-21-GROUP-FINANCE"),
        (str(uuid.uuid4()), role_map["FinanceAuditor"], "Group", "S-1-5-21-GROUP-AUDIT")
    ]
    for m in role_members:
        cur.execute("INSERT OR REPLACE INTO ROLE_MEMBERS VALUES (?,?,?,?)", m)

    print("[governance-seed] Inserting Tables & Columns...")
    # Define tables:
    # 1. finance.public.invoices (PostgreSQL)
    # 2. finance.dbo.finance_table_1 (PostgreSQL compatibility view)
    # 3. finance.public.finance_items (Child table for DataLoader)
    # 4. finance.dbo.finance_items (Child table compatibility view)
    # 5. finance.public.hr_salaries_confidential (High sensitivity table with four-eyes required)

    tables = [
        {
            "id": "11111111-1111-1111-1111-111111111111",
            "source_type": "PostgreSQL",
            "source_name": "finance",
            "schema_name": "public",
            "table_name": "invoices",
            "display_name": "Customer & Vendor Invoices",
            "sensitivity": "NORMAL",
            "four_eyes": 0,
            "columns": [
                ("id", "int", False, None),
                ("name", "varchar", False, None),
                ("amount", "decimal", False, None),
                ("email", "varchar", True, ("MASK_EMAIL", None, None)),
                ("iban", "varchar", True, ("MASK_IBAN", None, None)),
                ("salary", "decimal", True, ("NULLIFY", None, None)),
                ("department", "varchar", False, None),
                ("vendor", "varchar", False, None),
                ("status", "varchar", False, None),
                ("created_at", "datetime", False, None)
            ]
        },
        {
            "id": "22222222-2222-2222-2222-222222222222",
            "source_type": "PostgreSQL",
            "source_name": "finance",
            "schema_name": "dbo",
            "table_name": "finance_table_1",
            "display_name": "Finance Table 1",
            "sensitivity": "NORMAL",
            "four_eyes": 0,
            "columns": [
                ("id", "int", False, None),
                ("name", "varchar", False, None),
                ("amount", "decimal", False, None),
                ("email", "varchar", True, ("MASK_EMAIL", None, None)),
                ("iban", "varchar", True, ("MASK_IBAN", None, None)),
                ("salary", "decimal", True, ("NULLIFY", None, None)),
                ("department", "varchar", False, None),
                ("vendor", "varchar", False, None),
                ("status", "varchar", False, None),
                ("created_at", "datetime", False, None)
            ]
        },
        {
            "id": "33333333-3333-3333-3333-333333333333",
            "source_type": "PostgreSQL",
            "source_name": "finance",
            "schema_name": "public",
            "table_name": "finance_items",
            "display_name": "Invoice Line Items",
            "sensitivity": "NORMAL",
            "four_eyes": 0,
            "columns": [
                ("id", "varchar", False, None),
                ("parent_id", "varchar", False, None),
                ("product_name", "varchar", False, None),
                ("price", "decimal", False, None),
                ("sensitive_note", "varchar", True, ("REDACT", None, "[CONFIDENTIAL NOTE]"))
            ]
        },
        {
            "id": "44444444-4444-4444-4444-444444444444",
            "source_type": "PostgreSQL",
            "source_name": "finance",
            "schema_name": "dbo",
            "table_name": "finance_items",
            "display_name": "Finance Items DBO",
            "sensitivity": "NORMAL",
            "four_eyes": 0,
            "columns": [
                ("id", "varchar", False, None),
                ("parent_id", "varchar", False, None),
                ("product_name", "varchar", False, None),
                ("price", "decimal", False, None),
                ("sensitive_note", "varchar", True, ("REDACT", None, "[CONFIDENTIAL NOTE]"))
            ]
        },
        {
            "id": "55555555-5555-5555-5555-555555555555",
            "source_type": "Sqlite",
            "source_name": "hr",
            "schema_name": "dbo",
            "table_name": "hr_table_1",
            "display_name": "HR Employees Table",
            "sensitivity": "NORMAL",
            "four_eyes": 0,
            "columns": [
                ("id", "int", False, None),
                ("name", "varchar", False, None),
                ("email", "varchar", True, ("MASK_EMAIL", None, None)),
                ("department", "varchar", False, None),
                ("role", "varchar", False, None),
                ("salary", "decimal", True, ("NULLIFY", None, None)),
                ("created_at", "datetime", False, None)
            ]
        },
        {
            "id": "66666666-6666-6666-6666-666666666666",
            "source_type": "Sqlite",
            "source_name": "hr",
            "schema_name": "dbo",
            "table_name": "employees",
            "display_name": "HR Employees View",
            "sensitivity": "NORMAL",
            "four_eyes": 0,
            "columns": [
                ("id", "int", False, None),
                ("name", "varchar", False, None),
                ("email", "varchar", True, ("MASK_EMAIL", None, None)),
                ("department", "varchar", False, None),
                ("role", "varchar", False, None),
                ("salary", "decimal", True, ("NULLIFY", None, None)),
                ("created_at", "datetime", False, None)
            ]
        },
        {
            "id": "77777777-7777-7777-7777-777777777777",
            "source_type": "SqlServer",
            "source_name": "crm",
            "schema_name": "dbo",
            "table_name": "orders",
            "display_name": "CRM Sales Orders",
            "sensitivity": "NORMAL",
            "four_eyes": 0,
            "columns": [
                ("id", "int", False, None),
                ("order_number", "varchar", False, None),
                ("customer_name", "varchar", False, None),
                ("email", "varchar", True, ("MASK_EMAIL", None, None)),
                ("total_amount", "decimal", False, None),
                ("department", "varchar", False, None),
                ("status", "varchar", False, None),
                ("created_at", "datetime", False, None)
            ]
        },
        {
            "id": "88888888-8888-8888-8888-888888888888",
            "source_type": "Lakehouse",
            "source_name": "lakehouse",
            "schema_name": "dbo",
            "table_name": "orders",
            "display_name": "Lakehouse Iceberg Orders",
            "sensitivity": "HIGH",
            "four_eyes": 0,
            "data_source_type": 3,
            "columns": [
                ("orderId", "varchar", False, None),
                ("tenantId", "varchar", False, None),
                ("customerEmail", "varchar", True, ("MASK_EMAIL", None, None)),
                ("amount", "decimal", False, None),
                ("orderDate", "varchar", False, None)
            ]
        }
    ]

    col_id_map = {}

    for t in tables:
        cur.execute("""
            INSERT OR REPLACE INTO TABLES 
            (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1, ?)
        """, (t["id"], t["source_type"], t["source_name"], t["schema_name"], t["table_name"], t["display_name"], t["sensitivity"], t["four_eyes"], t.get("data_source_type", 0)))

        # Policy Epoch
        cur.execute("""
            INSERT OR REPLACE INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
            VALUES (?, ?, ?, ?, 1, ?)
        """, (t["id"], t["source_name"], t["schema_name"], t["table_name"], now_iso))

        # Columns
        for cname, ctype, csensitive, mask_info in t["columns"]:
            col_id = str(uuid.uuid5(uuid.UUID(t["id"]), cname))
            col_id_map[(t["id"], cname)] = col_id

            cur.execute("""
                INSERT OR REPLACE INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive)
                VALUES (?, ?, ?, ?, ?)
            """, (col_id, t["id"], cname, ctype, 1 if csensitive else 0))

            if mask_info:
                m_type, m_pattern, m_replacement = mask_info
                mask_id = str(uuid.uuid5(uuid.UUID(col_id), "mask"))
                cur.execute("""
                    INSERT OR REPLACE INTO COLUMN_MASKING_RULES 
                    (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                    VALUES (?, ?, ?, ?, ?, 'key-2026-q1')
                """, (mask_id, col_id, m_type, m_pattern or "", m_replacement or ""))

    # Table relations for DataLoader / GraphQL nested queries
    rel_id = str(uuid.uuid4())
    cur.execute("""
        INSERT OR REPLACE INTO TABLE_RELATIONS (id, parent_table_id, child_table_id, relation_name, join_key_parent, join_key_child, cardinality)
        VALUES (?, '22222222-2222-2222-2222-222222222222', '44444444-4444-4444-4444-444444444444', 'items', 'id', 'parent_id', 'OneToMany')
    """, (rel_id,))

    print("[governance-seed] Inserting Consents, Column Rules, and RLS Row Filters...")

    # Access levels: Clear = 1, Mask = 2, Deny = 3
    # Effect: "Allow", "Deny"
    # GranteeType: User = 1, Group = 2, Role = 3

    # --------------------------------------------------------------------------
    # CONSENT 1: Group S-1-5-21-GROUP-FINANCE (Standard Finance Users)
    # RLS Pushdown: department = 'Finance'
    # Column Rules: salary -> Deny, iban -> Mask, email -> Clear, others -> Clear
    # --------------------------------------------------------------------------
    for target_tid in ["11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"]:
        cid = str(uuid.uuid5(uuid.UUID(target_tid), "consent-finance-group"))
        cur.execute("""
            INSERT OR REPLACE INTO CONSENTS
            (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
            VALUES (?, ?, NULL, 'Allow', 'Group', 'S-1-5-21-GROUP-FINANCE', NULL, NULL, ?, ?, 0)
        """, (cid, target_tid, now_iso, far_future_iso))

        # Column rules
        col_rules = [
            ("salary", 3), # Deny
            ("iban", 2),   # Mask
            ("email", 1),  # Clear
            ("amount", 1), # Clear
            ("name", 1),   # Clear
            ("vendor", 1)  # Clear
        ]
        for cname, level in col_rules:
            col_id = col_id_map[(target_tid, cname)]
            cur.execute("""
                INSERT OR REPLACE INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
                VALUES (?, ?, ?, ?, ?)
            """, (str(uuid.uuid4()), cid, col_id, cname, level))

        # RLS Row Filter: department = 'Finance'
        dept_col_id = col_id_map[(target_tid, "department")]
        cur.execute("""
            INSERT OR REPLACE INTO CONSENT_ROW_FILTERS
            (id, consent_id, filter_group, table_column_id, column_name, operator, value_type, value_json, value_source, filter_type)
            VALUES (?, ?, 1, ?, 'department', 'EQ', 'STRING', ?, 'STATIC', 0)
        """, (str(uuid.uuid4()), cid, dept_col_id, json.dumps("Finance")))

    # --------------------------------------------------------------------------
    # CONSENT 2: Role FinanceAuditor
    # RLS Pushdown: amount >= 1000.00 (Cross-department auditing of high-value invoices)
    # Column Rules: salary -> Mask, iban -> Mask, email -> Mask
    # --------------------------------------------------------------------------
    for target_tid in ["11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"]:
        cid = str(uuid.uuid5(uuid.UUID(target_tid), "consent-auditor-role"))
        cur.execute("""
            INSERT OR REPLACE INTO CONSENTS
            (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
            VALUES (?, ?, NULL, 'Allow', 'Role', NULL, ?, 'FinanceAuditor', ?, ?, 0)
        """, (cid, target_tid, role_map["FinanceAuditor"], now_iso, far_future_iso))

        for cname in ["salary", "iban", "email"]:
            col_id = col_id_map[(target_tid, cname)]
            cur.execute("""
                INSERT OR REPLACE INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
                VALUES (?, ?, ?, ?, 2)
            """, (str(uuid.uuid4()), cid, col_id, cname))

        # RLS Row Filter: amount >= 1000.00
        amt_col_id = col_id_map[(target_tid, "amount")]
        cur.execute("""
            INSERT OR REPLACE INTO CONSENT_ROW_FILTERS
            (id, consent_id, filter_group, table_column_id, column_name, operator, value_type, value_json, value_source, filter_type)
            VALUES (?, ?, 1, ?, 'amount', 'GTE', 'NUMBER', '1000.00', 'STATIC', 0)
        """, (str(uuid.uuid4()), cid, amt_col_id))

    # --------------------------------------------------------------------------
    # CONSENT 3: Role FinanceManager
    # No row filter (unrestricted rows), all columns Clear
    # --------------------------------------------------------------------------
    for target_tid in ["11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"]:
        cid = str(uuid.uuid5(uuid.UUID(target_tid), "consent-manager-role"))
        cur.execute("""
            INSERT OR REPLACE INTO CONSENTS
            (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
            VALUES (?, ?, NULL, 'Allow', 'Role', NULL, ?, 'FinanceManager', ?, ?, 0)
        """, (cid, target_tid, role_map["FinanceManager"], now_iso, far_future_iso))

    # --------------------------------------------------------------------------
    # CONSENT 4: Hard DENY for Blocked User S-1-5-21-FORWARD-USER_BLOCKED
    # Tests Fail-Closed zero-trust behavior under load
    # --------------------------------------------------------------------------
    for target_tid in ["11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"]:
        cid = str(uuid.uuid5(uuid.UUID(target_tid), "consent-blocked-user"))
        cur.execute("""
            INSERT OR REPLACE INTO CONSENTS
            (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
            VALUES (?, ?, NULL, 'Deny', 'User', 'S-1-5-21-FORWARD-USER_BLOCKED', NULL, NULL, ?, ?, 0)
        """, (cid, target_tid, now_iso, far_future_iso))

    # --------------------------------------------------------------------------
    # CONSENT 5: Child table finance_items for Group Finance
    # --------------------------------------------------------------------------
    for target_tid in ["33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444"]:
        cid = str(uuid.uuid5(uuid.UUID(target_tid), "consent-items-finance-group"))
        cur.execute("""
            INSERT OR REPLACE INTO CONSENTS
            (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
            VALUES (?, ?, NULL, 'Allow', 'Group', 'S-1-5-21-GROUP-FINANCE', NULL, NULL, ?, ?, 0)
        """, (cid, target_tid, now_iso, far_future_iso))

        # sensitive_note -> Mask
        note_col_id = col_id_map[(target_tid, "sensitive_note")]
        cur.execute("""
            INSERT OR REPLACE INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
            VALUES (?, ?, ?, 'sensitive_note', 2)
        """, (str(uuid.uuid4()), cid, note_col_id))

    # --------------------------------------------------------------------------
    # CONSENT 6: SQLite HR tables (Cross-Database benchmarking)
    # --------------------------------------------------------------------------
    for hr_tid in ["55555555-5555-5555-5555-555555555555", "66666666-6666-6666-6666-666666666666"]:
        # Group Finance: Allow with RLS (department = 'Finance') and masked email, denied salary
        cid_grp = str(uuid.uuid5(uuid.UUID(hr_tid), "consent-hr-finance-group"))
        cur.execute("""
            INSERT OR REPLACE INTO CONSENTS
            (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
            VALUES (?, ?, NULL, 'Allow', 'Group', 'S-1-5-21-GROUP-FINANCE', NULL, NULL, ?, ?, 0)
        """, (cid_grp, hr_tid, now_iso, far_future_iso))

        for cname, lvl in [("salary", 3), ("email", 2), ("name", 1), ("department", 1), ("role", 1), ("created_at", 1)]:
            cur.execute("""
                INSERT OR REPLACE INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
                VALUES (?, ?, ?, ?, ?)
            """, (str(uuid.uuid4()), cid_grp, col_id_map[(hr_tid, cname)], cname, lvl))

        cur.execute("""
            INSERT OR REPLACE INTO CONSENT_ROW_FILTERS
            (id, consent_id, filter_group, table_column_id, column_name, operator, value_type, value_json, value_source, filter_type)
            VALUES (?, ?, 1, ?, 'department', 'EQ', 'STRING', ?, 'STATIC', 0)
        """, (str(uuid.uuid4()), cid_grp, col_id_map[(hr_tid, "department")], json.dumps("Finance")))

        # Role FinanceManager: Allow full access
        cid_mgr = str(uuid.uuid5(uuid.UUID(hr_tid), "consent-hr-manager-role"))
        cur.execute("""
            INSERT OR REPLACE INTO CONSENTS
            (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
            VALUES (?, ?, NULL, 'Allow', 'Role', NULL, ?, 'FinanceManager', ?, ?, 0)
        """, (cid_mgr, hr_tid, role_map["FinanceManager"], now_iso, far_future_iso))

        # Role FinanceAuditor: Allow with masked salary & email
        cid_aud = str(uuid.uuid5(uuid.UUID(hr_tid), "consent-hr-auditor-role"))
        cur.execute("""
            INSERT OR REPLACE INTO CONSENTS
            (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
            VALUES (?, ?, NULL, 'Allow', 'Role', NULL, ?, 'FinanceAuditor', ?, ?, 0)
        """, (cid_aud, hr_tid, role_map["FinanceAuditor"], now_iso, far_future_iso))
        for cname in ["salary", "email"]:
            cur.execute("""
                INSERT OR REPLACE INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
                VALUES (?, ?, ?, ?, 2)
            """, (str(uuid.uuid4()), cid_aud, col_id_map[(hr_tid, cname)], cname))

        # Blocked User: Hard Deny
        cid_blk = str(uuid.uuid5(uuid.UUID(hr_tid), "consent-hr-blocked-user"))
        cur.execute("""
            INSERT OR REPLACE INTO CONSENTS
            (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
            VALUES (?, ?, NULL, 'Deny', 'User', 'S-1-5-21-FORWARD-USER_BLOCKED', NULL, NULL, ?, ?, 0)
        """, (cid_blk, hr_tid, now_iso, far_future_iso))

    # --------------------------------------------------------------------------
    # CONSENT 7: SQL Server CRM tables (3-Way Cross-Database benchmarking)
    # --------------------------------------------------------------------------
    crm_tid = "77777777-7777-7777-7777-777777777777"
    # Group Finance: Allow with RLS (department = 'Finance') and masked email
    cid_crm_grp = str(uuid.uuid5(uuid.UUID(crm_tid), "consent-crm-finance-group"))
    cur.execute("""
        INSERT OR REPLACE INTO CONSENTS
        (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
        VALUES (?, ?, NULL, 'Allow', 'Group', 'S-1-5-21-GROUP-FINANCE', NULL, NULL, ?, ?, 0)
    """, (cid_crm_grp, crm_tid, now_iso, far_future_iso))

    for cname, lvl in [("email", 2), ("id", 1), ("order_number", 1), ("customer_name", 1), ("total_amount", 1), ("department", 1), ("status", 1), ("created_at", 1)]:
        cur.execute("""
            INSERT OR REPLACE INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
            VALUES (?, ?, ?, ?, ?)
        """, (str(uuid.uuid4()), cid_crm_grp, col_id_map[(crm_tid, cname)], cname, lvl))

    cur.execute("""
        INSERT OR REPLACE INTO CONSENT_ROW_FILTERS
        (id, consent_id, filter_group, table_column_id, column_name, operator, value_type, value_json, value_source, filter_type)
        VALUES (?, ?, 1, ?, 'department', 'EQ', 'STRING', ?, 'STATIC', 0)
    """, (str(uuid.uuid4()), cid_crm_grp, col_id_map[(crm_tid, "department")], json.dumps("Finance")))

    # Role FinanceManager: Allow full access
    cid_crm_mgr = str(uuid.uuid5(uuid.UUID(crm_tid), "consent-crm-manager-role"))
    cur.execute("""
        INSERT OR REPLACE INTO CONSENTS
        (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
        VALUES (?, ?, NULL, 'Allow', 'Role', NULL, ?, 'FinanceManager', ?, ?, 0)
    """, (cid_crm_mgr, crm_tid, role_map["FinanceManager"], now_iso, far_future_iso))

    # Role FinanceAuditor: Allow with masked email
    cid_crm_aud = str(uuid.uuid5(uuid.UUID(crm_tid), "consent-crm-auditor-role"))
    cur.execute("""
        INSERT OR REPLACE INTO CONSENTS
        (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
        VALUES (?, ?, NULL, 'Allow', 'Role', NULL, ?, 'FinanceAuditor', ?, ?, 0)
    """, (cid_crm_aud, crm_tid, role_map["FinanceAuditor"], now_iso, far_future_iso))
    cur.execute("""
        INSERT OR REPLACE INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
        VALUES (?, ?, ?, 'email', 2)
    """, (str(uuid.uuid4()), cid_crm_aud, col_id_map[(crm_tid, "email")]))

    # Blocked User: Hard Deny
    cid_crm_blk = str(uuid.uuid5(uuid.UUID(crm_tid), "consent-crm-blocked-user"))
    cur.execute("""
        INSERT OR REPLACE INTO CONSENTS
        (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
        VALUES (?, ?, NULL, 'Deny', 'User', 'S-1-5-21-FORWARD-USER_BLOCKED', NULL, NULL, ?, ?, 0)
    """, (cid_crm_blk, crm_tid, now_iso, far_future_iso))

    # --------------------------------------------------------------------------
    # CONSENT 8: Apache Iceberg Lakehouse orders table
    # --------------------------------------------------------------------------
    lake_tid = "88888888-8888-8888-8888-888888888888"
    # Group Finance: Allow with masked customerEmail
    cid_lake_grp = str(uuid.uuid5(uuid.UUID(lake_tid), "consent-lake-finance-group"))
    cur.execute("""
        INSERT OR REPLACE INTO CONSENTS
        (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
        VALUES (?, ?, NULL, 'Allow', 'Group', 'S-1-5-21-GROUP-FINANCE', NULL, NULL, ?, ?, 0)
    """, (cid_lake_grp, lake_tid, now_iso, far_future_iso))

    for cname, lvl in [("customerEmail", 2), ("orderId", 1), ("tenantId", 1), ("amount", 1), ("orderDate", 1)]:
        cur.execute("""
            INSERT OR REPLACE INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
            VALUES (?, ?, ?, ?, ?)
        """, (str(uuid.uuid4()), cid_lake_grp, col_id_map[(lake_tid, cname)], cname, lvl))

    # Role FinanceManager: Allow full access
    cid_lake_mgr = str(uuid.uuid5(uuid.UUID(lake_tid), "consent-lake-manager-role"))
    cur.execute("""
        INSERT OR REPLACE INTO CONSENTS
        (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
        VALUES (?, ?, NULL, 'Allow', 'Role', NULL, ?, 'FinanceManager', ?, ?, 0)
    """, (cid_lake_mgr, lake_tid, role_map["FinanceManager"], now_iso, far_future_iso))

    # Role FinanceAuditor: Allow with masked customerEmail
    cid_lake_aud = str(uuid.uuid5(uuid.UUID(lake_tid), "consent-lake-auditor-role"))
    cur.execute("""
        INSERT OR REPLACE INTO CONSENTS
        (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
        VALUES (?, ?, NULL, 'Allow', 'Role', NULL, ?, 'FinanceAuditor', ?, ?, 0)
    """, (cid_lake_aud, lake_tid, role_map["FinanceAuditor"], now_iso, far_future_iso))
    cur.execute("""
        INSERT OR REPLACE INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
        VALUES (?, ?, ?, 'customerEmail', 2)
    """, (str(uuid.uuid4()), cid_lake_aud, col_id_map[(lake_tid, "customerEmail")]))

    # Blocked User: Hard Deny
    cid_lake_blk = str(uuid.uuid5(uuid.UUID(lake_tid), "consent-lake-blocked-user"))
    cur.execute("""
        INSERT OR REPLACE INTO CONSENTS
        (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked)
        VALUES (?, ?, NULL, 'Deny', 'User', 'S-1-5-21-FORWARD-USER_BLOCKED', NULL, NULL, ?, ?, 0)
    """, (cid_lake_blk, lake_tid, now_iso, far_future_iso))

    conn.commit()

if __name__ == "__main__":
    db_file = get_db_path()
    init_governance_db(db_file)
    try:
        os.chmod(db_file, 0o660)
        hr_file = os.environ.get("HR_DB_PATH", "/data/hr.db")
        if os.path.exists(hr_file):
            os.chmod(hr_file, 0o660)
    except Exception as e:
        print(f"[governance-seed] Warning chmod: {e}")
