\set ON_ERROR_STOP on
-- Provision least-privilege grants for the runtime application role on PostgreSQL.
-- Run as the migration owner / database owner after governance schema migrations complete.
--
-- Usage:
--   psql -h <host> -U <migration_owner> -d <database> -v app_role=autheris_app -f deploy/sql/governance-app-role.postgresql.sql
--
-- Create the runtime role beforehand without superuser, database-creation, role-creation, or bypassrls privileges:
--   CREATE ROLE autheris_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT;

\if :{?app_role}
\else
\echo 'ERROR: Missing psql variable app_role. Pass -v app_role=<role_name>.'
DO $$ BEGIN RAISE EXCEPTION 'Missing psql variable app_role. Pass -v app_role=<role_name>.'; END $$;
\quit 1
\endif

SELECT set_config('autheris.app_role', :'app_role', false);

DO $do$
DECLARE
    app_role text := NULLIF(BTRIM(current_setting('autheris.app_role', true)), '');
    t text;
    seq_name text;
BEGIN
    IF app_role IS NULL OR app_role = ':''app_role''' THEN
        RAISE EXCEPTION 'Missing psql variable app_role. Pass -v app_role=<role_name>.';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = app_role) THEN
        RAISE EXCEPTION 'Role "%" does not exist. Create it first with LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT.', app_role;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_roles r
        WHERE r.rolname = app_role
          AND (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolbypassrls)
    ) THEN
        RAISE EXCEPTION 'Role "%" holds privileged attributes (SUPERUSER/CREATEDB/CREATEROLE/BYPASSRLS) and cannot be used as the runtime application role.', app_role;
    END IF;

    IF to_regclass('public.audit_log_entries') IS NULL THEN
        RAISE EXCEPTION 'Table public.audit_log_entries does not exist. Run governance schema migrations before provisioning the runtime application role.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_class c
        WHERE c.oid = to_regclass('public.audit_log_entries')
          AND (pg_get_userbyid(c.relowner) = app_role OR pg_has_role(app_role, c.relowner, 'USAGE'))
    ) THEN
        RAISE EXCEPTION 'Role "%" owns or inherits ownership of public.audit_log_entries and cannot be used as the runtime application role.', app_role;
    END IF;

    REVOKE CREATE ON SCHEMA public FROM PUBLIC;

    EXECUTE format('REVOKE CREATE ON SCHEMA public FROM %I', app_role);
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), app_role);
    EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', app_role);

    FOR t IN
        SELECT c.relname
        FROM pg_class c
        WHERE c.relkind = 'r'
          AND c.relnamespace = 'public'::regnamespace
    LOOP
        IF t = 'audit_log_entries' THEN
            -- Append-only audit log: runtime account receives SELECT and INSERT only (no UPDATE, DELETE, TRUNCATE, TRIGGER, REFERENCES).
            EXECUTE format('REVOKE ALL ON TABLE public.%I FROM PUBLIC, %I', t, app_role);
            EXECUTE format('GRANT SELECT, INSERT ON TABLE public.%I TO %I', t, app_role);
        ELSIF t = 'autheris_schema_migrations' THEN
            -- Schema history: read-only for Mode=Verify startup checks.
            EXECUTE format('REVOKE ALL ON TABLE public.%I FROM PUBLIC, %I', t, app_role);
            EXECUTE format('GRANT SELECT ON TABLE public.%I TO %I', t, app_role);
        ELSE
            -- Governance catalog, policy, consent, usage, governance_domains, governance_domain_tables,
            -- object_protection, access_policy_settings, governance_epochs, and remaining runtime tables.
            EXECUTE format('REVOKE ALL ON TABLE public.%I FROM %I', t, app_role);
            EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.%I TO %I', t, app_role);
        END IF;
    END LOOP;

    seq_name := pg_get_serial_sequence('public.audit_log_entries', 'rowid');
    IF seq_name IS NOT NULL THEN
        EXECUTE format('REVOKE ALL ON SEQUENCE %s FROM PUBLIC, %I', seq_name, app_role);
        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %s TO %I', seq_name, app_role);
    END IF;
END
$do$;
