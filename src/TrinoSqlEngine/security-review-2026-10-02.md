# Security Review – gql_sqlparser (FastSqlEngine / RLS-Rewriter), Stand 2026-10-02

**Scope:** `gql_sqlparser` im aktuellen Stand des Branches `security/review-2026-10-02`, inklusive der Fixes aus dem Gateway-Review (C-01, C-02, C-06, H-14, H-15, M-22, M-23, M-24) und der DML-Leitplanken. Betrachtet wurden: `SqlBase.g4` (Lexer und Parser), `RlsListener.cs`, `FastSqlEngine.cs`, `Analysis/*`, `SqlFunctionPolicy.cs`, `IRlsPolicyProvider.cs`, `ZeroCopyCaseInsensitiveStream.cs`, `SharedParserCache.cs` und `ParserPooledObjectPolicy.cs`. Tests und Fixtures nicht.
**Kontext:** Ob ein Befund ausnutzbar ist, ergibt sich aus der Nutzung in `GqlGateway.Application/Sql/Services/GovernedSqlExecutionService.cs` (WebSQL `/api/sql` und SQL-Endpoints).
**Methode:** Statische Analyse, Datenfluss von der Eingabe über Lexer, Parser, Analyzer, Listener und Rewrite bis zur Ziel-DB. Keine Laufzeit-PoCs. Bei Lexer-Differentials wird nur der Mechanismus beschrieben, fertige Payloads stehen bewusst nicht im Dokument.

---

## 1. Kernaussage

Parser-intern ist das Modul inzwischen robust:

- Parse-Fehler werden fail-closed behandelt.
- Tiefenlimits und eine iterative Prüfung der Baumtiefe sind vorhanden.
- Das Pooling setzt den Zustand korrekt zurück.
- Erfasste Tabellen bekommen konsequent RLS-Subqueries.
- Funktions-, Table-Function- und DML-Leitplanken greifen.

**Das zentrale Restrisiko ist architektonisch:** Grammatik und Lexer bilden den **Trino-Dialekt** ab. Das umgeschriebene SQL wird aber Token für Token (inkl. Kommentaren) unverändert an **PostgreSQL, SQL Server oder SQLite** geschickt. Wo diese Dialekte Strings, Kommentare oder Ausdrücke anders tokenisieren, kann die Ziel-DB SQL ausführen, das Analyzer, Listener und Policy nie gesehen haben. Damit fallen RLS, Masking und Consent vollständig weg (SQ-01, SQ-02).

| Schweregrad | Anzahl |
|---|---|
| 🔴 Kritisch | 2 |
| 🟠 Hoch | 3 |
| 🟡 Mittel | 4 |
| 🟢 Niedrig / Info | 7 |

---

## 2. Befunde

### SQ-01 🔴 Lexer-Differential bei String-Literalen (PostgreSQL `E'…'` / Backslash-Escapes)
`SqlBase.g4:1476-1478` (STRING), `:726` (`identifier string #typeConstructor`)
```antlr
STRING : '\'' ( ~'\'' | '\'\'' )* '\'' ;
| identifier string     #typeConstructor
```
- **Ursache:** Der Trino-Lexer kennt nur `''` als Escape. PostgreSQL wertet in `E'…'`-Strings (und bei `standard_conforming_strings=off` in allen Strings) Backslash-Escapes aus. Der Parser akzeptiert `E'…'` syntaktisch als `typeConstructor` (Bezeichner + String).
- **Folge:** Gateway und PostgreSQL sehen das String-Ende an unterschiedlichen Stellen. Text, den das Gateway für String-Inhalt hält, führt PostgreSQL als SQL aus, inkl. Tabellenzugriffen ohne RLS, gesperrter Funktionen oder weiterer Klauseln. `RewriteRls` gibt die Original-Tokentexte über `TokenStreamRewriter.GetText()` aus, der Inhalt geht also unverändert an die DB.
- **Betroffen:** PostgreSQL. SQL Server und SQLite haben keine Backslash-Escapes.
- **Fix:**
  - **Sofort:** Token-Allowlist vor dem Parsen (dialektabhängig): `typeConstructor` mit Bezeichner `E`/`e` sowie Backslashes in STRING-Tokens ablehnen, solange `standard_conforming_strings=on` nicht garantiert ist. Zusätzlich die DB-Session mit `SET standard_conforming_strings = on` erzwingen.
  - **Mittelfristig:** SQL aus dem AST dialektgerecht neu erzeugen (Literale neu quoten), statt Original-Tokens durchzureichen (siehe SQ-02).

### SQ-02 🔴 Lexer-Differential bei Block-Kommentaren (verschachtelt) und Kommentar-Durchreichung
`SqlBase.g4:1560-1562`
```antlr
BRACKETED_COMMENT : '/*' .*? '*/' -> channel(HIDDEN) ;
```
- **Ursache:** Der Trino-Lexer beendet einen Kommentar beim ersten `*/`. PostgreSQL und SQL Server **verschachteln** Block-Kommentare. Innerhalb eines Kommentars ignoriert die DB Quotes. Zusätzlich werden HIDDEN-Tokens (Kommentare) per `GetText()` an die DB durchgereicht.
- **Folge:** Mit verschachtelten Kommentaren und Quotes lassen sich Bereiche bauen, die das Gateway als Literal oder Kommentar liest, die DB aber als Code ausführt (oder umgekehrt). Auch ein vom Rewriter eingefügter Filter kann in der DB in einem Kommentar landen. Die Wirkung entspricht SQ-01.
- **Betroffen:** PostgreSQL und SQL Server. SQLite verschachtelt nicht.
- **Fix:**
  - Kommentare in WebSQL grundsätzlich ablehnen (`SIMPLE_COMMENT` und `BRACKETED_COMMENT` → Fehler) oder sie zumindest nicht an die DB ausgeben (nur Default-Channel-Tokens rendern).
  - `/*` innerhalb eines Kommentars ablehnen.
  - Für SQL Server zusätzlich `DOLLAR_STRING` (`$$…$$`) ablehnen. T-SQL kennt kein Dollar-Quoting, der Inhalt wäre für die DB kein String (nicht verifiziert, deshalb fail-closed ablehnen).
  - Für PostgreSQL `$tag$…$tag$` ist durch `UNRECOGNIZED` → Parse-Fehler bereits fail-closed.
- **Empfehlung (deckt SQ-01, SQ-02 und künftige Differentials ab):** Nach dem Rewrite das Ergebnis mit dem Parser der Ziel-DB bzw. einem Dialekt-Lexer gegenprüfen (gleiche Tabellenmenge, keine Kommentare, gleiche Literal-Anzahl). Alternativ das SQL aus dem AST generieren.

### SQ-03 🟠 H-15 umgehbar: Whole-Row-Referenz in UPDATE/DELETE (PostgreSQL)
`RlsListener.cs:831-845`
```csharp
SqlBaseParser.ColumnReferenceContext columnRef => columnRef.identifier()?.GetText(),
...
if (column.Length > 0 && _options.ColumnMaskingProvider.HasMask(normalizedTableName, column))
```
- **Ursache:** Die DML-Maskenprüfung vergleicht nur Spaltennamen. In PostgreSQL ist der bloße Tabellen- oder Aliasname in einem Ausdruck eine **Whole-Row-Referenz** mit allen Spalten. Bei DML wird die Zieltabelle, anders als bei SELECT, nicht durch eine maskierte Subquery ersetzt.
- **Folge:** Eine Zuweisung oder WHERE-Bedingung, die die ganze Zeile als Ausdruck verwendet (z. B. per Cast zu Text), kopiert Klartextwerte maskierter Spalten in eine sichtbare Spalte oder dient als Orakel. `MaxAffectedRows` begrenzt nur die Menge, nicht das Leck.
- **Voraussetzung:** `WebSql.AllowDml=true` und die Rolle in `DmlWriterRoles`.
- **Fix:** In UPDATE/DELETE jede `ColumnReference`/`Dereference` ablehnen, deren Name dem Zieltabellennamen oder seinem Alias entspricht (normalisiert). Konservativer: DML auf Tabellen mit Masken nur mit einer Allowlist einfacher Ausdrucksformen (Literale, Parameter, nicht maskierte Spalten) erlauben.

### SQ-04 🟠 Policy-Maps im Gateway nach Kurznamen geschlüsselt → RLS-Wegfall und Masken-Verwechslung
`GovernedSqlExecutionService.cs:281-283, 352-358, 392-393, 423`
```csharp
tableRlsFilters[target.TableName] = rlsFilter;
tablesWithoutRls.Add(target.TableName);
predicate: tbl => !tablesWithoutRls.Contains(tbl),
```
- **Ursache:** Filter, „ohne RLS“-Markierung, Spaltenlisten und Masken werden auch unter dem **unqualifizierten** Tabellennamen abgelegt, der zuletzt verarbeitete Eintrag gewinnt. Der Listener schlägt unter dem Namen nach, wie er im SQL steht.
- **Folge:**
  - Referenziert eine Query eine RLS-pflichtige Tabelle unqualifiziert und zusätzlich eine gleichnamige Tabelle eines anderen Schemas ohne RLS-Bedarf (keine `tenant_id`, kein Consent-Filter), steht der Kurzname in `tablesWithoutRls`. Die geschützte Tabelle wird dann **ohne Tenant- und Consent-Filter** gelesen.
  - Bei Masken kann eine Tabelle die Masken der anderen erhalten, z. B. HMAC statt `NULL` (Deny).
- **Voraussetzung:** Zwei katalogisierte, gleichnamige Tabellen in verschiedenen Schemas mit Consent für den Aufrufer.
- **Fix:** Nur unter dem vollständig aufgelösten Namen ablegen. Analyzer, Gateway und Listener nutzen denselben Resolver (Namensteile als Liste, nicht als String). Mehrdeutige Kurznamen in einer Query ablehnen. Den Rückfall auf Kurznamen in `DefaultRlsPolicyProvider` und `DefaultColumnMaskingPolicyProvider` entfernen.

### SQ-05 🟠 Statement wird für SQL Server ungültig umgeschrieben (`LIMIT`, `AS schema.table`)
`RlsListener.cs` (Root-LIMIT, `AS {rawTableName}` ≈ Z. 933-936)
- **Ursache:** Der Rewriter erzeugt immer Trino- bzw. PostgreSQL-Syntax: `LIMIT n` und bei qualifizierten Tabellen ohne Alias `AS public.orders`.
- **Folge:** WebSQL gegen SQL Server schlägt praktisch immer fehl. Qualifizierte Tabellen ohne eigenen Alias schlagen auf allen DBs fehl. Das ist fail-closed und damit kein Leck. Es ist aber ein starker Hinweis, dass der Rewrite nicht dialektbewusst ist, also das Muster hinter SQ-01 und SQ-02. Als Hoch eingestuft, weil Betreiber zum Umgehen naheliegend auf den Governance-Bypass ausweichen würden.
- **Fix:** Dialekt-Parameter in `RlsOptions` (`TOP`/`OFFSET … FETCH` für T-SQL). Alias = letzter Namensteil, gequotet.

### SQ-06 🟡 Funktions-Denylist unvollständig, Gateway nutzt nur den Denylist-Modus
`SqlFunctionPolicy.cs:17-54`. Im Gateway wird `AllowedFunctions` nie gesetzt.
- **Abgedeckt:** Schema-Qualifizierung, Quoting, Groß-/Kleinschreibung, FILTER/OVER. Analyzer und Listener wenden die Policy identisch an.
- **Nicht abgedeckt, Beispiele:**
  - PostgreSQL: `pg_notify`, `pg_current_logfile`, `pg_export_snapshot`, `pg_*replication*`, `pg_stat_reset*`, `pg_log_backend_memory_contexts`, `pg_get_*def`, `inet_server_addr`, `version`, `txid_current`.
  - SQL Server: `fn_dblog`, `fn_xe_file_target_read_file`, `fn_get_audit_file`, `fn_trace_gettable`, `HAS_DBACCESS`, `SUSER_SNAME`, `IS_SRVROLEMEMBER`.
- **Fix:** Dialektspezifische **Allowlist** im Gateway (Standard-Skalar-, Aggregat- und Fensterfunktionen), Denylist als zweite Linie. Betrieblich: Das WebSQL-DB-Login ohne Superuser-, `pg_read_server_files`-, `VIEW SERVER STATE`- oder `sysadmin`-Rechte betreiben.

### SQ-07 🟡 INSERT prüft nur die Tenant-Spalte, nicht die Consent-Zeilenfilter
`RlsListener.cs:412-452`
- **Folge:** Ein Writer kann Zeilen außerhalb seines `CombinedRowFilterSql` anlegen (z. B. `region='US'` bei einem Filter auf `EU`). Solche Datensätze landen bei anderen Nutzern desselben Mandanten.
- **Fix:** INSERT auf Tabellen mit Consent-Zeilenfilter ablehnen oder als `INSERT … SELECT … WHERE <filter>` umschreiben.

### SQ-08 🟡 Parsen ohne Zeitbudget / Stack-Reserve
`FastSqlEngine.cs:74-93`
- **Stand:** Die Tiefenlimits halten. Token-Zählung plus iterative Baumtiefe fangen Operator- und Dereferenz-Ketten ab. Eine Umgehung wurde nicht gefunden.
- **Offen:**
  - Es gibt kein `CancellationToken` und kein Zeitlimit für den SLL→LL-Fallback bei bis zu 64k Zeichen.
  - 200 Klammerebenen ergeben grob 2.400 Parser-Frames plus Rekursion in `ParserATNSimulator.closure`. Ob das auf Thread-Pool-Threads (Linux, ca. 1,5 MB Stack) sicher ist, wurde nicht gemessen.
- **Fix:** Parsen auf einem eigenen Thread mit großem Stack (z. B. 16 MB) und Zeitbudget. `MaxNestingDepth` auf 64–100 senken. Lasttest mit Maximal-Nesting im LL-Modus.

### SQ-09 🟡 Dreiteilige Namen (SQL Server): Katalogteil wird für die Policy ignoriert
`GovernedSqlExecutionService.cs:259-263`, `SqlQueryAnalyzer.cs:372-377`
- **Folge:** Bei `db.schema.table` fällt die Katalogsuche auf `default.schema.table` zurück. SQL Server versteht den ersten Teil als **Datenbank**, die Policy der registrierten Tabelle gilt dann für eine gleichnamige Tabelle in einer anderen Datenbank, auf die das Login Zugriff hat. Die Bindung an die Datenquelle wird umgangen.
- **Fix:** Den ersten Namensteil gegen die Domain bzw. Datenbank der Datenquelle prüfen. Rückfall auf `default` nur ohne Katalogteil. Namen mit vier oder mehr Teilen (Linked Server) ablehnen.

---

## 3. Niedrig / Info

| ID | Befund | Fix |
|---|---|---|
| SQ-10 | Lexer faltet Nicht-ASCII per `ToUpperInvariant` (`ZeroCopyCaseInsensitiveStream.cs:61`, z. B. `ı`→`I`, `ſ`→`S`). Gateway und DB sehen unterschiedliche Bezeichner bzw. Keywords. Keine Ausnutzung gefunden (unbekannte Tabellen → `1 = 0`). | Nicht-ASCII außerhalb von String- und Quoted-Tokens ablehnen |
| SQ-11 | Quotierter Name mit Punkt (`"a.b"`) wird nach der Normalisierung wie `a.b` behandelt (`RlsListener.cs:12-37`, `TableIdentifier.TryParse`) | Namensteile als Liste führen; Punkte und Steuerzeichen in quotierten Teilen ablehnen |
| SQ-12 | Groß-/Kleinschreibung: `"Orders"` und `orders` werden gleichgesetzt, PostgreSQL unterscheidet sie bei Quoting | Dialektgerechtes Case-Folding im Resolver |
| SQ-13 | `FOR TIMESTAMP/VERSION AS OF` wird beim Tabellen-Replace verworfen bzw. führt mit Subquery zu überlappenden Ersetzungen und einer Exception (fail-closed) | Time-Travel explizit ablehnen oder korrekt übernehmen |
| SQ-14 | Gepoolter Parser behält den letzten `TokenStream` und hält damit SQL im Speicher (`ParserPooledObjectPolicy`) | `parser.TokenStream = null` in `Return` |
| SQ-15 | Tautologie-Erkennung für UPDATE/DELETE ist zwangsläufig heuristisch (z. B. `IN (SELECT …)`, `coalesce(…)`). Die wirksame Grenze ist `MaxAffectedRows` (korrekt implementiert: Transaktion, Rollback, fail-closed bei `-1`) | `WebSql.MaxAffectedRows = 0` (unbegrenzt) außerhalb Development als DANGER einstufen |
| SQ-16 | `SqlParameterExtractor` entfernt Kommentare per Regex ohne String-Bewusstsein. Relevant nur für admin-gepflegte SQL-Endpoint-Dateien; 100-ms-Regex-Timeout vorhanden | String-bewusst über den Lexer extrahieren |

---

## 4. Geprüft ohne Befund

- **Fehlerpfade:** Alle Parse- und Security-Exceptions führen zur Ablehnung. Es gibt keinen Pfad, auf dem das Original-SQL trotz Fehler weitergeht. Ausnahme ist der bewusste Schalter `danger_bypass_websql_governance` (DANGER, nur Development).
- **Pooling und Nebenläufigkeit:** Prediction-Mode, ErrorHandler und Listener werden zurückgesetzt. `BailErrorStrategy` ist zustandslos. `DefaultErrorStrategy` wird pro Fallback neu erzeugt (N1-Fix). Der geteilte DFA ist ANTLR-Standard und thread-safe.
- **Frühere Fixes:**
  - C-02 (CTE mit Punkt): nur einteilige CTE-Namen schatten.
  - C-06: Tiefe per Token und Baum begrenzt.
  - C-01 / H-14: Funktions-Policy, Table Functions, `WITH SESSION` und `WITH FUNCTION` abgelehnt.
  - M-22: LIMIT am Root, ohne Umgehung über CTE, IN oder EXISTS.
  - M-23: alle VALUES-Zeilen und Set-Op-Zweige, nur Literale.
  - M-24: Spalten gequotet.
  - Keine Umgehung gefunden, außer SQ-03 (H-15) und den Lexer-Differentials.
- **DML-Grammatik:** MERGE, `UPDATE … FROM`, `DELETE … USING` und `RETURNING` sind nicht parsbar und werden abgelehnt. Bei `INSERT … SELECT` bekommt die Quelltabelle RLS.
- **Statement-Klassifikation:** Alles außer Select und DML (inkl. EXPLAIN, CALL, PREPARE, SHOW) lehnt das Gateway ab.
- **LIMIT-Formatierung:** kulturunabhängig. Hex-, Unterstrich- und Parameterwerte werden ersetzt. `WITH TIES` → `ONLY`.
- **Regex:** keine ReDoS-anfälligen Muster.

---

## 5. Empfohlene Reihenfolge

1. **Sofort (Konfiguration/Hotfix):**
   - Kommentare in WebSQL ablehnen.
   - Für PostgreSQL `E'…'` und Backslashes in Strings ablehnen und `standard_conforming_strings=on` erzwingen.
   - Für SQL Server `$$…$$` ablehnen (SQ-01, SQ-02).
2. **Kurzfristig:**
   - Whole-Row-Referenz in DML sperren (SQ-03).
   - Policy-Maps nur nach aufgelöstem Namen, mehrdeutige Kurznamen ablehnen (SQ-04).
   - Katalogteil prüfen (SQ-09).
   - INSERT gegen Consent-Filter (SQ-07).
3. **Mittelfristig:**
   - Dialektbewusster Rewrite: SQL-Erzeugung aus dem AST oder Verifikation mit dem Ziel-Dialekt-Parser (SQ-01/02/05).
   - Funktions-Allowlist (SQ-06).
   - Parsen mit Zeitbudget und Stack-Reserve (SQ-08).
4. **Betrieb:** WebSQL-DB-Login mit minimalen Rechten (nur SELECT bzw. DML auf freigegebene Tabellen, keine Server- oder Datei-Funktionen). Diese Maßnahme begrenzt den Schaden **aller** Parser-Differentials.
