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

## Result column governance by source (review D-2)

For catalog-validated procedures the validator now describes the result set with `sys.dm_exec_describe_first_result_set_for_object(@id, 1)` (browse mode) and stores the source schema, table and column of every result column. At execution every result column is governed by the decision of its **source** table and source column, not by its output name:

* Columns of the `@result-table` are denied/masked according to the consent of that table.
* Columns that originate from another referenced table are returned only with an explicit column-level `Clear`; `Mask` and `Deny` remove the column (fail closed).
* Computed or ambiguous columns (no source) are removed unless declared with `@result-column <name> clear`.
* Procedures in `declared` validation mode and table-valued functions have no source information; they keep the previous by-name governance against the `@result-table`.

Additional behaviour: the role/visibility check precedes the health check, so a caller without the required role receives the same 404 as for an unknown endpoint; the client IP is never taken from a token `ip` claim (unknown client resolves to `IPAddress.None`); HMAC pseudonyms in the result are tenant-scoped.

## Row scope for consent row filters (`row_scope_key`)

A consent (or Casbin rule) with a row filter on the **result table** normally denies the call, because the filter cannot
be pushed into the procedure. With `row_scope_key` the call is allowed and the result is filtered afterwards:

```yaml
result_table: md.crane
row_scope_key: serial_number        # or a list for a composite key: [client_id, code]
```

```sql
-- @result-table md.crane
-- @row-scope-key serial_number
```

How it works: after the call the gateway collects the key values of the result rows and runs
`SELECT key FROM <result_table> AS autheris_target WHERE <tenant> AND (<row filter>) AND (<key tuples>)` in the database.
Result rows whose key is not returned are removed. The filter therefore keeps its exact SQL semantics.

Rules (all fail closed: the call is denied):

- **Connection:** the lookup uses the governed read connection of the table's data source
  (`Gateway:DataSources:Connections:<source>`), never the EXECUTE-only procedure login (ADR-018). Supported: SQL Server,
  PostgreSQL, SQLite. The connection dialect must match the catalog dialect of the table.
- **Unique key:** the key columns must be exactly the primary key or a unique, unfiltered (non-partial), enabled index of
  the result table. This is verified in the database (`sys.indexes`, `pg_index`, `pragma_index_list` /
  `pragma_table_info`) and cached for 5 minutes. A non-unique key would let one allowed row admit every result row with
  the same key value.
- **Key columns** must be in the result set and effectively `Clear` for the caller (otherwise the match would be a side
  channel). With catalog validation the database-reported source of each key column must be the same column of the
  result table (D-2).
- **Security context:** the lookup carries the same caller context as the call: read-only `SESSION_CONTEXT` on SQL
  Server, transaction-local settings in a read-only transaction on PostgreSQL. The tenant column is filtered as on every
  governed read.
- Rows with a NULL key part are removed. Keys are compared in a canonical text form; values that differ only by case,
  padding, scale or type are not matched (removed, not admitted).
- Row filters on any **other** referenced table still deny the call.

Limits (residual risks, decide per procedure):

- **Aggregates:** if the procedure combines several table rows per key (SUM, COUNT, string aggregation), rows the filter
  denies still contribute to the value of an allowed key. Computed result columns are dropped (D-2) unless they are
  declared `clear`; do not declare such columns `clear` on a procedure with `row_scope_key`. Use row scope only for
  procedures that return rows of the result table.
- **`validation: declared`:** there is no source information for result columns, so a key column could come from another
  table under the same name. The declaration is trusted; review it like the procedure body.
