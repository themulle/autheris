IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'crmdb')
BEGIN
    CREATE DATABASE crmdb;
END
GO

USE crmdb;
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'orders' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.orders (
        id INT PRIMARY KEY,
        order_number VARCHAR(50) NOT NULL,
        customer_name VARCHAR(100) NOT NULL,
        email VARCHAR(100) NOT NULL,
        total_amount DECIMAL(18, 2) NOT NULL,
        department VARCHAR(50) NOT NULL,
        status VARCHAR(50) NOT NULL,
        created_at DATETIME2 NOT NULL
    );

    -- Seed 5,000 orders using recursive CTE
    WITH Numbers AS (
        SELECT 1 AS n
        UNION ALL
        SELECT n + 1 FROM Numbers WHERE n < 5000
    )
    INSERT INTO dbo.orders (id, order_number, customer_name, email, total_amount, department, status, created_at)
    SELECT 
        n,
        'ORD-' + RIGHT('00000' + CAST(n AS VARCHAR(10)), 5),
        CASE (n % 6)
            WHEN 0 THEN 'Acme Corp'
            WHEN 1 THEN 'Global Logistics'
            WHEN 2 THEN 'Nexus Systems'
            WHEN 3 THEN 'Starlight Media'
            WHEN 4 THEN 'Vanguard Tech'
            ELSE 'Delta Industrial'
        END,
        'order.' + CAST(n AS VARCHAR(10)) + '@customer.local',
        ROUND(100.0 + ((n * 37.5) % 9900.0), 2),
        CASE (n % 4)
            WHEN 0 THEN 'Finance'
            WHEN 1 THEN 'Operations'
            WHEN 2 THEN 'Sales'
            ELSE 'Marketing'
        END,
        CASE (n % 3)
            WHEN 0 THEN 'COMPLETED'
            WHEN 1 THEN 'PROCESSING'
            ELSE 'PENDING'
        END,
        DATEADD(hour, n, '2025-01-01 00:00:00')
    FROM Numbers
    OPTION (MAXRECURSION 5000);
END
GO

-- Least-privilege application user for Autheris (DEP-15)
USE master;
GO

IF NOT EXISTS (SELECT * FROM sys.server_principals WHERE name = 'crm_user')
BEGIN
    CREATE LOGIN crm_user WITH PASSWORD = '$(MSSQL_CRM_PASSWORD)';
END
GO

USE crmdb;
GO

IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = 'crm_user')
BEGIN
    CREATE USER crm_user FOR LOGIN crm_user;
    ALTER ROLE db_datareader ADD MEMBER crm_user;
    ALTER ROLE db_datawriter ADD MEMBER crm_user;
END
GO
