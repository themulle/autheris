-- @id 03fdfc27399a class=delete expected=accept source=generated:cte-delete
WITH x AS (SELECT id FROM orders WHERE id < 3) DELETE FROM orders WHERE id IN (SELECT id FROM x)

-- @id 06aec81d6a09 class=delete expected=accept source=generated:delete-foreign
DELETE FROM employees WHERE id = 1001

-- @id 9af3ecb751da class=delete expected=accept source=generated:delete-foreign
DELETE FROM orders WHERE id = 1001

-- @id a1d634258811 class=delete expected=accept source=generated:delete-foreign
DELETE FROM customers WHERE id = 1001

-- @id ea1e61122f45 class=delete expected=accept source=generated:delete-foreign
DELETE FROM entitlements WHERE id = 1001

-- @id efaad0ec0300 class=delete expected=accept source=generated:delete-foreign
DELETE FROM users WHERE id = 1001

-- @id 4f40eca019f0 class=delete expected=accept source=generated:delete-hidden
DELETE FROM orders WHERE id = 2001

-- @id 7eaaabca7338 class=delete expected=accept source=generated:delete-hidden
DELETE FROM employees WHERE id = 5001

-- @id 84d665ca0296 class=delete expected=accept source=generated:delete-hidden
DELETE FROM customers WHERE id = 3001

-- @id 915729a6688e class=delete expected=accept source=generated:delete-hidden
DELETE FROM users WHERE id = 6001

-- @id 9759a0a15b9f class=delete expected=accept source=generated:delete-hidden
DELETE FROM entitlements WHERE id = 4001

-- @id 1adad000b2f2 class=delete expected=accept source=generated:delete-own
DELETE FROM orders WHERE id = 2

-- @id 348cd5cbd67e class=delete expected=accept source=generated:delete-own
DELETE FROM users WHERE id = 2

-- @id 7efe933dff51 class=delete expected=accept source=generated:delete-own
DELETE FROM employees WHERE id = 2

-- @id e4c162c9e791 class=delete expected=accept source=generated:delete-own
DELETE FROM entitlements WHERE id = 2

-- @id edeb2b722b60 class=delete expected=accept source=generated:delete-own
DELETE FROM customers WHERE id = 2

-- @id 1e9a39f0ed39 class=delete expected=accept source=generated:delete-range
DELETE FROM employees WHERE id > 0

-- @id 449d519c0bb1 class=delete expected=accept source=generated:delete-range
DELETE FROM customers WHERE id > 0

-- @id 94c302f88721 class=delete expected=accept source=generated:delete-range
DELETE FROM users WHERE id > 0

-- @id 980f3138b595 class=delete expected=accept source=generated:delete-range
DELETE FROM entitlements WHERE id > 0

-- @id a80d8de042a5 class=delete expected=accept source=generated:delete-range
DELETE FROM orders WHERE id > 0

-- @id 1d5f2ffe1055 class=delete expected=reject source=generated:delete-tautology
DELETE FROM employees WHERE 1 = 1

-- @id 62fedb21eef2 class=delete expected=reject source=generated:delete-tautology
DELETE FROM entitlements WHERE 1 = 1

-- @id ba6e21a7d862 class=delete expected=reject source=generated:delete-tautology
DELETE FROM users WHERE 1 = 1

-- @id d2731ba756f6 class=delete expected=reject source=generated:delete-tautology
DELETE FROM customers WHERE 1 = 1

-- @id 9e946aa18571 class=delete expected=reject source=generated:delete-unfiltered
DELETE FROM entitlements

-- @id a577d64805d0 class=delete expected=reject source=generated:delete-unfiltered
DELETE FROM employees

-- @id c6c11019d024 class=delete expected=reject source=generated:delete-unfiltered
DELETE FROM customers

-- @id f4f8f8c2668e class=delete expected=reject source=generated:delete-unfiltered
DELETE FROM users

-- @id 3eaa20f31d30 class=insert expected=reject source=generated:insert-foreign
INSERT INTO employees (id, tenant_id, name, dept, salary) VALUES (9002, 'B', 'new-9002', 'new-9002', 5.5)

-- @id 59f097c0eccb class=insert expected=reject source=generated:insert-foreign
INSERT INTO customers (id, tenant_id, name, email, region) VALUES (9002, 'B', 'new-9002', 'new-9002', 'new-9002')

-- @id d4e27f8fd51f class=insert expected=reject source=generated:insert-foreign
INSERT INTO entitlements (id, tenant_id, order_id, note) VALUES (9002, 'B', 1, 'new-9002')

-- @id d72e354e29dc class=insert expected=reject source=generated:insert-foreign
INSERT INTO orders (id, tenant_id, customer_id, amount, status, region, dept, created_at, email, note) VALUES (9002, 'B', 1, 5.5, 'new-9002', 'new-9002', 'new-9002', TIMESTAMP '2024-05-01 10:00:00', 'new-9002', 'new-9002')

-- @id d88ffe9ed586 class=insert expected=reject source=generated:insert-foreign
INSERT INTO users (id, tenant_id, name, status, email) VALUES (9002, 'B', 'new-9002', 'new-9002', 'new-9002')

-- @id 113d45dbfda8 class=insert expected=reject source=generated:insert-no-tenant
INSERT INTO employees (id) VALUES (9003)

-- @id 19027432ca33 class=insert expected=reject source=generated:insert-no-tenant
INSERT INTO entitlements (id) VALUES (9003)

-- @id 5baa6b5865c1 class=insert expected=reject source=generated:insert-no-tenant
INSERT INTO orders (id) VALUES (9003)

-- @id f3d7d01d0ca4 class=insert expected=reject source=generated:insert-no-tenant
INSERT INTO customers (id) VALUES (9003)

-- @id f8172ebf7fff class=insert expected=reject source=generated:insert-no-tenant
INSERT INTO users (id) VALUES (9003)

-- @id 1d3959068c96 class=insert expected=accept source=generated:insert-own
INSERT INTO customers (id, tenant_id, name, email, region) VALUES (9001, 'A', 'new-9001', 'new-9001', 'new-9001')

-- @id 71b26e48fa9f class=insert expected=accept source=generated:insert-own
INSERT INTO employees (id, tenant_id, name, dept, salary) VALUES (9001, 'A', 'new-9001', 'new-9001', 5.5)

-- @id c7dfb295e122 class=insert expected=accept source=generated:insert-own
INSERT INTO users (id, tenant_id, name, status, email) VALUES (9001, 'A', 'new-9001', 'new-9001', 'new-9001')

-- @id c8dce2450170 class=insert expected=accept source=generated:insert-own
INSERT INTO entitlements (id, tenant_id, order_id, note) VALUES (9001, 'A', 1, 'new-9001')

-- @id f07d7c55c78a class=insert expected=accept source=generated:insert-own
INSERT INTO orders (id, tenant_id, customer_id, amount, status, region, dept, created_at, email, note) VALUES (9001, 'A', 1, 5.5, 'new-9001', 'new-9001', 'new-9001', TIMESTAMP '2024-05-01 10:00:00', 'new-9001', 'new-9001')

-- @id 3b7a7d48ecda class=insert expected=accept source=generated:insert-select
INSERT INTO customers (id, tenant_id, name, email, region) SELECT id, tenant_id, name, email, region FROM customers WHERE id = 1

-- @id 8e9659d7cf55 class=insert expected=accept source=generated:insert-select
INSERT INTO entitlements (id, tenant_id, order_id, note) SELECT id, tenant_id, order_id, note FROM entitlements WHERE id = 1

-- @id c91f2d1dfc0d class=insert expected=accept source=generated:insert-select
INSERT INTO employees (id, tenant_id, name, dept, salary) SELECT id, tenant_id, name, dept, salary FROM employees WHERE id = 1

-- @id ceb15b185ee0 class=insert expected=accept source=generated:insert-select
INSERT INTO orders (id, tenant_id, customer_id, amount, status, region, dept, created_at, email, note) SELECT id, tenant_id, customer_id, amount, status, region, dept, created_at, email, note FROM orders WHERE id = 1

-- @id efa5804e4da3 class=insert expected=accept source=generated:insert-select
INSERT INTO users (id, tenant_id, name, status, email) SELECT id, tenant_id, name, status, email FROM users WHERE id = 1

-- @id 6e03e73706fe class=merge expected=accept source=generated:merge-delete
MERGE INTO employees t USING users s ON t.id = s.id WHEN MATCHED THEN DELETE

-- @id 4e2256266c41 class=merge expected=accept source=generated:merge-insert
MERGE INTO users t USING customers s ON t.id = s.id WHEN NOT MATCHED THEN INSERT (id, tenant_id, name, status) VALUES (s.id, 'A', s.name, 'x')

-- @id 1a7721dbfe8f class=merge expected=accept source=generated:merge-update
MERGE INTO orders t USING customers s ON t.customer_id = s.id WHEN MATCHED THEN UPDATE SET status = 'merged'

-- @id 0923bd3470ba class=update expected=accept source=generated:update-column
UPDATE orders SET status = 'changed' WHERE id = 1

-- @id 1468479ff281 class=update expected=accept source=generated:update-column
UPDATE orders SET created_at = TIMESTAMP '2024-06-01 00:00:00' WHERE id = 1

-- @id 14cfdc335f81 class=update expected=accept source=generated:update-column
UPDATE orders SET region = 'changed' WHERE id = 1

-- @id 36d80a627867 class=update expected=accept source=generated:update-column
UPDATE users SET status = 'changed' WHERE id = 1

-- @id 3f811c055e51 class=update expected=accept source=generated:update-column
UPDATE entitlements SET order_id = 7 WHERE id = 1

-- @id 5146ac57edb0 class=update expected=accept source=generated:update-column
UPDATE orders SET amount = 7.5 WHERE id = 1

-- @id 6b8054eec69f class=update expected=accept source=generated:update-column
UPDATE customers SET name = 'changed' WHERE id = 1

-- @id a629b02ecb85 class=update expected=accept source=generated:update-column
UPDATE orders SET customer_id = 7 WHERE id = 1

-- @id aee7d45d65df class=update expected=accept source=generated:update-column
UPDATE customers SET region = 'changed' WHERE id = 1

-- @id b0ca1e18d061 class=update expected=accept source=generated:update-column
UPDATE employees SET salary = 7.5 WHERE id = 1

-- @id bb26b877f1a9 class=update expected=accept source=generated:update-column
UPDATE orders SET dept = 'changed' WHERE id = 1

-- @id c046e3746c0e class=update expected=accept source=generated:update-column
UPDATE employees SET name = 'changed' WHERE id = 1

-- @id cc07b2c6edbf class=update expected=accept source=generated:update-column
UPDATE orders SET note = 'changed' WHERE id = 1

-- @id e42f896d8f04 class=update expected=accept source=generated:update-column
UPDATE users SET name = 'changed' WHERE id = 1

-- @id e73df6f7c572 class=update expected=accept source=generated:update-column
UPDATE entitlements SET note = 'changed' WHERE id = 1

-- @id f43f20d820e7 class=update expected=accept source=generated:update-column
UPDATE employees SET dept = 'changed' WHERE id = 1

-- @id 2b9b32107790 class=update expected=accept source=generated:update-foreign
UPDATE employees SET id = id WHERE id = 1001

-- @id 618d4a4ad595 class=update expected=accept source=generated:update-foreign
UPDATE orders SET id = id WHERE id = 1001

-- @id 677918bca524 class=update expected=accept source=generated:update-foreign
UPDATE entitlements SET id = id WHERE id = 1001

-- @id 92973dd28bbd class=update expected=accept source=generated:update-foreign
UPDATE customers SET id = id WHERE id = 1001

-- @id b14d6fa8e1cc class=update expected=accept source=generated:update-foreign
UPDATE users SET id = id WHERE id = 1001

-- @id 05a80ac4b6e4 class=update expected=accept source=generated:update-foreign-range
UPDATE users SET status = 'changed' WHERE id > 1000

-- @id 08c62a96d985 class=update expected=accept source=generated:update-foreign-range
UPDATE orders SET status = 'changed' WHERE id > 1000

-- @id 096e3fd99011 class=update expected=accept source=generated:update-foreign-range
UPDATE orders SET amount = 7.5 WHERE id > 1000

-- @id 1109f94eef90 class=update expected=accept source=generated:update-foreign-range
UPDATE customers SET email = 'changed' WHERE id > 1000

-- @id 20961f431eda class=update expected=accept source=generated:update-foreign-range
UPDATE users SET name = 'changed' WHERE id > 1000

-- @id 236ed9a60aa5 class=update expected=accept source=generated:update-foreign-range
UPDATE employees SET salary = 7.5 WHERE id > 1000

-- @id 2a78c848c686 class=update expected=accept source=generated:update-foreign-range
UPDATE orders SET email = 'changed' WHERE id > 1000

-- @id 5d831b3041cf class=update expected=accept source=generated:update-foreign-range
UPDATE orders SET customer_id = 7 WHERE id > 1000

-- @id 87cdb5d97da0 class=update expected=accept source=generated:update-foreign-range
UPDATE entitlements SET note = 'changed' WHERE id > 1000

-- @id b81d4e101ea2 class=update expected=accept source=generated:update-foreign-range
UPDATE entitlements SET order_id = 7 WHERE id > 1000

-- @id bd89e432a481 class=update expected=accept source=generated:update-foreign-range
UPDATE orders SET note = 'changed' WHERE id > 1000

-- @id c5f70890c256 class=update expected=accept source=generated:update-foreign-range
UPDATE employees SET dept = 'changed' WHERE id > 1000

-- @id d6756c6bfaa7 class=update expected=accept source=generated:update-foreign-range
UPDATE users SET email = 'changed' WHERE id > 1000

-- @id de6e8ac9a587 class=update expected=accept source=generated:update-foreign-range
UPDATE employees SET name = 'changed' WHERE id > 1000

-- @id e28b138a8ab3 class=update expected=accept source=generated:update-foreign-range
UPDATE orders SET created_at = TIMESTAMP '2024-06-01 00:00:00' WHERE id > 1000

-- @id e40370a2f6dd class=update expected=accept source=generated:update-foreign-range
UPDATE customers SET region = 'changed' WHERE id > 1000

-- @id ed3b1a0bbe10 class=update expected=accept source=generated:update-foreign-range
UPDATE orders SET region = 'changed' WHERE id > 1000

-- @id f8d4a2897394 class=update expected=accept source=generated:update-foreign-range
UPDATE orders SET dept = 'changed' WHERE id > 1000

-- @id fb97cb076ded class=update expected=accept source=generated:update-foreign-range
UPDATE customers SET name = 'changed' WHERE id > 1000

-- @id 0eaa12459578 class=update expected=reject source=generated:update-masked
UPDATE orders SET email = 'changed' WHERE id = 1

-- @id 6af5a8029e86 class=update expected=reject source=generated:update-masked
UPDATE users SET email = 'changed' WHERE id = 1

-- @id 795a5e9f9d31 class=update expected=reject source=generated:update-masked
UPDATE customers SET email = 'changed' WHERE id = 1

-- @id 1e1d55046622 class=update expected=accept source=generated:update-range
UPDATE orders SET note = 'changed' WHERE id > 0

-- @id 30aead0ce794 class=update expected=accept source=generated:update-range
UPDATE orders SET dept = 'changed' WHERE id > 0

-- @id 4a4295570d6e class=update expected=accept source=generated:update-range
UPDATE employees SET name = 'changed' WHERE id > 0

-- @id 5d55e2b7d400 class=update expected=accept source=generated:update-range
UPDATE users SET name = 'changed' WHERE id > 0

-- @id 707ecbc5b4d5 class=update expected=accept source=generated:update-range
UPDATE orders SET customer_id = 7 WHERE id > 0

-- @id 755aec52475f class=update expected=accept source=generated:update-range
UPDATE orders SET status = 'changed' WHERE id > 0

-- @id 77f8042fcef0 class=update expected=accept source=generated:update-range
UPDATE users SET email = 'changed' WHERE id > 0

-- @id 87d2ae6a2609 class=update expected=accept source=generated:update-range
UPDATE entitlements SET note = 'changed' WHERE id > 0

-- @id 8bd62176ed17 class=update expected=accept source=generated:update-range
UPDATE orders SET email = 'changed' WHERE id > 0

-- @id 91007dd671e0 class=update expected=accept source=generated:update-range
UPDATE orders SET region = 'changed' WHERE id > 0

-- @id a2a95cfbbdd8 class=update expected=accept source=generated:update-range
UPDATE employees SET dept = 'changed' WHERE id > 0

-- @id b2295bd205a5 class=update expected=accept source=generated:update-range
UPDATE orders SET amount = 7.5 WHERE id > 0

-- @id cd2a00458f1d class=update expected=accept source=generated:update-range
UPDATE employees SET salary = 7.5 WHERE id > 0

-- @id d06fbb27075f class=update expected=accept source=generated:update-range
UPDATE users SET status = 'changed' WHERE id > 0

-- @id daaf8d65be68 class=update expected=accept source=generated:update-range
UPDATE entitlements SET order_id = 7 WHERE id > 0

-- @id daf4a2f158c7 class=update expected=accept source=generated:update-range
UPDATE orders SET created_at = TIMESTAMP '2024-06-01 00:00:00' WHERE id > 0

-- @id e21549d260bf class=update expected=accept source=generated:update-range
UPDATE customers SET email = 'changed' WHERE id > 0

-- @id ea93f9ee7440 class=update expected=accept source=generated:update-range
UPDATE customers SET region = 'changed' WHERE id > 0

-- @id f023f235015c class=update expected=accept source=generated:update-range
UPDATE customers SET name = 'changed' WHERE id > 0

-- @id e2712c89dd72 class=update expected=reject source=generated:update-region-policy-column
UPDATE orders SET region = 'US' WHERE id = 1

-- @id 2b65f66c4d80 class=update expected=accept source=generated:update-subquery
UPDATE entitlements SET id = id WHERE id IN (SELECT id FROM entitlements WHERE id < 3)

-- @id 2ef091293313 class=update expected=accept source=generated:update-subquery
UPDATE customers SET id = id WHERE id IN (SELECT id FROM customers WHERE id < 3)

-- @id 4646df7ea1f1 class=update expected=accept source=generated:update-subquery
UPDATE users SET id = id WHERE id IN (SELECT id FROM users WHERE id < 3)

-- @id 8a7ce1d6b39b class=update expected=accept source=generated:update-subquery
UPDATE orders SET id = id WHERE id IN (SELECT id FROM orders WHERE id < 3)

-- @id c35ff52aba63 class=update expected=accept source=generated:update-subquery
UPDATE employees SET id = id WHERE id IN (SELECT id FROM employees WHERE id < 3)

-- @id 119d15afc7c6 class=update expected=reject source=generated:update-tenant
UPDATE employees SET tenant_id = 'B' WHERE id = 1

-- @id 270045e7b60b class=update expected=reject source=generated:update-tenant
UPDATE users SET tenant_id = 'B' WHERE id = 1

-- @id 9115f82fefe7 class=update expected=reject source=generated:update-tenant
UPDATE customers SET tenant_id = 'B' WHERE id = 1

-- @id df176f3d4859 class=update expected=reject source=generated:update-tenant
UPDATE orders SET tenant_id = 'B' WHERE id = 1

-- @id fd56a6ed2f56 class=update expected=reject source=generated:update-tenant
UPDATE entitlements SET tenant_id = 'B' WHERE id = 1

-- @id 336d40b8e5c5 class=insert expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
INSERT INTO default.orders_ci VALUES (1, 'acme'), (2, 'ACME'), (3, 'other')

-- @id b106149506f7 class=insert expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
INSERT INTO default.entitlements VALUES (1, 'acme', 1), (2, 'other', 2), (3, 'acme', 6), (4, 'ACME', 3)

-- @id de84b6c1a414 class=insert expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
INSERT INTO default.orders VALUES
    (1, 'acme',  'EU', 'open',   10.50, 'alice.smith@acme.example'),
    (2, 'acme',  'US', 'open',   20.00, 'bob.jones@acme.example'),
    (3, 'ACME',  'EU', 'open',   30.00, 'upper.case@ACME.example'),
    (4, 'ACME',  'US', 'closed', 40.00, 'upper.us@ACME.example'),
    (5, 'other', 'EU', 'open',   50.00, 'carol@other.example'),
    (6, 'acme',  'EU', 'closed', 60.00, NULL)

-- @id 5b0487da6248 class=update expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDuckDbDmlExecutionTests.cs
UPDATE orders SET status = 'z' WHERE id > 0

-- @id ffb67c834333 class=delete expected=accept source=tests/Autheris.Tests.Integration/AuditEndpointCoverageTests.cs
DELETE /api/v1/jobs/{jobId}

-- @id 85d5f98497e8 class=delete expected=reject source=tests/Autheris.Tests.Integration/EndToEndSqliteTests.cs
DELETE FROM e2e_cranes

-- @id ba0521b637a3 class=delete expected=reject source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
DELETE FROM customers WHERE id = 1

-- @id 119fa9e98d2a class=insert expected=accept source=tests/Autheris.Tests.Integration/MsSqlIntegrationTests.cs
INSERT INTO dbo.IntegrationTenants (TenantKey, DisplayName, IsActive)
OUTPUT INSERTED.Id
VALUES (@tenantKey, @displayName, @isActive);

-- @id 025de4e9a433 class=insert expected=accept source=tests/Autheris.Tests.Integration/OracleIntegrationTests.cs
INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES ('tenant-beta', 'Oracle Beta Customer 2', 888.88)

-- @id 042a695d0820 class=insert expected=accept source=tests/Autheris.Tests.Integration/OracleIntegrationTests.cs
INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES ('tenant-alpha', 'Oracle Customer 3', 360.75)

-- @id 60ba111f0f36 class=insert expected=accept source=tests/Autheris.Tests.Integration/OracleIntegrationTests.cs
INSERT INTO integration_tenants (tenant_key, display_name, is_active)
VALUES (:p1, :p2, :p3)

-- @id ab0c4786d530 class=insert expected=accept source=tests/Autheris.Tests.Integration/OracleIntegrationTests.cs
INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES ('tenant-alpha', 'Oracle Customer 2', 240.50)

-- @id cec7e78bdc93 class=insert expected=accept source=tests/Autheris.Tests.Integration/OracleIntegrationTests.cs
INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES ('tenant-beta', 'Oracle Beta Customer', 999.99)

-- @id d21537f4cb5c class=insert expected=accept source=tests/Autheris.Tests.Integration/OracleIntegrationTests.cs
INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES ('tenant-alpha', 'Oracle Customer 1', 120.00)

-- @id dccf276268d1 class=insert expected=accept source=tests/Autheris.Tests.Integration/PostgreSqlConsentContractTests.cs
INSERT INTO ROLES (id, role_name, description) VALUES (@rid, 'GovernanceAdmin', 'Governance Administrator')
                ON CONFLICT (role_name) DO NOTHING;
                INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m1, id, 'User', 'S-1-5-21-APP1' FROM ROLES WHERE role_name = 'GovernanceAdmin';
                INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m2, id, 'User', 'S-1-5-21-APP2' FROM ROLES WHERE role_name = 'GovernanceAdmin';

-- @id 1a7618665884 class=update expected=accept source=tests/Autheris.Tests.Integration/PostgreSqlGovernanceContractTests.cs
UPDATE audit_log_entries SET decision = 'X'

-- @id 37fb110251b9 class=delete expected=accept source=tests/Autheris.Tests.Integration/PostgreSqlGovernanceContractTests.cs
DELETE FROM audit_log_entries

-- @id d588a6012048 class=insert expected=accept source=tests/Autheris.Tests.Integration/PostgreSqlIntegrationTests.cs
INSERT INTO integration_tenants (tenant_key, display_name, is_active)
VALUES ($1, $2, $3)
RETURNING id;

-- @id cac252e1d397 class=insert expected=accept source=tests/Autheris.Tests.Integration/SqlServerConsentContractTests.cs
INSERT INTO ROLES (id, role_name, description)
                SELECT @rid, 'GovernanceAdmin', 'Governance Administrator'
                WHERE NOT EXISTS (SELECT 1 FROM ROLES WHERE role_name = 'GovernanceAdmin');
                INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m1, id, 'User', 'S-1-5-21-APP1' FROM ROLES WHERE role_name = 'GovernanceAdmin';
                INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m2, id, 'User', 'S-1-5-21-APP2' FROM ROLES WHERE role_name = 'GovernanceAdmin';

-- @id 1a459074e5e2 class=update expected=accept source=tests/Autheris.Tests.Integration/SqlServerGovernanceContractTests.cs
UPDATE AUDIT_LOG_ENTRIES SET decision = 'X'

-- @id 461f12fd790a class=delete expected=accept source=tests/Autheris.Tests.Integration/SqlServerGovernanceContractTests.cs
DELETE FROM AUDIT_LOG_ENTRIES

-- @id 0c4d86714238 class=insert expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
INSERT INTO orders VALUES (1, 10, 'tenant_a'), (2, 20, 'tenant_a'), (3, 30, 'tenant_a'), (4, 40, 'tenant_a'), (5, 50, 'tenant_a');

-- @id 2540149d88f9 class=insert expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
INSERT INTO orders VALUES (6, 60, 'tenant_b'), (7, 70, 'tenant_b');

-- @id 2708fb366c95 class=delete expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
DELETE FROM orders WHERE 1=1

-- @id 4d6fcf9d879c class=delete expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
DELETE FROM orders WHERE id <= 2

-- @id 7a36d75aedb0 class=delete expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
DELETE FROM orders WHERE id = 1

-- @id 7ba3775964d6 class=delete expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
DELETE FROM orders WHERE amount = 10

-- @id 84d175480c71 class=update expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
UPDATE orders SET amount = 0 WHERE id > 0

-- @id 92d3d63c209c class=update expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
UPDATE orders SET amount = 0

-- @id bd39c34e16f6 class=update expected=reject source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
UPDATE orders SET amount = 0 WHERE true

-- @id dadfb86858a1 class=delete expected=reject source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
DELETE FROM orders WHERE id = 1 OR 1 = 1

-- @id e364d98c2276 class=delete expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
DELETE FROM

-- @id fe38fe1b1ab3 class=delete expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
DELETE FROM orders

-- @id 6184c50067e5 class=update expected=accept source=tests/Autheris.Tests.Unit/ComprehensiveSecurityAttackVectorTests.cs
UPDATE AUDIT_LOG_ENTRIES SET actor_sid = 'S-1-5-21-IMPERSONATED' WHERE id = @id

-- @id f90fd69de5d6 class=update expected=accept source=tests/Autheris.Tests.Unit/ComprehensiveSecurityAttackVectorTests.cs
UPDATE AUDIT_LOG_ENTRIES SET decision = 'ALLOW' WHERE id = @id

-- @id 7fbf24a35ba1 class=delete expected=accept source=tests/Autheris.Tests.Unit/ConcurrencyAndAuditReplicationTests.cs
DELETE FROM AUDIT_LOG_ENTRIES WHERE id = @id

-- @id c619c80ca35d class=update expected=accept source=tests/Autheris.Tests.Unit/ConcurrencyAndAuditReplicationTests.cs
UPDATE AUDIT_LOG_ENTRIES SET details_json = '{"tampered": true}' WHERE id = @id

-- @id f2fffbc34ebe class=update expected=accept source=tests/Autheris.Tests.Unit/ConcurrencyAndAuditReplicationTests.cs
UPDATE DATA_OWNERS
                                SET display_name = 'Anonymized User', email = 'anonymized@corp.local'
                                WHERE ad_sid = @sid

-- @id 49a2e935f0b5 class=insert expected=accept source=tests/Autheris.Tests.Unit/ConnectorSpiTests.cs
INSERT INTO orders VALUES ($id, 'tenant-1');

-- @id a3c06ef3e81b class=insert expected=accept source=tests/Autheris.Tests.Unit/DelegationAndFourEyesStressTests.cs
INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role)
                                VALUES (@id, @tid, @oid, 'DELEGATE');

-- @id f6c22eb5309e class=insert expected=accept source=tests/Autheris.Tests.Unit/DelegationAndFourEyesStressTests.cs
INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active)
                                VALUES (@id, 'S-1-5-21-INACTIVE-OWNER', 'CORP\\inactive', 'Inactive Owner', 'inactive@corp.local', 0);

-- @id 0abf8ae78b66 class=insert expected=accept source=tests/Autheris.Tests.Unit/Diagnostics/TrafficShadowingGuardTests.cs
INSERT orders (id) VALUES (1)

-- @id 24bc16f9ead4 class=insert expected=accept source=tests/Autheris.Tests.Unit/Diagnostics/TrafficShadowingGuardTests.cs
INSERT INTO customers (id, name) VALUES (1, 'Alice')

-- @id 3fdfdc550b7e class=merge expected=reject source=tests/Autheris.Tests.Unit/Diagnostics/TrafficShadowingGuardTests.cs
MERGE INTO target_table USING source_table ON (id)

-- @id 8714c2a17a92 class=delete expected=accept source=tests/Autheris.Tests.Unit/Diagnostics/TrafficShadowingGuardTests.cs
DELETE logs WHERE created_at < NOW()

-- @id 9da66d2f0deb class=delete expected=accept source=tests/Autheris.Tests.Unit/Diagnostics/TrafficShadowingGuardTests.cs
DELETE FROM logs WHERE created_at < NOW()

-- @id f9b97fce1bba class=update expected=accept source=tests/Autheris.Tests.Unit/Diagnostics/TrafficShadowingGuardTests.cs
UPDATE accounts SET balance = balance + 100 WHERE id = 42

-- @id ab77efdff603 class=update expected=accept source=tests/Autheris.Tests.Unit/DocumentationSourcePriorityTests.cs
UPDATE TABLE_COLUMNS SET meta_json = '{"pii": false}' WHERE column_name = 'email'

-- @id 28c6ea3ebe5d class=insert expected=accept source=tests/Autheris.Tests.Unit/Governance/ConsentFourEyesSensitivityTests.cs
INSERT OR IGNORE INTO ROLES (id, role_name, description) VALUES (@rid, 'GovernanceAdmin', 'Governance Administrator');
                INSERT OR IGNORE INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m1, id, 'User', 'S-1-5-21-APPROVER-1' FROM ROLES WHERE role_name = 'GovernanceAdmin';
                INSERT OR IGNORE INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
                SELECT @m2, id, 'User', 'S-1-5-21-APPROVER-2' FROM ROLES WHERE role_name = 'GovernanceAdmin';

-- @id 15e92c23d5bc class=update expected=accept source=tests/Autheris.Tests.Unit/InfrastructureTests.cs
UPDATE AUDIT_LOG_ENTRIES SET id = @tamperedId WHERE id = @originalId

-- @id dded497113f4 class=delete expected=accept source=tests/Autheris.Tests.Unit/InfrastructureTests.cs
DELETE FROM POLICY_EPOCHS WHERE table_name = 'finance_table_1'

-- @id c46b1a15d7b9 class=insert expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
INSERT INTO orders VALUES (1, 'a', 'eu', 'tenant_a'), (2, 'b', 'us', 'tenant_b');

-- @id be442b52e0f5 class=delete expected=accept source=tests/Autheris.Tests.Unit/Security/AuditAnchorHardeningTests.cs
DELETE FROM AUDIT_LOG_ENTRIES WHERE rowid IN (SELECT rowid FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 2)

-- @id 13c75147271d class=update expected=reject source=tests/Autheris.Tests.Unit/Security/CorrelatedRowFilterAliasTests.cs
UPDATE orders SET amount = 0 WHERE id = 1

-- @id df1d79768261 class=insert expected=accept source=tests/Autheris.Tests.Unit/Security/RepeatedReviewRound3Tests.cs
INSERT INTO orders VALUES (1);

-- @id 30ffa4acc8be class=insert expected=accept source=tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs
INSERT INTO accounts (id, balance) VALUES (1, 100)

-- @id 46c7d980f4fe class=update expected=accept source=tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs
UPDATE accounts SET balance = 100, TenantId = 'hacked' WHERE id = 1

-- @id 91ca274bf963 class=insert expected=accept source=tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs
INSERT INTO organizations (id, name, org_id) VALUES (42, 'Rogue', 'rogue_tenant')

-- @id 94060096a523 class=insert expected=accept source=tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs
INSERT INTO organizations (id, name) VALUES (42, 'Rogue')

-- @id 99bd3a998f72 class=update expected=accept source=tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs
UPDATE organizations SET name = 'New Corp', org_id = 'rogue_tenant' WHERE id = 42

-- @id ab9f307ecc26 class=update expected=accept source=tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs
UPDATE accounts SET balance = 50, account_tenant = 'hacked' WHERE id = 10

-- @id b493af868b19 class=insert expected=accept source=tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs
INSERT INTO accounts (id, balance, TenantId) VALUES (1, 500, 'tenant-safe')

-- @id bdb736a04787 class=update expected=accept source=tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs
UPDATE orders SET total = 200, order_tenant = 'hacked' WHERE id = 20

-- @id cc876441c218 class=insert expected=accept source=tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs
INSERT INTO accounts (id, balance, TenantId) VALUES (1, 100, 'tenant-hacked')

-- @id d44040cf4c27 class=update expected=accept source=tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs
UPDATE accounts SET balance = 500 WHERE id = 1

-- @id c96743202271 class=insert expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002InfraTests.cs
INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id)
                                VALUES (@id, @occ, 'LEGACY', 'S-1-5-21-LEGACY', 'hr.dbo.salaries', NULL, 'ALLOW', 'trace-legacy', '{}', @prev, @hash, 'legacy-single-tenant')

-- @id f72a6b20bc20 class=update expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002InfraTests.cs
UPDATE AUDIT_LOG_ENTRIES SET seq = 7 WHERE seq = 3

-- @id f82327fb67f7 class=delete expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002InfraTests.cs
DELETE FROM AUDIT_LOG_ENTRIES WHERE rowid = (SELECT MAX(rowid) FROM AUDIT_LOG_ENTRIES)

-- @id 1f7eb1c652ce class=insert expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
INSERT INTO orders (id, amount) VALUES (1, 10)

-- @id ae76e5939407 class=insert expected=reject source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
INSERT INTO orders (id, amount, tenant_id) VALUES (1, 10, '42')

-- @id e0d1ed85f91d class=insert expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
INSERT INTO orders (id, amount, tenant_id) VALUES (1, 10, 'tenant_a')

-- @id 001618967ba7 class=delete expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/SqlVirtualFilterKeySetProviderTests.cs
DELETE FROM client

-- @id 5ddaa419c308 class=update expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/SqliteVirtualFilterBindingFederationTests.cs
UPDATE VIRTUAL_FILTER_ACCESS_PROFILES SET definition_json = replace(definition_json, '"MaxPushdownKeys"', '"CompositeKeys":["a","b"],"MaxPushdownKeys"')

-- @id 53fb44436708 class=update expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/VirtualFilterAdministrationServiceTests.cs
UPDATE VIRTUAL_FILTERS SET definition_hash = 'manipuliert' WHERE name = 'nicht_ausgelieferte_krane';

-- @id 957380bee409 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/AnalysisAndMaskingTests.cs
DELETE FROM sessions WHERE expired = 1

-- @id d5153d52b4bf class=insert expected=accept source=tests/TrinoSqlEngine.Tests/AnalysisAndMaskingTests.cs
INSERT INTO audit_log (msg) VALUES ('test')

-- @id ee0082b618a0 class=update expected=accept source=tests/TrinoSqlEngine.Tests/AnalysisAndMaskingTests.cs
UPDATE users SET name = 'Alice' WHERE id = 1

-- @id 05c8e796a823 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE employees SET notes = (SELECT sub.val FROM (SELECT employees.*) sub) WHERE id = 1

-- @id 06c2221d766d class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM employees WHERE salary LIKE '500%'

-- @id 16e029218c93 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
INSERT INTO orders (id, amount) VALUES (1, 100)

-- @id 1f2023f2e93f class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE notes SET content = (SELECT salary FROM employees WHERE id = 1) WHERE id = 1

-- @id 235fa842263e class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE employees SET notes = 'x' WHERE (SELECT count(*) FROM (SELECT employees.*) sub) > 0

-- @id 2676d93a702b class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE orders SET tenant_id = 42 WHERE id = 1

-- @id 2d226b45921c class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
INSERT INTO orders (id, amount, tenant_id) VALUES (1, 100, 99)

-- @id 2dde2a041bbb class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE 1 != 2

-- @id 467dc7679b0e class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE orders SET x = 1 WHERE true

-- @id 4ccf2c9d1f30 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
INSERT INTO patients (id, name, tenant_id) VALUES (1, 'Alice', 42)

-- @id 5461b662820a class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE orders SET x = 1 WHERE 'a' != 'b'

-- @id 5ed3bc9ebd95 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE EXISTS (SELECT 1)

-- @id 5ef4604c16fa class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM employees WHERE CAST(salary AS VARCHAR) = '50000'

-- @id 5ffa69d4fc6d class=update expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE orders SET x = 1 WHERE amount > 100

-- @id 67d188c5e6de class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE employees SET salary = 100000 WHERE id = 1

-- @id 709940e3ca81 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE customer_id IN (SELECT id FROM customers)

-- @id 77f64320ae56 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE orders SET x = 1 WHERE 10 > 5

-- @id 77fe307f5acd class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE id BETWEEN id AND id

-- @id 7d5745c524e9 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE orders SET x = 1 WHERE 5 >= 2

-- @id 7e911c998b68 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE 100 + 200 = 300

-- @id 8b86980d885b class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM employees WHERE name LIKE 'test%' ESCAPE salary

-- @id 8fcb56f9dc61 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE id IN (id)

-- @id 93dfbe7b860a class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE id IN (SELECT count(*) FROM employees GROUP BY salary)

-- @id 9c59d652d1a9 class=update expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE orders SET total = (SELECT sum(price) FROM items) WHERE customer_id IN (SELECT id FROM customers)

-- @id a3946cfcc37b class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
INSERT INTO

-- @id a96871c2cff6 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE id IN (SELECT o.id FROM orders o JOIN employees e ON o.user_id = e.salary)

-- @id aa090118d7b1 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
INSERT INTO orders (id, amount, tenant_id) VALUES (1, 100, 42)

-- @id ae0dae4666b1 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE 1 = 1

-- @id b0600460c17a class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE col LIKE ('pattern' OR 'foo')

-- @id b33fd8639e91 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM employees WHERE salary > 50000

-- @id c160b4d41e6f class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE orders SET x = 1 WHERE id >= id

-- @id c8a7594e0299 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE 5 <= 5

-- @id d5f4fe0fecf0 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM public.employees WHERE (SELECT count(*) FROM (SELECT public.employees.*) sub) > 0

-- @id dae9dcca7de1 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM employees WHERE (SELECT count(*) FROM (SELECT employees.*) sub) > 0

-- @id dd4620ea209e class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE 'a' = 'a'

-- @id ddad9d0a17ea class=update expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE orders SET status = 'done' WHERE col LIKE 'test' ESCAPE ('/' OR '\')

-- @id f091c118fcce class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE id <= id

-- @id f2a7aaa89c78 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
DELETE FROM orders WHERE 1 < 2

-- @id f79dff2f0c56 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.cs
UPDATE orders SET x = 1

-- @id 12a9f0ad493c class=update expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
UPDATE orders SET status = 'DONE' WHERE id = 10

-- @id 8b5951623f6d class=update expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
UPDATE orders SET id = 2

-- @id a05730df9d24 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
DELETE FROM orders WHERE id = 10

-- @id bc8beec347d4 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
INSERT INTO orders VALUES (1)

-- @id 5940d4b56b99 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Ast/TargetDialectRowFilterTests.cs
DELETE FROM orders WHERE id = 5

-- @id 1ab7027ec449 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
DELETE FROM orders WHERE 1 = 1 AND tenant_id = 't1'

-- @id 07b118c0b609 class=update expected=accept source=tests/TrinoSqlEngine.Tests/AstSecurityDmlPrecedenceTests.cs
UPDATE orders SET status = 'active' WHERE (a + b) BETWEEN 1 AND 10

-- @id 41b879c516d3 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/AstSecurityDmlPrecedenceTests.cs
DELETE FROM orders WHERE amount > 10 OR status = 'pending'

-- @id 6680bfd6390a class=update expected=accept source=tests/TrinoSqlEngine.Tests/AstSecurityDmlPrecedenceTests.cs
UPDATE orders SET status = 'cancelled' WHERE (flag1 OR flag2) IN (TRUE)

-- @id 80643b03eba9 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/AstSecurityDmlPrecedenceTests.cs
DELETE FROM orders WHERE (flag1 OR flag2) IS DISTINCT FROM FALSE

-- @id dd06062eb851 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/AstSecurityDmlPrecedenceTests.cs
DELETE FROM orders WHERE (a + b) IN (SELECT id FROM other_orders)

-- @id 107ced8a8e62 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, 'acme')

-- @id 5527ad406baf class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
MERGE INTO orders t USING orders s ON t.id = s.id WHEN MATCHED THEN DELETE

-- @id a3b05e7d6f54 class=update expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
UPDATE orders SET status = 'x' WHERE id = 1

-- @id 0232c243faa8 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO t (a) SELECT [autheris_ins].[a] FROM (VALUES (1)) AS [autheris_ins] ([a])

-- @id 04cfbabc8883 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO t (a) SELECT 1 WHERE @p0 = 1

-- @id 22b2a0310816 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO t (a) SELECT 1 WHERE @p0 = 2

-- @id 2fb915517de1 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO t (a) SELECT 1

-- @id 60a7aec4535c class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO t (a) SELECT `autheris_ins`.`a` FROM (VALUES (1)) AS `autheris_ins` (`a`)

-- @id 7635f3cd2687 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO t SELECT 1

-- @id 95a60086388d class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO t (a) SELECT 5

-- @id 993fca30823b class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
DELETE FROM t

-- @id a5d89d1404d0 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO t (a) SELECT 1 UNION ALL SELECT 2

-- @id aad25998bb8a class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO t (a) SELECT AUTHERIS_INS.a FROM x AS AUTHERIS_INS

-- @id ddb58aa49307 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO nope (a) SELECT 1

-- @id fb16285d830e class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
INSERT INTO t (a) SELECT "autheris_ins"."a" FROM (VALUES (1)) AS "autheris_ins" ("a")

-- @id 00cad84facbe class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO mystery t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id 05aa15239bd7 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
DELETE FROM orders OUTPUT deleted.* WHERE id = 1

-- @id 08e4ca4be2d1 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET nope = 1 WHERE id = 2

-- @id 0a44bae07d95 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE id IN (SELECT id FROM entitlements WHERE orders.email = 'a')

-- @id 0b89fdb98fb5 class=update expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE id = 1 OR id = 2

-- @id 110beda09b88 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders USING (SELECT id FROM entitlements) orders ON orders.id = 1 WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id 1517b4990297 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE id = 1 AND orders.email LIKE 'a%'

-- @id 165ece9b6487 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE id = 1 RETURNING *

-- @id 16f30a64d3b4 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE

-- @id 18de20825727 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
DELETE FROM orders WHERE email = 'a@b.c'

-- @id 1b27a19a6820 class=merge expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING (SELECT id FROM entitlements) s ON t.id = s.id WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id 1cbee05ef7bd class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM orders LIMIT 3

-- @id 1dbd786928ae class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED THEN INSERT (id, tenantid, status) VALUES (s.id, 'acme', t.email)

-- @id 24a34476ee7e class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM orders UNION ALL SELECT id, 'other' FROM entitlements

-- @id 2781567eaa79 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements t

-- @id 27f302d7b4f2 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) SELECT * FROM orders

-- @id 2a5e25826146 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = email WHERE id = 1

-- @id 2c17df556a53 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING (SELECT id FROM entitlements) T

-- @id 2d1cb3502273 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED BY SOURCE THEN DELETE

-- @id 2eec753d51ff class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, EMAIL) VALUES (1, 'acme', 'a@b.c')

-- @id 32fd3f17f342 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, status) VALUES

-- @id 342c8be2ad6c class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET region = 'US'

-- @id 35ff1117618a class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, 'acme') RETURNING *

-- @id 388a1522dfd7 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU'), (2, 'acme', 'US')

-- @id 3cc17569552b class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED AND t.email = 'x' THEN UPDATE SET status = 'x'

-- @id 442670e8d841 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) WITH x AS (SELECT id FROM orders) SELECT id, 'acme' FROM x

-- @id 473288193a37 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, email) VALUES (1, 'acme', 'a@b.c')

-- @id 474e6bc3bcd2 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET tenantid = 'other' WHERE id = 1

-- @id 47f126576b32 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING (SELECT id FROM entitlements) orders

-- @id 4a79e1dec779 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN DELETE

-- @id 4cfe2154c7b2 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET email = 'x'

-- @id 4e36f6ce14ba class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET TENANTID = 'acme' WHERE id = 1

-- @id 50fcfd5a6779 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, 'acme'), (2, 'acme')

-- @id 511c74822357 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = email

-- @id 53c453118aae class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
DELETE FROM orders WHERE id = 7

-- @id 55727b9d08a7 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM orders UNION ALL SELECT id, 'acme' FROM entitlements

-- @id 580698d5b42e class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, (SELECT tenantid FROM orders))

-- @id 5d2dc825983c class=update expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = (SELECT max(id) FROM entitlements) WHERE id = 1

-- @id 6015b1bc8e98 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) SELECT d.orderid, 'acme' FROM (SELECT orderid FROM entitlements) d WHERE d.orderid > 1

-- @id 60171bc20986 class=update expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = (SELECT max(id) FROM entitlements) WHERE

-- @id 6037c11de8c9 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) OUTPUT inserted.* VALUES (1, 'acme')

-- @id 61b26d502430 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'a', STATUS = 'b' WHERE id = 2

-- @id 62e3dc2d75d5 class=update expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = (SELECT max(o2.status) FROM orders o2 WHERE o2.id = 1) WHERE id = 1

-- @id 62e68b942052 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = upper(Email) WHERE id = 1

-- @id 63d4bd4d3826 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO mystery (id) VALUES (1)

-- @id 64ac23670ed7 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET REGION = 'US' WHERE id = 7

-- @id 66d518e195e4 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE id IS NOT NULL AND status IS NULL

-- @id 69cce80067d4 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE id = 7

-- @id 6ab4726e072e class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM orders ORDER BY id

-- @id 6b8f02bc21d5 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders o JOIN entitlements e ON o.id = e.orderid SET status = 'x' WHERE o.id = 1

-- @id 6d825d7b642c class=merge expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING (SELECT orderid, id FROM entitlements WHERE id > 0) s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id 6d9d776cd8dc class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) SELECT 1, 'acme', 3 FROM orders

-- @id 7034b3f73983 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
DELETE FROM orders WHERE id = 7 OR status = 'x'

-- @id 70ea05ce3928 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE id > ANY (SELECT orderid FROM entitlements)

-- @id 75dc739929a1 class=merge expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid

-- @id 80769a34e6ff class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
DELETE FROM orders WHERE id = 1 RETURNING id

-- @id 8152beeaa7ed class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, lower('ACME'))

-- @id 85a2d0b3fc40 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, status) SELECT id, 'acme', status FROM entitlements WHERE id > 5

-- @id 870dff287c3a class=update expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE id = (SELECT max(orderid) FROM entitlements)

-- @id 8c4660c806be class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders VALUES (1, 'acme', 'EU', 'open', 1.5, 'x')

-- @id 8f31f2699225 class=merge expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders USING entitlements s ON orders.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id 90a21d8881ea class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU'), (2, 'acme', 'US'), (3, 'acme', 'EU')

-- @id 92d5d9e5861a class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, status) SELECT id, 'acme', email FROM orders

-- @id 9364b9d48f1b class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE NOT (id = 5)

-- @id 945cfaacaa70 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' FROM entitlements WHERE orders.id = entitlements.orderid

-- @id 9568ca1ef0aa class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING (SELECT id FROM entitlements) t

-- @id 9765acb579f7 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
DELETE FROM orders USING entitlements WHERE orders.id = entitlements.orderid

-- @id 9957fef01bb4 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED AND t.email IS NULL THEN INSERT (id, tenantid) VALUES (s.id, 'acme')

-- @id 9aef867cdcc3 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE mystery SET a = 1 WHERE b = 2

-- @id 9def2ea9941c class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders USING (SELECT id FROM entitlements) ORDERS ON orders.id = 1 WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id 9f99a7e55e94 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, id) VALUES (1, 'acme', 2)

-- @id a031693bda24 class=update expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = (SELECT max(id) FROM entitlements) WHERE id IN (SELECT orderid FROM entitlements)

-- @id ad27579df77a class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE 1 = 1

-- @id b0f26d199db7 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders@main SET status = 'x' WHERE id = 1

-- @id b350091638a4 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x'

-- @id b616084bedd9 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, nope) VALUES (1, 'acme', 2)

-- @id b6ed6be4cce1 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, __param_t)

-- @id b84275c812b1 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON true WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id b8861f10d865 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, NULL)

-- @id b8b0aea00e12 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' OUTPUT deleted.* WHERE id = 1

-- @id bdadfeec0bca class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = t.email

-- @id beb1afe2693e class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
DELETE FROM orders WHERE id = id

-- @id bf1a286e963b class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET tenantid = 'other'

-- @id c2f8deb8fadf class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED BY TARGET THEN INSERT (id, tenantid) VALUES (1, 'acme')

-- @id c8caf3070249 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'eu')

-- @id c8f5bc21813c class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, region) SELECT id, 'acme', 'EU' FROM entitlements

-- @id c982cd9f5b16 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders@main t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id cd2e4611b6c9 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE id = 1 OR 1 = 1

-- @id ced09e254a36 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = (SELECT max(o2.status) FROM orders o2 WHERE o2.id = 1) WHERE email = 'x'

-- @id d009771efa2e class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET email = 'x' WHERE id = 1

-- @id d7b137db2b68 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON 1 = 1 WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id daea266c61e9 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE true

-- @id dc034d122cf8 class=update expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE id IS NOT NULL OR status IS NULL

-- @id dcf8037cb19b class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, 'other')

-- @id ddc014b0b9f2 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) SELECT id, tenantid FROM orders

-- @id de5ef5a6102b class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET region = 'US' WHERE id = 7

-- @id de87b9dce0da class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, 'victim-tenant')

-- @id df1fa31465d0 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU')

-- @id df28eadea4e8 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) SELECT id, 'acme' FROM entitlements

-- @id e2f4c7e90074 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED BY SOURCE AND t.id > 1 THEN DELETE

-- @id e4e4070827e2 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.email = 'x' WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id e5cc18fba7f8 class=update expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'closed' WHERE id = 7

-- @id e7082774f232 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, 'acme ')

-- @id e7d517427daa class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
DELETE FROM orders WHERE EXISTS (SELECT 1 FROM entitlements e WHERE e.orderid = 1)

-- @id e94d077eaedc class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) SELECT id, 'other' FROM orders

-- @id ed66f28090c6 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, region, status) VALUES (1, 'acme', 'EU', 's')

-- @id edba8c612424 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, 'acme'), (2, 'other')

-- @id ef9bd7934e69 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, status) VALUES (1, 'acme')

-- @id f210a6ba1cee class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x', region = 'US' WHERE id = 7

-- @id f260bd62ca2a class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, 'ACME')

-- @id f35b80ec4684 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, status) VALUES (1, 'acme', 'open'), (2, 'acme', 'closed')

-- @id f42f29c212db class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, region, amount) VALUES (1, 'acme', 'EU', 5)

-- @id f5765f305205 class=update expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
UPDATE orders SET status = 'x' WHERE email = 'a@b.c'

-- @id f6b11a386130 class=merge expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING orders s ON t.id = s.id WHEN MATCHED AND s.status = 'a' THEN UPDATE SET status = 'b'

-- @id f7acd0d51312 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid, region, amount) VALUES (1, 'acme', 'EU', 99.999)

-- @id fb51c6b17446 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN NOT MATCHED THEN INSERT (id, tenantid, region) VALUES (s.id, 'acme', 'EU')

-- @id fe8be12b9685 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, tenantid) VALUES (1, 'acme', 'x')

-- @id fe9c49fa2210 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid OR 1 = 1 WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id fee0d04953a7 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
DELETE FROM orders WHERE id IN (SELECT orderid FROM entitlements)

-- @id ff46cd9fb9e4 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
DELETE FROM orders WHERE id IN (SELECT id FROM entitlements WHERE orders.EMAIL = 'x')

-- @id ffa10a0bb15d class=insert expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCompileTests.cs
INSERT INTO orders (id, status) VALUES (1, 'open')

-- @id 669c913c5e7d class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCoverageTests.cs
INSERT INTO orders (id, tenantid, region) VALUES (1, 'acme', 'EU'), (2, 'acme', 'EU')

-- @id b3d407195385 class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCoverageTests.cs
MERGE INTO [t] AS [a] USING [s] AS [b] ON [a].[x] = [b].[x] WHEN MATCHED THEN DELETE; DELETE FROM [t]

-- @id b91547eda1e1 class=merge expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCoverageTests.cs
MERGE INTO orders t USING entitlements s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'

-- @id be23725f8a26 class=merge expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DmlCoverageTests.cs
MERGE INTO [t] AS [a] USING [s] AS [b] ON [a].[x] = [b].[x] WHEN MATCHED THEN DELETE;

-- @id c169ab19d31f class=merge expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCoverageTests.cs
MERGE INTO [t] AS [a] USING [s] AS [b] ON [a].[x] = [b].[x] WHEN MATCHED THEN DELETE;;

-- @id ddfadd072e44 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/DmlLimitAndAliasSql78Tests.cs
DELETE FROM orders WHERE id IN (SELECT id FROM orders o WHERE o.amount > 3)

-- @id 291a10658fb9 class=merge expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
MERGE INTO inventory @ dev AS i                   USING changes AS c                   ON true                 WHEN MATCHED                   THEN DELETE

-- @id 4082230f06eb class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
INSERT INTO a."b/c".d (c1, c2) SELECT * FROM t

-- @id 43fe3a5c0c74 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
INSERT INTO a."b/c".d SELECT * FROM t

-- @id 54551509d0a4 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
DELETE FROM t WHERE a = b

-- @id 601171aaf5cc class=update expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
UPDATE foo_table                 SET bar = 23

-- @id 997b6e55873a class=merge expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
MERGE INTO inventory AS i                   USING changes AS c                   ON i.part = c.part                 WHEN MATCHED AND c.action = 'mod'                   THEN UPDATE SET                     qty = qty + c.qty                   , ts = CURRENT_TIMESTAMP                 WHEN MATCHED AND c.action = 'del'                   THEN DELETE                 WHEN NOT MATCHED AND c.action = 'new'                   THEN INSERT (part, qty) VALUES (c.part, c.qty)

-- @id 9c1326beed1f class=update expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
UPDATE foo_table                     SET bar = 23, baz = 3.1415E0, bletch = 'barf'                 WHERE (nothing = 'fun')

-- @id a9856523d58f class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
DELETE FROM "awesome table"

-- @id bc0e18a6c3ae class=delete expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
DELETE FROM t @ dev

-- @id c150d4b0b6d7 class=update expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
UPDATE foo_table @ dev SET bar = 23

-- @id faf707c3d94c class=insert expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
INSERT INTO t @ dev VALUES 1

-- @id 060a45436c2c class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE true

-- @id 07af787673a7 class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE orders SET status = 'shipped' WHERE id = 10

-- @id 0b3f1bb008ae class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE created_at < '2020-01-01'

-- @id 0e88eb7c54c9 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE 1 = 0

-- @id 19d30f373377 class=update expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE orders SET status = 'shipped' WHERE 1 = 1

-- @id 222e338bab01 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE (tenant_id = 42)

-- @id 25fb6e720e79 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders VALUES (1, 99)

-- @id 2a9dfe8d8b1c class=insert expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) VALUES (1, -42)

-- @id 2b606cebd25d class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE customers SET notes = 'x' WHERE id = 1

-- @id 3671725aaea7 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) VALUES (1, 40+2)

-- @id 3b5be3b0d4b5 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) SELECT id, tenant_id FROM staging

-- @id 3d38e672344b class=insert expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) TABLE staging

-- @id 4a886a76f470 class=update expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE orders SET status = 'shipped'

-- @id 4feb7035aa81 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) VALUES (1, '42'), (2, 42)

-- @id 57c9fe8d7429 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) VALUES (1, 't''1')

-- @id 62ec17564ac1 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE 1 = 1.0

-- @id 72671fe8d5ee class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (tenant_id) VALUES (42)

-- @id 7c98b8638d6d class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE 1=1 AND true

-- @id 85acd43caa62 class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE customers SET notes = 'x' WHERE (tenant_id = 42) AND (id = 1)

-- @id 8bc3db3865b9 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM customers WHERE ssn LIKE '1%'

-- @id 9682180608ba class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) VALUES (1, '''42''')

-- @id 980d95093c46 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) SELECT 1, 40+3

-- @id 9efbac1dae20 class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE customers SET notes = ssn WHERE id = 1

-- @id a248668ed4a3 class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE customers SET notes = customers.ssn

-- @id a5dec0920bd1 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE id = 1 OR id = 2

-- @id aa48600b1446 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) SELECT 1, 42 UNION ALL SELECT 2, '42'

-- @id afa2d47547f8 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) SELECT * FROM staging

-- @id b0a1ebd34408 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) VALUES (1, 't1')

-- @id b9be44450bb5 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) SELECT 1, 42 UNION ALL SELECT 2, 99

-- @id bb33ae58e493 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE (1=1)

-- @id ca04a8e68a57 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE NOT false

-- @id d348793dd6aa class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE id = 10 OR 1=1

-- @id d8b53886724e class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE (tenant_id = 42) AND (id = 10)

-- @id dc88cbe72022 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM customers WHERE id IN (SELECT id FROM customers WHERE "SSN" = '123')

-- @id e196ab416455 class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE customers SET ssn = 'overwritten' WHERE id = 1

-- @id e430671a69a3 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
INSERT INTO orders (id, tenant_id) VALUES (1, 42), (2, 41), (3, 42)

-- @id e5f1c1894ea3 class=delete expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
DELETE FROM orders WHERE 1 <> 2

-- @id e864d81a28de class=update expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE orders SET status = 'shipped' WHERE 'a' = 'a'

-- @id f692ec6c593f class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE orders SET status = 'shipped' WHERE id = 10 AND 1 = 1

-- @id f93a09ccc13a class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
UPDATE customers SET notes = 'x' WHERE ssn LIKE '1%'

-- @id 19bb090992dc class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
UPDATE sales.customers SET notes = CAST("sales.customers" AS VARCHAR) WHERE id = 1

-- @id 3cc76a3b7e61 class=delete expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
DELETE FROM customers WHERE customers IS NOT NULL AND id = 1

-- @id 6423407a988e class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
INSERT INTO orders (id, tenant_id) VALUES (1, 42)

-- @id 6bf92d07dd8e class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
UPDATE customers SET notes = 'clean' WHERE id = 1

-- @id 7988bedaa9af class=insert expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
INSERT INTO patients (id, tenant_id) VALUES (1, 42)

-- @id af06b94ef770 class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
UPDATE customers SET notes = 'clean'

-- @id b3b4ebf362e3 class=insert expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
INSERT INTO orders

-- @id ce13dcbb3ddf class=update expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
UPDATE customers SET notes = CAST(customers AS VARCHAR) WHERE id = 1

-- @id fe58c08d27e7 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
INSERT INTO health.records (id, tenant_id) VALUES (1, 42)

-- @id 1684d50a4cd2 class=update expected=reject source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
UPDATE orders SET status = 'shipped', tenant_id = 99 WHERE id = 10

-- @id 38039b5710b3 class=update expected=reject source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
UPDATE orders SET status = 'shipped' WHERE (tenant_id = 42)

-- @id 426212de424c class=update expected=reject source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
UPDATE orders SET status = 'shipped' WHERE (tenant_id = 42) AND (id = 10)

-- @id 6dfafa2940c4 class=update expected=reject source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
UPDATE orders SET status = 'shipped', "tenant_id" = 99 WHERE id = 10

-- @id 7367ab44be88 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
INSERT INTO orders (id, tenant_id) VALUES (1, 99)

-- @id 9fa25e50e1bf class=insert expected=reject source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
INSERT INTO orders (id, tenant_id) SELECT 1, 99

-- @id cd85c3927e3e class=delete expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
DELETE FROM orders WHERE (tenant_id = 42) AND (id = 10 OR 1=1)

-- @id e48c6a4f0500 class=insert expected=reject source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
INSERT INTO orders (id, "tenant_id") VALUES (1, 42), (2, 99)

