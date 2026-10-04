# Konsolidierungs-Review 2026-10-02 (Runde 5)

Stand: Branch `security/review-2026-10-02`. HEADs: gql `93f9dd2`, gql_extensions `fbc5a72`, gql_sqlparser `ddba316`.
Es wurde nur gelesen, kein Code geändert. Die Aussagen „ohne Leser“, „tot“ und „doppelt“ sind per grep über alle drei Repos belegt.

## 1. Prüfung der letzten Änderungen

| Änderung | Bewertung |
|---|---|
| `::ffff:0:0/96` aus der Sperrliste entfernt | Ok. Mapped-Adressen werden überall vorher zu IPv4 normalisiert. Restpunkt: Die Startvalidierung erkennt ein Netz, das den mapped-Bereich überdeckt, nicht mehr (z. B. `::fff0:0:0/92`). Zur Laufzeit ist das wirkungslos, aber die README verspricht hier einen Startabbruch (K-E01). |
| HTTPS-Check im DeclarativeHttp-Executor vor die DNS-Auflösung gezogen | Korrekt. |
| `methodCall` mit Bezeichner-Präfix als qualifizierte Funktion | Keine Regression zu P-01. Die SQL-Server-Methoden (`value`, `query`, `nodes`) wurden schon immer als `functionCall` geparst. Ursache war, dass `NORMALIZE` ein reserviertes Keyword ist. Sauberer: zurücknehmen und den Test auf `util."normalize"(x)` umstellen (K-P01). |
| Testfälle `openmetadata_auto_create_consents`/`dbt_…` aus `SEM_ReclassifiedSwitches_AreDanger` entfernt | Die Schalter sind weiter DANGER. Der Test scheiterte nur an uneinheitlichen Labels. Die Abdeckung ist damit verloren und wird mit K-K07 wiederhergestellt. |

**Die offenen Punkte aus der Nachprüfung sind im gepushten Stand nicht umgesetzt:** EX-03, 04, 05, 07, 08, 09, 10, 11, 14, 17, 18 sowie E-10, SQ-12 und SQ-16. Der Code-Diff gegenüber Runde 4 enthält nur die oben genannten Build-Fixes; die übrigen Änderungen sind Doku (`docs/`, ADRs, `.agents/`).

## 2. Neue Sicherheitsbefunde (Priorität)

1. **K-X01 (hoch):** Der OM-Reconcile widerruft Deny-Consents von Tabellen, die im aktuellen Lauf nur übersprungen wurden. Gründe für ein Überspringen: DB-Kollision, Map-Fehler, geänderter ServiceFilter, abgebrochenes Paging. Das ist fail-open, und ein OM-Table-Owner kann es auslösen, indem er eine gleichnamige Tabelle in einer anderen DB anlegt. Ursache ist mein Runde-4-Code.
2. **K-K01 (hoch):** Ein widerrufener Token wird nur bei WebSocket-Subscriptions geprüft. GraphQL/HTTP, WebSQL, SQL-Endpoints, OData und MCP akzeptieren ihn bis zum Ablauf.
3. **K-E02/K-E03 (mittel):** Der DeclarativeHttp-Executor und die HTTP-Plugins nutzen eine eigene, schwächere IP-Prüfung. Azure WireServer `168.63.129.16`, NAT64, Multicast und `240/4` sind dort nicht gesperrt. Er sollte auf `SecureOutboundHttp` umgestellt werden.
4. **K-X02 (mittel):** Der Katalog-Sync (Purview, Collibra, Alation) erkennt Art.-9- und PII-Tags an Spalten nicht. E-06 gilt nur für den OM-Sync. Neue Katalogtabellen sind sofort aktiv.
5. **K-X03 (mittel):** Webhook-Endpoints sind auch bei deaktivierter Integration gemappt. `Dbt.Enabled` und `Lakehouse.Enabled` werden nirgends gelesen.
6. **K-K04 (mittel):** `warn_allow_all_cors_origins` überspringt den gesamten Anti-CSRF-Header-Check und ist als WARN in Prod erlaubt.
7. **K-K02/K-K03 (mittel):** Client-IP und Tenant werden an 7 bzw. etwa 10 Stellen unterschiedlich ermittelt. Ein Pfad hat einen `X-Tenant-ID`-Header-Fallback, und MCP-Sessions haben immer die IP 127.0.0.1.
8. **K-K06:** Der Audit-Hash-Chain-Check und der WORM-Export werden nie ausgelöst. `Audit.Worm.Enabled` hat keine Wirkung.
9. **K-K05:** Sicherheitsrelevante Optionen haben keinen Leser: `Mcp.MaxResultRows`, `Mcp.RequirePiiMasking`, `Casbin.Enabled` und `PostAuthSidRateLimit.MaxCostPerMinute`.

## 3. Was entfallen kann

- **dbt-Webhook-Receiver (K-X04):** Er prüft nur die Signatur und loggt. Endpoint, Interface, Klasse und zwei Optionen können entfallen.
- **Katalog (K-X05):** `EnrichOrReferenceTableAsync` und `IDataCatalogClient.GetTableAsync` (4 Implementierungen; das Ergebnis wird verworfen, kostet aber einen HTTP-Call pro Tabelle) sowie `IngestManifestFileAsync`.
- **Parquet (K-K08):** `GET /api/export/parquet/{domain}/{table}` liefert nur ein leeres Gerüst, und das synchrone `ExportToParquet` nutzen nur Tests.
- **Plugins (K-K11):** `DynamicPluginAssemblyLoadContext` hat keine Aufrufer. Dazu kommen die Optionen `EnableHotReload` und `PluginDirectory`.
- **Subgraph-HttpClients (K-E06):** ungenutzt und ungehärtet. Ebenso der Default-`AddHttpClient()` und 7× `TryAddTransient<SsrfProtectionHandler>()` (K-E07).
- **Inline-`ValidateUrl` (K-E05):** in Purview, Collibra, Alation und AuditWorm. Das ist redundant zum Handler und blockiert freigegebene IP-Literale.
- **Parser:**
  - die toten `Reject*`-Properties der `FastSqlEngine` (K-P03)
  - der doppelte Kommentar-Check und das Feld `_tokens` im `RlsListener` (K-P04)
  - `Program.RunDemo`, außerdem Tests und xunit im Produktions-Assembly (K-P05)
  - der frühe Denylist-Check in GSES, der in der Allowlist-Prüfung enthalten ist (K-P02)
- **Optionen ohne Leser:** etwa 20 im Kern (K-K05) und 8 in den Extensions (K-X06).
- **Legacy-Schalter:** `Catalog.AllowLegacyPayloadOnlySignature` entfernen, `Itsm.LegacyGlobalWebhookSecret` vorher prüfen, Alias `danger_allow_anonymous_webhooks` (K-X07).

## 4. Was besser zu gestalten ist

- **Webhook-Infrastruktur (K-X08):**
  - Ein gemeinsamer HMAC-Verifier statt 4 Varianten.
  - Ein Replay-Cache, der erst nach Erfolg committet, statt 4 Caches (das schließt EX-14).
  - Per-System-Bypass statt Sammelschalter (das schließt E-10).
- **Security-Switch-Tabelle (K-K07):** `SecuritySwitch(Name, Level, IsActive)` mit festem Label-Schema und einer Test-Theory über alle Einträge.
- **Secret-Auflösung (K-X09):** ein Resolver nach Alation-Regeln (fail-closed). Viele Webhook- und Client-Secrets sind heute Klartext-Optionen.
- **Begrenztes Lesen (K-X10):** `CatalogHttpContent` für alle Clients. Jira, ServiceNow und OpenLineage lesen heute unbegrenzt.
- **Zerlegung großer Klassen:** `GovernedSqlExecutionService` (K-P07) und `OpenMetadataSyncService` (K-X11, deckt EX-03 mit ab).
- **Engine als Singleton (K-P08):** Heute wird pro Request eine neue Engine erzeugt und die SQL doppelt geparst.
- **Weitere Duplikate:**
  - Provider→Dialekt-Mapping dreifach (K-P06)
  - SigV4 doppelt mit fest codierter Region `us-east-1` (K-K12)
  - Aufbau des `SecurityEvaluationContext` 6× kopiert (K-K13)
  - Rollenprüfungen hart codiert statt per Policy (K-K10)

Die Einzelberichte mit Datei:Zeile, Vorschlag, Aufwand und Risiko folgen unten.


---

## R8 – Konsolidierungs-Review Bereich PARSER (K-P)

Prüfgegenstand: `gql_sqlparser/*` (ohne generierten Code; Tests nur für die Abdeckung), `GovernedSqlExecutionService.cs` (GSES), `WebSqlEndpoints.cs`. Es wurden keine Quelldateien geändert.

## Kurzfazit
- **Letzte Nutzer-Änderung (methodCall mit ColumnReference-Präfix): keine sicherheitsrelevante Regression zu P-01.** Sie macht `ident.<reserviertes Keyword>(…)` genauso durchlässig wie `ident.name(…)`, das die Grammatik schon immer als `functionCall` geparst hat. Nötig war sie nur, weil `NORMALIZE` ein reserviertes Keyword ist. Sauberer ist es, die Änderung zurückzunehmen und den Test auf eine quotierte oder nicht reservierte Schreibweise umzustellen (K-P01).
- **Größter Hebel bei der Konsolidierung:**
  - die dreifache Funktionsprüfung (K-P02),
  - die dreifach gepflegten Token-Schalter (K-P03),
  - die Aufteilung der GSES (K-P07).

---

## K-P01 · Sicherheit/Design · `RlsListener.cs:196-211`, `Analysis/SqlQueryAnalyzer.cs:136-147` (Nutzer-Änderung HEAD~1→HEAD)

### Grammatik-Befund (`SqlBase.g4:649-654, 670, 1099-1102`)
`functionCall` (`qualifiedName '(' …`) steht vor `methodCall` (`primaryExpression '.' methodName '(' …`). Bei `util.f(x)` passen beide Alternativen. ANTLR löst die Mehrdeutigkeit mit der kleinsten Alternative auf, also `functionCall`, in SLL und LL gleich. Das deckt sich mit FIX-P und dem Test `R4_P01_QualifiedXmlStyleMethod…`.

`util.normalize(x)` ist anders gelagert:
- `NORMALIZE` ist ein **reserviertes Keyword** und steht nicht in `nonReserved`. Laut Skript-Auswertung von g4 gehören zu den reservierten Keywords u. a. auch NORMALIZE, LEFT, RIGHT, TRIM, EXTRACT, CAST, JSON_VALUE, JSON_QUERY, JSON_EXISTS, LISTAGG, GROUPING.
- `qualifiedName → identifier` akzeptiert dieses Token nicht. `methodName` akzeptiert es dagegen über `{isKeyword()}? .`.
- Deshalb gibt es für `util.normalize(x)` nur den Parse als `methodCall`. Mit FIX-P wurde das hart abgelehnt, und `R4_P01_QualifiedFunctionCalls_StillWork` wurde rot. Das war der Grund für die Nutzer-Änderung.

### Regression zu P-01?
Im Ergebnis nein:
- Der neue Zweig wird nur erreicht, wenn der Präfix eine einzelne `ColumnReference` ist **und** der Methodenname ein reserviertes Keyword ist. Jeder andere Name läuft ohnehin über `functionCall`.
- Die in P-01 genannten SQL-Server-Methoden sind keine reservierten Keywords und waren nie `methodCall`. Das betrifft XML (`value`, `query`, `nodes`, `exist`, `modify`), Geography/Geometry (`STDistance`, `Filter`, `STAsText`, …) und hierarchyid (`GetAncestor`, `ToString`, `Read`, `Write`, …).
- Mit `EnforceFunctionPolicy=false` oder im Denylist-Modus (`AllowedFunctions=null`, der Library-Default) passieren diese Methoden die Prüfung, und zwar **schon vor der Änderung**. Neu durchgelassen werden nur `x.normalize()`, `x.left()`, `x.trim()` usw., also praktisch nur CLR-UDT-Methoden mit Keyword-Namen.
- Gateway: Er arbeitet immer im Allowlist-Modus (`GSES:495-506, 549`) mit exaktem, qualifiziertem Namensvergleich. Damit wird `x.normalize` abgelehnt, solange es nicht ausdrücklich in `AdditionalAllowedFunctions` steht.
- `(expr).f()`, `x[1].f()`, `a.b.f()` (Dereference-Präfix) und `T::f()` bleiben unbedingt gesperrt.
- **Rest-Inkonsistenz:**
  - Der Kommentar sagt „rejected unconditionally“. Tatsächlich hängt der neue Zweig an `EnforceFunctionPolicy`.
  - Der Analyzer fügt ohne `_seenFunctionCalls`-Dedupe hinzu (`SqlQueryAnalyzer.cs:142`). Das erzeugt Duplikate in `FunctionCalls`, die Funktion selbst ist harmlos.

### Präfix als Spalte?
- Der Analyzer hat kein `EnterColumnReference` und registriert den Präfix daher nicht als projizierte Spalte.
- Er landet aber über `ExtractIdentifiers` (`SqlQueryAnalyzer.cs:299-317`) in `JoinConditionColumns`, wenn der Aufruf in `ON …` oder in `a = b` steht. Dasselbe gilt seit jeher für die Namensteile eines `functionCall`, z. B. `util` und `myfn`.
- Folge ist nur eine Überdeckung: Eine maskierte Spalte, die zufällig wie das Schema heißt, löst SEC-JOIN-01 aus. Das schlägt in Richtung fail-closed aus und ist keine Lücke.
- Im RlsListener zählt `EnsureNoMaskedColumnReferences` den Präfix als `ColumnReference`. Das ist ebenfalls konservativ.

### Nebenbefund (bestehend, nicht durch die Änderung)
Beim `functionCall`-Parse von `maskedCol.value('…')` sind die Namensteile Teil von `qualifiedName` und **keine** `ColumnReference`. Damit sieht H-15 (`RlsListener.cs:921-929`) die maskierte Spalte in `UPDATE … SET x = maskedCol.value(…)` nicht (Copy-out).
- Im Gateway schließt die Allowlist das (`maskedcol.value` ist nicht gelistet).
- Bei Library-Nutzung im Denylist-Modus ist es offen, aber nur für SQL Server mit XML-typisierten maskierten Spalten.

### Vorschlag (bevorzugt A)
- **A:** Nutzer-Änderung in beiden Listenern zurücknehmen (unbedingte Ablehnung) und den Test auf `util."normalize"(name)` umstellen. Quoted identifier → `qualifiedName` → `functionCall`, normalisiert `util.normalize`, keine Punkte im Quote. Alternativ einen nicht reservierten Namen verwenden. Damit gibt es keinen neuen Pfad, die Policy-Prüfung bleibt an einer Stelle und der P-01-Kommentar stimmt wieder.
- **B (falls Keyword-Namen ohne Quotes gebraucht werden):**
  - Den Zweig behalten.
  - Die Namensbildung in einen gemeinsamen Helper `SqlIdentifierHelper.TryGetQualifiedFunctionName(MethodCallContext, out string)` legen; er greift nur bei `ColumnReference` + Keyword-`methodName`.
  - Im RlsListener über dieselbe private Prüfmethode wie `EnterFunctionCall` laufen lassen.
  - Im Analyzer mit `_seenFunctionCalls` deduplizieren und den Kommentar korrigieren.
  - Tests: `x.normalize()` mit `EnforceFunctionPolicy=false` (dokumentiert erlaubt), `a.b.normalize()` abgelehnt, Allowlist ohne Eintrag abgelehnt.
- Eine Grammatikänderung (Keywords in `qualifiedName`) wird **nicht** empfohlen: Sie weicht von Trino ab und gefährdet die Compliance-Fixtures.

**Aufwand:** S · **Risiko:** gering (A ändert nur 2 Methoden + 1 Test)

---

## K-P02 · Vereinfachen · `GSES:232-242`, `GSES:493-506`, `RlsListener.cs:179-189`, `SqlQueryAnalyzer.cs:124-132`
**Befund:** Die Funktions-Policy wird dreimal auf denselben Namen ausgewertet:
1. Früher Denylist-Check mit `new RlsOptions { EnforceFunctionPolicy = true }`.
2. Allowlist-Check mit `AllowedFunctions = Build(dialect)`.
3. `RlsListener.EnterFunctionCall` mit denselben `AllowedFunctions`.

`SqlFunctionPolicy.IsFunctionAllowed` prüft seit P-02 die Denylist auch im Allowlist-Modus (`SqlFunctionPolicy.cs:124-128`). Damit ist (1) vollständig in (2) enthalten, und (2) ist inhaltlich identisch mit (3).

Der einzige Mehrwert von (1) und (2) ist eine kuratierte Fehlermeldung mit Funktionsnamen; aus dem Listener kommt nur die generische Meldung „violates … row-level security policy“. (1) verhindert zusätzlich Katalog- und Consent-I/O bei abgelehnten Funktionen.

Leser von `metadata.FunctionCalls`: nur GSES:232/496 und Tests (`SecurityRemediationTests.cs:228-229, 585`).

**Vorschlag:**
- (1) streichen.
- (2) als einzige Gateway-Prüfung behalten, oder ganz streichen und eine Unterklasse `SqlFunctionPolicyException : SecurityException` (Name im Property) aus dem Listener in GSES auf `WebSqlPolicyException("Function '…' is not permitted")` abbilden.
- Bei der zweiten Variante kann auch die Funktionssammlung im Analyzer entfallen, einschließlich des Nutzer-Zweigs aus K-P01.
- Bestehende Tests (`C01_*`, `SQ06_*`, `P01_*`) prüfen nur `WebSqlPolicyException` und bleiben grün.

**Aufwand:** S · **Risiko:** gering

---

## K-P03 · Vereinfachen/Entfernen · `FastSqlEngine.cs:202-240`, `IRlsPolicyProvider.cs:233-287`, `FastSqlEngine.cs:35-77`, `GSES:45-53, 529-538`
**Befund:** Die 7 Token-Schalter (Comments, Backslash, E'', $$, Non-ASCII, Dots, TimeTravel) existieren dreifach:
- `RlsOptions` (Default true),
- `SqlTokenSecurityOptions` (immutable, P-04),
- als 7 `FastSqlEngine`-Properties mit Default false plus `CreateDefaultTokenSecurityOptions()`.

**Die Engine-Properties haben keinen einzigen Setter** in `gql/src`, `gql/tests`, `gql_extensions` oder `gql_sqlparser`. Geprüft per grep `(engine|_engine|_sqlEngine)\.Reject…\s*=` und Objekt-Initializer. Es gibt nur Asserts auf `false` (`SecurityRemediationTests.cs:646-652, 695`), die Properties sind also faktisch die Konstante `SqlTokenSecurityOptions.None`.

GSES pflegt die Liste ebenfalls doppelt (`AnalysisTokenOptions` und `rlsOptions`), wobei 6 der 7 Werte den sicheren `RlsOptions`-Defaults entsprechen.

**Vorschlag:**
- Die 7 Engine-Properties und `CreateDefaultTokenSecurityOptions` entfernen; `tokenOptions ?? SqlTokenSecurityOptions.None`.
- Ein Preset `SqlTokenSecurityOptions.Strict` einführen und für die Analyse in GSES verwenden.
- In `rlsOptions` nur `RejectDollarQuoting` explizit setzen (oder alle Werte bewusst stehen lassen, dann aber aus einer Quelle).
- Optional längerfristig: `RlsOptions` hält `SqlTokenSecurityOptions TokenSecurity` statt 7 Einzel-Properties. Das kostet etwa 20 Test-Stellen und lohnt erst zusammen mit K-P07.
- `RlsOptions.TablesWithMaskedColumns` ist im Gateway redundant: `HasMaskingForTable` (`RlsListener.cs:987-1015`) findet dieselben Tabellen über `ColumnMaskingProvider` + `TableColumnsProvider`, die GSES immer setzt. Nur behalten, wenn Library-Nutzer ohne `TableColumnsProvider` unterstützt werden sollen (prüfen).

**Aufwand:** S (Engine-Teil) / M (RlsOptions-Umbau) · **Risiko:** gering / mittel (Test-Churn)

---

## K-P04 · Entfernen · `RlsListener.cs:91, 97, 101-113`
**Befund:**
- Der Kommentar-Check im RlsListener-Konstruktor dupliziert `EnsureTokensAreSafe` (`FastSqlEngine.cs:491-495`). `RewriteRls` leitet `RejectComments` über `FromRlsOptions` immer an den Lexer-Check weiter.
- Der Check greift nur bei direktem `new RlsListener(tokens, …)`. Das kommt außer in `RewriteRls` nur in `Program.cs:20` und `TrinoParserComplianceTests.cs:136,151` vor, und dort deckt er nur 1 von 7 Schaltern ab (halbe Verteidigung).
- Das Feld `_tokens` wird zugewiesen, aber nie gelesen.

**Vorschlag:** Loop und `_tokens` entfernen. Optional den Konstruktor `internal` machen; die Compliance-Tests dann auf `RewriteRls` umstellen bzw. `InternalsVisibleTo` (Tests liegen im selben Assembly, siehe K-P05).

**Aufwand:** S · **Risiko:** gering

---

## K-P05 · Entfernen/Design · `gql_sqlparser/Program.cs`, `TrinoTestExtractor.cs`, `TrinoSqlEngine.csproj`
**Befund:**
- `Program.RunDemo` hat 0 Aufrufer (grep `RunDemo`). `TrinoTestExtractor` wird nur von `RunDemo` aufgerufen.
- Beide liegen im Produktions-Assembly, das `Autheris.Application` referenziert. Dazu gehören eine Demo mit Default-`RlsOptions` (Filter `tenant_id = 42`) und ein Java-Fixture-Extraktor mit Dateisystemzugriff.
- Ebenfalls im Library-Projekt liegen alle Testklassen (`*Tests.cs`) sowie `xunit` und `Microsoft.NET.Test.Sdk` als PackageReference. Testcode wird also mit dem Gateway ausgeliefert.

**Vorschlag:**
- `Program.cs` und `TrinoTestExtractor.cs` löschen oder in ein `tools/`-Projekt verschieben.
- Mittelfristig ein Projekt `TrinoSqlEngine.Tests` abspalten (Tests, Fixtures, xunit, Test.Sdk). In `TrinoSqlEngine.csproj` bleiben nur ANTLR und ObjectPool. Die Projektmappe (`Autheris.sln:40`) braucht dann einen Eintrag mehr.

**Aufwand:** S (Löschen) / M (Projekt-Split) · **Risiko:** gering (Split: CI- und Sln-Anpassung)

---

## K-P06 · Vereinfachen · `GSES:1063-1085`, `Infrastructure/Persistence/SqlConnectionFactory.cs:19-25`, `Domain/Common/DatabaseDialect.cs:71-87`; `GSES:280/1040-1057, 666-668, 698-700`
**Befund:**
- Das Mapping Provider → Dialekt existiert dreifach:
  - in GSES (P-05, „identisch zur Factory“ von Hand gepflegt),
  - in der `SqlConnectionFactory`,
  - in `ParseDialect` für den Katalog, mit anderen Aliasen (`pgsql`, `sql_server`) und Fallback **PostgreSql** bei unbekannten Werten.
- GSES schlägt dieselbe Verbindung dreimal nach (`ResolveConnectionDialect`, `ExecuteCoreAsync:666`) und mappt den Provider dabei zweimal.

**Vorschlag:**
- Ein einziges `DatabaseDialectExtensions.TryParseConnectionProvider(string?, out DatabaseDialect)` in Domain anlegen und in Factory und GSES verwenden.
- In GSES einmal einen `WebSqlDataSource(Name, ConnectionOptions?, Dialect?)` auflösen und an Rewrite und Execute durchreichen.
- Die Tests (`TryMapProviderToDialect` mit 5 Aufrufen) auf die neue Methode umstellen.

**Aufwand:** S · **Risiko:** gering (Mapping-Tests vorhanden)

---

## K-P07 · Design · `GovernedSqlExecutionService.cs` (1333 Zeilen; `RewriteCoreAsync` 107-590 ≈ 480 Zeilen)
**Befund:** Eine Klasse vereint:
- Eingangsprüfung,
- Data-Source-Allowlist,
- Dialekt-Auflösung,
- Statement- und Funktions-Policy,
- DML-Rollenprüfung,
- Katalog, Consent und ABAC pro Tabelle,
- RLS- und Masken-Aufbau inklusive HMAC und Schlüsselableitung,
- RlsOptions-Bau,
- Ausführung,
- DML-Transaktion und Audit,
- einen Synthetic-Reader.

Das Muster „FullName und zusätzlich TableName als Schlüssel“ ist viermal kopiert (343-347, 418-422, 426-430, 464-470).

**Lohnend.** Vorschlag mit unveränderter Fassade (Konstruktor und Interface bleiben, damit die Tests nicht brechen):

| Baustein | Inhalt | Ziel |
|---|---|---|
| `WebSqlDataSourceResolver` | `ResolveAllowedDataSource`, `IsDataSourceAllowedForTenant`, `ResolveConnectionDialect`, Provider-Mapping, Session-Init (891-966, 1036-1093) | rein, statisch testbar |
| `WebSqlStatementPolicy` | Statement-Typ, DDL, DML-Writer-Rolle, table-less, Table-Functions/Session/Inline, Funktions-Allowlist (167-242, 493-506) | rein, ohne I/O |
| `WebSqlTableGovernanceResolver` | Katalog, Dialekt-/Source-/Catalog-Prüfung, Consent, ABAC (`RestrictWithPolicy`), RLS-Filter, Spaltenmasken, Join-Guard (282-472, 973-1034) → liefert `GovernedTablePlan` | Kernlogik isoliert |
| `WebSqlMaskExpressionBuilder` | `GetMaskExpressionForRule`, `TryBuildKeyedHmacExpression`, `GetMasterHmacKey` (1124-1254) | Krypto an einer Stelle |
| `WebSqlRlsOptionsFactory` | 474-557 | eine Quelle für die Schalter (K-P03) |
| `WebSqlDmlExecutor` | `ExecuteDmlInTransactionAsync`, `RecordDmlAuditAsync` (754-838) | DML-Guardrails gebündelt |
| `SyntheticDataTableReader` | 1273-1333 | eigene Datei |

**Aufwand:** M–L · **Risiko:** mittel (reines Refactoring; die bestehenden Tests prüfen nur die Fassade und Helper mit `internal static`, die über Weiterleitungen erhalten bleiben)

---

## K-P08 · Design/Performance · `GSES:55, 145, 563`; `GatewayServiceCollectionExtensions.cs:374`; `FastSqlEngine.cs:150-151`
**Befund:**
- GSES ist als **Scoped** registriert und erzeugt `new FastSqlEngine()` pro Request. Der `ObjectPool<SqlBaseParser>` ist ein Instanzfeld, also gibt es pro Request einen neuen Pool, und das Pooling verpufft.
- Jede Anfrage parst die SQL **zweimal** (`Analyze` und `RewriteRls`), auf je einem eigenen Parser-Thread mit eigenem Zeitbudget und eigenem Semaphore-Slot.
- Die beiden Listener müssen synchron gehalten werden; das zeigt die Nutzer-Änderung, die in beiden Listenern nötig war.

**Vorschlag:**
1. `FastSqlEngine` als Singleton in DI registrieren und injizieren. Das ist seit P-04 threadsicher, weil keine Mutation mehr pro Aufruf stattfindet.
2. (prüfen) Einmal parsen und Analyzer und RlsListener auf demselben Baum und denselben Tokens laufen lassen, etwa über eine neue Engine-Methode `ParseForGovernance` → `(tree, tokens)`. Die Dollar-Quoting-Regel hängt am Dialekt und wird dann nach der Dialektbestimmung als Token-Scan geprüft.

**Aufwand:** S (1) / M (2) · **Risiko:** gering / mittel

---

## K-P09 · Sicherheit (niedrig) · `WebSqlEndpoints.cs:113-119`, `:63`
**Befund zur Tenant-Auflösung:** Der Endpoint löst den Tenant selbst aus den Claims auf (`tenant_id`/`tid`/`tenant`) und weicht damit von der kanonischen Auflösung `EndpointSecurity.GetRequestTenant` bzw. `TenantResolutionMiddleware` ab:
- Ein ungültiger Tenant-Claim fällt still auf `TenantId.LegacySingleTenant` zurück. `GetTenantId()` würde dagegen eine `SecurityException` werfen. Die Middleware antwortet vorher meist mit 400, die Logik ist aber doppelt und fail-open formuliert.
- Der Claim `…/identity/claims/tenantid` wird ignoriert.
- Die Admin-Header-Auswahl aus der Middleware wird ignoriert.

**Befund zur Größenprüfung:** Die 2-MB-Prüfung greift nur bei gesetztem `Content-Length`. Bei Chunked-Upload liest `ReadToEndAsync` bis zum Kestrel-Limit (Default 30 MB) in den Speicher; `MaxQueryLength` wird erst danach geprüft.

**Vorschlag:**
- `var tenantId = EndpointSecurity.GetRequestTenant(httpContext);`
- Den Body begrenzt lesen: `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize = 2 MB` vor dem Lesen setzen, oder `[RequestSizeLimit]`/`.WithMetadata(new RequestSizeLimitAttribute(...))`.

**Aufwand:** S · **Risiko:** gering

---

## K-P10 · Vereinfachen · `WebSqlEndpoints.cs:225-271` und `:336-381`
**Befund:** Die drei Catch-Blöcke (403/400/500 mit `HasStarted`-Prüfung und kuratierter Meldung) sind für den JSON- und den Parquet-Pfad wortgleich kopiert, also sechs Blöcke.

**Vorschlag:** Ein `private static async Task WriteWebSqlErrorAsync(HttpContext, ILogger, Exception, CancellationToken)` mit Exception→Status-Mapping; beide Pfade fangen dann nur noch `catch (Exception ex) => WriteWebSqlErrorAsync(...)`.

**Aufwand:** S · **Risiko:** gering (bestehende Endpoint-Tests decken die Statuscodes ab)

---

## K-P11 · Vereinfachen · Kurzname- und Tabellen-Duplikate
- **„Simple name“ per `LastIndexOf('.')`, 8× kopiert:**
  - `IRlsPolicyProvider.cs:41, 90, 105`
  - `RlsListener.cs:488, 917, 993, 1074`
  - `SqlFunctionPolicy.cs:152`

  → `SqlIdentifierHelper.GetSimpleName(string)`.
- **Gleicher Rumpf in drei Handlern:** `RlsListener.EnterTableName`, `EnterTable` und `EnterTableArgumentTable` (`:337-387`) sind bis auf den Ersetzungsbereich identisch → ein `SecureRelation(QualifiedNameContext, ParserRuleContext replaceScope)`.
- **Zwei Normalisierungen nebeneinander:**
  - `NormalizeIdentifier`: Quotes ab, kein Unescape von `""`, kein Case-Fold.
  - `FoldIdentifierForScope`: Unescape plus Lower-Case.
  - Die Wahl ist je Aufrufstelle bewusst (Tabellen- und Policy-Lookup vs. CTE-Scope). Bitte im XML-Kommentar von `NormalizeIdentifier` festhalten, dass kein Unescape erfolgt. SQ-11 schließt Dots aus, doppelte Quotes in Tabellennamen sind aber nicht gesperrt (prüfen).

**Aufwand:** S · **Risiko:** gering

---

## K-P12 · Design (Library-Defaults) · `IRlsPolicyProvider.cs:22, 25, 74, 167, 191`
**Befund:** Die Sicherheitsbibliothek enthält Demo-Defaults:
- `DefaultRlsPolicyProvider(defaultFilter = "tenant_id = 42")`,
- `ExpectedTenantValue = "42"`,
- `FallbackToSimpleName = true` (SQ-04-Kollisionen),
- `AllowedFunctions = null` (Denylist-Modus, siehe Nebenbefund K-P01).

Der Gateway überschreibt alle Werte (`GSES:508-549`). Ein künftiger Nutzer, der das vergisst, bekommt Tenant „42“ und Kurznamen-Fallback.

**Vorschlag:**
- `FallbackToSimpleName` per Default auf `false` setzen (2 Testsetter).
- `ExpectedTenantValue` und `defaultFilter` ohne Demo-Wert lassen: Leer bzw. null heißt fail-closed (`1 = 0`) bzw. eine Exception beim INSERT/UPDATE-Check. Die Compliance- und Remediation-Tests setzen den Wert dann über einen Test-Helper.
- `AllowedFunctions = SqlFunctionAllowlists.GetDefault(TargetDialect)` als Default (prüfen: Auswirkung auf die Trino-Fixtures).

**Aufwand:** M · **Risiko:** mittel (viele Tests nutzen „42“)

---

## K-P13 · Entfernen (prüfen) · `SharedParserCache.cs`, `SqlBaseParserExtensions.cs`, `ParserPooledObjectPolicy.cs:13-18`
**Befund:**
- Der ANTLR-C#-Generator erzeugt im Parser-Konstruktor bereits `Interpreter = new ParserATNSimulator(this, _ATN, decisionToDFA, sharedContextCache)` mit **statischem**, prozessweit geteiltem DFA- und Context-Cache.
- `SharedParserCache` baut einen zweiten, ebenfalls statischen Cache, und `SetInterpreter` ersetzt den Interpreter durch einen, der darauf zeigt.
- Einzige Nutzer: `ParserPooledObjectPolicy`.

**Vorschlag:**
- Den generierten Code nach dem Build verifizieren (`obj/**/SqlBaseParser.cs`).
- Wenn er so aussieht, beide Dateien löschen und in `Create()` nur `new SqlBaseParser(null)` verwenden.

**Aufwand:** S · **Risiko:** gering (Performance-Verhalten identisch, sofern verifiziert)

---

## Nicht aufgenommen (geprüft, kein Handlungsbedarf)
- **`RejectEscapedStringLiterals` neben `RejectBackslashInStrings`:** Ohne Backslash ist `E'…'` semantisch gleich `'…'`. Der Schalter ist also streng genommen redundant, als zusätzliche Verteidigungslinie aber billig und sinnvoll.
- **`EnforceFunctionPolicy`, `AdditionalDeniedFunctions`, `AllowedTableFunctions`, `AllowedSessionProperties`, `AllowInlineFunctionDefinitions`, `DisallowTenantColumnModificationInUpdate`:** Nur von Tests bzw. Defaults genutzt (grep). Sie sind legitime Library-Schalter mit sicheren Defaults; nicht entfernen.
- **Grammatik-Elemente (`NORMALIZE(x)`, `TRIM`, `EXTRACT`, `JSON_*` unqualifiziert):** Sie sind keine `functionCall` und laufen deshalb nicht durch die Allowlist. Das ist dokumentiert (`SqlFunctionAllowlists.cs`, Kopfkommentar) und harmlos.
- **`left`/`right` in `SqlFunctionAllowlists.CommonFunctions`:** Die Namen sind nur quotiert erreichbar (`"left"(x)`), weil LEFT und RIGHT reserviert sind. Kein Fehler, nur ein Hinweis für Nutzer.

---

## R8-EGRESS: Konsolidierungs-Review für den ausgehenden Verkehr (nur lesen)

**Geprüft:**
- `Application/Security/{EgressAllowlist,EgressAddressRules,SecureOutboundHttp,SsrfProtectionHandler}.cs`
- `Application/Services/DeclarativeHttpDataSourceExecutor.cs`
- Alle `AddHttpClient`-Registrierungen in gql/src und gql_extensions/src
- Lakehouse-Provider, AuditWorm/CDN, Subgraph-Clients, Plugin-HTTP
- Letzte Nutzer-Änderungen (`git diff HEAD~1 HEAD`)

Es wurden keine Quelldateien geändert.

## Kurzfazit

- **IPv4-mapped-Adressen werden überall normalisiert.** Nach der Entfernung von `::ffff:0:0/96` gibt es zur Laufzeit keinen Bypass. Die Startvalidierung hat aber jetzt eine Lücke: Sie nimmt Netze an, die den mapped-Bereich überdecken. Das Netz ist dann wirkungslos, und README und Validierung widersprechen sich (K-E01).
- **Das eigentliche Restrisiko ist der zweite, ältere Egress-Pfad.** DeclarativeHttp und die HttpPlugins nutzen ihn. Er arbeitet nur mit `IsRestrictedIp` und hat einen eigenen ConnectCallback, deshalb sperrt er u. a. Azure WireServer, Multicast und NAT64 nicht (K-E02).
- **Konsolidierung lohnt sich:**
  - Es sollte nur eine Adress- und URL-Policy in `Application/Security` geben, für alle Clients ein Primary-Handler (`SecureOutboundHttp`) und einen schlanken SSRF-Handler.
  - Inline-`ValidateUrl`-Aufrufe in den Clients und tote DI-Registrierungen können entfallen.

## Antworten auf die Prüffragen

**1. Normalisierung von IPv4-mapped-Adressen** (`::ffff:10.0.0.1`, `::ffff:169.254.169.254`, `::ffff:127.0.0.1`): an allen Stellen umgesetzt.

| Stelle | Normalisiert? | Beleg |
|---|---|---|
| `EgressAllowlist.IsInTrustedNetwork` | ja | `EgressAllowlist.cs:90` (`EgressAddressRules.Normalize`) |
| `EgressAddressRules.IsAlwaysForbidden` / `IsMetadataAddress` / `IsPrivate` / `IsAddressPermitted` | ja | `EgressAddressRules.cs:45, 62, 126, 137` |
| ConnectCallback (`SecureOutboundHttp.SelectPermittedAddresses`) | ja, und es verbindet zur normalisierten IPv4 | `SecureOutboundHttp.cs:125, 131` |
| `SsrfProtectionHandler` Trusted-Pfad | ja (über die beiden oben) | `SsrfProtectionHandler.cs:139, 158` |
| `SsrfProtectionHandler` Nicht-Trusted-Pfad und `DeclarativeHttpDataSourceExecutor` | ja, `IsRestrictedIp` ruft `MapToIPv4` auf | `DeclarativeHttpDataSourceExecutor.cs:523-526` |
| DeclarativeHttp-ConnectCallback | ja (über `IsRestrictedIp`) | `GatewayServiceCollectionExtensions.cs:339` |

Tests decken das ab: `Round4EgressTests.cs:166-167, 379, 395` und `ComprehensiveSecurityAttackVectorTests.cs:568-570`.

**2. Kann man ein mapped Netz in `TrustedInternalNetworks` eintragen und damit Sperren umgehen?** Nein.
- **Direkte mapped Netze werden abgelehnt.** Das betrifft Netze, deren Basisadresse IPv4-mapped ist, z. B. `::ffff:0:0/96` oder `::ffff:10.0.0.0/104` (`EgressAllowlist.cs:200`).
- **Überdeckende Netze kommen seit der Nutzer-Änderung durch die Validierung.** Beispiele: `::fff0:0:0/92` oder `::8000:0:0/81`, deren Basisadresse nicht mapped ist. Siehe K-E01.
- **Solche Netze sind zur Laufzeit aber wirkungslos:**
  - Jede Kandidaten-IP wird vorher zu IPv4 normalisiert.
  - `IPNetwork.Contains` liefert bei unterschiedlicher AddressFamily `false`.
  - Ein mapped Ziel wird also nie über ein IPv6-Netz als vertraut eingestuft.
  - Die Immer-Sperren (`IsAlwaysForbidden`) gelten ohnehin vor jeder Allowlist-Prüfung.

**3. Sollte DeclarativeHttp auf EgressAddressRules/SecureOutboundHttp umgestellt werden?** Ja, siehe K-E02 und K-E03.
- Die letzte Nutzer-Änderung ist korrekt: Der HTTPS-Check kommt jetzt vor dem DNS-Lookup, abgelehnte URLs lösen also keinen Lookup mehr aus.
- Folge im Test: `CreateExecutor` läuft jetzt standardmäßig mit `isDev: true`. Die übrigen Positivtests decken dadurch den Nicht-Development-DNS-Pfad nicht mehr ab (vorher `_environment == null`, also Nicht-Development).

**4. Weitere eigene Private-IP-Implementierungen?**
- Gesucht wurde nach `IsLoopback`, `169.254`, `IsIPv6LinkLocal`, `IsIPv6SiteLocal` und `bytes[0] == 10`.
- Für ausgehenden Verkehr gibt es nur `DeclarativeHttpDataSourceExecutor.IsRestrictedIp` und `EgressAddressRules.IsAlwaysForbidden`.
- `GarnetServerManager.IsLoopbackBinding` und `RedisConnectionSecurity.IsLoopbackHost` prüfen Bind- bzw. Verbindungsziele auf Loopback. Sie haben einen anderen Zweck und sollen nicht zusammengeführt werden.

**5. Ist der SsrfProtectionHandler neben dem ConnectCallback noch nötig?** Ja, aber schlanker (K-E04). Er leistet drei Dinge, die der Callback nicht leistet:
- HTTPS-Pflicht außerhalb von Development;
- die einzige Zielprüfung im Proxy-Betrieb: Der Callback nimmt den Proxy-Endpunkt aus (`SecureOutboundHttp.cs:163-170`);
- eine frühe, verständliche Fehlermeldung.

Ohne Proxy ist sein DNS-Vorab-Lookup redundant (ein zusätzlicher Lookup pro Request).

**6. Registrierungen, die nicht über den gehärteten Primary-Handler laufen:**
- `DeclarativeHttp` sowie HttpPlugins über `SsrfProtectedHttpClientFactory` (K-E02);
- Subgraph-Clients: kein ConnectCallback, und Redirects sind an, weil der Default-Handler `AllowAutoRedirect = true` setzt (K-E06);
- `services.AddHttpClient()` (Default-Client): kein Nutzer gefunden (K-E07).

Alle Extension-Clients sowie AuditWorm und CDN nutzen `AddSecureOutboundHandlers`.

## Befunde

### K-E01: Sperre für den IPv4-mapped-Bereich in der Validierung wiederherstellen (ohne `IPNetwork.Parse`)
- **Kategorie:** Sicherheit (Defense in Depth) / Konsistenz
- **Ort:** `gql/src/Autheris.Application/Security/EgressAllowlist.cs:46-63, 200-221`; `gql/README.md:236`
- **Befund:**
  - Seit `IPNetwork.Parse("::ffff:0:0/96")` aus `ForbiddenNetworks` entfernt ist, erkennt nur noch die Prüfung `BaseAddress.IsIPv4MappedToIPv6` mapped Netze. Sie fängt nur Netze innerhalb des mapped-Bereichs.
  - Ein überdeckendes Netz wie `::fff0:0:0/92` oder `::8000:0:0/81` (Präfix ≥ /32, Basis nicht mapped, überlappt keinen anderen gesperrten Bereich) besteht die Startvalidierung.
  - Zur Laufzeit bleibt das folgenlos (siehe Antwort 2). Es ist aber ein stilles, wirkungsloses Netz, und README:236 verspricht etwas anderes.
- **Vorschlag:**
  - Eine Zeile in `TryParseTrustedNetwork` nach der Prüfung der Basisadresse: `if (parsed.Contains(IPAddress.Any.MapToIPv6())) { error = ...; return false; }`.
  - `IPAddress.Any.MapToIPv6()` ergibt `::ffff:0.0.0.0` und braucht kein `IPNetwork.Parse`. Damit ist die vermutete Ursache der Entfernung (Typinitialisierungsfehler, siehe FIX-E „Build-Risiken“) umgangen.
  - Das deckt beide Überlappungsrichtungen ab: Liegt das Netz innerhalb des mapped-Bereichs, ist die Basis mapped; überdeckt es ihn, enthält es `::ffff:0.0.0.0`.
  - Test: `[InlineData("::fff0:0:0/92")]` in `E01_InvalidOrTooBroadTrustedNetwork_AbortsStartup_InEveryEnvironment`.
- **Aufwand:** S. **Risiko:** sehr gering.

### K-E02: DeclarativeHttp und HttpPlugins auf den gehärteten Primary-Handler umstellen
- **Kategorie:** Sicherheit
- **Ort:**
  - `gql/src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs:299-365` (eigener ConnectCallback)
  - `DeclarativeHttpDataSourceExecutor.cs:521-575` (`IsRestrictedIp`)
  - `gql/src/Autheris.Infrastructure/Plugins/PluginHttpDataSourceExecutor.cs:78-82`: alle Plugin-Clients nutzen den DeclarativeHttp-Client
- **Befund:** Der Pfad prüft nur mit `IsRestrictedIp`. Diese Regel ist deutlich schwächer als `EgressAddressRules.IsAlwaysForbidden`:
  - **Azure WireServer 168.63.129.16 ist erreichbar.** Die Adresse ist öffentlich. Ein DNS-Name, der darauf auflöst, besteht `ValidateDestinationUrlAsync` und den Callback. Auch das IP-Literal wird nicht gesperrt.
  - **IPv4-Multicast und 240.0.0.0/4 sind nicht gesperrt** (nur 255.255.255.255).
  - **NAT64 wird nicht geprüft:** `64:ff9b::a00:1` erreicht in IPv6-only-Clustern mit DNS64/NAT64 das Ziel 10.0.0.1. Ebenso sind `::/96` (IPv4-kompatibel) und `fd00:ec2::/32` offen; Letzteres wird nur über die ULA-Regel abgedeckt.
  - **In Development ist alles erlaubt** (`isDev || ...`, Z. 339), auch ein DNS-Name, der auf 169.254.169.254 oder Loopback auflöst. Der neue Callback sperrt diese Adressen auch in Development.
  - **Ein System-Proxy wird nicht berücksichtigt.** Steht HTTPS_PROXY auf einen privaten Proxy, schlagen in Production alle DeclarativeHttp-Aufrufe fehl (prüfen, ob das jemand betreibt).
  - FIX-E hat den Umbau bewusst zurückgestellt (`FIX-E.md`, letzter Punkt unter „Entscheidungen“).
- **Vorschlag:**
  - Den Inline-Handler ersetzen durch `.ConfigurePrimaryHttpMessageHandler(sp => SecureOutboundHttp.CreatePrimaryHandler(sp, "DeclarativeHttp"))`. Der Name kann als Konstante in `EgressIntegrations` stehen, aber nicht in `AllowlistCapable`.
  - Damit gilt:
    - `EgressAllowlist.Create` liefert für den Namen `Empty`.
    - `Validate` weist ihn in `TrustedIntegrations` zurück. Es entsteht also keine neue Freigabemöglichkeit.
    - Zertifikats-Handling und `AllowAutoRedirect = false` sind identisch. Die same-origin-Redirect-Logik des Executors (`SendWithRedirectProtectionAsync`) bleibt.
  - Verhaltensänderungen:
    - Eine gemischte DNS-Antwort (öffentlich + privat) wird abgelehnt, statt die erste öffentliche IP zu nehmen.
    - Ein Proxy funktioniert.
  - Zusätzlich die Vorab-Prüfung in `ValidateDestinationUrlAsync` um `EgressAddressRules.IsAlwaysForbidden` ergänzen, siehe K-E03.
- **Aufwand:** S–M. **Risiko:**
  - gering: Es gibt keinen Test auf den DeclarativeHttp-ConnectCallback (grep `HttpClientName` in tests: nur OpenJev).
  - Funktional möglich: Ziele mit gemischten DNS-Antworten brechen.

### K-E03: Adress- und URL-Policy an einer Stelle in `Application/Security` zusammenführen
- **Kategorie:** Vereinfachen / Design
- **Ort:**
  - `DeclarativeHttpDataSourceExecutor.cs:434-489` (`ValidateDestinationUrlAsync`), `:491-509` (`ValidateUrl`), `:511-519` (`IsForbiddenMetadataHost`), `:521-575` (`IsRestrictedIp`)
  - `EgressAddressRules.cs:115, 126`; `EgressAllowlist.cs:272`; `SecureOutboundHttp.cs:111`; `SsrfProtectionHandler.cs:66, 109`
- **Befund:**
  - **Umgekehrte Abhängigkeit:** Die Security-Klassen hängen am Datenquellen-Executor (`using Autheris.Application.Services`), nicht umgekehrt.
  - **Zwei parallele Regelwerke** (`IsRestrictedIp` und `IsAlwaysForbidden`/`IsPrivate`) mit unterschiedlicher Abdeckung; das ist die Ursache von K-E02.
  - **`ValidateDestinationUrlAsync` dupliziert Z. 438-455 von `ValidateUrl`** (Z. 493-508).
  - **Die String-Liste `"127.0.0.1" | "::1" | "169.254.169.254"` (Z. 447/500) ist redundant** zu `IsRestrictedIp`. Nur `localhost` ist nicht abgedeckt, `*.localhost` fehlt.
  - Aufrufer (grep):
    - `ValidateUrl`: 12 Aufrufe in src (Lakehouse 5, Catalog 3, AuditWorm 2, SubgraphContextPropagation 1, Executor-intern 0) sowie 4 Stellen in Tests.
    - `ValidateDestinationUrlAsync`: 3 (Executor, `SsrfProtectionHandler`, `SubgraphSecurityDelegatingHandler`).
    - `IsRestrictedIp`/`IsForbiddenMetadataHost`: 7 in src und 16 Zeilen in `SecurityFindingsRemediationTests.cs`.
- **Vorschlag:**
  - Logik nach `EgressAddressRules` verschieben: `IsPrivate` mit dem Body von `IsRestrictedIp`, `IsForbiddenHost` (Metadaten, Kubernetes, `localhost`, `*.localhost`).
  - Neu eine `EgressUrlPolicy` mit zwei Methoden:
    - `ValidateStatic(Uri)`: Schema, Host, Literal gegen `IsAlwaysForbidden || IsPrivate`;
    - `ValidateResolvedAsync(Uri, isDev, ct)`.
  - `IsRestrictedIp` als `IsAlwaysForbidden(ip) || IsPrivate(ip)` definieren. `IsAddressPermitted` ändert sich dadurch nicht, weil `IsAlwaysForbidden` vorher geprüft wird.
  - Die Executor-Methoden bleiben als einzeilige Delegates, damit Tests und Extensions nicht brechen; alternativ die 16 Testzeilen umstellen.
- **Aufwand:** M. **Risiko:** gering bis mittel (reine Verschiebung). Der Mehrwert in der Sicherheit kommt aus der gemeinsamen Regel.

### K-E04: SsrfProtectionHandler auf eine Entscheidungsfunktion reduzieren
- **Kategorie:** Vereinfachen
- **Ort:** `SsrfProtectionHandler.cs:59-84` und `:91-171`; `SecureOutboundHttp.cs:100-152`
- **Befund:**
  - Der Trusted-Pfad des Handlers (Z. 91-171) bildet die Entscheidung von `SelectPermittedAddresses` nach: alle IPs im Netz oder Host vertraut, Immer-gesperrt, Metadaten-Host. Für nicht vertraute Ziele läuft zusätzlich das ältere `ValidateDestinationUrlAsync`.
  - Beide Pfade entscheiden in Development unterschiedlich: Der Handler sperrt private Literale auch in Development, der Callback erlaubt sie.
  - Der Handler leistet drei Dinge, die der Callback nicht leistet: HTTPS-Pflicht, Zielprüfung im Proxy-Betrieb, frühe Fehlermeldung (siehe Antwort 5).
- **Vorschlag:**
  - `SendAsync` wird zu: Schema-Prüfung außerhalb von Development, dann `await SecureOutboundHttp.ResolvePermittedAddressesAsync(host, isDev, _allowlist, _resolver, ct)`.
  - Dadurch gilt dieselbe Regel wie im Callback. DNS-Fehler werden schon im Handler fail-closed, nicht erst beim Connect.
  - Optional den Vorab-Lookup nur ausführen, wenn `HttpClient.DefaultProxy` für die URI einen Proxy liefert. Ohne Proxy prüft der Callback ohnehin.
  - Damit entfallen etwa 80 Zeilen (`IsTrustedInternalDestinationAsync` hat nur 2 Aufrufer, beide im Handler selbst).
- **Aufwand:** M. **Risiko:**
  - mittel: Fehlermeldungen und Development-Verhalten ändern sich.
  - Die E01-Tests in `Round4EgressTests`/`EgressAllowlistTests` prüfen über `SendAsync` auf `SecurityException` und bleiben voraussichtlich grün; Meldungstexte prüfen.

### K-E05: Inline-`ValidateUrl` in Catalog- und AuditWorm-Clients entfernen oder allowlist-fähig machen
- **Kategorie:** Vereinfachen / Design (funktionale Inkonsistenz)
- **Ort:**
  - `PurviewDataCatalogClient.cs:49`, `CollibraDataCatalogClient.cs:44`, `AlationCatalogClient.cs:69`
  - `AuditWormExportService.cs:327, 348`
  - `SubgraphContextPropagationService.cs:38`: doppelt zu `SubgraphSecurityDelegatingHandler.cs:48`, der vorher `ValidateDestinationUrlAsync` aufruft
- **Befund:**
  - Die Aufrufe sperren private IP-Literale ohne Rücksicht auf die Allowlist. Eine freigegebene On-Prem-Basis-URL als Literal scheitert deshalb trotz `TrustedInternalNetworks`. Beispiele: `https://10.20.0.5` für Collibra, oder ein WORM-Endpoint `https://10.20.0.7:9000` mit `AuditWorm` in `TrustedIntegrations`.
  - Hostnamen funktionieren.
  - Sicherheitlich sind die Aufrufe redundant: Alle diese Clients laufen über `AddSecureOutboundHandlers`, also über Handler und Callback.
- **Vorschlag:**
  - Die 5 Client-Aufrufe und den in `SubgraphContextPropagationService` streichen.
  - Die Lakehouse-Aufrufe (`S3LakehouseStorageProvider.cs:144, 206`, `AzureBlobStorageProvider.cs:166, 199`, `IcebergMetadataReader.cs:341`) behalten. Lakehouse hat keine Allowlist, und die frühe Prüfung vor dem Signieren ist sinnvoll.
- **Aufwand:** S. **Risiko:** gering. Tests prüfen, die eine `SecurityException` direkt aus dem Client-Konstruktor oder `GetTablesAsync` mit Literal-URL erwarten: in `gql/tests` stehen 4 `ValidateUrl`-Treffer.

### K-E06: Subgraph-HttpClients sind ungehärtet und ohne Nutzer
- **Kategorie:** Sicherheit (latent) / Entfernen (prüfen)
- **Ort:** `gql/src/Autheris.GraphQL/Federation/FusionGatewayExtensions.cs:26-46`; `SubgraphSecurityDelegatingHandler.cs:38, 48`
- **Befund:**
  - Für jeden `Federation.Subgraphs`-Eintrag wird ein benannter Client registriert. Er hat den Default-Primary-Handler, also Redirects an und keinen ConnectCallback.
  - Die Validierung läuft nur einmal vor dem Senden. Redirect-Ziele und DNS-Rebinding sind ungeprüft. Eigene Header (Subject-SID, Tenant, Rollen) würden bei Redirects mitgehen.
  - `_isDev` hängt an `HasAnyDangerBypassActive` (E-10, Production nicht betroffen).
  - Nutzer: Ein HotChocolate.Fusion-Paket ist nicht referenziert (grep `HotChocolate.Fusion`/`AddFusionGateway` in src: 0). `CreateClient(<subgraphName>)` gibt es nirgends. Die Clients werden aktuell nicht verwendet.
  - Interne Subgraphs (k8s, 10.x) würden in Production zudem an `ValidateDestinationUrlAsync` scheitern; es gibt keine Allowlist-Option.
- **Vorschlag:**
  - Entweder die Registrierung entfernen, bis Fusion tatsächlich verdrahtet ist.
  - Oder sie mit `.AddSecureOutboundHandlers(EgressIntegrations.Federation)` härten (neuer Name, ggf. allowlist-fähig) und `SubgraphSecurityDelegatingHandler` auf Header-Propagation reduzieren.
- **Aufwand:** S (entfernen) bis M (härten). **Risiko:** gering. Test `FederationTests.cs:179` instanziiert den Handler direkt.

### K-E07: Tote DI-Registrierungen entfernen
- **Kategorie:** Entfernen
- **Ort:**
  - `services.TryAddTransient<SsrfProtectionHandler>()` an 7 Stellen: `GatewayServiceCollectionExtensions.cs:275`, `ExtensionsServiceCollectionExtensions.cs:37`, `Itsm…:27`, `DataCatalog…:27`, `OpenMetadata…:49`, `Lineage…:29`, `Lakehouse…:31`
  - `services.AddHttpClient()` in `GatewayServiceCollectionExtensions.cs:298`
- **Befund:**
  - Kein Code löst `SsrfProtectionHandler` aus dem DI-Container auf. Belege per grep: `AddHttpMessageHandler<SsrfProtectionHandler>`, `GetService/GetRequiredService<SsrfProtectionHandler>` und `typeof(SsrfProtectionHandler)` in src und tests ergeben 0 Treffer. Alle Clients erzeugen den Handler über `SsrfProtectionHandler.Create(sp, name)`.
  - Eine DI-Auflösung ergäbe zudem einen Handler ohne Integrationsnamen, also eine Falle.
  - Der unbenannte Default-Client hat keinen Nutzer: Alle `CreateClient`-Aufrufe sind benannt. `IHttpClientFactory` wird schon von den benannten Registrierungen bereitgestellt.
- **Vorschlag:** Alle 8 Zeilen entfernen.
- **Aufwand:** S. **Risiko:** sehr gering. Vorher `ExtensionsRegistrationTests` laufen lassen.

### K-E08: Lakehouse mit On-Prem-MinIO ist außerhalb von Development nicht betreibbar
- **Kategorie:** Design (prüfen)
- **Ort:** `EgressAllowlist.cs:106-116`; `LakehouseServiceCollectionExtensions.cs:33-37`; `S3LakehouseStorageProvider.cs:160-166, 199-206`
- **Befund:**
  - Lakehouse darf die Allowlist nie nutzen (E-02).
  - Ein privates `Lakehouse.Storage.S3Endpoint` (MinIO/Ceph im Firmennetz, laut Klassendoku unterstützt) wird deshalb vom Callback immer geblockt.
  - Die Begründung „Ziel-URLs stammen aus Producer-Daten“ trifft aber nur auf den Pfad zu. Der Host ist bei gesetztem `S3Endpoint` exakt auf den Betreiberwert festgelegt (Host, Port und Schema, `ResolveS3Uri` Z. 160-166).
- **Vorschlag:**
  - Für Lakehouse ausschließlich den konfigurierten `S3Endpoint`-Host als vertrauten Host zulassen, über eine eigene Allowlist aus genau diesem Host, nicht über die globale.
  - Alternativ dokumentieren, dass Lakehouse in Production nur öffentliche Endpunkte unterstützt.
- **Aufwand:** S–M. **Risiko:** gering, weil das Host-Pinning im Provider bereits existiert.

### K-E09: Weitere eingebettete IPv4-Präfixe (niedrig)
- **Kategorie:** Sicherheit (niedrig, prüfen)
- **Ort:** `EgressAddressRules.cs:110-116`
- **Befund:** Nur das Well-known-NAT64-Präfix `64:ff9b::/96` wird ausgewertet. Nicht abgedeckt sind:
  - das lokale NAT64-Präfix `64:ff9b:1::/48` (RFC 8215);
  - 6to4 `2002::/16`, in dem die IPv4 in den Bytes 2-5 eingebettet ist.

  Beide sind nur relevant, wenn im Betriebsnetz ein entsprechendes Gateway existiert.
- **Vorschlag:** Bei der Zusammenführung (K-E03) `64:ff9b:1::/48` als Immer-gesperrt aufnehmen (eingebettete IPv4 ist dort nicht fest positioniert). Für 2002::/16 die eingebettete IPv4 prüfen.
- **Aufwand:** S. **Risiko:** sehr gering.

### K-E10: Hosts und Netze der Allowlist gelten global für alle freigegebenen Integrationen
- **Kategorie:** Design (optional)
- **Ort:** `GatewayOptions.cs:1141-1161`; `EgressAllowlist.cs:119-145`
- **Befund:**
  - `TrustedIntegrations` schaltet pro Integration nur ein oder aus. Die Hosts und Netze sind aber für alle freigegebenen Integrationen dieselben.
  - Ein ITSM-Client darf damit auch den Katalog-Host im 10.20/16-Netz erreichen.
  - Alle Ziel-URLs der freigegebenen Integrationen stammen aus der Konfiguration; geprüft wurde, dass kein Client server-gelieferten URLs folgt (Paging nur per Cursor). Der Nutzen ist daher begrenzt.
- **Vorschlag:** Nur bei Bedarf: `Egress:Integrations:<Name>:{Hosts,Networks}` mit dem bisherigen globalen Block als Fallback.
- **Aufwand:** M. **Risiko:** gering, abwärtskompatibel möglich.

## Priorisierung

1. **K-E02 und K-E03** (gemeinsam umsetzen): beseitigen die einzige echte Lücke (WireServer, Multicast, NAT64 und Development-Metadaten im DeclarativeHttp- und Plugin-Pfad) und den doppelten Regelsatz.
2. **K-E01:** eine Zeile plus ein Test.
3. **K-E07 und K-E05:** Code-Abbau mit kleinem Risiko.
4. **K-E06:** Entscheidung Fusion: entfernen oder härten.
5. **K-E04, K-E08, K-E09, K-E10:** nach Bedarf.

---

## R8 – Konsolidierungs-Review Bereich EXTENSIONS (Präfix K-X)

Stand: Workspace-HEAD `32fb7d2`. Nur gelesen, keine Quelldatei geändert. Die letzte Nutzer-Änderung (`HEAD~1..HEAD`) betrifft in diesem Bereich nur den Dateimodus von `OpenMetadataTableIdentity.cs` und einen Cast in `Round4OpenMetadataTests.cs:97`. Dort gibt es keine Regression.
Alle Angaben „ohne Leser“ oder „ungenutzt“ sind per grep über `gql/src`, `gql/tests`, `gql_extensions/src+tests` und `gql_sqlparser` geprüft.

## Kurzfazit
- **Sicherheit, neu:**
  - **K-X01:** Der OM-Reconcile widerruft Deny-Consents von Tabellen, die im laufenden Sync nur übersprungen wurden. Das ist fail-open und von einem OM-Table-Owner auslösbar.
  - **K-X02:** Der Katalog-Sync (Purview, Collibra, Alation, OM als Katalog) erkennt Art.-9-Tags an Spalten nicht. Die E-06-Logik gibt es nur im OM-Sync.
  - **K-X03:** Alle Webhook-Endpoints sind anonym und immer gemappt. `Dbt.Enabled` und `Lakehouse.Enabled` haben keinen Leser.
- **Größter Konsolidierungshebel:**
  - Webhook-Infrastruktur: 4 Signaturprüfungen, 4 Replay-Caches, 2 kopierte Katalog-Endpoints, Bypass-Flags, die nur über ein Sammel-Flag wirken.
  - Secret-Auflösung: 3 unterschiedliche Semantiken, viele Klartext-Optionen.
  - Begrenztes Lesen: 3 Kopien, ITSM und OpenLineage lesen unbegrenzt.
- **Entfernbar:**
  - der dbt-Webhook-Receiver (macht nichts außer Loggen)
  - `EnrichOrReferenceTableAsync` / `IDataCatalogClient.GetTableAsync` (Ergebnis wird verworfen)
  - `IngestManifestFileAsync`
  - 8 Optionen ohne Leser
- **Offene EX-Befunde:** EX-03, EX-04, EX-05, EX-07, EX-08, EX-09, EX-10, EX-11, EX-14, EX-17 und EX-18 sind im aktuellen Code weiterhin offen (Tabelle am Ende).

---

## Befunde

### K-X01 · Sicherheit · OM-Reconcile widerruft Deny-Consents übersprungener Tabellen (fail-open)
- **Fundstellen:**
  - `gql_extensions/src/Autheris.Extensions/OpenMetadata/OpenMetadataSyncService.cs:88-112`: Tabellen werden übersprungen (ServiceFilter, Mapping, Kollision).
  - `:116-143`: Map- oder Upsert-Fehler pro Tabelle führen nur zu einer Warnung.
  - `:263`: `desired` wird nur über `tableMetadataMap` gebildet.
  - `:379-434`: Jeder Marker-Consent, der nicht in `effective` steht, wird per `RevokeSystemConsentAsync` widerrufen, **auch mit Effect Deny**.
  - `OpenMetadataClient.cs:170-179`: Seitenlimit und doppelter Cursor führen zu `break`. Die Liste kommt dann verkürzt zurück, ohne Fehler.
- **Befund:**
  - Eine Tabelle, die in diesem Lauf nicht verarbeitet wurde, verliert ihre aus OM synchronisierten Denies. Ursachen können sein:
    - transienter DB-Fehler beim Upsert
    - Kollisionsablehnung
    - geänderter ServiceFilter
    - verkürztes Paging
  - Ein Deny verschärft nur. Sein Wegfall öffnet den Zugriff, wenn ein manueller oder Workflow-Allow existiert.
  - **Angriff ohne OM-Admin-Rechte:** Voraussetzung ist eine leere `ServiceDatabaseToDomainMap` (Default). Ein Sandbox-Owner legt in einer anderen Datenbank dieselbe `service.schema.table` an. `RejectDatabaseCollisions` verwirft dann auch die Prod-Tabelle, und der nächste Sync widerruft deren Denies (vgl. EX-06-Angreifermodell).
- **Vorschlag:**
  - Deny-Marker-Consents nur für Tabellen widerrufen, die in diesem Lauf erfolgreich ausgewertet wurden (`tableMetadataMap.ContainsKey`), oder wenn OM die Tabelle nachweislich nicht mehr liefert (vollständiges Paging).
  - Denies übersprungener oder fehlerhafter Tabellen bleiben stehen.
  - Paging-Abbruch (`maxPages`, Cursor-Zyklus) als Exception statt `break`.
  - Kollisionen erzeugen eine Warnung im Sync-Ergebnis plus `Success=false`.
  - Test: Kollision und Upsert-Fehler, Deny bleibt erhalten.
- **Aufwand:** S–M. **Risiko:** gering (nur weniger Widerrufe bei Deny; Allow bleibt fail-closed).

### K-X02 · Sicherheit/Vereinfachen · Zwei Tag-Klassifikationen; Katalog-Sync ignoriert Art. 9 an Spalten
- **Fundstellen:**
  - `gql_extensions/src/Autheris.Extensions/DataCatalog/DataCatalogSyncService.cs:68-69`: Art. 9 nur aus Tabellen-Tags und -Klassifikationen, exakter Vergleich.
  - `:82-83`: Eine Spalte ist nur über `TagToMaskingRuleMap` oder `PiiTags` sensitiv.
  - `:59`: Neue Tabellen von Purview, Collibra und Alation sind sofort aktiv.
  - Gegenstück mit E-06-Logik: `OpenMetadataSyncService.cs:653-720` (hierarchisches `MatchesAnyTag`, Art. 9 an Spalte führt zu HIGH und Four-Eyes plus REDACT, fest verdrahtete `PII.`/`PersonalData.`-Präfixe, Substring „Sensitive“/„FourEyes“).
  - Doppelte Default-Maps: `GatewayOptions.cs:674-680` (`OpenMetadata.TagToMaskingRuleMap`) und `:851-861` (`Catalog.TagToMaskingRuleMap`).
- **Befund:**
  - Eine Spalte mit dem Tag `HealthData` wird im Katalog-Pfad weder sensitiv noch maskiert, und die Tabelle wird nicht HIGH bzw. Four-Eyes.
  - Dieselbe OM-Tabelle wird über `Catalog.Provider=OpenMetadata` (`OpenMetadataCatalogAdapter` → `DataCatalogSyncService`) schwächer klassifiziert als über den OM-Sync.
  - E-06 und E-09 wurden nur im OM-Pfad behoben.
- **Vorschlag:**
  - Ein gemeinsamer `CatalogTagClassifier` in `gql/src/Autheris.Application/DataCatalog/Services/` (neben `CatalogGovernanceRatchet`), der Tabelle und Spalten in Sensitivity, FourEyes, IsSensitive und MaskingRule übersetzt. Ihn nutzen OM-Sync, OM-Webhook und Katalog-Sync.
  - Nur noch eine `TagToMaskingRuleMap` (Catalog). `OpenMetadata.TagToMaskingRuleMap` als Alias auslaufen lassen.
  - Optional: `Catalog.ActivateNewTables` analog zu `OpenMetadata.ActivateNewTables`.
- **Aufwand:** M. **Risiko:** mittel. Mehr Spalten werden maskiert bzw. mehr Tabellen werden Four-Eyes; das ist gewollt, sollte aber kommuniziert werden.

### K-X03 · Sicherheit/Design · Anonyme Webhooks immer gemappt; Enabled-Schalter ohne Wirkung
- **Fundstellen:**
  - `gql/src/Autheris.Api/Extensions/GatewayApplicationBuilderExtensions.cs:319-321`: Mapping ohne Bedingung.
  - `WebhookEndpoints.cs:21, 130-145, 148, 246`: alle `AllowAnonymous`.
  - `DbtEndpoints.cs:247-279`: anonymer dbt-Webhook.
  - `Dbt.Enabled` (`GatewayOptions.cs:952`) und `Lakehouse.Enabled` (`:917`): **0 Leser**. Im Vergleich: `Backstage.Enabled` wird in `BackstageEndpoints.cs:17` ausgewertet.
  - `LakehouseServiceCollectionExtensions.cs:50-51` registriert den Executor immer.
- **Befund:**
  - Webhooks für OM, Katalog, ITSM und dbt sind auch bei deaktivierter Integration erreichbar.
  - Der EventGrid-Handshake (`WebhookEndpoints.cs:165-190`, `263-287`) antwortet unsigniert, auch ohne Katalog.
  - `JsonDocument.Parse(payload)` ist dort ungeschützt; kaputtes JSON führt anonym zu einer 500-Antwort.
  - Die Signaturprüfungen sind fail-closed, die anonyme Angriffsfläche (Parsing, Dedup-Caches) besteht aber unnötig.
  - Der Parameter `gatewayOptions` von `MapWebhookEndpoints` wird nicht verwendet.
- **Vorschlag:**
  - Endpoints nur mappen bei `OpenMetadata.Enabled`, `Catalog.Enabled`, `Itsm.Enabled` bzw. `Dbt.Enabled` (Muster wie Backstage).
  - Den Lakehouse-Executor nur bei `Lakehouse.Enabled` registrieren.
  - Den Handshake in try/catch kapseln.
- **Aufwand:** S. **Risiko:** gering. Betreiber mit aktivem Webhook, aber `Enabled=false`, müssen die Option setzen; das ist in der Release-Note zu nennen.

### K-X04 · Entfernen · dbt-Webhook-Receiver ist funktionslos
- **Fundstellen:**
  - `gql_extensions/src/Autheris.Extensions/Dbt/DbtWebhookReceiver.cs:73-158`: prüft Signatur und Replay, loggt und gibt OK zurück. Keine Folgeaktion (kein Ingest, kein Circuit-Breaker).
  - `:18, 28`: `_circuitBreaker` wird injiziert, aber **nie benutzt**.
  - `:80-82`: zweite Secret-Quelle über die Umgebungsvariable `DBT_WEBHOOK_SECRET`.
  - `:34-35`: Das eigene Flag wird redundant zu `IsWebhookSignatureBypassed` geprüft, das es schon enthält (`GatewayOptions.cs:81`).
- **Belege:** Aufrufer sind nur `DbtEndpoints.cs:247ff`, `DbtServiceCollectionExtensions.cs:27`, `DbtWebhookReceiverTests.cs` (7 Treffer) und `SecurityReview20261002ExtensionsTests.cs` (2).
- **Vorschlag:**
  - Endpoint `/api/extensions/dbt/webhooks/dbt-cloud`, `IDbtWebhookReceiver`, `DbtWebhookReceiver` und `DbtWebhookReceiverTests` entfernen.
  - `Dbt.WebhookSecret` und `Dbt.danger_bypass_webhook_signature_validation` entfernen, ebenso den Eintrag in `IsWebhookSignatureBypassed`.
  - Alternativ, falls fachlich gewünscht: an `IDbtHealthCircuitBreaker` bzw. den Ingest anbinden. Dann gehört der Receiver aber zu K-X08.
- **Aufwand:** S. **Risiko:** gering. Externe dbt-Cloud-Hooks erhalten 404 statt 200, eine Funktion geht nicht verloren.

### K-X05 · Entfernen · Toter Katalog-Code (Enrich, GetTableAsync, Datei-Ingest, Modellfelder)
- **`IDataCatalogSyncService.EnrichOrReferenceTableAsync`**
  - Fundstellen: `gql/src/Autheris.Application/DataCatalog/Interfaces/IDataCatalogInterfaces.cs:20`, Implementierung `DataCatalogSyncService.cs:190-198`.
  - Einziger Aufrufer ist `CatalogWebhookHandler.cs:137`, und der **verwirft das Ergebnis**.
  - Trotzdem kostet es je Tabelle im (signierten) Webhook einen ausgehenden Katalog-Call. Bei Purview ist das eine vollständige Suche (`PurviewDataCatalogClient.cs:80-84`).
- **`IDataCatalogClient.GetTableAsync`**
  - 4 Implementierungen; einziger Aufrufer ist die obige Methode.
  - Die OM-Variante baut eine 3-teilige FQN (`OpenMetadataCatalogAdapter.cs:80`), die zu einer 4-teiligen OM-FQN nie passt.
- **`IDbtMetadataIngestionService.IngestManifestFileAsync`** (`DbtMetadataIngestionService.cs:62-76` inkl. `ValidateSafeFilePath`): 0 Aufrufer in src und tests.
- **Weitere Reste:**
  - `CatalogWebhookHandler.ExtractAffectedTables(rawPayload, provider)` (`:289`): Parameter `provider` ungenutzt.
  - `CatalogTableAsset.CustomProperties` und `CatalogColumnAsset.IsPrimaryKey` (`DataCatalogModels.cs:16, 30`): 0 Leser und Schreiber.
  - `OwnerRef` und Spalten-`Classifications`: nur geschrieben (Adapter), 0 Leser.
- **Vorschlag:**
  - Entfernen; der Test `CatalogWebhookHandlerTests.cs:79` (`Received(1).EnrichOrReferenceTableAsync`) entfällt.
  - Der Webhook bumpt nur noch Epochen.
- **Aufwand:** S. **Risiko:** gering.

### K-X06 · Entfernen/Vereinfachen · Integrations-Optionen ohne Leser
- **Optionen ohne Leser** (geprüft per grep auf `.Name` und den Namen in `*.cs`/`*.json`):
  - `Collibra.CommunityId` (`GatewayOptions.cs:779`)
  - `Alation.CustomFieldIdPii` (`:786`)
  - `Catalog.SyncMode` (`:805`) plus `enum DataCatalogSyncMode` (`GovernanceV2Models.cs:105`): Der Reference-Modus wurde in EXT-MOVE nicht übernommen.
  - `Lakehouse.MaxConcurrentFileScans` (`:919`)
  - `Lakehouse.MaxScanRowsLimit` (`:920`), siehe EX-18
  - `LakehouseTableOptions.PartitionColumns` (`:911`): wird nur in Tests gesetzt.
  - `Dbt.Enabled` und `Lakehouse.Enabled`, siehe K-X03
- **Ungenutzte Parameter:**
  - `AddGatewayExtensions(..., IHostEnvironment? environment = null)` (`ExtensionsServiceCollectionExtensions.cs:31`); der Kern ruft ohne ihn auf (`GatewayServiceCollectionExtensions.cs:495`).
  - `MapWebhookEndpoints(app, gatewayOptions)`.
- **Ungenutzter Import:** `using Autheris.Application.OpenMetadata.Interfaces;` in `GatewayServiceCollectionExtensions.cs:9`, dort 0 Verwendungen.
- **Nicht tot (zur Abgrenzung):**
  - `Itsm.InstanceToTenantMap` wird über `GetTenantForInstance` gelesen (`ItsmWebhookHandler.cs:285`).
  - Die per-System-`warn_*`/`danger_*`-Flags werden in den Aggregaten gelesen (siehe K-X07).
- **Vorschlag:** löschen; `MaxScanRowsLimit` stattdessen als Obergrenze in `ScanCoreAsync` umsetzen (`LakehouseDataSourceExecutor.cs`, `limit`).
- **Aufwand:** S. **Risiko:** gering. Alte Config-Schlüssel werden von der Bindung ignoriert.

### K-X07 · Vereinfachen/Sicherheit · Webhook-Bypass- und Legacy-Schalter
- **Fundstellen:**
  - `GatewayOptions.cs:81`: `IsWebhookSignatureBypassed` = `Insecure.danger_bypass_webhook_signature_validation || Insecure.danger_allow_anonymous_webhooks || Itsm.… || OpenMetadata.… || Dbt.…`
  - `:83`: `IsWebhookTimestampToleranceIgnored` analog.
- **Befund (E-10 unverändert offen):**
  - Die per-System-Flags `Itsm.danger_bypass_webhook_signature_validation` (`:740`), `OpenMetadata.…` (`:695`) und `Dbt.…` (`:954`) sowie die `warn_ignore_webhook_timestamp_tolerance`-Flags (`:696`, `:741`) werden **nur** in diesen Aggregaten gelesen.
  - Jedes einzelne Flag wirkt damit auf **alle** Webhooks (ITSM, OM, Katalog, dbt).
  - Für den Katalog gibt es kein eigenes Flag. `danger_allow_anonymous_webhooks` ist ein zweiter Alias.
- **Vorschlag:** ein Mechanismus.
  - `enum WebhookSource { Itsm, OpenMetadata, Catalog, Dbt }` plus `GatewayOptions.IsWebhookSignatureBypassed(WebhookSource)` und `IsWebhookTimestampToleranceIgnored(WebhookSource)`.
  - Logik: global `Insecure.*` ODER das Flag der Quelle.
  - Die Bypass-Liste meldet die Einträge pro Quelle.
  - Den Alias `danger_allow_anonymous_webhooks` entfernen.
  - Aufrufer:
    - `WebhookEndpoints.cs:40, 84, 202, 300`
    - `ItsmWebhookHandler.cs:140, 147`
    - `OpenMetadataSyncService.cs:488, 532`
    - `CatalogWebhookHandler.cs:58`
    - `DbtWebhookReceiver.cs:34` (entfällt mit K-X04)
- **Legacy-Schalter:**
  - `Catalog.AllowLegacyPayloadOnlySignature` (`:827`, Leser `CatalogWebhookHandler.cs:94`): **Entfernen empfohlen.** Ohne Zeitstempel in der Signatur beruht der Replay-Schutz nur auf dem prozesslokalen Dedup-Cache mit 15 Minuten TTL (`CatalogWebhookHandler.cs:31-34, 228-250`). Nach TTL-Ablauf, Neustart oder auf einem anderen Knoten ist eine signierte Zustellung unbegrenzt wiederholbar.
  - `Itsm.LegacyGlobalWebhookSecret` (`:751`, Leser `ItsmWebhookHandler.cs:412`) erlaubt instanzübergreifendes Signieren, also Fremd-Mandanten-Tickets. **Prüfen**, ob ein Betreiber es nutzt; sonst mit Ablaufdatum entfernen.
  - Tests mit Bezug: `IntegrationGapATests`, `BypassSemanticsAndDmlGuardrailTests`, `SecurityReview20261002ExtensionsTests` bzw. `…EndpointTests`.
- **Aufwand:** M. **Risiko:** gering bis mittel; Config-Bruch bei Betreibern mit Legacy-Flags.

### K-X08 · Vereinfachen/Sicherheit · Gemeinsame Webhook-Bausteine (Verifier, Replay-Cache, Endpoint)
- **4 HMAC-Prüfungen mit abweichender Semantik:**
  - `CatalogWebhookHandler.cs:254-287`: Hex **oder** Base64, `"{ts}.{payload}"`.
  - `OpenMetadataSyncService.cs:614-636`: vergleicht Hex-Strings, nur Payload.
  - `DbtWebhookReceiver.cs:32-69`: Hex, nur Payload.
  - `ItsmWebhookHandler.cs:187-221`: Hex, `"t={ts}.v1={payload}"`, zwei Zeitstempel-Varianten.
- **4 Replay-Caches**, drei davon statisch und prozesslokal:
  - `CatalogWebhookHandler.cs:34`
  - `OpenMetadataSyncService.cs:38` (Bereinigungsblock `:562-573` falsch eingerückt)
  - `DbtWebhookReceiver.cs:71`
  - dazu `ItsmWebhookReplayCache`
- **Replay-Cache vor der Verarbeitung (EX-14):** Katalog `:116-121`, OM `:556`, dbt `:126` tragen das Event vor der Verarbeitung ein. Eine Zustellung, die mit einem Fehler endet, wird beim Retry als Duplikat verworfen.
- **Kopierte Endpoints:** `/api/webhooks/catalog` und `/api/v1/governance/catalog/webhook/{provider}` sind ca. 95 Zeilen Copy-Paste (`WebhookEndpoints.cs:148-244` und `246-340`), inklusive EventGrid-Handshake.
- **Vorschlag:**
  - `Autheris.Application.Security.WebhookSignature.VerifyHmacSha256(ReadOnlySpan<byte> key, string signedContent, string header, SignatureEncoding allowed)`. Den signierten Inhalt baut weiterhin jeder Handler protokollspezifisch.
  - Ein `WebhookReplayCache` mit Muster „Reserve → Commit nach Erfolg / Release bei Fehler“, optional über den vorhandenen Redis/`IDistributedCache`. Das behebt den Dedup-Teil von EX-14 für alle Quellen.
  - Katalog-Endpoint als lokale Funktion wie `ProcessItsmWebhookAsync`.
- **Aufwand:** M. **Risiko:** mittel; Signaturformate exakt per Tests absichern (vorhandene Tests decken Hex/Base64/Legacy ab).

### K-X09 · Vereinfachen/Sicherheit · Secret-Auflösung uneinheitlich
- **Fundstellen und Verhalten:**
  - `OpenMetadataClient.cs:52-75`:
    - In Development wird bei Lookup-Fehler der Rohwert als Token gesendet.
    - Es gibt keine Placeholder-Prüfung (aufgelöster Wert == Referenz).
    - Mit `environment == null` gibt es kein fail-closed.
  - `AlationCatalogClient.cs:139-183`: strikt (Placeholder, leer, fehlender Provider führt zu `SecurityException`).
  - `ItsmWebhookHandler.cs:421-436`: eigene `TryGetSecret`/`IsPlaceholder`-Logik.
  - `DbtWebhookReceiver.cs:80-82`: Fallback auf die Umgebungsvariable.
- **Klartext aus Options, nie über `IKeyVaultSecretProvider`:**
  - `OpenMetadata.WebhookSecret` und `Catalog.WebhookSecret` (`CatalogWebhookHandler.cs:79-81`, mit Fallback auf das OM-Secret, EX-14)
  - `Dbt.WebhookSecret`
  - `Purview.ClientSecret` (`PurviewDataCatalogClient.cs:128`)
  - `Collibra.ApiToken`/`Password` (`CollibraDataCatalogClient.cs:71-77`)
  - `Itsm.JiraApiToken` (`JiraCloudRestClient.cs:89-91`)
  - `Itsm.ServiceNowPassword` (`ServiceNowTableApiClient.cs:68-70`)
- **Purview:**
  - Ohne Credentials wird in allen Umgebungen das Mock-Token `purview-dev-mock-bearer-token` gesendet (`PurviewDataCatalogClient.cs:114-119`).
  - Der Token-Cache ist statisch und prozessweit (`:29-31`).
- **Vorschlag:**
  - `Autheris.Application.Security.SecretReferenceResolver.Resolve(IKeyVaultSecretProvider?, string reference, IHostEnvironment?, bool allowPlaintextInDevelopment)` mit Alation-Semantik.
  - Nutzen in allen Extension-Clients und Webhook-Handlern; Optionen als Referenzen dokumentieren.
  - Purview-Mock nur in Development. HTTP-Fehler propagieren statt `[]` (`:69-73`), wie bei Alation.
- **Aufwand:** M. **Risiko:** mittel; Betreiber mit Klartext-Secrets brauchen eine Referenz bzw. das Development-Opt-in.

### K-X10 · Vereinfachen · Begrenztes Lesen in drei Kopien, ITSM und OpenLineage unbegrenzt
- **Fundstellen:**
  - `DataCatalog/CatalogHttpContent.cs`: vorhanden, wird aber nur von Purview, Collibra und Alation genutzt.
  - `OpenMetadataClient.cs:137-155`: eigene Kopie, dazu die Content-Length-Prüfung doppelt (`:113-116`, `:190-193`).
  - `Lakehouse/Services/LakehouseLocationGuard.cs:105-140`: dritte Variante.
  - Unbegrenzt: `ReadAsStringAsync` in `JiraCloudRestClient.cs:100`, `ServiceNowTableApiClient.cs:79` und `OpenLineageClient.cs:129`.
- **Vorschlag:** `CatalogHttpContent` als `BoundedHttpContent` in die Extensions-Wurzel verschieben (String- und Stream-Variante mit Limit-Parameter) und in OM, ITSM, OpenLineage und Lakehouse nutzen.
- **Aufwand:** S. **Risiko:** gering.

### K-X11 · Design · OpenMetadataSyncService zerlegen (1080 Zeilen)
Die Zerlegung ist sinnvoll. Die Klasse vereint vier unabhängige Verantwortungen; die Grenzen sind schon durch interne Typen markiert:

| Neue Datei | Inhalt (aktuelle Zeilen) |
|---|---|
| `OpenMetadataTableMapper` | `MapToTableMetadata`, `MatchesAnyTag` (`:638-764`), danach durch den gemeinsamen Klassifikator aus K-X02 ersetzt |
| `OpenMetadataPrincipalIndex` | `TeamSidIndex`, `UserSidIndex`, `SidKeyMap`, `TryStripPrefix` (`:766-971`) |
| `OpenMetadataConsentReconciler` | `ReconcileConsentsAsync`, Gültigkeiten, `IsExcludedFromAutoGrant`, `ResolvePolicies`, `IsRuleApplicableToTable`, `TryGetDataAccessEffect`, `SyncConsentKey`, `CreateConsentFor*` (`:197-482`, `:973-1079`); hier sitzen K-X01 und EX-03 |
| `OpenMetadataWebhookHandler` | `HandleWebhookEventAsync`, `VerifyWebhookSignature` (`:484-636`), dann auf K-X08 umgestellt |
| `OpenMetadataSyncService` | nur noch Orchestrierung (Lock, Tabellen, Epochen), ca. 150 Zeilen |

- **Weitere Doppelung:** Die Schleife „Kandidaten auflösen und Kollisionen ablehnen“ steht identisch im Sync (`:88-112`) und im Adapter (`OpenMetadataCatalogAdapter.cs:50-67`). Sie gehört als `OpenMetadataTableIdentity.ResolveAll(tables, options, onSkip)` an eine Stelle.
- **Testauswirkung gering:** Tests nutzen nur `OpenMetadataSyncService.IsExcludedFromAutoGrant` (6), `.OpenMetadataSyncConsentMarker` (6) und `.VerifyWebhookSignature` (5). Diese können als Weiterleitungen bleiben.
- **Aufwand:** M. **Risiko:** gering bei reinem Verschieben.

### K-X12 · Design · Tabellen-Identität mehrfach und widersprüchlich abgeleitet
- **Fundstellen:**
  - `CatalogWebhookHandler.cs:301-319` parst OM-FQNs selbst (`parts[0]`, `parts[^2]`, `parts[^1]`) und ignoriert `ServiceDatabaseToDomainMap` bzw. `OpenMetadataTableIdentity`.
  - `CatalogWebhookHandler.cs:349-353`: Purview ergibt `(parts[0], "public", parts[^1])`.
  - Der Purview-Sync ergibt dagegen `(parts[0], parts[1], name)` (`PurviewDataCatalogClient.cs:168-171`). Bei echten qualifiedNames (`mssql://host/db/schema/table`) liefert er zudem Domain `mssql:` und ein leeres Schema.
- **Befund:**
  - Webhook-Invalidierungen treffen nicht die Tabellen, die der Sync anlegt (Epoch-Bump ins Leere).
  - Zusätzlich gibt es zwei OM-Importpfade mit derselben Identität, aber unterschiedlicher Klassifikation: OM-Sync und `Catalog.Provider=OpenMetadata`.
  - `DataCatalogSyncService.cs:85-90, 111-134` implementiert die Ratchet-Regeln (Sensitivity, FourEyes, IsSensitive, Masking-Merge) ein zweites Mal vor `CatalogGovernanceRatchet.Merge` (`:159`).
- **Vorschlag:**
  - Die Identität gehört zum Client: `IDataCatalogClient.TryResolveIdentity(JsonElement event, out TableIdentifier)`. Alternativ OM-Events am Katalog-Endpoint an `IOpenMetadataSyncService` delegieren.
  - **Prüfen:** `Catalog.Provider=OpenMetadata` zugunsten des OM-Syncs streichen. Damit entfallen der Adapter und ein Pfad.
  - Die manuelle Ratchet-Kopie im Katalog-Sync löschen.
- **Aufwand:** M. **Risiko:** mittel; Identitätsänderungen bei Purview betreffen bestehende Einträge.

### K-X13 · Design · Lakehouse-Executor liefert synthetische Daten und ist immer aktiv
- **Fundstellen:**
  - `LakehouseDataSourceExecutor.cs:243-273`: Zeilen aus `GenerateSampleValue` (`:291ff`, z. B. IBAN `DE89…`, „Hypertension Mild“).
  - Partitionswerte werden übernommen, der Tenant wird überschrieben (`:270`).
  - Die Registrierung ignoriert `Lakehouse.Enabled` (K-X03).
- **Befund:** Der Executor ist ein Stub im produktiven DI-Graphen. EX-04, EX-09 und EX-17 wirken erst mit einem echten Scan voll, bleiben aber Designschulden des Pfads.
- **Vorschlag:**
  - Registrierung nur bei `Lakehouse.Enabled`.
  - Außerhalb von Development ohne echten Reader mit `NotSupportedException` abbrechen, oder den Pfad als experimentell in die Bypass/WARN-Liste aufnehmen.
- **Aufwand:** S. **Risiko:** gering.

---

## Status der offenen EX-Befunde (aktueller Code)

| ID | Status | Beleg (Datei:Zeile) |
|---|---|---|
| EX-03 | offen | `OpenMetadataSyncService.cs:1049-1079`: `CreateConsentForRole/Sid` setzt keinen `TenantId`. Der Default ist `LegacySingleTenant` (`ConsentModels.cs:61`). `SyncConsentKey` (`:1038`) enthält keinen Mandanten. |
| EX-04 | offen | `LakehouseDataSourceExecutor.cs:65-90` maskiert im Executor. `RlsPushdownExecuted`/`InDbColumnMaskingExecuted` werden nicht gesetzt (0 Treffer in `gql_extensions/src`); die Pipeline liest sie in `GatewayExecutionService.cs:339-360`. |
| EX-05 | offen | `ODataHandler.cs:39-53` filtert nur nach Tenant und Domain (bei `LegacySingleTenant` alles, Domain `default` immer), ohne Consent-, Spalten- oder IsActive-Filter. Nutzer: `ODataEndpoints.cs:26-41`. |
| EX-07 | offen | `OpenMetadataSyncService.cs:695-702, 718`: ColumnName, DataType und DisplayName ungeprüft. `CatalogGovernanceRatchet.cs:34`: eingehender DisplayName überschreibt den des Gateways. `DataCatalogSyncService.cs:101-104, 145`: analog. |
| EX-08 | offen | `DbtEndpoints.cs:24-27`: DataOwner bzw. DbtAdmin dürfen `/sync`. `IDbtMetadataIngestionService.cs:13`: kein Principal-Parameter, also keine Ownership-Prüfung möglich. |
| EX-09 | offen | `IcebergPartitionPruner.cs:83-89`: min/max-Bounds genügen für die Pflichtspalte (Mischdateien). `:175, 184, 193`: `OrdinalIgnoreCase` statt binär. `LakehouseDataSourceExecutor.cs:270`: Tenant-Spalte wird überschrieben statt geprüft. |
| EX-10 | offen | `S3LakehouseStorageProvider.cs:204`: `$"{endpoint}/{bucket}/{key}"` ohne Ablehnung von `?`/`#`/`%`. `:286`: Die Query wird mitsigniert. `AzureBlobStorageProvider.cs:197`: analog für den Blob-Namen. Weder in `IcebergMetadataReader` noch in `LakehouseLocationGuard` gibt es eine Query/Fragment-Prüfung. |
| EX-11 | offen | `IcebergMetadataReader.cs:377-378`: Bei `tablePrefix.Length > 0` und leerem Präfix ist der ganze Bucket frei. `:463-482`: Ein Segment mit Punkt gilt als Datei. |
| EX-14 | offen | Siehe K-X07 bis K-X09: OM-Secret-Fallback `CatalogWebhookHandler.cs:79-81`, Dedup vor der Verarbeitung `:116-121` und `OpenMetadataSyncService.cs:556`, kein Limit für Tabellen pro Payload (`CatalogWebhookHandler.cs:289-366`), voller Sync pro Policy-/Role-/Team-/User-Event `OpenMetadataSyncService.cs:602-609`, unsignierter Handshake `WebhookEndpoints.cs:165-190, 263-287`. |
| EX-17 | offen | `LakehouseDataSourceExecutor.cs:38-46` mit `TableAccessDecision.cs:38-52`: Eine Spalte, die nicht im Katalog steht (`GetColumn` → null), gilt als Clear; der Filter auf ein Partitionsfeld bleibt erhalten. |
| EX-18 | offen | `MaxScanRowsLimit` (`GatewayOptions.cs:920`) hat 0 Leser. `DbtMetadataIngestionService.cs:422-437`: Approve ohne Statusprüfung (auch abgelehnte Vorschläge), `RuleType = SuggestedRuleType` ungeprüft. `LocalStorageProvider.cs:64-76`: Lesen ohne Größenlimit, TOCTOU zwischen `File.Exists` und Öffnen. |

## Empfohlene Reihenfolge
1. **Sicherheit, klein:** K-X01, K-X03, K-X04, K-X05.
2. **Webhook- und Secret-Konsolidierung (behebt EX-14 und E-10 mit):** K-X07, K-X08, K-X09, dazu K-X10.
3. **Klassifikation und Identität:** K-X02, K-X12, dann K-X11 (Zerlegung, enthält EX-03).
4. **Lakehouse:** K-X13 und K-X06 (`MaxScanRowsLimit`), dann EX-04/09/10/11/17.

---

## R8-KERN – Konsolidierungs-Review Kern (gql/src ohne GovernedSql, Egress/SSRF, DataCatalog/Webhook/Dbt/OData/Cdc)

Nur gelesen, keine Quelldateien geändert. Aufrufer wurden per grep über gql/src, gql/tests, gql_extensions/src+tests und gql_sqlparser gezählt.
Letzte Nutzer-Änderung (HEAD~1..HEAD) im Kern: `DeclarativeHttpDataSourceExecutor` (die https-Prüfung läuft jetzt vor der DNS-Auflösung, korrekt), `rbac_with_abac.conf` (nur Zeilenenden geändert), Testanpassungen (siehe K-K07). Durch diese Änderungen ist im Kern keine Regression entstanden.

## Übersicht (priorisiert)
| ID | Kategorie | Kurz | Aufwand | Risiko |
|---|---|---|---|---|
| K-K01 | Sicherheit | Token-Widerruf wirkt nur auf WebSockets, nicht auf HTTP | S–M | gering |
| K-K02 | Sicherheit/Vereinfachen | 7 Varianten zur Ermittlung der Client-IP; ABAC sieht die Proxy-IP bzw. 127.0.0.1; MCP-IP wird nie gesetzt | M | mittel |
| K-K03 | Sicherheit/Vereinfachen | Tenant-Claim wird ~10× mit abweichender Reihenfolge und Fallback gelesen; SQL-Endpoints fallen auf Header bzw. "default" zurück | M | mittel |
| K-K04 | Sicherheit | WARN-Schalter `warn_allow_all_cors_origins` schaltet den kompletten CSRF-Schutz ab (in Prod erlaubt) | S | gering |
| K-K05 | Sicherheit/Entfernen | Optionen ohne Leser, darunter sicherheitsrelevante (`Mcp.MaxResultRows`, `Casbin.*`, Claim-Typen, `SensitiveTableTtlSeconds`) | S–M | gering |
| K-K06 | Sicherheit/Design | Hash-Ketten-Prüfung und WORM-Export werden nie ausgelöst | M | gering |
| K-K07 | Vereinfachen | Bypass-Mechanik in GatewayOptions tabellengetrieben; Labels vereinheitlichen; Webhook-Sammelschalter | M | gering |
| K-K08 | Vereinfachen/Entfernen | Parquet: doppelte Negotiation und Prüfungen, totes Export-Endpoint, synchrone API, Speicherkopien | S–M | gering |
| K-K09 | Design | Puffer-Grenzen Extensibility (16 MB) und Parquet (64 MB) passen nicht zusammen; Egress wird stillschweigend übersprungen; 3 Streaming-Detektoren | S–M | gering |
| K-K10 | Vereinfachen | Drei Mechanismen für Rollenprüfungen, ~60 hart codierte `IsInRole` neben `GatewayPolicies` | M | mittel |
| K-K11 | Entfernen | `DynamicPluginAssemblyLoadContext` ist tot und dupliziert PluginTrustList/PluginAssemblyLoadContext | S | gering |
| K-K12 | Vereinfachen | S3-SigV4 doppelt implementiert, Region fest auf `us-east-1` | S | gering |
| K-K13 | Vereinfachen | `SecurityEvaluationContext` wird an 6 Stellen per Copy-Paste aufgebaut | M | mittel |
| K-K14 | Design (prüfen) | Prozesslokaler Sicherheitszustand im HA-Betrieb (HitL-Tickets, MCP-Sessions) | M | mittel |

---

## K-K01 – Sicherheit: Token-Widerruf wird nur für WebSocket-Subscriptions durchgesetzt
- **Ort:** `Autheris.Api/Endpoints/TokenRevocationEndpoints.cs:29` (POST `/api/admin/tokens/revoke`); Prüfstellen nur in `Autheris.GraphQL/Subscriptions/WebSocketAuthInterceptor.cs:102,130,277`.
- **Befund:** `IsRevokedAsync` hat genau diese 3 Aufrufer (grep über src und Extensions). Ein widerrufener Token (jti oder Subject) funktioniert für GraphQL-HTTP, WebSQL, SQL-Endpoints, OData, MCP und die Admin-APIs bis zu seinem `exp` weiter. M-14 war zwar ursprünglich nur für WebSockets beauftragt (GAP-B). Der Admin-Endpoint wird aber als „Token widerrufen“ angeboten und auditiert (`TOKEN_REVOKED`), der Name verspricht also mehr, als er hält.
- **Vorschlag:** Eine einzige Prüfstelle direkt nach `UseAuthentication()`: entweder ein `JwtBearerEvents.OnTokenValidated`-Hook oder eine kleine Middleware vor `UseAuthorization()`, die bei `IsRevokedAsync == true` mit 401 antwortet. Die Redis-Implementierung ist bei einem Ausfall bereits fail-open mit lokalem Layer, das Verhalten bleibt also gleich wie bei WS. Der WS-Interceptor behält nur die periodische Prüfung.
- **Aufwand:** S–M. **Risiko:** gering (ein zusätzlicher Redis-GET pro Request, maximal 3 Keys; bei Bedarf mit kurzem L1-Cache).

## K-K02 – Sicherheit/Vereinfachen: Client-IP wird an 7 Stellen mit unterschiedlicher Semantik ermittelt
- **Orte:**
  - `Api/Security/HttpContextClientIpResolver.cs:24-38`: bevorzugt `Items["OriginalTcpRemoteIp"]`, also die TCP-Gegenstelle **vor** `UseForwardedHeaders`. Ergebnis ist die Proxy-IP. Fallback ist der `ip`-Claim, sonst Loopback.
  - `Api/Middleware/RateLimitingMiddleware.cs:46-65`: nutzt `RemoteIpAddress` nach ForwardedHeaders, also die echte Client-IP. Der Zweig `OriginalTcpRemoteIp` ist redundant, weil ohne ReverseProxy beide Werte identisch sind.
  - `GatewayExtensibilityMiddleware.cs:87` (Break-Glass), `ResourceGroupMiddleware.cs:237` und `GraphQL/Interceptors/CostAndQuotaMiddleware.cs:68` nutzen `RemoteIpAddress`.
  - `Application/Services/GatewayExecutionService.cs:207,767` und `Sql/Services/GovernedSqlExecutionService.cs:1104`: `resolver ?? ip-Claim ?? Loopback`. Der Claim-Fallback ist tot, weil der Resolver nie null liefert.
  - `Application/Connectors/CrossDomain/DefaultCrossDomainAccessResolver.cs:90`: nur der `ip`-Claim, sonst **Loopback**.
  - `Application/Mcp/Services/AiDataGuardrailService.cs:220`: `McpSessionContext.ClientIp`, sonst **Loopback**. `ClientIp` wird in src **nie gesetzt**, nur in Tests (der Konstruktor in `McpSessionStore.cs:73` übergibt 8 von 9 Parametern).
  - `Application/Streaming/Services/StreamRlsPolicyEnforcer.cs:295-321`: Loopback wird verworfen, Fallback ist **`IPAddress.None`**.
- **Folge:** `SecurityEvaluationContext.ClientIp` landet als `r.ctx` in Casbin-`sub_rule`s (`CasbinEnforcementService.cs:339,524`). Damit gilt:
  - Hinter einem korrekt konfigurierten Reverse Proxy sehen ABAC-Regeln für GraphQL und WebSQL die interne Proxy-IP. Rate-Limit und Break-Glass sehen dagegen die echte IP.
  - Für MCP-Agenten und Cross-Domain-Zugriffe ist die IP immer 127.0.0.1. Eine IP- bzw. Netzzonen-Regel („nur intern“) würde dadurch alle Agenten als lokal einstufen.
  - Derzeit gibt es keine `sub_rule` mit IP-Bezug im Repo (grep `ctx.ClientIp`: 0 Treffer). Die Lücke ist also latent, wird aber scharf, sobald ein Kunde solche Regeln anlegt.
- **Vorschlag:**
  - Es bleibt genau ein `IClientIpResolver`: `RemoteIpAddress` nach ForwardedHeaders, unbekannt = `IPAddress.None` (nie Loopback).
  - Alle Stellen oben nutzen ihn bzw. `Items[ClientIpItemKey]`.
  - `OriginalTcpRemoteIp` wird nur noch für die Proxy-Vertrauensprüfung in `ForwardAuthAuthenticationHandler.cs:79-90` gebraucht.
  - `McpSessionStore` setzt `ClientIp` beim Anlegen der Session (Aufrufer `McpEndpoints` hat den HttpContext).
  - Den RateLimit-Zweig `OriginalTcpRemoteIp` streichen.
- **Aufwand:** M. **Risiko:** mittel (IP-abhängige Regeln und Cache-Keys ändern ihr Verhalten; ein Test für den Resolver fehlt bisher, grep `HttpContextClientIpResolver` in tests: 0).

## K-K03 – Sicherheit/Vereinfachen: Tenant-Claim wird uneinheitlich ausgelesen
- **Varianten:**
  - `Domain/Common/Sid.cs:122` `GetTenantId()`: tenant_id → tid → tenant → MS-URI; ungültig → Exception; fehlt → `legacy-single-tenant`.
  - `TenantResolutionMiddleware.cs:19`: gleiche Reihenfolge wie `GetTenantId()`, zusätzlich Header nur für Admins. Ergebnis landet in `Items["TenantId"]`.
  - `EndpointSecurity.GetRequestTenant` (`Api/Endpoints/EndpointSecurity.cs:25`): kanonisch, Items vor Claim.
  - `WebSqlEndpoints.cs:113`: tenant_id → tid → tenant; ungültig → legacy. Ignoriert Items, also auch den Admin-Tenantwechsel.
  - **`SqlEndpointRoutes.cs:330-338`**: tenant_id → tid → **Header `X-Tenant-ID`** → **"default"**. Der Claim `tenant` und die MS-URI werden ignoriert, ein ungültiger Header führt zu `ArgumentException` und damit 400.
  - `StreamRlsPolicyEnforcer.cs:74`: tenant_id → **tenant → tid** (andere Reihenfolge).
  - `ResourceGroupMiddleware.cs:209`: Fallback "default".
  - `GovernanceEndpoints.cs:349`: nur tenant_id, Fallback "default".
  - `TokenRevocationEndpoints.cs:50`: nur tenant_id.
  - Außerdem `DeclarativeHttpDataSourceExecutor.cs:417`, `IdentitySubjectResolver.cs:27` und `SubgraphContextPropagationService.cs:71`.
- **Konkrete Fehlfälle:**
  1. Ein Token mit nur `tenant=A` (oder nur MS-tenantid-URI) und ohne Header: die Pipeline läuft als A, `/api/sql-endpoints/*` dagegen läuft governed als Tenant **"default"** (`SqlEndpointExecutionService.cs:84` reicht den Wert an `ExecuteQueryBufferedAsync` weiter). Ist "default" in einer Installation ein echter Tenant (ResourceGroups und OpenLineage verwenden den Wert ebenfalls), ist das ein Tenant-Übergriff. **Prüfen**, ob "default" produktiv vergeben wird.
  2. Ein Token mit `tid=X` und `tenant=Y` (Entra-`tid` plus eigenes Attribut): HTTP-Abfragen laufen als X, CDC-Streams filtern auf Y.
  3. Ein Admin wechselt per Header den Tenant: GraphQL nutzt den neuen Tenant, WebSQL und SQL-Endpoints bleiben beim Claim-Tenant.
- **Vorschlag:** In der Api ausschließlich `EndpointSecurity.GetRequestTenant(context)` verwenden (Items stammen aus der TenantResolutionMiddleware), in der Application `principal.GetTenantId()`. `SqlEndpointRoutes.ResolveTenantId`, den WebSQL-Block und die Fallbacks auf "default" streichen. Für StreamRls die Reihenfolge an `GetTenantId()` angleichen.
- **Aufwand:** M. **Risiko:** mittel (Verhaltensänderung für Tokens mit unüblicher Claim-Kombination; der Admin-Header wirkt danach überall).

## K-K04 – Sicherheit: `warn_allow_all_cors_origins` (WARN, in Prod erlaubt) deaktiviert den CSRF-Schutz vollständig
- **Ort:** `Api/Extensions/GatewayApplicationBuilderExtensions.cs:86-103`.
- **Befund:** Bei `IsAllCorsAllowed` (`Insecure`/`GraphQL.warn_allow_all_cors_origins` oder Quickstart) reflektiert die Inline-Middleware jedes `Origin`, beantwortet `OPTIONS` mit 200 und ruft `next()` mit `return` auf, **bevor** Preflight-Header- und Origin-Prüfung laufen. Seit der SEMANTICS-Neueinstufung ist der Schalter WARN und damit in Prod zulässig.
  - Credentialed Cross-Origin-Reads blockt der Browser weiterhin (kein `Access-Control-Allow-Credentials`).
  - „Simple Requests“ mit ambienten Credentials (Negotiate/Basic, Cookie) werden aber nicht mehr geprüft, z.B. ein `multipart/form-data`-POST an `/graphql`, falls HotChocolate Multipart akzeptiert. **Prüfen**, ob ein solcher Request im Projekt ausführbar ist.
  - Gleichzeitig existiert die reguläre `UseCors()`-Policy (`GatewayServiceCollectionExtensions.cs:503`), es gibt also zwei CORS-Mechanismen.
- **Vorschlag:** Den Sonderzweig entfernen. „Alle Origins“ wird nur über die CORS-Policy abgebildet (`AllowAnyOrigin()` ohne Credentials). Die CSRF-Prüfung (Preflight-Header) läuft immer; nur die Origin-Allowlist-Prüfung darf der Schalter lockern.
- **Aufwand:** S. **Risiko:** gering (Browser-Clients im Dev brauchen dann den `GraphQL-Preflight`-Header, den Banana Cake Pop/Nitro ohnehin setzt).

## K-K05 – Sicherheit/Entfernen: Optionen ohne Leser
Ermittlung: Alle `{ get; init|set; }`-Properties in `GatewayOptions.cs` wurden gegen `.Name`-Zugriffe in gql/src, gql_extensions/src und gql_sqlparser abgeglichen und danach einzeln per String-grep (auch in appsettings) bestätigt.
- **Mit Sicherheitssemantik, sollten verdrahtet werden:**
  - `McpOptions.MaxResultRows` (=100, Z. 882): kein Leser. MCP-Tools übernehmen `limit` vom Agenten (`GraphQL/Mcp/GatewayMcpQueryExecutor.cs:268`) und werden nur durch `GraphQL.MaxResponseRows` (=5000) begrenzt (`GatewayExecutionService.cs:261`). **Vorschlag:** in `GatewayMcpQueryExecutor` auf `Mcp.MaxResultRows` begrenzen.
  - `McpOptions.RequirePiiMasking` (=true): kein Leser. Das Masking steuert allein `IsMcpUnmaskedAllowed`. Entfernen oder in `AiDataGuardrailService.cs:376` mit einbeziehen.
  - `CasbinOptions` komplett (`Enabled`, `EnforceInQueryPipeline`, `ModelPath`, `PolicyPath`, Z. 942): kein Leser. `Casbin.Enabled=false` suggeriert, dass Casbin abschaltbar ist, die Option bewirkt aber nichts (fail-safe, aber irreführend). Entfernen.
  - `EntraIdAuthOptions`/`AdfsAuthOptions`: `SidClaimType`, `RolesClaimType`, `GroupsClaimType`, `GroupSidClaimType`. Kein Leser; `Sid.cs` codiert die Claim-Typen fest. Entweder in `GetUserSid/GetUserRoles/GetGroupSids` verdrahten oder entfernen.
  - `L1MemoryCacheOptions.SensitiveTableTtlSeconds` / `DefaultTtlMinutes` (Z. 525): kein Leser. `ConsentCacheService.cs:144` setzt beim L2→L1-Befüllen fest 5 min.
  - `PostAuthSidRateLimitOptions.MaxCostPerMinute`: steht in `appsettings.json:63`, kein Leser (die Quota kommt aus `ClientQuotaPolicy`).
- **Ohne Wirkung, entfernen (oder `[Obsolete]`):**
  - `Audit.TierAEnabled`, `TierBAggregationWindowSeconds`, `AuditLogRetentionDays`, `ElasticsearchSinkUrl`, `VerifyHashChainIntervalHours` (siehe K-K06)
  - `DataMasking.MaskingCacheTtlHours` (steht in appsettings)
  - `EpochValidation.PipelinedMGetEnabled` (steht in appsettings)
  - `Extensibility.PluginDirectory` (Duplikat von `Plugins.Directory`, das gelesen wird)
  - `GraphQL.EnableBananaCakePop`, `GovernanceDb.EnableOutboxProcessor`
  - `Authentication.ServicePrincipalName` (sogar `[Required]`), `Authentication.GroupCacheTtlMinutes`
  - `SqlEndpoints.MaxQueryTimeoutSeconds`, `SingleQueryPushdown.FallbackToBatchingOnUnsupportedDialect`
  - `Lakehouse.MaxScanRowsLimit`/`MaxConcurrentFileScans`, `Catalog.SyncMode`, `Collibra.CommunityId`, `Alation.CustomFieldIdPii` (jeweils auch in gql_extensions ohne Leser)
- **Aufwand:** S (Entfernen) / M (Verdrahten). **Risiko:** gering, da .NET-Config-Binding unbekannte Keys ignoriert. Die Keys auch aus appsettings*.json und `docs/configuration-guide.md` entfernen.

## K-K06 – Sicherheit/Design: Hash-Ketten-Verifikation und WORM-Export laufen nie
- **Ort:** `Infrastructure/Persistence/AuditWormExportService.cs:46` (`ExportAuditSnapshotAsync`) und `SqliteGovernanceRepository.Audit.cs:252` (`VerifyAuditHashChainAsync`).
- **Befund:** `ExportAuditSnapshotAsync` hat in src **keinen Aufrufer**: es gibt keinen Endpoint und keinen HostedService, nur die Registrierung in `GatewayServiceCollectionExtensions.cs:278`. `VerifyAuditHashChainAsync` wird nur von dort aufgerufen.
  - `Audit.Worm.Enabled=true` bewirkt deshalb nichts.
  - `VerifyHashChainIntervalHours` hat keinen Leser.
  - Eine Ketten-Verletzung (`FlagAuditChainViolation`) wird nur beim nächsten Schreibvorgang als `LogCritical` gemeldet und erscheint nicht im Health-Report.
  - Der Kommentar in `AuditChainAnchorStores.cs:8` („mirrored to WORM storage“) beschreibt also keinen existierenden Ablauf.
- **Vorschlag:**
  - Einen `AuditIntegrityBackgroundService` einführen, der alle `VerifyHashChainIntervalHours` die Kette prüft und, falls aktiviert, exportiert.
  - Das Ergebnis als Health-Komponente melden (unhealthy bei Verletzung).
  - Alternativ WORM-Service und Optionen entfernen und die Doku korrigieren.
  - Nebenbei: `FixedTimeEqualsString` für öffentliche Kettenhashes (Z. 39, 300, 322 …) ist unnötig; nötig ist es nur für die Anker-Signatur (Z. 557). Kosmetik.
- **Aufwand:** M. **Risiko:** gering (Last durch volle Kettenprüfung: seitenweise wie beim Export).

## K-K07 – Vereinfachen: Bypass-Mechanik in GatewayOptions tabellengetrieben machen
- **Ort:** `Domain/Options/GatewayOptions.cs:70-200`.
- **Befunde:**
  1. **Inkonsistente Labels:** Meist `"DANGER:<name>"`, aber `"DANGER:open_schema (OpenSchema / Catalog.OpenSchema)"`, `"DANGER:openmetadata_auto_create_consents (OpenMetadata.AutoCreateConsents)"`, `DangerPrefix + "websql_unlimited_affected_rows (...)"`, alle WARN-Legacy-Einträge mit Klammerzusatz. Tests müssen deshalb mal `ShouldContain(exakt)`, mal `StartsWith` prüfen. Genau deshalb sind in der letzten Nutzer-Änderung die Fälle `openmetadata_auto_create_consents` und `dbt_…` aus der generischen Theory `SEM_ReclassifiedSwitches_AreDanger` (`BypassSemanticsAndDmlGuardrailTests.cs`) herausgefallen. Für AutoCreateConsents gibt es noch einen exakten Einzeltest (Z. 162), für Dbt nur `StartsWith` auf das Sammellabel (Z. 175).
  2. **Sammelschalter für Webhooks:** `IsWebhookSignatureBypassed` (Z. 81) verodert `Insecure.*`, `Itsm.*`, `OpenMetadata.*` und `Dbt.danger_bypass_webhook_signature_validation`. Alle Handler (ITSM, OpenMetadata, Catalog, Dbt, `WebhookEndpoints`) lesen nur den Sammelwert. **Der domänenspezifische Schalter `Dbt.…` schaltet daher die Signaturprüfung *aller* Webhook-Typen ab.** Dasselbe gilt für `AreUntrustedCertificatesAllowed` (Itsm/OpenMetadata). Das Label nennt die Quelle nicht. `DbtWebhookReceiver.cs:34` prüft `Dbt.…` zusätzlich, was redundant ist. Dank DANGER nur in Dev möglich, aber semantisch falsch.
  3. **Wrapper ohne externe Leser** (nur in `GetAllActiveBypasses` genutzt, grep: 0): `IsLegacyWebSqlDmlSwitchActive`, `IsOpenSchemaExplicitlyEnabled`, `IsLegacyGlobalItsmWebhookSecretAllowed`, `IsLegacyCatalogPayloadOnlySignatureAllowed`, `IsOpenMetadataAutoCreateConsentsEnabled`. Außerdem `IsEgressAllowlistActive` (nur Tests).
  4. **Doku-Drift:** `README.md:150` und `_changes/SEMANTICS.md` führen `OpenMetadata.AutoCreateConsents` als WARN, der Code meldet DANGER.
  5. **Kopplung:** `GraphQL/Federation/SubgraphSecurityDelegatingHandler.cs:39` nutzt `_isDev = HasAnyDangerBypassActive`. Damit lockert z.B. `AutoCreateConsents` oder `websql_unlimited_affected_rows` im Dev die SSRF-Prüfung für Subgraphen. Richtig wäre `IHostEnvironment.IsDevelopment()`, wie es der `DeclarativeHttpDataSourceExecutor` seit der letzten Nutzer-Änderung macht.
  6. Die Startprüfung `AllowDml && DmlWriterRoles.Count == 0` (`GatewayServiceCollectionExtensions.cs:889`) ignoriert die Legacy-Aliase. Zur Laufzeit wird fail-closed abgelehnt, die Prüfung ist also nur inkonsistent.
- **Vorschlag (Skizze):**
  ```csharp
  public enum SwitchLevel { Danger, Warn }
  public sealed record SecuritySwitch(string Name, SwitchLevel Level, string Source, Func<GatewayOptions, bool> IsActive)
  {
      public string Label => (Level == SwitchLevel.Danger ? DangerPrefix : WarnPrefix) + Name; // immer "<LEVEL>:<name>"
  }
  public static readonly IReadOnlyList<SecuritySwitch> SecuritySwitches = [
      new("danger_bypass_consent_checks", SwitchLevel.Danger, "Insecure.* / GovernanceDb.*", o => o.IsConsentBypassed),
      new("dbt_bypass_webhook_signature", SwitchLevel.Danger, "Dbt.danger_bypass_webhook_signature_validation", o => o.Dbt.danger_bypass_webhook_signature_validation),
      new("openmetadata_auto_create_consents", SwitchLevel.Danger, "OpenMetadata.AutoCreateConsents", o => o.OpenMetadata.AutoCreateConsents),
      ... ];
  public IReadOnlyList<string> GetAllActiveBypasses() => SecuritySwitches.Where(s => s.IsActive(this)).Select(s => s.Label).ToList();
  ```
  - Die `Source` erscheint nur im Banner, im Health-Report und im Dev-Header, nicht im Label.
  - Pro Integration gibt es einen eigenen Accessor (`IsItsmWebhookSignatureBypassed` = Insecure-global || Itsm.x usw.); das globale Sammel-Property entfällt.
  - Die Wrapper aus Befund 3 entfallen.
  - Tests: Eine Theory über `SecuritySwitches` prüft für jeden Eintrag: Label == `"<LEVEL>:" + Name`, DANGER blockiert Prod, WARN startet. Die entfernten Fälle sind damit automatisch wieder abgedeckt.
- **Aufwand:** M. **Risiko:** gering (Label-Texte ändern sich nur bei den Klammer-Einträgen; Dev-Header und Health-Detail sind nicht vertraglich).

## K-K08 – Vereinfachen/Entfernen: Parquet
- **Orte:** `Api/Serialization/ParquetContentNegotiation.cs`, `ParquetResponseWriter.cs`, `Api/Middleware/ParquetGraphQLResponseMiddleware.cs`, `Application/Serialization/ParquetExportService.cs`, `Api/Endpoints/ExportEndpoints.cs`.
- **Befunde:**
  1. **Doppelte Negotiation:** Die Middleware ruft `Evaluate()` für jeden Request auf (Z. 58). WebSQL (`WebSqlEndpoints.cs:128`), SQL-Endpoints (`SqlEndpointRoutes.cs:303`, `parquet.Requested`) und OData (`ODataEndpoints.cs:279`) parsen den Accept-Header erneut.
  2. **Doppelte Verfügbarkeitsprüfung:** Die Kanäle rufen `TryRejectUnavailableAsync` vor der Abfrage auf (`SqlEndpointRoutes.cs:183,249`, `WebSqlEndpoints.cs:297`, `ODataEndpoints.cs:284`), `WriteAsync` ruft es intern noch einmal auf (`ParquetResponseWriter.cs:80`). Die Optionen stammen einmal aus dem Middleware-Konstruktor (`_options.ParquetEgress`), einmal per `RequestServices` (`GetEgressOptions`) und im Service ein drittes Mal (`ParquetExportService.cs:79`, wirft `InvalidOperationException`).
  3. **Inkonsistentes Verhalten bei `ParquetEgress.Enabled=false`:** GraphQL fällt bei einer JSON-Alternative im Accept-Header auf JSON zurück (Middleware Z. 76), die Marker-Routen antworten immer mit 406.
  4. **`IParquetExportService.ExportToParquet` (synchron):** einziger Aufrufer sind Tests (`Wave3MarketFeaturesTests.cs`, 5×), src: 0. Es handelt sich um Sync-over-async (`GetAwaiter().GetResult()`). Aus dem Interface entfernen und die Tests auf die Async-Variante umstellen.
  5. **`GET /api/export/parquet/{domain}/{table}` (`ExportEndpoints.cs:28`):** liefert eine 0-Zeilen-Datei, deren Spalten aus dem Query-Parameter `columns` stammen (alles string), es gibt keinen Datenbezug. Die Funktion ist durch die Accept-Negotiation vollständig ersetzt. Der Marker `ParquetOutputSupportedMetadata` ist dort bedeutungslos. **Entfernen** (inkl. `MapExportEndpoints` und README-Absatz); in den Tests gibt es keine Referenz auf die Route.
  6. **Speicher im GraphQL-Pfad:** Der JSON-Puffer (`MemoryStream`), `GetBufferedBytes()` (Kopie per `ToArray`, `BoundedResponseBufferStream.cs:39`), `JsonDocument`, die Parquet-`MemoryStream` und `result.Data` (byte[]) ergeben bis zu ~4× `MaxBufferedSourceBytes` (64 MB) pro Request.
- **Vorschlag:**
  - Das Ergebnis von `Evaluate()` einmal in `HttpContext.Features` (bzw. Items) ablegen, z.B. `ParquetNegotiationFeature { Preferred, HasJsonAlternative, Enabled }`. Die Kanäle lesen nur das Feature.
  - `TryRejectUnavailableAsync` bleibt die einzige Prüfstelle vor der Abfrage, `WriteAsync` setzt sie voraus (Debug.Assert).
  - Die Regel für „disabled + JSON-Alternative → JSON“ an einer Stelle festlegen.
  - Punkt 4 und 5 entfernen.
  - Punkt 6: `JsonDocument.Parse(ReadOnlyMemory)` direkt auf `GetBuffer()`-Segment statt `ToArray()`.
- **Aufwand:** S–M. **Risiko:** gering (die PARQ_-Tests decken das Verhalten ab).

## K-K09 – Design: Puffer-Grenzen und stilles Überspringen von Egress
- **Orte:** `GatewayExtensibilityMiddleware.cs:18,145-150`, `ParquetGraphQLResponseMiddleware.cs:26` / `ParquetEgressOptions.MaxBufferedSourceBytes` (Z. 1035), `ResourceGroupMiddleware.cs:162`, `BoundedResponseBufferStream.cs:156`.
- **Befund:**
  - Überschreitet eine Antwort 16 MB, schaltet die Extensibility-Middleware auf Pass-through und überspringt **alle** Egress-Interceptors stillschweigend (nur ein Warning-Log). Heute betrifft das nur `AuditLineageEgressInterceptor`: es fehlt dann der `X-Audit-Lineage-Hash`. Ein über Plugins geladener DLP- oder Redaction-Interceptor würde bei großen Antworten aber umgangen.
  - Die Parquet-Middleware liegt außen und puffert bis 64 MB. GraphQL-Ergebnisse zwischen 16 und 64 MB werden also ohne Egress-Phase nach Parquet konvertiert.
  - Drei getrennte Streaming-Detektoren (`IsStreamingRequest`, `IsPersistentConnectionRequest`, `IsStreamingContentType`) mit leicht unterschiedlichen Listen. `ResourceGroupMiddleware` codiert `/mcp` fest, statt `GatewayApplicationBuilderExtensions.ResolveMcpBasePath(options)` zu nutzen.
- **Vorschlag:**
  - Grenze konfigurierbar machen und `ParquetEgress.MaxBufferedSourceBytes ≤ Extensibility-Grenze` erzwingen bzw. validieren.
  - Interceptors bekommen ein Flag `RequiresFullBody`; ist es gesetzt, gibt es bei Überschreitung 413 statt Pass-through (fail-closed).
  - Ein gemeinsamer `StreamingRequestClassifier` (Request- und Content-Type-Seite) für alle drei Stellen.
- **Aufwand:** S–M. **Risiko:** gering.

## K-K10 – Vereinfachen: Drei Rollenprüf-Mechanismen
- **Befund:**
  - `GatewayPolicies.HasAnyRole` (`Api/Security/GatewayPolicies.cs:33`): IsInRole plus rohe `role/roles`-Claims, **Ordinal**.
  - `Sid.GetUserRoles()` (`Domain/Common/Sid.cs:109`): 4 Claim-Typen, **OrdinalIgnoreCase**.
  - Hart codierte `context.User.IsInRole("…")`-Ketten: `GovernanceEndpoints.cs` 31×, `StreamingCdcEndpoints.cs` 4×, `TenantResolutionMiddleware.cs` 2× (`GatewayAdmin`/`PlatformAdmin`, in keiner Policy außer SchemaAdmin), `ResourceGroupMiddleware`, `SystemEndpoints`, `GatewayExecutionService` je 1×. `DbtEndpoints` (24×) und `DbtHealthExecutionMiddleware` fallen in einen anderen Bereich.
  - Der Kommentar in GatewayPolicies („statt hand-written role checks“) ist damit nur teilweise umgesetzt. Beispiel: `GovernanceEndpoints.cs:202-205/221-224/247-250` entsprechen exakt `PrivacyAdminRoles`, Z. 30-32 und 103-106 Varianten von `SchemaAdminRoles`.
  - Weil `EnterpriseClaimsTransformation` alle Provider-Rollen nach `ClaimTypes.Role` normalisiert, ist der Rohclaim-Fallback in `HasAnyRole` für HTTP redundant. Er greift nur für Principals, die nicht transformiert werden, z.B. den WS-`connection_init`-Principal und den MCP-Principal.
- **Vorschlag:**
  - Handler-Prüfungen durch `.RequireAuthorization(GatewayPolicies.X)` ersetzen; fehlende Rollensätze ergänzen: `GovernanceReader` = GovernanceAdmin/SchemaAdmin/ClusterAdmin/DataOwner(/Developer), `TenantSwitch` = GatewayAdmin/PlatformAdmin/ClusterAdmin.
  - Programmatische Prüfungen nur noch über `GatewayPolicies.HasAnyRole` laufen lassen, intern auf `GetUserRoles()` (eine Claim-Liste, eine Vergleichsregel).
- **Aufwand:** M. **Risiko:** mittel (Groß-/Kleinschreibung und zusätzliche Claim-Typen können Zugriffe erweitern; Vergleichsregel bewusst festlegen, Empfehlung Ordinal).

## K-K11 – Entfernen: `DynamicPluginAssemblyLoadContext`
- **Ort:** `Application/Extensibility/DynamicPluginAssemblyLoadContext.cs` (gesamte Datei).
- **Befund:** 0 Aufrufer in src. Referenzen gibt es nur in `SecurityFindingsRemediationTests.cs:726` und `SecurityReview20261002InfraTests.cs:684-685`. Der tatsächliche Ladepfad ist `PluginManager` → `PluginAssemblyLoadContext` + `PluginTrustList` (M-27). Die Klasse dupliziert Hash-Prüfung (`ReadVerified` ≈ `PluginTrustList.VerifyBytes/HashesEqual`) und Dependency-Allowlist. Ihr Doc-Kommentar verspricht Hot-Reload von „Ingress/Egress middleware plugins“, die es nicht gibt (`Plugins.EnableHotReload` hat ebenfalls keinen Leser; nur `SqlEndpoints.EnableHotReload` wird gelesen).
- **Vorschlag:** Klasse und die zwei Tests löschen (der M-27-Schutz ist durch `PluginTrustList`-Tests abgedeckt), `Plugins.EnableHotReload` und `Extensibility.PluginDirectory` entfernen.
- **Aufwand:** S. **Risiko:** gering.

## K-K12 – Vereinfachen: S3-SigV4 doppelt, Region fest
- **Orte:** `Infrastructure/Persistence/AuditWormExportService.cs:371-428` (`SignS3Request`, `region = "us-east-1"`, Aufrufe Z. 340/362 ohne Region) und `gql_extensions/.../Lakehouse/Services/S3LakehouseStorageProvider.cs:263-304` (`SignAwsSigV4(..., region: "us-east-1")`).
- **Befund:** Die Schlüsselableitung und der Aufbau des String-to-Sign sind zweimal implementiert. Keine der Optionen (`WormAuditOptions`, `LakehouseStorageOptions`) kennt eine Region, deshalb schlagen Signaturen gegen AWS-Buckets außerhalb von us-east-1 fehl. Die Secrets (`S3SecretKey`, `AzureAccountKey`) stehen als Klartext in den Optionen statt als `…VaultRef` (wie `HmacSecretKeyVaultRef`, `L2IntegrityKeyVaultRef`).
- **Vorschlag:** Einen `AwsSigV4Signer` in `Autheris.Application` anlegen (Extensions referenziert nur Domain und Application), Optionen `S3Region` ergänzen und die Secrets über `IKeyVaultSecretProvider` auflösen.
- **Aufwand:** S. **Risiko:** gering.

## K-K13 – Vereinfachen: `SecurityEvaluationContext` per Copy-Paste
- **Orte:** `GatewayExecutionService.cs:200-225` und `:760-785`, `GovernedSqlExecutionService.cs:~370-390` (fremder Bereich, nur als Aufrufer genannt), `StreamRlsPolicyEnforcer.cs:150-170`, `DefaultCrossDomainAccessResolver.cs:88-110`, `AiDataGuardrailService.cs:210-240`.
- **Befund:** Sechs Stellen bauen den ABAC-Kontext je für sich auf: Claims → `Attributes`, `purpose`/`purpose_id`, Gruppen, Abteilung/Region/Clearance, IP (siehe K-K02). Die Abweichungen (IP-Fallback, fehlende Attribute bei MCP) entstehen genau hier.
- **Vorschlag:** `ISecurityEvaluationContextFactory.Create(principal, tenant, table, columns, purposeOverride?)` in der Application, die intern `IClientIpResolver` und die Claim-Helper nutzt. Alle sechs Stellen rufen nur noch die Factory auf.
- **Aufwand:** M. **Risiko:** mittel (der Cache-Key in `CasbinEnforcementService.cs:240` hängt an den Feldern; Ergebnisse für bisher abweichende Kanäle ändern sich bewusst).

## K-K14 – Design (prüfen): Prozesslokaler Sicherheitszustand im HA-Betrieb
- **Orte:** `Application/Mcp/Services/HitLStepUpApprovalService.cs:20` (Tickets in `ConcurrentDictionary`, Singleton), `McpSessionStore` (Singleton, in-memory), `CasbinEnforcementService._decisionCache` (60 s, wird nur lokal bei Policy-Änderung geleert).
- **Befund:** Beim Betrieb mehrerer Knoten (ADR-006 HA-Cluster) gilt:
  - Eine auf Knoten A erteilte HitL-Freigabe ist auf Knoten B unbekannt (fail-closed, aber funktional kaputt).
  - MCP-Sessions brechen ohne Sticky Sessions.
  - Ein Policy-Widerruf greift auf anderen Knoten erst nach bis zu 60 s.
  - Token-Revocation und Consent-Cache haben dagegen bereits Redis-Varianten.
- **Vorschlag:** **Prüfen**, ob Multi-Node mit MCP/HitL vorgesehen ist. Falls ja, die Stores über die bestehende `IConnectionMultiplexer`-Registrierung nach dem Muster von `RedisTokenRevocationService` (lokaler Layer plus Redis) umsetzen und die Invalidierung des Casbin-Caches über den vorhandenen `IEventBus` verteilen.
- **Aufwand:** M. **Risiko:** mittel.

---

## Geprüft, ohne Handlungsbedarf
- `InMemoryTokenRevocationService` / `RedisTokenRevocationService`: Gemeinsame Logik liegt bereits in `TokenRevocationKeys`. Die Redis-Variante nutzt In-Memory als lokalen Layer, es gibt keine kopierte Logik.
- `ConsentCacheService` L2-Integrität (HMAC-Framing, HKDF-Subkey, Ablauf im MAC) und `EpochValidationService` (fail-closed bei sensiblen Tabellen): Beides ist korrekt.
- `FileAuditChainAnchorStore` / `InMemoryAuditChainAnchorStore`: Beide sind schlank; In-Memory wird nur bei `:memory:` gewählt.
- `PluginManager` + `PluginTrustList`: Verifizierte Bytes werden geladen (kein TOCTOU), außerhalb von Dev fail-closed.
- `WebSocketAuthInterceptor`: Ablauf und Widerruf greifen korrekt für beide Pfade (Token in `connection_init` und HTTP-Upgrade). Hinweis: Der `connection_init`-Principal durchläuft keine `EnterpriseClaimsTransformation`, siehe K-K10.
- Letzte Nutzer-Änderung `DeclarativeHttpDataSourceExecutor` (Scheme-Prüfung vor DNS) und der neue Test-Parameter `environment:`: korrekt. Der Egress-Teil (`EgressAllowlist` ohne `::ffff:0:0/96`) gehört nicht zu diesem Bereich.

## Unsicherheiten
- K-K03 Fall 1: Die Ausnutzbarkeit hängt davon ab, ob „default“ in einer Installation ein echter Tenant mit Daten ist.
- K-K04: Ob HotChocolate im Projekt `multipart/form-data`-Operationen ohne Preflight annimmt, wurde nicht verifiziert.
- K-K05: Die Leserprüfung basiert auf Member-Zugriffen und String-Suche. Reflection-basierte Leser (z.B. `IConfiguration["Gateway:…"]`) wurden für die genannten Keys per String-grep ausgeschlossen. Dynamisch zusammengesetzte Keys sind theoretisch möglich, aber nicht gefunden.
