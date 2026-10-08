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
| **WebSQL** `POST /api/v1/sql` | Geht mit dem Standard-Rewriter (`LegacyTokenStream`): GROUP BY, CTE, Fensterfunktionen, Unterabfragen, `@param`, `EXTRACT`, `date_trunc` gegen Postgres, JOIN (belegt als Selbst-Join). Gegen SQL Server wird `LIMIT` übersetzt | Der dreiteilige Name muss dem Namen der Datenquelle gleichen. Im Image ging er unverändert an die Datenbank; mit den Namen der eigenen Demo (`governancedb`, `crmdb`) endete jede Abfrage mit 500 (Postgres: „cross-database references are not implemented“). **Im Arbeitsverzeichnis (nicht committet)** entfernt autheris den Katalogteil jetzt vor der Ausführung (`SqlDialectGeneratorBase.FormatTableName`, `SqlIdentifierHelper.StripCatalogPrefix`); ohne Angabe wählt er die Datenquelle (`GovernedSqlExecutionService.cs:306`, `:430-441`). Eine Abbildung auf einen abweichenden Namen gibt es weiter nicht (`GatewayOptions.cs:890-903`). Antwort ohne `truncated`; die Grenze ist immer `DefaultMaxRows`, ein `LIMIT 5000` liefert still 100 Zeilen (`AstSecurityVisitor.cs:97-106`, `WebSqlEndpoints.cs:183-240`) |
| **WebSQL mit `SqlRewriterEngine=AstCompiler`** | Einfache SELECT gehen | 500 bei `COUNT(*)` („count(*) must be used…“), bei `@param` (nicht gebunden), bei `EXTRACT`/`COALESCE` (Typen) und bei deklarierten Abfragen. Gegen SQL Server: Abfrage mit Zeilenfilter als ungültig abgelehnt (400), `CAST … GROUP BY` mit Syntaxfehler (500). Für uns heute nicht nutzbar; Ursachen siehe Wunsch 4 |
| **Deklarierte Abfragen** `/api/v1/queries/{name}` | Geht: `.sql` mit `@name`, `@datasource`, `@param name: typ[!][= wert]`; Zeilenfilter und Masken greifen; fehlende oder falsche Parameter → 400 mit klarer Meldung, unbekannte Abfrage → 404 | Antwort ist nur ein Array der Zeilen, ohne Spalten und ohne `truncated` (`SqlEndpointRoutes.cs:351`). Registrierung nur als Datei im Verzeichnis von autheris (`SqlEndpointLoader`) |
| **Prozeduren** `/api/v1/procedures/{name}` | Geht: `{columns, rows, rowCount, truncated}`; mit `row_scope_key` filtert autheris das Ergebnis nach dem Zeilenfilter der Person (Gruppe Finance: Sales-Aufträge 0 Zeilen, Finance-Aufträge 1.250) | Masken im Speicher (`ColumnMaskingProvider`), in anderer Form als in SQL: `o***@***.local` statt `***` |
| **OData** `/odata/v4/{quelle}/{schema}/{tabelle}` | `$top` und `$select` gehen, Zeilenfilter und Masken greifen; verweigerte Spalten fehlen | `$filter` und `$orderby` → 501 (`ODataEndpoints.cs:364-384`); für Abfragen nicht brauchbar |
| **MCP** `POST /mcp` | **Seit dem Spike neu gebaut** (offizielles SDK, Streamable HTTP, zustandslos): Werkzeuge `query_graphql`, `list_datasets`, `describe_dataset`, `sample_rows`, `query_data_catalog`, `simulate_query`, `get_golden_queries`; Ressource `autheris://datasets/{dataset}`. Alle Aufrufe laufen über die Guardrails | Daten nur über GraphQL, also mit denselben Grenzen wie oben. Nicht im Spike erprobt |
| **Entra-Bearer** | Geht mit unserem Mock: Issuer `{Instance}{Tenant}/v2.0`, Audience, Signatur über die Metadaten. `oid` wird SID, `tid` wird Mandant, `roles` werden Rollen. Eine Einwilligung für Anna (Zeilen „Sales“, `iban`/`email` maskiert) wirkt in GraphQL und WebSQL; Thomas ohne Einwilligung 403, ein kaputtes Token 401 | `scp` kommt im Quellcode nicht vor: Ein Token mit `api://autheris/Agent.Read` liefert dieselben Zeilen wie eines ohne (R13 gegen autheris nicht erfüllbar). App-Tokens ohne Person (Client-Credentials) gelten als angemeldet; ihre `appid` wird zur SID der Person (`Sid.cs:22-36`), ihre `roles` wirken wie Rollen von Personen. Einwilligungen gelten je Mandant: Mit Entra müssen sie die Tenant-ID tragen; Basic-Konten liegen ohne eigene Angabe im Mandanten `legacy-single-tenant` |

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
   - **Einwilligungen mit Zeilenfilter:** Die Auflösung (`ConsentResolutionService.cs:187-195`) startet bei `Clear` und senkt nur auf kleinere Werte. Jeder Wert über 2 wird so zu einem expliziten Clear, auch für katalog-sensible Spalten. Belegt: `salary` (Maskierungsregel NULLIFY) kam im Klartext. Versuch mit der Regel für `salary`: 0 → `null`, 1 → maskiert, 2 → Klartext, **3 → Klartext**.
   - **Einwilligungen ohne Zeilenfilter:** Der Wert bleibt stehen (`:179`, `Max`). Katalog-sensible Spalten werden dann maskiert, alle anderen gehen als Rohwert durch, weil die Projektionen nur auf `Deny` und `Mask` prüfen (`GovernedConnectorReader.cs:165`, `:183`; `GovernedProcedureExecutionService.cs:534`, `:545`).
   - Betrifft alle Lesewege (WebSQL, GraphQL, OData, Prozeduren), beobachtet in WebSQL und OData. Filter, Sortierung und JOIN auf solche Spalten werden abgewiesen.
   - Fix: beim Laden `Enum.IsDefined` prüfen und unbekannte Werte als `Deny` behandeln, auch im Cache; dazu ein Test.
2. **Der Demo-Seed nummeriert die Stufen umgekehrt.**
   - `seed_governance.py:510` sagt „Clear = 1, Mask = 2, Deny = 3“; autheris kennt `Deny = 0, Mask = 1, Clear = 2` (`ColumnAccessLevel.cs`).
   - Folge in der Demo: Die Gruppe Finance sieht `iban` (gemeint: maskiert) und `salary` (gemeint: verweigert) im Klartext, `email`, `name`, `amount` und `vendor` (gemeint: frei) maskiert.
   - Spalten ohne Regel (`id`, `department`, `status`, `created_at`) sind verweigert, weil autheris „keine Regel“ als Deny wertet, sobald eine Einwilligung überhaupt Spaltenregeln hat (`ConsentResolutionService.cs:200-213`). Der Seed-Kommentar „others -> Clear“ (`:517`) trifft also nicht zu.
   - Zusammen mit Befund 1 öffnet jedes „Deny“ des Seeds die Spalte.
3. **`scp` und Token-Art werden nicht geprüft:**
   - Agenten-Scope, Person und App sind für autheris gleich.
   - Es gibt schon einen `IdentitySubjectResolver`, der `idtyp=app` und App-Tokens erkennt (`IdentitySubjectResolver.cs:57-62`). Er ist registriert (`GatewayServiceCollectionExtensions.cs:301`), wird aber nirgends aufgerufen.
   - Die `appid` eines App-Tokens wird zur SID (`Sid.cs:22-36`, `ClaimsNormalizer.cs:72-77`) und kann so Einwilligungen für Personen oder Service-Principals treffen (`ConsentResolutionService.cs:224-226`).
4. **GraphQL zeigt den Katalog:**
   - Introspection ist standardmäßig nur in Development an. Über `GraphQL:EnableIntrospection` lässt sie sich aber in jeder Umgebung ohne Warnung einschalten (`GatewayServiceCollectionExtensions.cs:989-993`). Die Sperre für den Betrieb (`:1285-1291`) prüft nur den Schalter `warn_enable_introspection`, nicht diesen.
   - Das Schema entsteht aus allen aktiven Katalogtabellen, unabhängig von der Person (`CatalogSchemaModel.cs:102-114`). Ein Konto ohne jede Einwilligung sieht alle Tabellen und Spalten; ein App-Token auch.
   - Außerhalb von Development beantwortet `GraphQlEnumerationShieldMiddleware` unbekannt und verweigert jetzt gleich (400 `INVALID_QUERY`). In Development bleiben sie unterscheidbar.
   - `dev_fix_hints` mit den Schaltern `Insecure:*` gibt es nur in Development.
5. **Development-Modus:**
   - Das Startbanner druckt Klartext-Passwörter der Basic-Konten samt Login-Links (`DevStartupBanner.cs:63-71`).
     - Abschaltbar ist es jetzt über `Gateway:Dev:Banner`; außerhalb von Development ist es aus (`DevOptions.cs:22`, `:82-84`).
     - In Development ist es aber standardmäßig an.
     - Nur Hashes mit `$pbkdf2$` erscheinen als `<hashed>`; andere Hashes, etwa `$argon2id$`, druckt es voll.
   - Umgebungsvariablen verschmelzen nach Index mit den Dev-Personas aus `appsettings.Development.json` (`Users[0..5]`, `:37-44`). Ein Konto ohne eigene Rolle bekam so „DataOwner“, ein anderes den Mandanten `tenant-b`.
   - Die Erprobung im PoC läuft im Development-Modus.
6. **Filter auf nicht freigegebene Spalten laufen in WebSQL gegen den Ersatzwert.**
   - Der Ersatzwert ist `NULL` für Deny und NULLIFY, sonst `'***'` (`GovernedSqlExecutionService.cs:540`, `:1511-1519`). In Postgres ist er immer Text.
   - Textvergleiche liefern still 0 Treffer; Vergleiche mit Zahl oder Datum enden mit 500. Das gilt gleich, ob die Spalte verweigert oder maskiert ist.
   - Die Ursache „text > integer“ ist aus dem Verhalten hergeleitet; in den Logs von autheris steht sie nicht.
   - Abgewiesen wird ein WHERE auf solche Spalten nur bei DML (`RejectMaskedColumnsInDml`). fee1544 betrifft nur JOIN-Bedingungen.
   - Kein Leck, aber für den Agenten weder erkennbar noch von einem Datenbankfehler unterscheidbar.
   - Verweigerte Spalten erscheinen in WebSQL als vorhandene Spalte mit lauter `NULL`.
7. **Maskenform je Weg verschieden:** WebSQL und GraphQL setzen in SQL `'***'`, Prozeduren maskieren im Speicher und zeigen `o***@***.local` (`ColumnMaskingProvider.cs:297-328`).

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
  - `truncated` leitet der Adapter selbst ab (eine Zeile mehr anfragen). Das geht nur, solange seine Grenze unter `DefaultMaxRows` von autheris liegt; der Adapter muss diesen Wert kennen.
  - Verweigerte Spalten kommen als `NULL`; der Adapter gleicht die Spalten gegen `describe_data` ab, sonst ist „nicht freigegeben“ nicht von „leer“ zu unterscheiden.
- **Benannte Abfragen der Version:** Deklarierte Abfragen passen fachlich (typisierte Parameter, Zeilenfilter), aber nur als Datei im Verzeichnis von autheris und mit einer Antwort ohne Spalten. Bis es eine API gibt, schickt der Adapter das SQL der Abfrage-Datei über WebSQL.
- **R13 (Agent liest nur):** gegen autheris weiter nicht erfüllt. Der Adapter gibt das Token der Person nicht an autheris, wenn der Agent fragt, solange autheris den Scope nicht prüft. Introspection muss im Betrieb aus sein.
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
| 1 | Unbekannte `access_level`-Werte beim Laden ablehnen oder als `Deny` behandeln, an allen vier Stellen (auch `CachedConsentEnvelope`), mit Test | Befund 3.1 | **Hoch**: fail-open für jede Spalte, schon durch einen falschen Seed ausgelöst | offen | klein |
| 5 | `scp` auswerten (Scope für den Agenten, nur lesen) und App-Tokens ohne Person getrennt behandeln; der vorhandene `IdentitySubjectResolver` wäre der Ansatz | Abschnitt 1, Befund 3.3 | **Hoch**: R13 nicht erfüllbar; die `appid` eines App-Tokens kann Einwilligungen von Personen treffen | offen | mittel |
| 2 | `seed_governance.py` auf `Deny = 0, Mask = 1, Clear = 2` umstellen und für die gemeinten freien Spalten Clear-Regeln anlegen oder den Kommentar anpassen | Befund 3.2 | **Mittel**: nur Demo, aber zusammen mit Wunsch 1 Klartext statt Deny | offen | klein |
| 9 | Schema je Person oder Introspection im Betrieb sperren (`GraphQL:EnableIntrospection` außerhalb von Development nur mit Warnung) | Befund 3.4 | **Mittel**: Katalog lesbar für jedes Konto | teilweise: Unterscheidbarkeit behoben, Schalter und Schema offen | klein (Schalter) bis groß (Schema je Person) |
| 8 | Filter auf nicht freigegebene Spalten in WebSQL mit 403 und Meldung beantworten statt 500 oder 0 Treffern | Befund 3.6 | **Mittel**: kein Leck, aber stille Falschergebnisse | offen | mittel |
| 7 | WebSQL: `truncated` in der Antwort; explizites `LIMIT` bis `MaxAllowedRows` statt still auf `DefaultMaxRows` | Abschnitt 1 | **Mittel**: stille Kürzung | offen | klein |
| 3 | WebSQL: Namen der Datenquelle auf den Datenbanknamen abbilden oder vor der Ausführung entfernen | Abschnitt 1 | **Mittel**: WebSQL mit abweichenden Namen unbrauchbar | **teilweise, nicht committet**: Katalogteil wird entfernt; eine Abbildung auf abweichende Namen fehlt weiter | klein (committen) |
| 4 | AST-Rewriter: siehe die Punkte unter der Tabelle | Abschnitt 1 | **Mittel**: nicht Standard, aber Ziel des laufenden Branches; fehlende Gruppierungen ändern Ergebnisse still | offen; `EXTRACT` und `CAST` teilweise | groß |
| 6 | GraphQL-Kostenlimit konfigurierbar oder je Rolle; API-Keys registrierbar machen | Abschnitt 1 | **Niedrig**: begrenzt Agenten auf kleine Seiten | offen | mittel |
| 12 | Deklarierte Abfragen: Antwort mit `columns` und `truncated` wie Prozeduren; Registrierung per API | Abschnitt 1 | **Niedrig** | offen | mittel |
| 11 | Eine Maskenform je Regel, gleich in allen Wegen | Befund 3.7 | **Niedrig** | offen | mittel |
| 10 | Development-Modus: Ausgabe der Passwörter im Banner standardmäßig aus und für alle Hash-Formate unterdrücken; Dev-Personas nicht mit eigenen Konten verschmelzen | Befund 3.5 | **Niedrig**: nur Development, aber der PoC läuft so | teilweise: Banner abschaltbar, außerhalb von Development aus | klein |
| 13 | `deploy/sql/01-init-schema.sql` und `02-seed-data.sh` mit LF speichern (etwa `.gitattributes` mit `eol=lf`); mit CRLF bricht das Einlesen im Entrypoint von Postgres ab | Aufbau | **Niedrig**: blockiert aber den Aufbau der Demo | offen (beide Dateien `i/crlf`, keine `.gitattributes`) | klein |

**Wunsch 4 im Einzelnen:**
- **`COUNT(*)`:** wird zu einem leeren Aufruf (`SqlAstBuilder.cs:813` wird nie erreicht). Tests schreiben die falsche Ausgabe fest (`ComplexTrinoBenchmarkQueriesTests.cs:51`, `:63`).
- **Funktionsnamen:** werden gequotet (`SqlDialectGeneratorBase.cs:496`), also `[COUNT]` bzw. `"coalesce"`. Daran scheitert auch `COALESCE`.
- **Parameter:** `@param` wird positional ausgegeben, findet aber die gebundenen Namen nicht wieder (`RestoreClientParameters`).
- **`EXTRACT`:** geht nur als ANSI; für SQL Server und SQLite fehlt die Übersetzung.
- **Gruppierungen:** `ROLLUP`, `CUBE` und `GROUPING SETS` fallen still weg (`SqlAstBuilder.cs:351-366`).
- **Ohne Fix und ohne Test:** SQL Server mit Zeilenfilter, `CAST … GROUP BY` und deklarierte Abfragen.
