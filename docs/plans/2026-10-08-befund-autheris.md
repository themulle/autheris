# Spike „autheris lokal“: Befund vom 07./08.10.2026

**Stand:** autheris `main` 68bde5b, Image `ghcr.io/themulle/autheris` vom 07.10. 14:09 UTC, lokal als `localhost/autheris:main` getaggt. Robins lokales `:latest` (Stand 9f390a2, Image-ID 33a011f5fa9f) ist unverändert. Geprüft von Fable gegen Ergebnisse, Logs und Quellcode; Pfade unten relativ zum Repo von autheris.

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
| **GraphQL, typisierte Felder** (`finance_public_invoices(where, orderBy, first, offset)`) | Geht: Filter (`eq`, `gt`, `in`, `startsWith`, `and/or/not`), Sortierung, Offset; Zeilenfilter und Masken greifen. Filter und Sortierung auf nicht freigegebenen Spalten lehnt autheris ab (`INVALID_QUERY` „cannot be used for filtering, sorting or joining“, Gegenprobe mit `name`) | **Kostenlimit 250** je Anfrage in der Stufe „Standard“, das ist jede angemeldete Person ohne Claim `tier`. Kosten = `first × (10 + Felder + 3 × sensible Felder)` (`QueryCostAnalyzerRule.cs:276-286`): 22 Zeilen bei einem Feld, 9 Zeilen bei sieben Feldern mit drei sensiblen. `first` als Variable oder ohne `first` überschreitet das Komplexitätsbudget. Keine Aggregation. Die Stufe kommt nur aus den Claims `tier`, `client_tier` oder `urn:autheris:tier`; API-Keys lassen sich nicht registrieren (`RegisterApiKey` hat keinen Aufrufer). Die Grenzen je Stufe stehen fest im Code (`ClientTierModels.cs:21-24`) |
| **WebSQL** `POST /api/v1/sql` | Geht mit dem Standard-Rewriter (`LegacyTokenStream`): GROUP BY, CTE, Fensterfunktionen, Unterabfragen, `@param`, `EXTRACT`, `date_trunc` gegen Postgres, JOIN (belegt als Selbst-Join). Gegen SQL Server wird `LIMIT` übersetzt | Nur wenn die Datenquelle im Katalog **so heißt wie die Datenbank**: Der dreiteilige Name muss dem Namen der Datenquelle gleichen (`GovernedSqlExecutionService.cs:385-389`), und autheris reicht ihn unverändert an die Datenbank; eine Abbildung auf den Datenbanknamen gibt es nicht (`GatewayOptions.cs:890-903`). Mit den Namen der eigenen Demo (`governancedb`, `crmdb`) endete im ersten Lauf jede WebSQL-Abfrage mit 500 (Postgres: „cross-database references are not implemented“). Antwort ohne `truncated`; die Grenze ist immer `DefaultMaxRows`, ein `LIMIT 5000` liefert still 100 Zeilen |
| **WebSQL mit `SqlRewriterEngine=AstCompiler`** | Einfache SELECT gehen | 500 bei `COUNT(*)` („count(*) must be used…“), bei `@param` (nicht gebunden), bei `EXTRACT`/`COALESCE` (Typen) und bei deklarierten Abfragen. Gegen SQL Server: Abfrage mit Zeilenfilter als ungültig abgelehnt (400), `CAST … GROUP BY` mit Syntaxfehler (500). Für uns heute nicht nutzbar |
| **Deklarierte Abfragen** `/api/v1/queries/{name}` | Geht: `.sql` mit `@name`, `@datasource`, `@param name: typ[!][= wert]`; Zeilenfilter und Masken greifen; fehlende oder falsche Parameter → 400 mit klarer Meldung, unbekannte Abfrage → 404 | Antwort ist nur ein Array der Zeilen, ohne Spalten und ohne `truncated`. Registrierung nur als Datei im Verzeichnis von autheris |
| **Prozeduren** `/api/v1/procedures/{name}` | Geht: `{columns, rows, rowCount, truncated}`; mit `row_scope_key` filtert autheris das Ergebnis nach dem Zeilenfilter der Person (Gruppe Finance: Sales-Aufträge 0 Zeilen, Finance-Aufträge 1.250) | Masken im Speicher (`ColumnMaskingProvider`), in anderer Form als in SQL: `o***@***.local` statt `***` |
| **OData** `/odata/v4/{quelle}/{schema}/{tabelle}` | `$top` und `$select` gehen, Zeilenfilter und Masken greifen; verweigerte Spalten fehlen | `$filter` → 501; für Abfragen nicht brauchbar |
| **MCP** `POST /mcp` | `initialize`, `tools/list`, `resources/list` gehen mit Anmeldung; Ressourcen je Person (Glossar, dbt-Lineage) | Protokoll `2024-11-05`, kein Stream auf `POST /mcp`; kein Werkzeug führt SQL aus |
| **Entra-Bearer** | Geht mit unserem Mock: Issuer `{Instance}{Tenant}/v2.0`, Audience, Signatur über die Metadaten. `oid` wird SID, `tid` wird Mandant, `roles` werden Rollen. Eine Einwilligung für Anna (Zeilen „Sales“, `iban`/`email` maskiert) wirkt in GraphQL und WebSQL; Thomas ohne Einwilligung 403, ein kaputtes Token 401 | `scp` kommt im Quellcode nicht vor: Ein Token mit `api://autheris/Agent.Read` liefert dieselben Zeilen wie eines ohne (R13 gegen autheris nicht erfüllbar). App-Tokens ohne Person (Client-Credentials) gelten als angemeldet; ihre `roles` wirken wie Rollen von Personen (`Sid.cs:108-117`). Einwilligungen gelten je Mandant: Mit Entra müssen sie die Tenant-ID tragen; Basic-Konten liegen ohne eigene Angabe im Mandanten `legacy-single-tenant` |

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

## 3. Sicherheitsbefunde

1. **Eine ungültige Stufe in einer Spaltenregel wirkt nie als Deny (fail-open).**
   - autheris liest `access_level` an vier Stellen ohne Prüfung: `SqliteGovernanceRepository.Consent.cs:354` und `:471`, `PostgreSqlGovernanceRepository.Consent.cs:267` und der Cache `CachedConsentEnvelope.cs:56`.
   - **Einwilligungen mit Zeilenfilter:** Die Auflösung (`ConsentResolutionService.cs:175-196`) startet bei `Clear` und senkt nur auf kleinere Werte. Jeder Wert über 2 wird so zu einem expliziten Clear, auch für katalog-sensible Spalten. Belegt: `salary` (Maskierungsregel NULLIFY) kam im Klartext. Versuch mit der Regel für `salary`: 0 → `null`, 1 → maskiert, 2 → Klartext, **3 → Klartext**.
   - **Einwilligungen ohne Zeilenfilter:** Der Wert bleibt stehen. Katalog-sensible Spalten werden dann maskiert, alle anderen gehen als Rohwert durch, weil die Projektionen nur auf `Deny` und `Mask` prüfen.
   - Betrifft alle Lesewege (WebSQL, GraphQL, OData, Prozeduren), beobachtet in WebSQL und OData. Filter, Sortierung und JOIN auf solche Spalten werden abgewiesen.
   - Fix: beim Laden `Enum.IsDefined` prüfen und unbekannte Werte als `Deny` behandeln, auch im Cache; dazu ein Test.
2. **Der Demo-Seed nummeriert die Stufen umgekehrt.**
   - `seed_governance.py` sagt „Clear = 1, Mask = 2, Deny = 3“; autheris kennt `Deny = 0, Mask = 1, Clear = 2` (`ColumnAccessLevel.cs`).
   - Folge in der Demo: Die Gruppe Finance sieht `iban` (gemeint: maskiert) und `salary` (gemeint: verweigert) im Klartext, `email`, `name`, `amount` und `vendor` (gemeint: frei) maskiert.
   - Spalten ohne Regel (`id`, `department`, `status`, `created_at`) sind verweigert, weil autheris „keine Regel“ als Deny wertet, sobald eine Einwilligung überhaupt Spaltenregeln hat (`ConsentResolutionService.cs:200-213`). Der Seed-Kommentar „others -> Clear“ trifft also nicht zu.
   - Zusammen mit Befund 1 öffnet jedes „Deny“ des Seeds die Spalte.
3. **`scp` und Token-Art werden nicht geprüft:**
   - Agenten-Scope, Person und App sind für autheris gleich.
   - Es gibt schon einen `IdentitySubjectResolver`, der `idtyp=app` und App-Tokens erkennt (`IdentitySubjectResolver.cs:57-90`). Er ist registriert, wird aber nirgends aufgerufen.
4. **GraphQL zeigt den Katalog:**
   - Introspection ist standardmäßig nur in Development an. Über `GraphQL:EnableIntrospection` lässt sie sich aber in jeder Umgebung ohne Warnung einschalten (`GatewayServiceCollectionExtensions.cs:971-974`).
   - Das Schema entsteht aus allen aktiven Katalogtabellen, unabhängig von der Person (`CatalogSchemaModel.cs:69-73`). Ein Konto ohne jede Einwilligung sieht alle Tabellen und Spalten; ein App-Token auch.
   - Unbekannt (400 vom Validator) und verweigert (200 mit `ACCESS_DENIED`) bleiben in jeder Umgebung unterscheidbar.
   - `dev_fix_hints` mit den Schaltern `Insecure:*` gibt es nur in Development.
5. **Development-Modus:**
   - Das Startbanner druckt Klartext-Passwörter der Basic-Konten samt Login-Links (`DevStartupBanner.cs:60-66`). Mit gehashten Passwörtern (`$pbkdf2$`) erscheint `<hashed>`.
   - Umgebungsvariablen verschmelzen nach Index mit den Dev-Personas aus `appsettings.Development.json`. Ein Konto ohne eigene Rolle bekam so „DataOwner“, ein anderes den Mandanten `tenant-b`.
   - Die Erprobung im PoC läuft im Development-Modus.
6. **Filter auf nicht freigegebene Spalten laufen in WebSQL gegen den Ersatzwert.**
   - Der Ersatzwert ist `NULL` für Deny und NULLIFY, sonst `'***'`. In Postgres ist er immer Text.
   - Textvergleiche liefern still 0 Treffer; Vergleiche mit Zahl oder Datum enden mit 500. Das gilt gleich, ob die Spalte verweigert oder maskiert ist.
   - Die Ursache „text > integer“ ist aus dem Verhalten hergeleitet; in den Logs von autheris steht sie nicht.
   - Kein Leck, aber für den Agenten weder erkennbar noch von einem Datenbankfehler unterscheidbar.
   - Verweigerte Spalten erscheinen in WebSQL als vorhandene Spalte mit lauter `NULL`.
7. **Maskenform je Weg verschieden:** WebSQL und GraphQL setzen in SQL `'***'`, Prozeduren maskieren im Speicher und zeigen `o***@***.local`.

Gut:
- Zeilenfilter lassen sich nicht umgehen (`OR 1=1` bleibt bei 285 von 2.000).
- Ein Filter auf eine maskierte IBAN mit dem echten Wert findet nichts.
- JOIN auf maskierte Spalten wird abgewiesen.
- Ein Deny-Eintrag sperrt die ganze Tabelle (403 bzw. `ACCESS_DENIED`).

## 4. Folgen für den Datenadapter in Talos

- **`find_data`/`describe_data` und kleine Vorschauen:**
  - GraphQL mit Literal-`first`, so gewählt, dass `first × (10 + Felder + 3 × sensible Felder) ≤ 250`; Seiten über `offset`.
  - Eine höhere Stufe gibt es nur über einen Claim `tier`, den Entra nicht ohne Weiteres ausstellt. Der GraphQL-Weg bleibt für Talos praktisch bei 250.
- **`try_query` des Agenten und Abfragen der Apps:**
  - WebSQL mit dem Standard-Rewriter, Trino-SQL mit dreiteiligen Namen; Datenquelle im Katalog = Datenbankname.
  - `truncated` leitet der Adapter selbst ab (eine Zeile mehr anfragen). Das geht nur, solange seine Grenze unter `DefaultMaxRows` von autheris liegt; der Adapter muss diesen Wert kennen.
  - Verweigerte Spalten kommen als `NULL`; der Adapter gleicht die Spalten gegen `describe_data` ab, sonst ist „nicht freigegeben“ nicht von „leer“ zu unterscheiden.
- **Benannte Abfragen der Version:** Deklarierte Abfragen passen fachlich (typisierte Parameter, Zeilenfilter), aber nur als Datei im Verzeichnis von autheris und mit einer Antwort ohne Spalten. Bis es eine API gibt, schickt der Adapter das SQL der Abfrage-Datei über WebSQL.
- **R13 (Agent liest nur):** gegen autheris weiter nicht erfüllt. Der Adapter gibt das Token der Person nicht an autheris, wenn der Agent fragt, solange autheris den Scope nicht prüft. Introspection muss im Betrieb aus sein.
- **Fehler:** 400, 403 und GraphQL-Codes auf die festen Talos-Codes abbilden. Ein 500 bei Filtern ist heute nicht von Datenbankfehlern zu trennen (gleicher Text, nur `traceId`); erst nach Wunsch 8 abbildbar.
- **Mandant:** Einwilligungen gelten je `tid`; Basic- und Entra-Zugriffe sehen verschiedene Einwilligungen.

## 5. Liste für den Kollegen

| Nr | Wunsch | Beleg |
|---|---|---|
| 1 | Unbekannte `access_level`-Werte beim Laden ablehnen oder als `Deny` behandeln, an allen vier Stellen (auch `CachedConsentEnvelope`), mit Test | Befund 3.1 |
| 2 | `seed_governance.py` auf `Deny = 0, Mask = 1, Clear = 2` umstellen und für die gemeinten freien Spalten Clear-Regeln anlegen oder den Kommentar anpassen | Befund 3.2 |
| 3 | WebSQL: Namen der Datenquelle auf den Datenbanknamen abbilden oder vor der Ausführung entfernen; mit den Namen der Demo (`governancedb`, `crmdb`) scheitert WebSQL heute | Abschnitt 1 |
| 4 | AST-Rewriter: `COUNT(*)`, Parameterbindung, `EXTRACT`/`COALESCE`, deklarierte Abfragen, SQL Server mit Zeilenfilter und mit `CAST … GROUP BY` | Abschnitt 1 |
| 5 | `scp` auswerten (Scope für den Agenten, nur lesen) und App-Tokens ohne Person getrennt behandeln; der vorhandene `IdentitySubjectResolver` wäre der Ansatz | Abschnitt 1, Befund 3.3 |
| 6 | GraphQL-Kostenlimit konfigurierbar oder je Rolle; API-Keys registrierbar machen | Abschnitt 1 |
| 7 | WebSQL: `truncated` in der Antwort; explizites `LIMIT` bis `MaxAllowedRows` statt still auf `DefaultMaxRows` | Abschnitt 1 |
| 8 | Filter auf nicht freigegebene Spalten in WebSQL mit 403 und Meldung beantworten statt 500 oder 0 Treffern | Befund 3.6 |
| 9 | Schema je Person oder Introspection im Betrieb sperren (`GraphQL:EnableIntrospection` außerhalb von Development nur mit Warnung) | Befund 3.4 |
| 10 | Development-Modus: Ausgabe der Passwörter im Banner abschaltbar machen; Dev-Personas nicht mit eigenen Konten verschmelzen | Befund 3.5 |
| 11 | Eine Maskenform je Regel, gleich in allen Wegen | Befund 3.7 |
| 12 | Deklarierte Abfragen: Antwort mit `columns` und `truncated` wie Prozeduren; Registrierung per API | Abschnitt 1 |
| 13 | `deploy/sql/01-init-schema.sql` und `02-seed-data.sh` mit LF speichern (etwa `.gitattributes` mit `eol=lf`); mit CRLF bricht das Einlesen im Entrypoint von Postgres ab | Aufbau |
