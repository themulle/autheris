# WebSQL und Trino-Endpunkt: Befunde aus der Integration des PoC mit Autheris 1.1.0

**Stand:** Image `ghcr.io/themulle/autheris:latest` vom 08.10.2026 (10:35 UTC), Quellcode `feat/ast-target-dialect-generator` auf a98200f. Geprüft im Talos-PoC (SQL Server `LWETEM_PROD`, Nutzer `david` mit Zeilenfilter) mit `scripts/verify-autheris.sh` gegen den laufenden Container. Den Quellcode habe ich gelesen, aber nicht gebaut oder getestet (kein .NET-SDK auf dem Rechner). Zeilennummern beziehen sich auf a98200f.

## Übersicht

| Nr. | Art | Befund | Wirkung | Abschnitt |
|---|---|---|---|---|
| 2.1 | Fehler | `schema.tabelle` wird nie in der Domäne der Datenquelle aufgelöst (`TryParse` zuerst) | WebSQL nur mit Dreier-Namen, `dataSource` im Body wirkt nicht | 2.1 |
| 4.2 | Fehler | Parquet meldet das WebSQL-Zeilenlimit nicht (`X-Export-Truncated: false` bei 10 000 von 993 630 Zeilen) | stiller Datenverlust beim Laden als Parquet | 4.2 |
| 4.1 | Fehler | Flight SQL liefert feste Beispielzeilen und ignoriert die Abfrage | Endpunkt nicht nutzbar, täuscht Funktion vor | 4.1 |
| 2.2 | Fehler | Trino-Spaltentyp immer `varchar` | typisierte Trino-Clients (JDBC, Python) können falsche Typen annehmen | 2.2 |
| 2.3 | Kleiner Fehler | Spalten ohne Alias haben leeren Namen (`COUNT(*)`), auch in WebSQL-JSON | Schlüssel `""` in der Zeile; Trino nutzt `_col0` | 2.3 |
| 4.3 | Kleiner Fehler | Arrow-Export mit Tabelle nur im Body scheitert immer mit 403 (ReBAC liest nur die Query) | Body-Variante des Schemas ist unbenutzbar | 4.3 |
| 4.3 | Auffälligkeit | Parquet über OData liefert Datums- und Zeitspalten als `string` (über WebSQL als `timestamp`) | Typverlust in Parquet-Clients | 4.3 |
| 4.3 | Auffälligkeit | `Accept: text/csv`, `x-ndjson`, Arrow bei OData wird ignoriert (JSON mit 200); `?format=parquet` bei WebSQL ebenso | falsches Format ohne Fehler | 4 |
| 2.4 | Ungenau | `truncated` heißt „Limit erreicht“, nicht „es gibt mehr“ | bei genau `n` Zeilen `true` | 2.4 |
| 4 | PoC-Konfiguration | Arrow-Export und OLAP: 403 mangels ReBAC-Beziehungen; Iceberg ohne Tabellen | im PoC nicht prüfbar, Einrichtung fehlt | 4.3 |

| 4a.1 | Fehler | OData kürzt auf 100 (Standard) beziehungsweise 1000 Zeilen (`$top`-Grenze) ohne `@odata.nextLink` | Excel lädt still unvollständig | 4a.1 |
| 4a.2 | Abweichung | Pfad `lwetem_prod/md/crane` ≠ Mengenname `lwetem_prod_md_crane` (flacher Pfad 404) | Clients, die den Pfad aus dem Namen bilden, scheitern | 4a.2 |
| 4a.3 | Lücke | `$filter`, `$orderby`, `$count` geben 501 | Query Folding in Power Query schlägt fehl | 4a.3 |

| 4a.4 | Fehler | `$metadata`: bei 73 von 124 Typen zeigt der `Key` auf eine nicht vorhandene Eigenschaft `id` | Excel: „Metadatendokument des Feeds ist offenbar ungültig“; Feed nicht ladbar | 4a.4 |

| 4b.1 | Fehler | MCP `list_datasets` wird beim Token-Budget mitten im JSON abgeschnitten, ohne Seitenwechsel (25 von 124 Tabellen, `md.crane` fehlt) | KI-Agenten sehen den Katalog unvollständig und unlesbar | 4b.1 |
| 4b.2 | Fehler | MCP `query_data_catalog` scheitert immer (GraphQL-Feld `catalogAssets` fehlt) | Werkzeug angeboten, aber nicht nutzbar | 4b.2 |
| 4b.3 | Auffälligkeit | GraphQL über MCP: `first: $n` rechnet mit Kosten 110 000 (Budget 5 000), `first: 1000` mit 12 000 | parametrisierte Abfragen unbenutzbar | 4b.3 |

Nicht als Fehler gewertet: MCP ohne SSE (Absicht), Trino synchron (gültig), Parquet bei Prozeduren 406 (gewollt).

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

## 4. Transportformate (E2E gegen den laufenden Container, 08.10.2026)

Geprüft mit Python (`urllib`, `pyarrow` zum Dekodieren) als `david` (Zeilenfilter `is_delivered IS NULL`) und `admin`. Bei Python unter Windows `ProxyHandler({})` setzen, sonst läuft `localhost` über den Firmenproxy und die Aufrufe hängen.

| Weg | Ergebnis |
|---|---|
| **Parquet über OData** (`Accept: application/vnd.apache.parquet`) | Geht: `md/crane $top=5` in 0,1 s, `fms/air1 $top=1000` (1000 Zeilen, 30 kB). Spalten teils als `string` (z. B. `created_at`, `date_of_delivery` in `md.crane`) |
| **Parquet über WebSQL** (`POST /api/v1/sql`, `Accept` wie oben) | Geht und ist typisiert (`client_id: int64`, `ts: timestamp[us, tz=UTC]`, `COUNT(*)` als Spalte `n`). Der Zeilenfilter gilt: 7 235 Zeilen in `md.crane`, alle mit `is_delivered = NULL`. `?format=parquet` wird ignoriert (JSON), nur der Header zählt |
| **Parquet über Prozeduren** | 406 „This route cannot produce Apache Parquet“, laut Meldung gewollt (nur GraphQL, WebSQL, SQL-Endpunkte, OData) |
| **Parquet über GraphQL** | 406 bei einer Abfrage ohne genau ein Wurzelfeld (Abfrage `__typename`); mit Wurzelfeld nicht geprüft |
| **OData-Formate** `text/csv`, `application/x-ndjson`, Arrow | `Accept` wird ignoriert, Antwort ist immer OData-JSON (200). Das ist verträglich, aber eine Anfrage nach CSV bekommt kein CSV |
| **Arrow-Export** `POST /api/v1/export/arrow` | 403 „Access denied by ReBAC policy“ für `david` **und** `admin`; im Body-Fall „Target object identifier missing for ReBAC check“, weil die Route den Namen nur aus der Query (`?table=`) prüft (`RequireRebac(… RebacParameterSource.Query)`). Im PoC gibt es keine ReBAC-Beziehungen (`viewer` auf Tabelle), also bleibt der Export zu. Nicht geprüft, ob er nach dem Anlegen einer Beziehung Daten liefert |
| **OLAP (DuckDB)** `POST /api/v1/olap/query` | 403 „ReBAC Access Denied: Not authorized by relationship graph“, ebenfalls für `admin`. Gleiche Ursache |
| **Iceberg REST Catalog** (`/v1/{prefix}/namespaces…`) | Leere Listen (`namespaces: []`), Tabelle 403. Es sind keine Iceberg-Tabellen registriert; im Log steht je Aufruf „Iceberg table md.crane not found or inactive … answering as denied“ |
| **Flight SQL (HTTP)** `/api/v1/flight/sql/*` | `tables` leer; `info` meldet ein festes Schema `id:int, value:string` und `estimatedRowCount: 100`; `stream` liefert **immer** zwei Beispielzeilen (`Flight-Result-1/2`), egal welche Abfrage im Ticket steht |

### 4.1 Fehler: Flight SQL liefert Platzhalterdaten

- **Ort:** `src/Autheris.Application/Serialization/ArrowFlightSqlServer.cs:168-175` (`DoGetStreamAsync`): feste `sampleRows`, die Abfrage des Tickets wird weder ausgeführt noch geprüft. `GetFlightInfoAsync` meldet entsprechend ein festes Schema.
- **Folge:** Kein Datenabfluss (die Ticketprüfung auf Signatur, Person, Mandant und 30 Minuten Gültigkeit läuft davor), aber der Endpunkt ist nicht nutzbar und täuscht Funktion vor. Ein Client erhält mit jeder Abfrage dieselben Zeilen.
- **Entscheidung nötig:** Entweder an `IGovernedSqlExecutionService` anbinden (dann gelten Katalog, Zeilenfilter und Masken wie bei WebSQL) oder den Endpunkt bis dahin abschalten bzw. mit 501 antworten. Ohne Anbindung nicht als Funktion bewerben.
- **Test:** Ticket für `SELECT … FROM lwetem_prod.md.crane LIMIT 5` ergibt Arrow mit den Spalten und höchstens 5 Zeilen der Tabelle, nicht `id/value`; ein Ticket auf eine nicht freigegebene Tabelle wird abgelehnt.

### 4.2 Fehler: Parquet meldet das WebSQL-Zeilenlimit nicht

- **Beobachtung:** `SELECT id FROM lwetem_prod.tem.crane_state LIMIT 60000` (993 630 Zeilen in der Tabelle) als Parquet liefert 10 000 Zeilen mit `X-Export-Truncated: false` und ohne `X-Autheris-Truncated`. Als JSON steht in derselben Lage `truncated: true`.
- **Ursache:** `X-Export-Truncated` kommt aus `ParquetExportService.cs:99` (`rows.Count > effectiveMaxRows` mit `MaxRowsPerFile`, Standard 100 000) und betrifft nur diese Dateigrenze. Das WebSQL-Limit (`MaxAllowedRows`, hier 10 000) greift vorher in der Abfrage; die Information aus `GovernedSqlResult.Truncated` wird für Parquet nicht übernommen.
- **Folge:** Wer Parquet lädt, hält 10 000 Zeilen für das vollständige Ergebnis. Für Auswertungen ist das ein stiller Datenverlust.
- **Fix:** `Truncated` des Abfrageergebnisses in `ParquetExportRequest`/`ParquetExportResult` führen und `X-Export-Truncated` (und `X-Autheris-Truncated`) daraus setzen, wie es der JSON-Pfad tut.
- **Test:** WebSQL-Abfrage mit mehr Zeilen als `MaxAllowedRows` als Parquet: Header `X-Export-Truncated: true`.

### 4.3 Auffälligkeiten ohne Entscheidung

- **Typen in Parquet über OData:** Zeit- und Datumsspalten (`created_at`, `date_of_delivery`) kommen als `string`, über WebSQL als `timestamp`. Ob das von der OData-Schicht kommt, ist nicht geprüft.
- **Arrow-Export mit Body:** Die ReBAC-Prüfung liest den Tabellennamen nur aus der Query. Ein Aufruf nur mit Body (`{"table": …}`, wie das Schema `ArrowExportPayload` es zulässt) scheitert immer mit 403. Entweder die Prüfung aus dem Body speisen oder den Body-Fall entfernen.
- **ReBAC im PoC:** Für Arrow-Export und OLAP fehlen Beziehungen. Wenn diese Wege im PoC gebraucht werden, muss die Einrichtung (Beziehung `viewer` auf `table`) dokumentiert und im PoC angelegt werden.

## 4a. OData für Excel (08.10.2026)

Geprüft als `david` gegen `http://127.0.0.1:8080/odata/v4/`. Die Tabelle unten stammt aus HTTP-Aufrufen (curl, Python). **In Excel selbst** (Daten, Aus OData-Feed, URL `http://127.0.0.1:8080/odata/v4`, Anmeldung Basis) kommt: „OData: Das Metadatendokument des Feeds ist offenbar ungültig“ (Beobachtung des Nutzers, Ursache siehe 4a.4). Solange das nicht behoben ist, ist der Feed in Excel nicht ladbar, die Punkte 4a.1 bis 4a.3 sind in Excel daher bisher nicht erreichbar und nur per HTTP belegt.

| Prüfpunkt | Ergebnis |
|---|---|
| Service-Dokument `/odata/v4/` | 200, JSON mit `name` (`lwetem_prod_md_crane`) und `url` (`lwetem_prod/md/crane`) je Entitätsmenge |
| `$metadata` | 200, `application/xml`, `OData-Version: 4.0`, gültiges EDMX 4.0 (622 kB für alle Tabellen); `EntitySet` heißt `lwetem_prod_md_crane` |
| Abruf der Entitätsmenge | 200 unter `/odata/v4/lwetem_prod/md/crane`; `@odata.context` zeigt auf `…/$metadata#lwetem_prod_md_crane` |
| Abruf unter dem Namen der Menge `/odata/v4/lwetem_prod_md_crane` | **404** |
| Quelle als Wurzel `/odata/v4/lwetem_prod/` | 404 |
| Ohne `$top` | 100 Zeilen, **kein `@odata.nextLink`** |
| `$top=5000` und `$top=100000` | je 1000 Zeilen, kein `nextLink`, keine Meldung |
| `$select`, `$skip`, `$top` | gehen |
| `$filter`, `$orderby`, `$count` (auch `/$count`) | **501** NotImplemented |
| Anonym | 401 |

### 4a.1 Fehler: Seitenlimits ohne `nextLink` schneiden Ergebnisse still ab

- **Beobachtung:** Standardseite 100 Zeilen, Obergrenze 1000 Zeilen; beides ohne `@odata.nextLink`. Clients, die dem Link folgen (Excel/Power Query, `OData.Feed`), laden dadurch höchstens 100 beziehungsweise 1000 Zeilen und erfahren nicht, dass es mehr gibt (`md.crane` hat für `david` 7 235 Zeilen, `fms.air1` 254 749).
- **Standard:** OData 4.0 sieht für serverseitig gekürztes Paging `@odata.nextLink` vor.
- **Fix:** Wird gekürzt, `@odata.nextLink` mit `$skip` (oder `$skiptoken`) setzen. Alternativ bei `$top` über der Obergrenze mit 400 antworten statt still zu kürzen.
- **Test:** Entitätsmenge mit mehr Zeilen als der Standardseite: Antwort enthält `nextLink`; wer ihm folgt, erhält alle Zeilen.

### 4a.2 Abweichung vom Standard: Pfad entspricht nicht dem Namen der Entitätsmenge

- **Beobachtung:** Die Menge heißt in `$metadata` und im Service-Dokument `lwetem_prod_md_crane`, erreichbar ist sie aber nur unter `lwetem_prod/md/crane`. In OData 4.0 ist der erste Pfadsegment der Mengenname; Schrägstriche bedeuten Navigation. Das Feld `url` im Service-Dokument darf abweichen, ob Clients es nutzen, hängt vom Client ab.
- **Folge:** Clients, die den Pfad aus dem Mengennamen bilden, bekommen 404. Auch die direkte Eingabe der Tabellen-URL hilft in Excel nicht: `@odata.context` zeigt auf das Wurzel-`$metadata`, und genau dieses Dokument lehnt Excel ab (4a.4). Ob Excel nach Behebung von 4a.4 mit dem abweichenden Pfad zurechtkommt, ist offen.
- **Fix (Wahl):** Den Mengennamen als Pfad ebenfalls bedienen (`/odata/v4/lwetem_prod_md_crane`), mit dem langen Pfad als Alias.

### 4a.3 Nicht umgesetzt: `$filter`, `$orderby`, `$count`

- 501 ist nach der Spezifikation eine zulässige Antwort für nicht unterstützte Optionen, aber damit erreicht der Dienst nicht die „Minimal Conformance“ (verlangt `$top`, `$skip`, `$filter`, `$orderby`, `$select`, `$count`). Power Query schiebt Filter, die ein Benutzer im Editor setzt, per Query Folding als `$filter` an den Server und scheitert dann mit 501; ob und wann Excel selbst `$count` sendet, ist nicht geprüft.

### 4a.4 Fehler: `$metadata` ist für Excel ungültig (Schlüssel auf nicht vorhandene Eigenschaft)

- **Beobachtung:** Excel (Daten, Aus OData-Feed, URL `http://127.0.0.1:8080/odata/v4`) meldet „das Metadatendokument des Feeds ist offenbar ungültig“. Das XML ist wohlgeformt (`ET.fromstring` ohne Fehler), 622 kB, 124 Entitätstypen und Mengen, keine doppelten oder ungültigen Namen. **In 73 von 124 Typen verweist `<Key><PropertyRef Name="id"/></Key>` auf eine Eigenschaft `id`, die der Typ nicht hat** (z. B. `lwetem_prod_fms_air1`, `lwetem_prod_dm_dm1`, `lwetem_prod_conf_attribute`). Nach CSDL muss jede `PropertyRef` auf eine vorhandene Eigenschaft des Typs zeigen; Power Query prüft das streng.
- **Ursache:** `src/Autheris.Extensions/OData/ODataCsdlGenerator.cs:27` setzt den Schlüssel auf `["id"]`, wenn die Tabelle keinen Primärschlüssel hat. Die Eigenschaft `id` wird nur im Fall „Tabelle ohne jede Spalte“ erzeugt (Zeilen 82-86). Tabellen ohne Primärschlüssel, aber mit Spalten, erhalten also einen Schlüssel ins Leere. Im PoC fehlen die Primärschlüssel bei den meisten Telemetrie- und Diagnosetabellen.
- **Nicht bestätigt:** Dass Excel genau daran scheitert, habe ich nicht in Excel nachgewiesen (der Fehlertext nennt keine Stelle). Es ist die einzige gefundene Schema-Verletzung; ein zweiter möglicher Grund ist unten genannt.
- **Zweiter möglicher Grund:** Die Annotationen verwenden `Core.Description`/`Core.LongDescription` (1 210 Stück) ohne `edmx:Reference` auf das Core-Vokabular (`Org.OData.Core.V1`, Alias `Core`). Strenge Parser können den unbekannten Alias ablehnen. Behebung: vor `edmx:DataServices` einfügen:
  `<edmx:Reference Uri="https://oasis-tcs.github.io/odata-vocabularies/vocabularies/Org.OData.Core.V1.xml"><edmx:Include Namespace="Org.OData.Core.V1" Alias="Core" /></edmx:Reference>`
- **Fix:** Den Schlüssel nur auf vorhandene Eigenschaften setzen. Reihenfolge: Primärschlüssel aus dem Katalog (nur Spalten, die in `table.Columns` stehen), sonst eine vorhandene Spalte `id`, sonst alle Spalten als zusammengesetzter Schlüssel (mit `Nullable="false"`; nur Lesezugriff, deshalb unkritisch, aber die Eindeutigkeit ist nicht garantiert) oder eine synthetische Zeilennummer. Zusätzlich die `edmx:Reference` ergänzen.
- **Test:** Metadaten für Tabellen mit und ohne Primärschlüssel erzeugen und prüfen, dass jede `PropertyRef` auf eine `Property` desselben Typs zeigt; XML gegen das CSDL-Schema (XSD) oder mit einem OData-4-Parser (z. B. Microsoft.OData.Edm `CsdlReader.TryParse`) validieren.
- **Status:** **BEHOBEN** (Commit `a9fc97f`).
  1. `<edmx:Reference>` für `Org.OData.Core.V1` mit Alias `Core` im EDMX-Header ergänzt.
  2. EntityType-Keys werden in `ResolveEntityKeys` streng validiert:
     - Primärschlüssel aus dem Katalog nur, wenn die Spalte tatsächlich in `table.Columns` existiert.
     - Fallback auf vorhandene Spalte `id` (case-insensitiv).
     - Fallback auf alle vorhandenen Spalten als zusammengesetzter Schlüssel (alle mit `Nullable="false"`).
     - Fallback auf `["id"]` nur bei 0 deklarierten Spalten (wo auch `<Property Name="id" ... />` emittiert wird).
  3. Verifiziert durch Unit-Tests in `Autheris.Extensions.Tests/ODataTests.cs` (214/214 Tests grün).
- **Umgehung im PoC (nicht mehr erforderlich):** `AUTHERIS_OPEN_SCHEMA=false` setzen. Dann enthält `$metadata` nur die Tabellen, für die die Person eine Einwilligung hat (Kommentar `ODataEndpoints.cs:63`). Sind das nur Tabellen mit Primärschlüssel (z. B. `md.crane`), wäre das Dokument gültig. Das wirkt auf alle Schnittstellen (auch Katalog, Swagger, GraphQL-Schema) und ist nicht geprüft.

## 4b. MCP-Schnittstelle (E2E, 08.10.2026)

Geprüft per JSON-RPC (`POST /mcp`, Streamable HTTP, Protokoll 2025-03-26) als `david` und `admin`, 19 feste Prüfungen plus Einzelproben (Skripte im Scratchpad der Sitzung, nicht im Repo).

**Mit echtem Client (offizielles MCP-Python-SDK 2.3.0, `streamable_http_client`, Basic-Authentifizierung):** Verbindung, `initialize`, `list_tools`, `call_tool` (alle 7), `list_resources`, `read_resource`, `list_resource_templates` laufen. Der Client handelt Protokoll **2025-11-25** aus (der Server unterstützt also mehr als die 2025-03-26 der Handarbeitsprobe). Ergebnis deckt sich mit den Einzelproben:
- `david`: `list_datasets search=md.` liefert 8 Tabellen, `admin` 15 (die Liste folgt den Einwilligungen); `list_resources`: 1 210 Einträge für `david`, 1 637 für `admin`.
- `list_datasets` ohne Filter: abgeschnitten, ungültiges JSON, nur `_meta.truncated: true` (4b.1). `query_data_catalog`: `is_error: true` (4b.2). `describe_dataset` ohne Argument: `is_error: true` mit „Access denied … fail-closed“ (4b.4).
- `query_graphql`: `david` und `admin` erhalten verschiedene Zeilen (Zeilenfilter, bei `david` ohne ausgelieferte Krane).
- Ohne Anmeldung verweigert der Server die Verbindung (das SDK meldet das als `ExceptionGroup`, nicht als lesbare 401-Meldung).
- Das Skript liegt im PoC als `scripts/verify-mcp-client.py` (`pip install mcp`; die Datei darf nicht `mcp.py` heißen).
- Nicht geprüft: Claude Desktop, Cursor, MCP Inspector und OAuth-Anmeldung.

**Bestanden:**
- `initialize` (Server `Autheris.McpServer` 2.0.0; Fähigkeiten `logging`, `resources`, `tools`), `notifications/initialized` (202), `ping`, `tools/list` (7 Werkzeuge, kein `query_customers`), unbekannte Methode → JSON-RPC-Fehler -32601, `prompts/list` → -32601 (nicht deklariert).
- Anmeldung: ohne Anmeldung und mit falschem Passwort 401; `GET /mcp` 405 (`Allow: POST`); ohne `Accept: text/event-stream` 406; leerer Body 400.
- `describe_dataset` (Spalten, Typen, Beispielabfrage), unbekannte Tabelle → Fehler `NOT_FOUND`, kein Absturz.
- `sample_rows`: für `david` kein ausgelieferter Kran in 20 Zeilen, für `admin` 20 von 20 ausgeliefert: der Zeilenfilter greift. `count=500` wird still auf 20 begrenzt.
- `query_graphql`: Abfrage mit Filter und Sortierung, ungültiges Feld → Fehler, Mutation → abgelehnt („fail-closed“), Filter auf ausgelieferte Krane liefert für `david` 0 Zeilen.
- `simulate_query`, `get_golden_queries` (leer, Demodaten aus), `resources/list` (1 210 Einträge: Glossar und dbt-Spaltendokumentation), `resources/read` für `glossary://…` und `autheris://datasets/…`, `resources/templates/list`.

**Befunde:**

### 4b.1 Fehler: `list_datasets` schneidet mitten im JSON ab, ohne Weiterblättern

- **Beobachtung:** Die Antwort ist auf rund 4096 Token (16 424 Zeichen) begrenzt, endet mit `… [TRUNCATED DUE TO MCP TOKEN BUDGET]` mitten in einer Beschreibung und ist kein gültiges JSON mehr (`json.loads` scheitert). Ohne Filter sind **25 von 124 Tabellen** sichtbar (`conf.attribute` bis `fms.eec3_mot`, alphabetisch); `md.crane` fehlt. Auch `search=crane` (28 Treffer) und `domain=lwetem_prod` werden abgeschnitten. Nur ein enger Filter (`search=md.`, `search=air1`) liefert vollständige Antworten. Die Kennzeichnung steht nur in `_meta.truncated: true`; es gibt weder `offset`/Seitenwechsel noch einen Hinweis im Text auf weitere Tabellen.
- **Ursache:** Jede Tabelle bringt den kompletten Langtext (`description` mit Protobuf-Quelle, Go-Modell, Herkunft usw.) mit; das Token-Budget wird nach etwa 25 Tabellen erreicht.
- **Folge:** Ein KI-Agent, der wie in den Anweisungen des Servers mit `list_datasets` beginnt, sieht weniger als ein Fünftel der Tabellen und bekommt unlesbares JSON.
- **Fix:** In der Liste nur die Kurzbeschreibung (`Core.Description`) liefern, Langtext nur in `describe_dataset`; Seitenwechsel (`offset`, `limit`, `nextOffset`) ergänzen; beim Abschneiden gültiges JSON mit `truncated: true` und `total` liefern, statt Text abzuhacken.
- **Test:** Katalog mit mehr Tabellen als das Budget: Antwort ist gültiges JSON, `total` stimmt, mit `offset` sind alle Tabellen erreichbar.

### 4b.2 Fehler: `query_data_catalog` funktioniert nicht

- **Beobachtung:** Jeder Aufruf scheitert mit `EXECUTION_FAILED`: „The field `catalogAssets` does not exist on the type `Query`“ (außerdem „The following variables were no…“, vermutlich nicht verwendete Variablen).
- **Ursache:** Das Werkzeug setzt eine GraphQL-Abfrage auf ein Feld `catalogAssets` ab, das im Schema nicht vorhanden ist. Das Werkzeug wird trotzdem in `tools/list` angeboten.
- **Fix:** Entweder das Feld im Schema bereitstellen oder das Werkzeug erst anbieten, wenn es funktioniert (und vorher aus `tools/list` entfernen).
- **Test:** `query_data_catalog` mit `tableName=crane` liefert Katalogeinträge oder ist nicht in `tools/list`.

### 4b.3 Auffälligkeit: `first` über eine Variable ist praktisch unbenutzbar

- **Beobachtung:** `first: 100` (ein Feld) geht, `first: 1000` scheitert mit `QUERY_TOO_COMPLEX` (Kosten 12 000, Budget 5 000), und `first: $n` mit Variable rechnet mit Kosten 110 000, auch bei `n = 2`. Mit Variablen lässt sich `first` also gar nicht verwenden.
- **Folge:** Parametrisierte Abfragen (was MCP-Clients üblicherweise tun) scheitern; die Fehlermeldung nennt die Ursache (Variable) nicht.
- **Fix:** Bei einer Variable deren Wert (`variables`) für die Kostenberechnung verwenden, nicht den Höchstwert; Fehlertext mit dem Hinweis ergänzen.

### 4b.4 Kleinere Auffälligkeiten

- `describe_dataset` ohne Argument meldet „Access denied … (fail-closed)“ statt eines Parameterfehlers (-32602); das ist irreführend.
- JSON-RPC-Batches (Array im Body) werden mit 400 abgelehnt. Das Protokoll 2025-03-26 erlaubt Batches, spätere Fassungen nicht mehr; für aktuelle Clients unkritisch.
- `/.well-known/oauth-protected-resource` und `/.well-known/oauth-authorization-server` antworten ohne Anmeldung mit 401. MCP-Clients mit OAuth-Erkennung rufen diese Adressen anonym ab; im PoC gibt es nur Basic-Authentifizierung, daher nicht geprüft, ob sie bei aktivierter Entra-Anmeldung anonym erreichbar sind.
- Der PoC setzt `allow_all_cors_origins` (Header `X-Gateway-Insecure-Mode`); ein fremder `Origin` wird entsprechend zurückgespiegelt (`Access-Control-Allow-Origin`). Nur Entwicklungsbetrieb, aber der MCP-Endpunkt nimmt Basic-Anmeldedaten an; für einen Betrieb ist das zu schließen.
- Introspection (`__schema`) ist über `query_graphql` für `david` möglich (Entwicklungsbetrieb).
- `describe_dataset` zeigt für `david` und `admin` dieselben 11 Spalten von `md.crane`. Maskierung gesperrter Spalten ist im PoC nicht nötig und wurde nicht geprüft: MCP liefert dort nur statische Beispieldaten (`sample_rows`).

## 5. Nachprüfung im PoC

Nach jedem Fix: Image neu bauen oder ziehen, Container neu starten und `bash scripts/verify-autheris.sh` im Talos-PoC ausführen. Das Skript erwartet derzeit, dass `schema.tabelle` ohne Datenquelle mit 403 abgelehnt wird; nach 2.1 muss diese Prüfung auf Erfolg umgestellt werden.
