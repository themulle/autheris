# F-SQL-02: Governed Stored Procedures (Phase 1, lesend)

Stored Procedures und Table-Valued Functions (TVFs) werden deklarativ als REST-Endpoint veröffentlicht. Architektur und Begründung: [ADR-018](../adr/ADR-018-governed-stored-procedures.md).

## Konfiguration

```json
"Gateway": {
  "SqlEndpoints": {
    "Procedures": {
      "Enabled": true,
      "Directory": "procedures",
      "ConnectionName": "procedures",
      "AllowedSchemas": [ "api" ]
    }
  },
  "DataSources": {
    "Connections": {
      "procedures": { "Provider": "SqlServer", "ConnectionString": "<Login autheris_proc>" }
    }
  }
}
```

Weitere Optionen: `RevalidationIntervalMinutes` (15), `LockTimeoutMs` (5000), `MaxRows` (5000), `MaxStringParameterLength` (4000), `MaxTimeoutSeconds` (60), `AllowedDataSources`, `EnableHotReload`.

## Deklaration `procedures/get_orders.proc.sql`

```sql
-- @name get_orders
-- @procedure api.usp_GetOrders
-- @mode read
-- @summary Liefert Aufträge eines Kunden
-- @param customer_id int required Kundennummer
-- @param note nvarchar(40) optional Freitext
-- @context tenant_id -> @TenantId
-- @context user_sid  -> @ActorSid
-- @result-table sales.orders
-- @result-column order_id clear
-- @roles order-reader
-- @timeout 30
```

| Header | Bedeutung |
|---|---|
| `@procedure` | `schema.name`, Schema muss in `AllowedSchemas` stehen |
| `@mode` | `read` (Phase 1). `write` wird abgelehnt |
| `@param` | `name sqltype required\|optional [Beschreibung]`. Typen: int, bigint, smallint, tinyint, bit, decimal(p,s), float, real, (n)varchar(n\|max), (n)char(n), uniqueidentifier, date, datetime, datetime2, smalldatetime, datetimeoffset |
| `@context` | Bindet `tenant_id`, `user_sid` oder `purpose` an einen Prozedurparameter. Nicht vom Client setzbar, nicht in der OpenAPI-Beschreibung |
| `@result-table` | Katalogtabelle, deren Spalten die Ergebnisspalten governen (Deny/Mask) |
| `@result-column x clear` | Ergebnisspalte ohne Katalogbezug freigeben |
| `@roles` | Optional: Rollen, von denen mindestens eine nötig ist |
| `@rls none` | Nur in Development erlaubt |
| `@allow-dynamic-sql` | Nur nach DBA-Review |

Ergebnisspalten, die weder zur `@result-table` gehören noch als `clear` deklariert sind, werden entfernt.

## Endpoints

- `GET|POST /api/v1/procedures/{name}`: Antwort `{ columns, rows, rowCount, truncated }`, immer `Cache-Control: no-store`.
- `GET /api/v1/procedures`: aktive Endpoints, die der Aufrufer sehen darf.
- `GET /api/v1/procedures/openapi.json`: OpenAPI 3.0.

Fehlercodes: 400 (unbekannter/fehlender/ungültiger Parameter), 403 (Rolle/Consent), 404, 422 (`THROW 50000-59999` der Prozedur), 503 (Endpoint deaktiviert).

## Datenbankseitige Voraussetzungen

```sql
CREATE USER autheris_proc FOR LOGIN autheris_proc;
GRANT EXECUTE ON SCHEMA::api TO autheris_proc;
GRANT VIEW DEFINITION ON SCHEMA::api TO autheris_proc;   -- Validator
GRANT VIEW DATABASE STATE TO autheris_proc;              -- Validator
-- KEIN SELECT/INSERT/UPDATE/DELETE auf Tabellen

CREATE FUNCTION sec.fn_tenant_predicate(@tenant_id nvarchar(64))
RETURNS TABLE WITH SCHEMABINDING AS
RETURN SELECT 1 AS ok WHERE @tenant_id = CAST(SESSION_CONTEXT(N'autheris.tenant_id') AS nvarchar(64));

CREATE SECURITY POLICY sec.TenantIsolation
  ADD FILTER PREDICATE sec.fn_tenant_predicate(tenant_id) ON sales.orders
WITH (STATE = ON);
```

Jede von der Prozedur direkt gelesene Tabelle braucht eine aktive Policy mit FILTER-Predicate und einen Katalogeintrag, sonst bleibt der Endpoint deaktiviert.

## Grenzen Phase 1

Katalogvalidierung nur für SQL Server; YAML, TVF, `validation: declared` und PostgreSQL unterliegen den Regeln im Nachtrag von ADR-018 (`AllowDeclaredValidation` ist außerhalb von Development nötig). Nur das erste Result-Set, keine OUTPUT-/TVP-Parameter, keine Consent-Zeilenfilter, keine verschachtelten Prozeduren/Views, kein GraphQL/MCP, keine schreibenden Prozeduren.
