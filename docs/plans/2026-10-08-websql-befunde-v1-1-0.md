# WebSQL und Trino-Endpunkt: Befunde aus der Integration des PoC mit Autheris 1.1.0

**Stand:** Image `ghcr.io/themulle/autheris:latest` vom 08.10.2026 (10:35 UTC), Quellcode `feat/ast-target-dialect-generator` auf a98200f. Geprüft im Talos-PoC (SQL Server `LWETEM_PROD`, Nutzer `david` mit Zeilenfilter) mit `scripts/verify-autheris.sh` gegen den laufenden Container. Den Quellcode habe ich gelesen, aber nicht gebaut oder getestet (kein .NET-SDK auf dem Rechner). Zeilennummern beziehen sich auf a98200f.

## 1. Beobachtungen am laufenden Container

| Aufruf | Ergebnis |
|---|---|
| `SELECT … FROM fms.air1` (mit und ohne `dataSource` im Body) | 403 „Access to table 'fms.air1' is denied or the table is not registered in the governance catalog“ |
| `SELECT … FROM lwetem_prod.fms.air1` | 200; Parameter (`@id`), `COUNT(*)` (0,8 s), `ORDER BY … LIMIT`, Zeilenfilter, Limit 10 000, DML-Sperre wie erwartet |
| `LIMIT 2` auf einer großen Tabelle | `truncated: true` |
| `POST /v1/statement` (Klartext, `SELECT COUNT(*) …`) | 200, synchron `FINISHED`; Spalte `{"name":"","type":"varchar"}`, Wert `7235` als Zahl |
| `GET /mcp/sse`, `/mcp/message` | 404; `POST /mcp` (Streamable HTTP) funktioniert |

Der PoC läuft mit `Gateway__RowFilters__SubqueryStrategy=InCorrelated`. Aggregate unter 1 s gelten für diese Strategie; `Exists`/`In` wurden in 1.1.0 nicht erneut gemessen.

## 2. Befunde

### 2.1 Fehler: `schema.tabelle` wird nie in der Domäne der Datenquelle aufgelöst

- **Status:** Behoben. `ResolveTableIdentifier` löst zweiteilige Namen zuerst gegen die Datenquelle auf; ist die Tabelle dort nicht katalogisiert, gilt weiter der Rückfall auf `default`. Tests: `tests/Autheris.Tests.Unit/Sql/WebSqlTwoPartNameDataSourceTests.cs`.
- **Ort:** `src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs:1528-1541` (`ResolveTableIdentifier`), Aufruf in Zeile 358.
- **Ursache:** Die Methode ruft `TableIdentifier.TryParse(target.FullName, …)` zuerst auf. `TryParse` macht aus zwei Teilen sofort die Domäne `default` (`src/Autheris.Domain/Common/TableIdentifier.cs:65-68`). `fms.air1` wird also zu `default.fms.air1`, bevor der Zweig mit der Datenquelle (Zeilen 1536-1538) erreicht wird. Dieser Zweig ist tot. Der Katalog führt die Tabellen nur unter `lwetem_prod`, der Fallback in Zeile 393-397 geht zusätzlich auf `default` und findet erst recht nichts.
- **Folge:** WebSQL akzeptiert nur noch Dreier-Namen. Der Kommentar der Methode und das Feld `dataSource` im Body versprechen etwas anderes. Der Fall betrifft jede Datenquelle, deren Domäne nicht `default` heißt, nicht nur den PoC. In 1.0.12 war der Fehler sichtbar (403 für alles), in 1.1.0 nur noch für zweiteilige Namen.
- **Fix:** Zweiteilige Namen zuerst gegen die Datenquelle auflösen, `TryParse` danach (nur für Dreier-Namen und als Fallback):

  ```csharp
  private static TableIdentifier ResolveTableIdentifier(TableAccessTarget target, string? dataSourceName)
  {
      if (string.IsNullOrWhiteSpace(target.Catalog) && !string.IsNullOrWhiteSpace(target.Schema) &&
          !string.IsNullOrWhiteSpace(dataSourceName))
      {
          return new TableIdentifier(dataSourceName, target.Schema, target.TableName);
      }
      if (TableIdentifier.TryParse(target.FullName, out var parsed)) { return parsed; }
      // … bisheriger Rückfall auf "default" / "public"
  }
  ```

  Zu prüfen: Der Rückfall auf `default` (Zeilen 393-397) soll für zweiteilige Namen bestehen bleiben. Ob die Rückgabe über `target.FullName` an anderen Stellen (Zeilen 345, 458, 504) zu Kollisionen führt, ist ungeprüft.
- **Test:** Unit-Test mit einer Datenquelle `lwetem_prod`, die Tabelle nur unter dieser Domäne im Katalog: `SELECT … FROM md.crane` mit `dataSource=lwetem_prod` muss gelingen; ohne `dataSource` bleibt es 403 (Standardquelle); `lwetem_prod.md.crane` bleibt unverändert; eine Tabelle, die nur unter `default` katalogisiert ist, wird weiter gefunden.

### 2.2 Fehler: Trino-Spaltentyp ist immer `varchar`

- **Ort:** `src/Autheris.Api/Endpoints/WebSqlEndpoints.cs:894-896` (`WriteTrinoStatementResponseAsync`): `status.Columns.Select(c => new { name = c, type = "varchar" })`.
- **Folge:** Die Daten sind echte Zahlen, Zeitstempel usw., der angekündigte Typ ist aber immer `varchar`. Tolerante Clients lesen das noch; typisierte Clients (JDBC, trino-python mit Typkonvertierung) können falsche Werte erzeugen oder abbrechen. Das widerspricht dem Ziel „100 % Trino-kompatibel“ aus `2026-10-08-trino-compatibility-websql.md`.
- **Fix:** Spaltentyp aus dem Ergebnis des Readers (CLR-Typ oder `GetDataTypeName`) auf Trino-Typen abbilden (`bigint`, `integer`, `double`, `decimal(p,s)`, `boolean`, `varchar`, `timestamp(3) with time zone`, `date`, `varbinary`). `StatementExecutionStatus.Columns` hält heute nur Namen; die Typen müssen mitgeführt werden. Unbekannte Typen bleiben `varchar`. Offen: Die Klasse mit `GetDataTypeName(...) => "varchar"` in `GovernedSqlExecutionService.cs:1780` ist vermutlich ein Hilfs-Reader und liefert die Typen nicht; Herkunft der Typen vor der Umsetzung klären.
- **Test:** Statement mit `bigint`-, `datetimeoffset`- und `nvarchar`-Spalte: die Spaltentypen der Antwort passen zu den Werten.

### 2.3 Kleiner Fehler: Spalten ohne Namen bleiben leer

- **Ort:** Namen kommen aus `reader.GetName(i)` (`GovernedSqlExecutionService.cs:1172`) und werden unverändert in die Antwort geschrieben.
- **Ursache:** SQL Server liefert für Ausdrücke ohne Alias (`COUNT(*)`) einen leeren Namen. Das betrifft auch das Ergebnis von `POST /api/v1/sql`, nicht nur Trino: Das JSON-Objekt der Zeile erhält einen Schlüssel `""`.
- **Fix:** Leere oder doppelte Namen durch `_col0`, `_col1` … (Position) ersetzen, wie Trino es tut, beim Lesen des Readers. Gilt für WebSQL und Trino gleich.
- **Test:** `SELECT COUNT(*) FROM …` liefert `columns: ["_col0"]` und `rows: [{"_col0": n}]`.

### 2.4 Ungenau, kein Fehler: `truncated` bei `LIMIT n`

- **Ort:** `GovernedSqlExecutionService.cs:1189-1194` (Regex auf `LIMIT n` im umgeschriebenen SQL) und `WebSqlEndpoints.cs:312` (`rowCount >= effectiveLimit`).
- **Verhalten:** `truncated` heißt „das Limit wurde erreicht“, nicht „es gibt weitere Zeilen“. Hat eine Tabelle genau `n` Zeilen, steht `truncated: true`, obwohl nichts fehlt. Bei größeren Tabellen ist das Feld richtig.
- **Optional:** Eine Zeile mehr lesen (`limit + 1`) und sie verwerfen; dann ist `truncated` exakt. Kostet eine zusätzliche Zeile je Abfrage. Die Aussage sollte in der WebSQL-Dokumentation stehen, falls es bei der Heuristik bleibt.

### 2.5 Keine Fehler (zur Einordnung)

- **MCP ohne SSE:** Absicht seit Commit d16d27c (offizielles C#-SDK, Streamable HTTP). Clients, die nur HTTP+SSE sprechen, brauchen Streamable-HTTP-Unterstützung.
- **Trino synchron:** `FINISHED` in einer Antwort ohne `nextUri`, wenn die Abfrage innerhalb der Wartezeit endet. Trino selbst antwortet asynchron; Clients, die `nextUri` folgen, funktionieren trotzdem.

## 3. Reihenfolge

1. **2.1** zuerst (blockiert zweiteilige Namen und das Feld `dataSource`), mit Test.
2. **2.2** und **2.3** zusammen (beide betreffen `columns` der Antwort), mit Tests.
3. **2.4** nur, wenn eine exakte Aussage gebraucht wird.

Ein Stash mit dem Entwurf des Fixes zu 2.1 liegt lokal (`git stash list`, „ResolveTableIdentifier-Fix …“); er passt auf den alten Stand und muss auf die jetzige Methode übertragen werden.

## 4. Nachprüfung im PoC

Nach jedem Fix: Image neu bauen oder ziehen, Container neu starten und `bash scripts/verify-autheris.sh` im Talos-PoC ausführen. Das Skript erwartet derzeit, dass `schema.tabelle` ohne Datenquelle mit 403 abgelehnt wird; nach 2.1 muss diese Prüfung auf Erfolg umgestellt werden.
