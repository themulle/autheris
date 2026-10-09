# Spike „autheris lokal“: Befund vom 07./08.10.2026

**Stand:** autheris `main` 68bde5b, Image `ghcr.io/themulle/autheris` vom 07.10. 14:09 UTC, lokal als `localhost/autheris:main` getaggt. Robins lokales `:latest` (Stand 9f390a2, Image-ID 33a011f5fa9f) ist unverändert. Geprüft von Fable gegen Ergebnisse, Logs und Quellcode; Pfade unten relativ zum Repo von autheris.

**Nachprüfung 08.10.2026:**
- **Gegen welchen Stand:** `feat/ast-target-dialect-generator` (3ce4188) samt den noch nicht committeten Änderungen im Arbeitsverzeichnis.
- **Wie geprüft:** Quellcode und vorhandene Tests gelesen; nichts ausgeführt.
- **Entfernt, weil überholt:**
  - die Grenzen von MCP (Protokoll `2024-11-05`, kein Werkzeug für Abfragen)
  - die Unterscheidbarkeit von „unbekannt“ und „verweigert“ in GraphQL außerhalb von Development
- **Zeilennummern:** auf den geprüften Stand aktualisiert.
- **Bewertung je Wunsch:** in Abschnitt 5.

**Umgebung:** nur lokal, nur an 127.0.0.1, ohne echte Daten.
- **Demo-Datenbanken aus dem autheris-Repo:**
  - Postgres `finance` (2.000 Rechnungen, 500 Positionen)
  - SQL Server 2022 `crm` (5.000 Aufträge)
  - Umbenannt von `governancedb`/`crmdb`, siehe Abschnitt 1.
- **Governance-Seed des Kollegen** in SQLite (`deploy/containers/governance-seed`). Für die Prüfungen auf Masken und Zeilenfilter sind die Stufen im Seed auf die Aufzählung von autheris umgestellt (Befund 3.2).
- **Anmeldung:**
  - Basic mit sechs Spike-Konten (Gruppe Finance, Prüfer, Manager, gesperrt, ohne Einwilligung, Admin)
  - Entra-Bearer aus unserem Entra-Mock (`Instance` auf `https://entra-mock:8443/`)
- **Skripte** im selben Ordner: `up.sh`, `down.sh`, `probe.py`, `client.py`, `sqltry.py`, `bearer.py`. Ergebnisse in `results-*.json` und `run-*.txt`.

## 1. Was der Adapter heute nutzen kann

| Weg | Ergebnis | Grenzen und Auffälligkeiten |
|---|---|---|
| **GraphQL, typisierte Felder** (`finance_public_invoices(where, orderBy, first, offset)`) | Geht: Filter (`eq`, `gt`, `in`, `startsWith`, `and/or/not`), Sortierung, Offset; Zeilenfilter und Masken greifen. Filter und Sortierung auf nicht freigegebenen Spalten lehnt autheris ab (`INVALID_QUERY` „cannot be used for filtering, sorting or joining“, Gegenprobe mit `name`) | **Kostenlimit 250** je Anfrage in der Stufe „Standard“, das ist jede angemeldete Person ohne Claim `tier`. Kosten ≈ `first × (10 + Felder + 3 × sensible Felder)` (`QueryCostAnalyzerRule.cs:247-297`, `:364-377`): 22 Zeilen bei einem Feld, 9 Zeilen bei sieben Feldern mit drei sensiblen. `first` als Variable oder ohne `first` rechnet mit 1.000 Zeilen und überschreitet das Budget. Keine Aggregation. Die Stufe kommt nur aus den Claims `tier`, `client_tier` oder `urn:autheris:tier` (`ClientTierResolver.cs:58-60`); API-Keys lassen sich nicht registrieren (`RegisterApiKey` hat keinen Aufrufer). Die Grenzen je Stufe stehen fest im Code (`ClientTierModels.cs:19-26`) |
| **WebSQL** `POST /api/v1/sql` | Geht mit dem Standard-Rewriter (`LegacyTokenStream`): GROUP BY, CTE, Fensterfunktionen, Unterabfragen, `@param`, `EXTRACT`, `date_trunc` gegen Postgres, JOIN. Gegen SQL Server wird `LIMIT` übersetzt. Dreiteilige Namen werden sicher aufgelöst (Katalogteil vor Backend-Ausführung entfernt), `truncated` und explizites `LIMIT` bis `MaxAllowedRows` werden unterstützt | Zieldialekt-Konvertierung noch nicht für alle Funktionen/Dialekte |
| **WebSQL mit `SqlRewriterEngine=AstCompiler`** | Einfache SELECT gehen | 500 bei `COUNT(*)` („count(*) must be used…“), bei `@param` (nicht gebunden), bei `EXTRACT`/`COALESCE` (Typen) und bei deklarierten Abfragen. Gegen SQL Server: Abfrage mit Zeilenfilter als ungültig abgelehnt (400), `CAST … GROUP BY` mit Syntaxfehler (500). Für uns heute nicht nutzbar; Ursachen siehe Wunsch 4 |
| **Deklarierte Abfragen** `/api/v1/queries/{name}` | Geht: `.sql` mit `@name`, `@datasource`, `@param name: typ[!][= wert]`; Zeilenfilter und Masken greifen; standardisierte Antwort mit `{ columns, rows, rowCount, truncated }` und `X-Autheris-Truncated`-Header | Registrierung nur als Datei im Verzeichnis von autheris (`SqlEndpointLoader`) |
| **Prozeduren** `/api/v1/procedures/{name}` | Geht: `{columns, rows, rowCount, truncated}`; mit `row_scope_key` filtert autheris das Ergebnis nach dem Zeilenfilter der Person (Gruppe Finance: Sales-Aufträge 0 Zeilen, Finance-Aufträge 1.250) | Masken im Speicher (`ColumnMaskingProvider`), in anderer Form als in SQL: `o***@***.local` statt `***` |
| **OData** `/odata/v4/{quelle}/{schema}/{tabelle}` | `$top` und `$select` gehen, Zeilenfilter und Masken greifen; verweigerte Spalten fehlen | `$filter` und `$orderby` → 501 (`ODataEndpoints.cs:364-384`); für Abfragen nicht brauchbar |
| **MCP** `POST /mcp` | **Seit dem Spike neu gebaut** (offizielles SDK, Streamable HTTP, zustandslos): Werkzeuge `query_graphql`, `list_datasets`, `describe_dataset`, `sample_rows`, `query_data_catalog`, `simulate_query`, `get_golden_queries`; Ressource `autheris://datasets/{dataset}`. Alle Aufrufe laufen über die Guardrails | Daten nur über GraphQL, also mit denselben Grenzen wie oben. Nicht im Spike erprobt |
| **Entra-Bearer** | Geht: Issuer `{Instance}{Tenant}/v2.0`, Audience, Signatur über die Metadaten. `oid` wird SID, `tid` wird Mandant, `roles` werden Rollen. Scopes (`scp`) und App-Tokens werden per `ReadOnlyTokenMiddleware` und `EntraTokenPolicy` strikt durchgesetzt (R13 erfüllt: reine Lese-Scopes verweigern DML/Mutationen) | Basiskonten und Entra-Zugriffe verwalten getrennte Mandanten |

## 2. Dialekt und abgelehnte SQL-Formen

- **Sprache:** Abfragen schreibt man in Trino-/ANSI-SQL (`LIMIT`, dreiteilige Namen `quelle.schema.tabelle`). Den Zieldialekt wählt die Datenquelle. Gegen SQL Server wird `LIMIT` übersetzt, Funktionen nicht.
- **Abgelehnt mit 400:** T-SQL `TOP`, eckige Klammern, Kommentare (`--`), zwei Anweisungen.
- **Abgelehnt mit 403:**
  - DML (`UPDATE`) und DDL (`DROP`)
  - Tabellen ohne Katalogeintrag, auch `information_schema` und `pg_catalog`
  - Namen ohne Quelle (`invoices`, `public.invoices`)
  - Abfragen ohne Katalogtabelle (`SELECT GETDATE()`, `SELECT pg_sleep(1)`)
  - `date_trunc` gegen SQL Server
  - Verknüpfung über zwei Quellen
  - JOIN auf eine maskierte Spalte
- Unbekannt und verweigert sehen in WebSQL gleich aus („is denied or the table is not registered“).

## 3. Offene Befunde

1. **GraphQL zeigt den gesamten Katalog (Schema je Person):**
   - Das Schema entsteht aus allen aktiven Katalogtabellen, unabhängig von der Person (`CatalogSchemaModel.cs:102-114`). Ein Konto ohne jede Einwilligung sieht alle Tabellen und Spalten; ein App-Token auch.
   - Außerhalb von Development beantwortet `GraphQlEnumerationShieldMiddleware` unbekannt und verweigert jetzt gleich (400 `INVALID_QUERY`). Der Introspection-Schalter `GraphQL:EnableIntrospection` ist im Betrieb gesperrt (Opt-In erforderlich).
   - Die Einschränkung des sichtbaren Schemas je Person/Rolle bleibt offen (Wunsch 9).

2. **Filter auf nicht freigegebene Spalten laufen in WebSQL gegen den Ersatzwert (Wunsch 8):**
   - Der Ersatzwert ist `NULL` für Deny und NULLIFY, sonst `'***'` (`GovernedSqlExecutionService.cs:540`, `:1511-1519`). In Postgres ist er immer Text.
   - Textvergleiche liefern still 0 Treffer; Vergleiche mit Zahl oder Datum enden mit 500. Das gilt gleich, ob die Spalte verweigert oder maskiert ist.
   - Abgewiesen wird ein WHERE auf solche Spalten nur bei DML (`RejectMaskedColumnsInDml`).
   - Kein Leck, aber für den Agenten weder erkennbar noch von einem Datenbankfehler unterscheidbar. Ziel: 403 Forbidden mit klarer Meldung.

3. **Maskenform je Weg verschieden (Wunsch 11):**
   - WebSQL und GraphQL setzen in SQL `'***'`, Prozeduren maskieren im Speicher und zeigen `o***@***.local` (`ColumnMaskingProvider.cs:297-328`). Ziel: einheitliche Maskierungsausdrücke.

Gut:
- Zeilenfilter lassen sich nicht umgehen (`OR 1=1` bleibt bei 285 von 2.000).
- Ein Filter auf eine maskierte IBAN mit dem echten Wert findet nichts.
- JOIN auf maskierte Spalten wird abgewiesen.
- Ein Deny-Eintrag sperrt die ganze Tabelle (403 bzw. `ACCESS_DENIED`).
- Außerhalb von Development lässt GraphQL unbekannte und verweigerte Felder nicht unterscheiden.

## 4. Folgen für den Datenadapter in Talos

- **`find_data`/`describe_data` und kleine Vorschauen:**
  - GraphQL mit Literal-`first`, so gewählt, dass `first × (10 + Felder + 3 × sensible Felder) ≤ 250`; Seiten über `offset`.
  - Eine höhere Stufe gibt es nur über einen Claim `tier`, den Entra nicht ohne Weiteres ausstellt. Der GraphQL-Weg bleibt für Talos praktisch bei 250.
  - Alternativ die MCP-Werkzeuge `list_datasets`/`describe_dataset`; sie sind neu und noch nicht gegen Talos erprobt.
- **`try_query` des Agenten und Abfragen der Apps:**
  - WebSQL mit dem Standard-Rewriter, Trino-SQL mit dreiteiligen Namen; Datenquelle im Katalog = Name im ersten Namensteil.
  - `truncated` wird im JSON-Result und im `X-Autheris-Truncated`-Header geliefert; explizite `LIMIT` bis `MaxAllowedRows` werden respektiert.
  - Verweigerte Spalten kommen als `NULL`; der Adapter gleicht die Spalten gegen `describe_data` ab, sonst ist „nicht freigegeben“ nicht von „leer“ zu unterscheiden.
- **Benannte Abfragen der Version:** Deklarierte Abfragen bieten typisierte Parameter, Zeilenfilter und standardisierte Metadaten (`columns`, `rows`, `rowCount`, `truncated`).
- **R13 (Agent liest nur):** Erfüllt über `ReadOnlyTokenMiddleware` und `EntraTokenPolicy` (reine Lese-Scopes wie `Agent.Read` verweigern DML und Mutationen).
- **Fehler:** 400, 403 und GraphQL-Codes auf die festen Talos-Codes abbilden. Ein 500 bei Filtern ist heute nicht von Datenbankfehlern zu trennen (gleicher Text, nur `traceId`); erst nach Wunsch 8 abbildbar.
- **Mandant:** Einwilligungen gelten je `tid`; Basic- und Entra-Zugriffe sehen verschiedene Einwilligungen.

## 5. Liste für den Kollegen

**Bewertung:**
- **Schwere:**
  - **Hoch:** Daten werden entgegen der Regel sichtbar, oder ein Sicherheitsziel ist nicht erreichbar.
  - **Mittel:** Ein Weg ist falsch, still unvollständig oder für Agenten nicht unterscheidbar.
  - **Niedrig:** Komfort, Einheitlichkeit oder nur Development betroffen.
- **Status:** gegen den Stand der Nachprüfung.

Reihenfolge nach Schwere; die Nummern bleiben, weil Abschnitt 4 auf sie verweist.

| Nr | Wunsch | Beleg | Schwere | Status | Aufwand |
|---|---|---|---|---|---|
| 9 | Schema je Person (Katalog für unberechtigte Konten einschränken) | Befund 3.4 | **Mittel**: Katalog lesbar für jedes Konto | **behoben**: Introspection/SDL im Betrieb aus (nur mit Opt-In); GraphQL `catalog`, OData `$metadata`, MCP filtern je Person; die letzten ungefilterten Listen (Iceberg-REST `namespaces`/`tables`, Flight SQL `tables`) nutzen jetzt dieselbe Sichtbarkeit (`CatalogVisibility.VisibleTablesAsync`), Iceberg antwortet für unbekannte Tabellen wie für verweigerte (403). Ein eigenes GraphQL-Schema je Person gibt es nicht; mit gesperrter Introspection und gleicher Antwort für unbekannt/verweigert ist es nicht nötig | mittel |
| 8 | Filter auf nicht freigegebene Spalten in WebSQL mit 403 und Meldung beantworten statt 500 oder 0 Treffern | Befund 3.6 | **Mittel**: kein Leck, aber stille Falschergebnisse | behoben: WebSqlPolicyException (HTTP 403) bei WHERE/HAVING-Filtern und ORDER BY auf maskierten oder verbotenen Spalten (SEC-FILTER-01), auch in Unterabfragen; HMAC-Spalten ausgenommen | mittel |
| 4 | AST-Rewriter: siehe die Punkte unter der Tabelle | Abschnitt 1 | **Mittel**: nicht Standard, aber Ziel des laufenden Branches; fehlende Gruppierungen ändern Ergebnisse still | **behoben** (66a219f…bafab16, Plan [Wunsch 4](2026-10-08-umsetzungsplan-wunsch-4-ast-rewriter.md)): alle Punkte unten; nicht abbildbare Konstrukte werden mit 400 abgelehnt statt still verändert | groß |
| 6 | GraphQL-Kostenlimit konfigurierbar oder je Rolle; API-Keys registrierbar machen | Abschnitt 1 | **Niedrig**: begrenzt Agenten auf kleine Seiten | behoben: konfigurierbare TierLimits, RoleTierMappings und ApiKeys in ClientTierOptions / ClientTierResolver | mittel |
| 11 | Eine Maskenform je Regel, gleich in allen Wegen | Befund 3.7 | **Niedrig** | behoben: dialektspezifische SQL-Maskierungsausdrücke für MASK_EMAIL und MASK_IBAN in GovernedSqlExecutionService | mittel |

**Wunsch 4 im Einzelnen** (alle behoben, Commits in Klammern):
- **`COUNT(*)`** (12bd022): wird zu einem leeren Aufruf (`SqlAstBuilder.cs:813` wird nie erreicht). Tests schreiben die falsche Ausgabe fest (`ComplexTrinoBenchmarkQueriesTests.cs:51`, `:63`).
- **Funktionsnamen** (12bd022): werden gequotet (`SqlDialectGeneratorBase.cs:496`), also `[COUNT]` bzw. `"coalesce"`. Daran scheitert auch `COALESCE`.
- **Parameter** (0195090, gilt auch für deklarierte Abfragen): `@param` wird positional ausgegeben, findet aber die gebundenen Namen nicht wieder (`RestoreClientParameters`).
- **`EXTRACT`** (b6a6786; dabei auch `DOW` in PostgreSQL korrigiert, das Sonntag = 0 zählte): geht nur als ANSI; für SQL Server und SQLite fehlt die Übersetzung.
- **Gruppierungen** (1b5c8b5; SQLite lehnt sie mit 400 ab): `ROLLUP`, `CUBE` und `GROUPING SETS` fallen still weg (`SqlAstBuilder.cs:351-366`).
- **SQL Server mit Zeilenfilter** (48d29d5), **`CAST … GROUP BY`** (12bd022), **deklarierte Abfragen** (0195090); vorher: SQL Server mit Zeilenfilter, `CAST … GROUP BY` und deklarierte Abfragen.
