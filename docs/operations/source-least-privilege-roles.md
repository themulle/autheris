# Least-Privilege Source Database Roles

Grant per governed object, never per database or catalog. The data login below is read-only. Writes use a separate login configured as `WriteConnection` ([WP-SIMPL-45](../plans/simplification/WP-SIMPL-45-governed-write-access.md), OD-25/OD-26) that holds only the privileges listed by `GET /api/v1/admin/datasources/{name}/required-write-privileges` (column-level `INSERT`/`UPDATE` on tables with active write grants, `DELETE` only where a delete grant exists); it never holds catalog privileges either.

## PostgreSQL

```sql
CREATE ROLE autheris_data NOLOGIN;
GRANT CONNECT ON DATABASE sales TO autheris_data;
GRANT USAGE ON SCHEMA core TO autheris_data;
GRANT SELECT ON core.orders, core.v_orders TO autheris_data;
GRANT EXECUTE ON FUNCTION core.fn_orders_for(integer) TO autheris_data;
CREATE ROLE autheris_svc LOGIN PASSWORD '<secret>' IN ROLE autheris_data;
ALTER ROLE autheris_svc SET default_transaction_read_only = on;
```

- Never grant `pg_read_all_data`, `pg_read_all_stats`, `pg_read_all_settings`, `pg_monitor` or superuser.
- PostgreSQL always exposes `pg_catalog` to every role. That cannot be revoked per role, so for PostgreSQL OD-25 is enforced by Autheris: the guard and no type loading.

## SQL Server

```sql
CREATE USER autheris_svc FOR LOGIN autheris_svc;
CREATE ROLE autheris_data;
GRANT SELECT ON OBJECT::core.Orders TO autheris_data;
GRANT EXECUTE ON OBJECT::core.usp_Orders TO autheris_data;
ALTER ROLE autheris_data ADD MEMBER autheris_svc;
DENY VIEW DEFINITION TO autheris_data;
DENY VIEW DATABASE STATE TO autheris_data;
DENY SELECT ON SCHEMA::sys TO autheris_data;
DENY SELECT ON SCHEMA::INFORMATION_SCHEMA TO autheris_data;
```

- Keep `EXECUTE` on `sys.sp_set_session_context` (the public default; ADR-018).
- Never use `db_datareader`, `db_owner`, `VIEW ANY DEFINITION` or `VIEW SERVER STATE`.
- CDC module only: `VIEW CHANGE TRACKING` on the tracked tables.

## Oracle

```sql
CREATE USER autheris_svc IDENTIFIED BY "<secret>";
GRANT CREATE SESSION TO autheris_svc;
CREATE ROLE autheris_data;
GRANT READ ON core.orders TO autheris_data;
GRANT autheris_data TO autheris_svc;
ALTER USER autheris_svc DEFAULT ROLE autheris_data;
```

- Use `READ`, not `SELECT`, so no `FOR UPDATE` locks are possible.
- Never grant `SELECT ANY DICTIONARY`, `SELECT_CATALOG_ROLE`, `SELECT/READ ANY TABLE`, `DBA` or `RESOURCE`.
- `ALL_*` views list only granted objects and cannot be revoked from PUBLIC safely, so they are guarded by Autheris.
- Oracle procedures stay rejected (WP-39 D3).

## Databricks (Unity Catalog)

```sql
GRANT USE CATALOG ON CATALOG sales TO `autheris-svc`;
GRANT USE SCHEMA ON SCHEMA sales.core TO `autheris-svc`;
GRANT SELECT ON TABLE sales.core.orders TO `autheris-svc`;
```

- Grant `CAN USE` on the SQL warehouse only.
- Never grant `BROWSE`, `ALL PRIVILEGES`, `MANAGE`, ownership, or any privilege on catalog `system`.
