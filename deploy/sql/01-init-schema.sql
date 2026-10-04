-- ==============================================================================
-- GqlGateway Benchmark Database Schema (PostgreSQL)
-- ==============================================================================

CREATE SCHEMA IF NOT EXISTS dbo;
CREATE SCHEMA IF NOT EXISTS public;

-- Invoices Table (Primary benchmark table with sensitive & masking test columns)
CREATE TABLE IF NOT EXISTS public.invoices (
    id SERIAL PRIMARY KEY,
    name VARCHAR(255) NOT NULL,
    amount NUMERIC(12,2) NOT NULL,
    email VARCHAR(255),
    iban VARCHAR(64),
    salary NUMERIC(12,2),
    department VARCHAR(64) NOT NULL,
    vendor VARCHAR(255) NOT NULL,
    status VARCHAR(32) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Indexes for high-performance RLS pushdown and filtering benchmarks
CREATE INDEX IF NOT EXISTS idx_invoices_department ON public.invoices(department);
CREATE INDEX IF NOT EXISTS idx_invoices_status ON public.invoices(status);
CREATE INDEX IF NOT EXISTS idx_invoices_amount ON public.invoices(amount);
CREATE INDEX IF NOT EXISTS idx_invoices_created_at ON public.invoices(created_at);

-- Child Table for DataLoader / N+1 and Relation queries
CREATE TABLE IF NOT EXISTS public.finance_items (
    id VARCHAR(64) PRIMARY KEY,
    parent_id VARCHAR(64) NOT NULL,
    product_name VARCHAR(255) NOT NULL,
    price NUMERIC(12,2) NOT NULL,
    sensitive_note VARCHAR(255)
);

CREATE INDEX IF NOT EXISTS idx_finance_items_parent_id ON public.finance_items(parent_id);

-- DBO compatibility views matching GqlGateway default catalog definitions
CREATE OR REPLACE VIEW dbo.finance_table_1 AS 
    SELECT id, name, amount, email, created_at, iban, salary, department, vendor, status 
    FROM public.invoices;

CREATE OR REPLACE VIEW dbo.finance_items AS 
    SELECT id, parent_id, product_name, price, sensitive_note 
    FROM public.finance_items;

CREATE OR REPLACE VIEW dbo.hr_table_1 AS 
    SELECT id, name, salary AS amount, email, created_at 
    FROM public.invoices 
    WHERE department = 'HR';

-- OpenMetadata database creation for optional openmetadata-server container
SELECT 'CREATE DATABASE openmetadata_db OWNER gqluser'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'openmetadata_db')\gexec
GRANT ALL PRIVILEGES ON DATABASE openmetadata_db TO gqluser;

