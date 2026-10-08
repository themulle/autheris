# Status und Umsetzungsplan (Stand db3aef1, 2026-10-07)

Branch `feat/ast-target-dialect-generator`, Stand `db3aef1`.

## Was noch offen ist (Kurzüberblick, Stand db3aef1)

**Sofort (Sicherheit, klein):**
1. **F-3 `TenantId` akzeptiert `*`:** Die Härtung ist für alle Eingänge zurückgenommen (Claims, `X-Tenant-ID`, Envoy, ForwardAuth). Kein konkretes Leck gefunden, aber unnötig. Zurückbauen, Wildcard nur intern im Casbin-Service.
2. **F-1 ✔ Mandanten-Datei nur mit `g`-Zeilen:** Sie löscht ohne Fehler alle Deny- und RLS-Regeln des Mandanten (fail-open).
3. **S-1/D-2 erfundene Daten:** ✅ behoben (DemoDataSwitch.Resolve verlangt strikt Development, null/leer/Test ist fail-closed deaktiviert; synthetischer Fallback in WebSQL & SQL-Executor außerhalb Dev blockiert).
4. **S-2 ✔ 501 mit Originaltext:** ✅ behoben (generischer Text bei 501, Antlr4-Parserfehler auf 400).
5. **G-1 Decimal-Filter:** ✅ behoben (kulturunabhängige Konvertierung in `39b6619`).
6. **R-GQL-8 ✔ WebSocket-Subjekt:** ✅ behoben (Subjektprüfung für alle Tenanten strikt, Token ohne SID abgewiesen).
7. **PoC DEP-1/2/3:** Passwörter und HMAC-Schlüssel rotieren, Ports, LWETEM_PROD.

**Casbin-Rest:**
- R-POL-5: ✅ behoben (Policy-Datei beim Start mit p-Regel-Prüfung validiert).
- F-7: ✅ behoben (ModelPath wird validiert, sobald konfiguriert, auch bei `Enabled=false`).
- R-POL-8: ✅ behoben (Warnung geloggt bei `Enabled=false`).
- C-2: ✅ behoben (Simulation nutzt konfiguriertes Modell & zentralen Parser, SecurityEvaluationContext statt anonymes Objekt, 6 Paritätstests hinzugefügt).
- F-4: ✅ behoben (Thread-Sicherheit von `Enforce` und `HasRoleForUser` per Lock garantiert).
- E-4: ✅ behoben (`GetOrCreateEnforcer` ist nicht mehr öffentlich, `GetEnforcer` internal).
- E-5: ✅ behoben (Cache-Einträge mit alter Epoch werden bei Snapshot-Wechsel verworfen).
- F-2: ✅ behoben.
- F-5: ✅ behoben.
- F-6: Die Doku beschreibt die Probes falsch.
- Test01, Test04, Test05, Test07: ✅ gehärtet (direkte Casbin-Enforcer-Prüfung, Entzugsprüfung nach g-Löschung, monotone Epoch, Erhalt bestehender Policies).

**Tests:**
- Die meisten Fixes seit `c4f2c32` haben keinen oder einen wertlosen Test (Phase 2).
- Ein PostgreSQL-Integrationstest fehlt (D-1, R-SQL-3).

**GraphQL:** stabile Typnamen (G-2), Budget konfigurierbar (D-8), HMAC-Spalten (G-8), Memo pro Operation (G-3, G-4, D-7), Katalog-Aufzählung (R-GQL-6), Mutationen (R-GQL-9), Null-Semantik, Schema-Reload, SQL2-11, SQL2-12, Ende-zu-Ende-Tests.

**SQL/Prozeduren:** R-SQL-4, R-SQL-8, R-SQL-10, R-SQL-12, R-SQL-13, D-3, D-5, D-6, D-9, D-10, SQL-2, SQL-3, SQL-5, SQL2-9.

**Gesamt-Review, nie angefasst:**
- mittel: EXT-1-Rest, INF-1, MCP-1, POL-5, POL-6, GQL-3, GQL-4, EXT-3, EXT-5, API-2, API-3, DEP-5, DEP-7, DEP-8, R-DEP-1;
- alle Laufzeitfehler, **DEP-4: ✅ behoben** (Container startet mit /app/data Volume, chown $APP_UID, HTTP 8080);
- alle niedrigen Befunde und die Architekturpunkte.

**Erledigt seit d77d650:**
- `5c795e4`: E-2 (unveränderliche Snapshots), E-1 global, E-6, C-1, C-4, C-5.
- `8636038`: E-3 (Modellprüfung per Probe), R-POL-5 (Modellteil).
- `db3aef1`: Der CI-Linter prüft nur noch echte Casbin-Dateien.
- Aktuell: **F-1, F-2, F-3, F-5 behoben**: Mandanten-Sicherheit & Casbin-Härtung (strenge TenantId-Regex, Wildcard-Isolation, Fail-Closed bei reinen g-Zeilen in Mandanten-Dateien, entkoppelter globaler Reload, zero-allocation HasPolicies).

## Nachtrag 5c795e4, 8636038, db3aef1 (Casbin, Linter)

| ID | Status | Begründung |
|---|---|---|
| E-1 | 🟡 | Global ✅: Eine Datei ohne `p`-Regel wird abgelehnt (Test08). Für Mandanten-Dateien offen: F-1. |
| E-2 | ✅ | `PolicySources` und `BuildSnapshot`; `Publish` unter Lock. `AddPolicy`/`AddGroupingPolicy` stehen nur noch in `CreateEnforcer`. Test01 prüft jetzt direkt den Casbin-Enforcer, Test04 hat die Gegenprobe, Test05 prüft monotone Epoch, Test07 prüft Erhalt des vorherigen Snapshots. |
| E-3 | ✅ | `CasbinModelContract` mit M1–M8 und W1–W5 entspricht der Anleitung; die Textsuche ist entfernt; die Prüfung läuft beim Start in `ValidateGatewayOptions`. Die Doku dazu ist falsch (F-6). |
| E-4 | ✅ | `GetOrCreateEnforcer` ist `internal Enforcer GetEnforcer` (`:192-201`). |
| E-5 | ✅ | `_decisionCache.TryAdd` wird nur ausgeführt, wenn Snapshot-Epoch unverändert ist (`:670-674`). |
| E-6 | ✅ | `TenantHasPolicies` zählt `*` nicht mehr. |
| C-1, C-3, C-4, C-5 | ✅ | |
| C-2 | ✅ | Modelltext & zentraler Parser werden gemeinsam genutzt; `SecurityEvaluationContext` mit echten Attributen übergeben; Paritäts-Testsuite vergleicht Durchsetzung und Simulation. |
| R-POL-5 | ✅ | Modell & Policy werden beim Start geprüft (Validierung von Modellvertrag und p-Regeln). |
| R-POL-8 | ✅ | Warnung geloggt bei `Enabled=false`. |
| CI-Linter | ✅ | `db3aef1`: `nginx.conf` und Benchmark-CSV werden übersprungen. Ungebaut, der nächste CI-Lauf zeigt es. |

Neue Befunde:

| ID | Schwere | Befund | Fundstelle | Behebung |
|---|---|---|---|---|
| F-1 ✔ | ✅ behoben | Eine Mandanten-Datei nur mit `g`-Zeilen (z. B. abgeschnitten) ersetzt `TenantFiles[t]` durch eine leere Liste. Die Prüfung verlangt „keine `p`- **und** keine `g`-Regeln“. Alle Deny- und RLS-Regeln des Mandanten aus dieser Datei verschwinden ohne Fehler. | `CasbinEnforcementService.cs` `LoadPolicyFromText(TenantId, …)`, Prüfung `tenantRules.Length == 0 && tenantGrouping.Length == 0` | Ablehnen, wenn die neue Datei keine `p`-Regel hat, `TenantFiles[t]` aber mindestens eine hatte. Test analog zu Test08 (Test11). |
| F-2 | ✅ behoben | Die globale E-1-Prüfung nutzt `oldSources.HasAnyPolicies`. Eine reine Rollen-Datei global, zusammen mit `p`-Regeln in Mandanten-Dateien, wird beim Reload abgelehnt, obwohl das laut Semantik vorgesehen ist. | `CasbinEnforcementService.cs:1319-1321` | Nur die `p`-Regeln der alten globalen Datei zählen (Test12). |
| F-3 | ✅ behoben | `TenantId` akzeptiert `*` überall (`TenantId.cs:13, 30`). Betroffen sind u. a. Claims (`Sid.cs:136`, `ClaimsNormalizer`, `SecurityContextFactory`), `X-Tenant-ID` für ClusterAdmins und anonym (`TenantResolutionMiddleware.cs:85, 92`; vorher 400), Envoy reicht `*` an Upstreams weiter (`EnvoyExtAuthzService.cs:210, 248`), außerdem `ForwardAuth.DefaultTenantId`, ITSM und OpenMetadata. Ein konkretes Leck ist nicht gefunden: SQL, Redis und ReBAC vergleichen literal; `${tenant}` in RLS wirft. Risiken: Downstream-Systeme deuten `*` als „alle“; ein eigenes Modell mit Wildcard auf der Request-Seite besteht die Probes; `LoadPolicyFromText(TenantId.Wildcard, …)` landet in `TenantFiles["*"]` und wird von `BuildSnapshot` ignoriert, Deny-Regeln fallen still weg. Im Produktivcode wird `TenantId.Wildcard` nicht genutzt, nur in Tests. | `TenantId.cs`; Aufrufer siehe links | Strenge Regex wiederherstellen (`^[a-zA-Z0-9_-]{1,64}\z`). Wildcard nur intern (`internal const string WildcardTenant = "*"`) mit eigener Methode für programmatische `*`-Regeln. Probe W6: Regel für `probe_a`, Request-Mandant `*` ergibt false. Testfall `*` in der Theory zu ungültigen Mandanten-IDs. In `CasbinHotReloadTests.cs:78, 129` `t == tenant.Value` schreiben. |
| F-4 | ✅ behoben | Thread-Sicherheit per `lock (enforcer)` in `EvaluatePolicyAsync` sichergestellt; parallele Evaluierungen getestet. | `CasbinEnforcementService.cs:563` | `lock (enforcer)` blockiert Race Conditions im Casbin-Enforcer. |
| F-5 | ✅ behoben | `PolicySnapshot.HasPolicies` ruft pro Request `GetPolicy().Any()` auf (kopiert die Policy-Liste). | `CasbinEnforcementService.cs:90-97` | Nur `Rules` prüfen (zero-allocation). |
| F-6 | niedrig (Doku) | `configuration-guide.md:767-786` beschreibt die Probes falsch (z. B. M2 „Aktions-Mismatch“ statt Mandanten-Trennung, M5 „unbekannter Mandant“ statt „Deny gewinnt“; W4, W5). Die Semantik aus E-2 Abschnitt 3 fehlt. „`ModelPath` null bedeutet Standardmodell“ widerspricht der Pflicht bei `Enabled=true`. | `docs/configuration-guide.md` | Tabelle aus der E-3-Anleitung übernehmen, Semantikabschnitt ergänzen. |
| F-7 | ✅ behoben | `ModelPath` wird in `ValidateGatewayOptions` geprüft, sobald konfiguriert (auch bei `Enabled=false`). | `GatewayServiceCollectionExtensions.cs:997-1019` | Vorab-Validierung bei Vorhandensein von `ModelPath`. |
| F-8 | info | Jede einzelne Änderung baut alle Enforcer neu; viele `AddPolicy`-Aufrufe kosten O(n²). | `CasbinEnforcementService.cs:297-324` | Später: unveränderte Enforcer wiederverwenden. |

Tests (Casbin):
- `CasbinModelContractTests` ist sinnvoll.
- Bei `CasbinSnapshotImmutabilityTests` gibt es Lücken:
  - Test01 ist ohne Fix grün.
  - Test05 prüft weder „alter oder neuer Stand“ noch, dass die Epoch monoton steigt.
  - Bei Test04 fehlt die Gegenprobe nach dem Entfernen der `g`-Zeile.
  - Bei Test07 fehlt die Prüfung, dass der alte Stand aktiv bleibt.
- Weitere fehlende Tests:
  - Mandanten-Datei nur mit `g`-Zeilen (F-1);
  - Request mit Mandant `*`;
  - Startvalidierung mit ungültigem Modell;
  - Vergleich von Simulation und Durchsetzung.
- Bestehende Tests: kein Bruch erkennbar.

**Nachtrag d77d650:**
- D-1 ist behoben: Der Reader wird in einem eigenen `await using`-Block vor `CommitAsync` freigegeben, im Fehlerfall vor dem Rollback.
- Zwei neue Tests in `SecurityReview20261002WebSqlTests` (`D01_…DisposesReaderBeforeCommit`, `D01_…DisposesReaderBeforeRollback`) nutzen einen Fake-Treiber, der wie Npgsql bei offenem Reader wirft.
- Ein Integrationstest gegen echtes PostgreSQL fehlt weiter.
- Neuer Kleinbefund D-10: Nach erfolgreichem Commit wird `tx = null` gesetzt, damit überspringt der `finally`-Block `tx.DisposeAsync()`. Die committete Transaktion wird nie freigegeben. Bei Npgsql und SqlClient ist das praktisch folgenlos, sauber ist ein eigenes Flag `committed` statt `tx = null`.

**Grundlagen:**
- [security-review-2026-10-07.md](security-review-2026-10-07.md): Gesamt-Review mit den IDs SQL-, SQL2-, POL-, GQL-, MCP-, API-, EXT-, INF-, DEP-.
- [review-c4f2c32.md](review-c4f2c32.md): Review des ersten Fix-Commits mit den IDs R-….
- Review von `d57312f`: neue Befunde **B-** (Build/Tests), **C-** (Casbin), **S-** (SQL/WebSQL), **G-** (GraphQL).
- Review von `d58449c`: neue Befunde **D-**.
- Review von `cb16e6c` (Casbin): neue Befunde **E-**.

**Vorgehen:** Alles nur gelesen, nichts gebaut. ✔ heißt von Hand am Code nachgeprüft.

## 0. Nachtrag cb16e6c (Casbin) – Stand vor 5c795e4, siehe oben

`cb16e6c` setzt die Casbin-Befunde C-1 bis C-5 um.

- **Policy-Zustand als Snapshot:** Enforcer, Regeln und Epoch liegen in einem `PolicySnapshot`. Er wird unter Lock per `Interlocked.Exchange` getauscht, die Epoch steht im Cache-Key.
- **C-1:** Eine leere oder nur kommentierte Datei wird abgelehnt.
- **C-2:** Die Modelldatei und die Simulation kennen jetzt Wildcards. Ein Modell ohne Wildcard-Klausel lehnt `*`-Regeln ab.
- **C-3:** Der Mandanten-Vergleich ignoriert Groß-/Kleinschreibung.
- **C-5:** Ein unbekannter Mandant legt keinen Enforcer mehr an, und Policies, die pro Mandant geladen wurden, überleben den globalen Reload.
- **Tests:** Neu sind Tests für leere Datei, Groß-/Kleinschreibung, unbekannten Mandanten und parallele Auswertung während eines Reloads.

| ID | Schwere | Befund | Fundstelle | Behebung |
|---|---|---|---|---|
| E-1 ✔ | mittel, **fail-open** | Rest von C-1. Eine Datei **nur mit `g`-Zeilen** wird weiter angenommen: Vorher legt der Code einen leeren `*`-Eintrag an (`if (rulesByTenant.Count == 0 && globalGroupingRules.Count > 0)`), damit greift die neue Prüfung `rulesByTenant.Count == 0 && globalGroupingRules.Count == 0` nicht. Alle `p`-Regeln verschwinden, `HasPolicies()` ist überall false, Casbin wird übersprungen. | `CasbinEnforcementService.cs` globales `LoadPolicyFromText` (Fail-closed-Prüfung bei „C-1: FAIL-CLOSED CHECK“) | Ablehnen, wenn die neue Datei **keine `p`-Regel** enthält, aber aktuell welche aktiv sind. Test mit einer Datei nur aus `g`-Zeilen. |
| E-2 ✔ | mittel | Der Snapshot ist nicht wirklich unveränderlich. `Enforcer`-Instanzen werden zwischen alten und neuen Snapshots geteilt und an Ort und Stelle geändert. `AddPolicy`/`AddRoleForUser` ändern den Enforcer des laufenden Snapshots, bevor getauscht wird; der Casbin-`Enforcer` ist nicht threadsicher. Der globale Reload fügt die `*`-Regeln bei jedem Lauf erneut in die erhaltenen Enforcer der Mandanten-Dateien ein, die Duplikate wachsen. Global **entfernte** Wildcard-Allows bleiben dort bestehen und werden bei `enforcer.Enforce(...)` (`:429`) weiter als Allow gewertet. | `CasbinEnforcementService.cs` (`AddPolicy`, `AddRoleForUser`, globaler Reload „apply them to preserved per-tenant enforcers“) | Für jeden geänderten Mandanten einen neuen Enforcer aus Mandanten- und aktuellen `*`-Regeln bauen; laufende Enforcer nie ändern. Test: Wildcard-Allow global entfernen, Mandant mit eigener Datei verliert den Zugriff. |
| E-3 | niedrig | Ob das Modell Wildcards kann, wird per `Contains` auf sechs Schreibweisen geprüft. Andere Leerzeichen oder Klammern ergeben ein falsches „nein“; `*`-Regeln werden dann abgelehnt (fail-closed). | `CasbinEnforcementService.cs` Konstruktor (`_modelSupportsWildcardTenant`) | Probe-Enforcer mit einer `*`-Regel auswerten statt Textsuche. |
| E-4 | niedrig | `GetOrCreateEnforcer` ist öffentlich und gibt für unbekannte Mandanten den geteilten `_emptyFallbackEnforcer` zurück. Ändert ein Aufrufer ihn, gilt das für alle unbekannten Mandanten. Heute gibt es keinen externen Aufrufer. | `CasbinEnforcementService.cs` `GetOrCreateEnforcer` | `private`/`internal` oder eine frische Instanz zurückgeben; Methodennamen anpassen. |
| E-5 | niedrig | Laufende Auswertungen können nach `_decisionCache.Clear()` Einträge mit alter Epoch einfügen. Sie werden nie mehr gelesen, aber auch erst beim nächsten Clear entfernt. | `CasbinEnforcementService.cs` `EvaluatePolicyAsync` | Größenlimit oder Einträge mit alter Epoch beim Schreiben verwerfen. |
| E-6 | info | Eine leere Mandanten-Datei wird abgelehnt, sobald irgendeine `*`-Regel aktiv ist (`HasPolicies` zählt `*` mit). Einen Mandanten per leerer Datei zu leeren ist damit nicht möglich. | `CasbinEnforcementService.cs` `LoadPolicyFromText(TenantId, …)` | Bewusst so lassen (fail-closed) und dokumentieren, oder nur Mandantenregeln zählen. |

Noch ohne Test: Datei nur mit `g`-Zeilen (E-1); Wildcard-Regeln zusammen mit Mandanten-Dateien (E-2); Modell ohne Wildcard-Klausel lehnt `*` ab (C-2); Simulation und Enforcement liefern dasselbe (C-2). Startprüfung von Modell und Policy (R-POL-5) und Warnung bei `Enabled=false` (R-POL-8) sind weiter offen.

## 1. Lage in Kürze (Stand d58449c; Casbin siehe Abschnitt 0)

- **Die beiden Build-Fehler sind behoben ✔** (B-1, B-2). Ob der Branch baut und die Tests grün sind, ist ungeprüft. Das muss `dotnet build` und `dotnet test` im Container zeigen.
- **Die bekannten Testbrüche sind adressiert** (B-3, B-4, B-5). B-5 wurde aber dadurch gelöst, dass WebSQL ein fehlendes Environment jetzt ebenfalls als Development behandelt. Damit sind OData und WebSQL einheitlich, aber in der unsicheren Richtung (S-1, D-2).
- **✅ behoben mit d77d650 – WebSQL-Lesezugriffe auf PostgreSQL** (D-1). Die neue Transaktion wird committet, während der DataReader noch offen ist. Npgsql lehnt das ab. Lesende WebSQL-Abfragen auf PostgreSQL dürften damit in Produktion fehlschlagen. Die Unit-Tests laufen auf SQLite und merken das nicht.
- **Casbin:** Mit `cb16e6c` weitgehend behoben (Abschnitt 0). Offen bleiben E-1 (Datei nur mit `g`-Zeilen ist weiter fail-open) und E-2 (geteilte, veränderte Enforcer).
- **Neu erledigt mit `d58449c`:**
  - R-SQL-3 (lokale GUCs in WebSQL; D-1 beachten), R-SQL-6 (Prozeduren nutzen den Initializer), R-SQL-7 / SQL2-18 (Rollback abgesichert), R-SQL-11 (Mandant validiert);
  - R-POL-4 (Events mit `string`, Logger), R-POL-10 (unbekannte Sensitivität gilt als hoch; D-4 beachten);
  - R-EXT-2 (`with`-Kopie), R-API-2 (FinOps-Fallback);
  - R-GQL-4 / SQL2-7 teilweise (Gesamt-Zeilenbudget 50.000 im Builder).
- **Für keinen Fix aus `d58449c` gibt es einen neuen Test.** Die tautologischen Tests aus `d57312f` sind unverändert (SQL-1 ✔).
- **Die PoC-Punkte DEP-1/2/3 sind unverändert offen.** Sie liegen im PoC-Repo.

## 2. Neue Befunde aus d58449c

| ID | Schwere | Befund | Fundstelle | Behebung |
|---|---|---|---|---|
| D-1 ✔ | ✅ behoben (d77d650), war hoch | WebSQL liest auf PostgreSQL jetzt in einer Transaktion. `CommitAsync` läuft, während `reader` (`await using`) noch offen ist; er wird erst am Ende des Blocks freigegeben. Npgsql wirft dann „An operation is already in progress“. Bei SQL Server und SQLite liefert der Initializer keine Transaktion, dort tritt es nicht auf. | `GovernedSqlExecutionService.cs:921-926` | Reader vor dem Commit schließen (eigener Block oder `await reader.DisposeAsync()` vor `CommitAsync`). Integrationstest gegen PostgreSQL (Testcontainers). |
| D-2 ✔ | mittel | S-1 in die unsichere Richtung vereinheitlicht: WebSQL behandelt `env == null` und `"Test"` jetzt auch als Development und liefert ohne Verbindung erfundene Daten. Der Ersatz-Executor in `GatewayExecutionService` bleibt. | `GovernedSqlExecutionService.cs:800-802`, `SqlDataSourceExecutor.cs:99-101`, `GatewayExecutionService.cs:40, 159-166` | Wie S-1: `null` gilt als Produktion, kein `"Test"`, Ersatz-Executor entfernen, Tests mit Development-Environment. |
| D-3 | niedrig | `ExecuteDmlInTransactionAsync` öffnet bei fehlender Transaktion (SQL Server, SQLite) eine neue, gibt sie aber nicht mehr frei (`await using` entfernt). Schlägt die DML fehl, rollt der äußere catch eine schon zurückgerollte Transaktion nochmals zurück; das wird nur geloggt. | `GovernedSqlExecutionService.cs:979` | Selbst geöffnete Transaktion in `try/finally` freigeben; Zuständigkeit für Commit und Rollback an einer Stelle. |
| D-4 | mittel | `Table.IsSensitivityHigh` stuft jeden Wert außer PUBLIC, INTERNAL, NORMAL und LOW als hoch ein. Im Code vorkommende Werte wie MEDIUM, CONFIDENTIAL und PII lösen damit jetzt Vier-Augen-Freigabe aus. Über `IsHighlySensitive` greifen auch kürzere Consent-TTL, Degraded-Mode, Backstage und weitere Stellen. Konservativ und damit sicher, aber eine fachliche Verhaltensänderung ohne Test und ohne Doku. | `TableModels.cs:37-52`, `ConsentApprovalPolicy.cs:122-123` | Bewusst entscheiden und dokumentieren (Migrationshinweis). Tests für die Wertetabelle. Gegebenenfalls MEDIUM ausnehmen. |
| D-5 | niedrig | `TableMetadata` ist jetzt ein `record`: Wertgleichheit statt Referenzgleichheit, und `ToString()` gibt alle Eigenschaften aus, einschließlich Maskierungsregeln mit `HmacKeyId`, falls das Objekt irgendwo geloggt wird. | `TableModels.cs:126` | Logging-Stellen prüfen; gegebenenfalls `PrintMembers` überschreiben. Verwendung als Dictionary-Key prüfen. |
| D-6 | niedrig | `GatewayForbiddenException($"Invalid tenant identity '{tenantVal}'.")` gibt den rohen Claim-Wert zurück. `FORBIDDEN` steht auf der Whitelist des `ErrorSanitizingFilter`, der Text geht also an den Client. | `SqlDataSourceExecutor.cs:328-331` | Generischer Text, Wert nur loggen. |
| D-7 | niedrig | Ohne `operationId` gilt jetzt eine Default-opId pro Scope. Aufrufer ohne opId teilen sich bei WebSocket das Memo für die ganze Verbindung (R-GQL-3 für diese Aufrufer wieder offen). Heute übergibt der GraphQL-Resolver die opId, deshalb ist das nur latent. | `GovernedTreeQueryService.cs:65, 106` | G-7: eine Methode mit Pflicht-`operationId` oder Memo pro Aufruf, wenn keine opId kommt. |
| D-8 ✔ | ✅ behoben | Das Gesamt-Zeilenbudget und `MaxOffset` sind jetzt konfigurierbar in `GraphQLOptions` (`MaxAggregateRowBudget`, `MaxAllowedOffset`) und werden direkt in `GovernedTreeQueryService` und `GraphQlTreeBuilder` durchgesetzt. | `GovernedTreeQueryService.cs`, `GraphQlTreeBuilder.cs`, `GatewayOptions.cs` | Budget und `MaxOffset` in die Optionen und in `GovernedTreeQueryService` verlagert. Tests für Budget und Offset. |
| D-9 | niedrig | `MssqlProcedureInvoker` baut `new TenantId(security.TenantId)` ohne Prüfung; ein ungültiger Wert ergibt 500 statt 403. | `MssqlProcedureInvoker.cs:77, 89` | `TenantId.TryParse` wie in `SqlDataSourceExecutor`. |

Unauffällig in `d58449c`:
- **Prozeduren:** Der Initializer setzt auf PostgreSQL die GUCs transaktionslokal, bei SQL Server bleiben `XACT_ABORT` und `LOCK_TIMEOUT` separat.
- **Rollback:** Er ist in Executor, Initializer und WebSQL abgesichert.
- **FinOps:** Der Fallback nutzt `TenantId.TryParse` statt zu werfen.
- **OData-Integrationstest:** Er fragt `$count` nicht mehr ab (501 seit O-Härtung).
- **GraphQL-Mocks:** Sie stubben jetzt die Überladung mit opId.

## 3. Befunde aus d57312f und ihr Stand

### Build und Tests

| ID | Status | Befund (Kurz) | Rest |
|---|---|---|---|
| B-1 ✔ | ✅ | `using` für `GatewayThrottledException` fehlte | |
| B-2 ✔ | ✅ | `TreeRawFilter` existierte nicht | Durch `TreeOrFilter([])` ersetzt. |
| B-3 | ✅ | GraphQL-Mocks stubbten die falsche Überladung | |
| B-4 | ✅ | Test „einmal pro Tabelle“ bekam zwei Aufrufe | Durch Default-opId pro Scope (D-7). |
| B-5 | ✅ 🔻 | WebSQL-Tests ohne Environment brachen | Durch `null` = Development gelöst (D-2). |

### Casbin

| ID | Status | Befund | Fundstelle | Behebung |
|---|---|---|---|---|
| C-1 | ✅ (5c795e4) | Global: leere, nur kommentierte und nur-`g`-Dateien werden abgelehnt. Mandanten-Ebene siehe F-1. | | F-1, F-2. |
| C-2 ✔ | 🟡 (Simulation, siehe Nachtrag) | `rbac_with_abac.conf` und `PolicySimulationService` haben jetzt `(r.tenant == p.tenant \|\| p.tenant == "*")`; ein Modell ohne Wildcard-Klausel lehnt `*`-Regeln ab. | | Tests fehlen; E-3. |
| C-3 | ✅ (cb16e6c) | Deny- und Allow-Abgleich ignorieren Groß-/Kleinschreibung (Test vorhanden). Der Casbin-Matcher selbst bleibt case-sensitiv (fail-closed). | | |
| C-4 | ✅ (5c795e4) | Unveränderliche Snapshots, neue Enforcer bei jeder Änderung. | | E-5, F-4. |
| C-5 | ✅ (cb16e6c) | Keine Mutation im Lesepfad (Test vorhanden), Mandanten-Dateien überleben den globalen Reload, Laden pro Mandant übernimmt `*`-Regeln. | | E-4, E-6. |

### SQL und WebSQL

| ID | Status | Befund | Behebung |
|---|---|---|---|
| S-1 | 🔻 | Ersatz-Executor liefert erfundene Daten; `null` und `"Test"` gelten überall als Development (D-2). | `null` gilt als Produktion, kein `"Test"`, Ersatz-Executor entfernen. |
| S-2 ✔ | ✅ | `GatewayNotImplementedException` mit generischem Text; Parserfehler auf 400. |

### GraphQL

| ID | Status | Befund | Behebung |
|---|---|---|---|
| G-1 | ⛔ | `val.ToString()` kulturabhängig: Unter de-DE wird `1.5` für eine Decimal-Spalte zu 15 (`GraphQlTreeBuilder.cs`, `ResolveValue`/`CoerceValue`). | `CultureInfo.InvariantCulture`; Test unter de-DE. |
| G-2 ✔ | ✅ | Kollisionsauflösung deterministisch mit `StringComparer.Ordinal`; kollidierende Tabellen werden geloggt und ausgelassen, kanonische Tabellen behalten ihren stabilen Namen (`CatalogSchemaModel.cs`). | `StringComparer.Ordinal`; stabile Namenszuordnung; Kollisionen loggen. |
| G-3 | ✅ | Memo und Audit-Set pro Operation bereinigt (`GovernedTreeQueryService.cs`, `CatalogOperationCleanupMiddleware.cs`). | Memo pro Operation mit `ClearOperation` nach Request-Ende. |
| G-4 | ✅ | opId threadsicher unter Lock in `CatalogGraphQlTypeModule.cs` gesetzt. | Atomic Check-and-Set mit Request-Lock. |
| G-5 | ⛔ | Variable in `orderBy` wird nicht aufgelöst. | `ResolveVariableLiteral`. |
| G-6 | ⛔ | `IsStringType` dupliziert `MapDataType`. | Gemeinsamer Typklassifizierer. |
| G-7 | ✅ | Pflicht-`operationId` in `ExecuteAsync`; Default pro Scope und parameterlose Überladung entfernt (D-7). | Eine Methode mit Pflicht-`operationId`. |
| G-8 ✔ | ✅ | HMAC auf Nicht-String-Spalten wird im Schema als String typisiert; `NumberStyles.Any` durch strikte Formate ersetzt; Bool unterstützt Zahlenwerte wie `1.0`; Maskierte Werte auf Nicht-String-Spalten melden Fehlercode `MASKED` und `null`. | `CatalogSchemaModel.cs`, `CatalogGraphQlTypeModule.cs` |
| G-9 | ⛔ | `isNull: null` wird `IS NOT NULL`; `not: {}` filtert nichts. | Ablehnen bzw. als false. |

## 4. Gesamtstatus aller Befunde

Legende: ✅ behoben · 🟡 teilweise · ⛔ offen · 🔻 verschlechtert · 🧪 Test fehlt oder ist wertlos

### Gesamt-Review: hoch

| ID | Status | Rest |
|---|---|---|
| POL-1 Casbin nie geladen | 🟡 | Laden beim Start, Logging, unveränderliche Snapshots (E-2), Modellprüfung per Probe (E-3), Wildcards, Groß-/Kleinschreibung sind da. Offen: F-1 (fail-open bei Mandanten-Datei nur mit `g`-Zeilen), F-3, R-POL-5 (Policy beim Start), R-POL-8, C-2-Rest, F-4. |
| SQL-1 Mandantenspalte bei UPDATE | ✅ 🧪 | Beide SQL-1-Tests sind tautologisch ✔ (`EnforceReadOnlyQueries` true). |
| SQL2-1 `clear` in Prozeduren | ✅ 🧪 | Test „maskiert + clear bleibt Mask“ fehlt; ADR-018 ergänzen. |
| DEP-1/2/3 PoC | ⛔ | Im PoC-Repo, mit Owner abstimmen. |

### Gesamt-Review: mittel

| ID | Status | Rest |
|---|---|---|
| GQL-1 WebSocket-Identität | 🟡 | R-GQL-8, R-GQL-9; keine Tests. |
| POL-2 Vier-Augen bei HIGH | ✅ 🧪 | Jetzt für jeden unbekannten Wert (D-4); Repository-Tests fehlen. |
| POL-3 ITSM-Spaltensnapshot | ✅ 🧪 | Dreifach dupliziert (R-POL-11). |
| POL-4 Lakehouse-HMAC | ✅ 🧪 | Test nur für den Helper; R-POL-12. |
| POL-5 Art.-9-Spaltentags | ⛔ | |
| POL-6 ReBAC nur auf drei Pfaden | ⛔ | |
| POL-7 ReBAC-Wildcard | ✅ 🧪 | API-400 und Userset ungetestet. |
| EXT-1 dbt-Vorschläge | 🟡 🧪 | R-EXT-1 offen; Test prüft den Ratchet. |
| EXT-2 SourceName | ✅ | |
| EXT-3 / API-12 Shadowing-Header | ⛔ | |
| EXT-5 Lakehouse-Allowlists | ⛔ | |
| INF-1 Epoch bei Redis-Fehler | ⛔ | |
| SQL-2, SQL-3, SQL-5 | ⛔ | |
| MCP-1 Ressourcen ohne Spaltenfilter | ⛔ | |
| GQL-3, GQL-4 Subscriptions | ⛔ | |
| API-1 DANGER-Schalter | ✅ | Migrationshinweis fehlt. |
| API-2, API-3 | ⛔ | |
| API-4 FinOps-Budget | ✅ 🧪 | |
| DEP-6 / POL-14 Schlüssellänge | 🟡 🧪 | R-DEP-1. |
| DEP-5, DEP-7, DEP-8 | ⛔ | |
| SQL2-3 synthetische Daten | 🔻 | S-1, D-2. |
| SQL2-8 Byte-Budget Prozeduren | 🟡 🧪 | R-SQL-8. |
| SQL2-9 DuckDB-Timeout | ⛔ | |

### Gesamt-Review: niedrig, Laufzeit, Architektur

| Gruppe | Status |
|---|---|
| Niedrig: POL-8 bis -13, POL-19, GQL-5, -6, -12, MCP-4 bis -7, API-8, -9, -11, -13, -16, -17, EXT-6, -7, INF-2, -3, SQL-6, SQL2-10, -14, -15, -16, -19, WF-1, DEP-9 bis -16 | ⛔ alle offen |
| POL-15 PG-SID | ✅ 🧪 |
| SQL2-18 Rollback-Token | ✅ (Executor, Initializer, WebSQL, Baum) |
| SQL2-7 Zeilenbudget, MaxOffset | ✅ Konfigurierbares Zeilenbudget (`MaxAggregateRowBudget`) und `MaxOffset` in Optionen und Dienst durchgesetzt (D-8) |
| SQL2-11 Parameter-Präfix | ⛔ (fail-closed) |
| SQL2-12 HMAC-Normalisierung | ⛔ |
| SQL2-13 Memo pro Tabelle | ✅ weitgehend (G-3, G-4, D-7) |
| Laufzeit: DEP-4 (✅ behoben), SQL2-2 (✅ behoben), SQL2-4 (✅ behoben), SQL-4, GQL-2, MCP-2/-3, EXT-4, API-7, -14, -15, INF-4, -5, EXT-8, GQL-8, -11, SQL2-17, SQL-7, -8 | 🟡 DEP-4, SQL2-2, SQL2-4 behoben, Rest offen |
| Arch 1 zentrale Zugriffsentscheidung | ⛔ |
| Arch 2 gemeinsamer Konnektor-Lesepfad | ⛔ |
| Arch 3 toter Code | 🟡 `SingleQueryAstCompiler` entfernt; R-SQL-12 offen |
| Arch 4 Stubs ehrlich machen | 🔻 (S-1, D-2) |
| Arch 5 Provider- und Session-Init | 🟡 Initializer jetzt auch für Prozeduren; Provider-Namen weiter an mehreren Stellen |
| Arch 6 Doku an Optionen | ⛔ |
| Arch 7 Tree vor dem Verdrahten | 🟡 SQL2-7 teilweise; SQL2-11, -12 offen |

### Review c4f2c32 (R-…)

| ID | Status | Rest |
|---|---|---|
| R-SQL-1 Tests brechen | 🟡 🔻 | Durch S-1/D-2 „gelöst“. |
| R-SQL-2 501 | ✅ | S-2 behoben. |
| R-SQL-3 Sitzungs-GUCs WebSQL | ✅ | D-1 behoben (d77d650); D-10. |
| R-SQL-4 READ ONLY Row-Scope | ⛔ | |
| R-SQL-5 429 Drosselung | ✅ 🧪 | |
| R-SQL-6 Prozeduren ohne Initializer | ✅ | D-9. |
| R-SQL-7 Rollback verdeckt Exception | ✅ | |
| R-SQL-8 LOB-Budget | ⛔ | |
| R-SQL-9 SQL-1-Tests | 🧪 | Tautologisch. |
| R-SQL-10 `clear` ohne Quellinfo | ⛔ | Test und ADR-018. |
| R-SQL-11 TenantId wirft | ✅ | D-6. |
| R-SQL-12 Reste `SingleQueryAstCompiler` | ⛔ | |
| R-SQL-13 toter Code `GetSessionInitializationSql` | ⛔ | |
| R-POL-1 Wildcard-Allow | ✅ | C-2 (cb16e6c). |
| R-POL-2 globales Deny | ✅ | C-3 (cb16e6c). |
| R-POL-3 Reload | 🟡 | E-2 ✅; F-1. |
| R-POL-4 Events/Logger | ✅ | |
| R-POL-5 Start-Parse | 🟡 | Modell ✅ (8636038); Policy-Datei offen; F-7. |
| R-POL-6 Default-Methoden | ✅ 🧪 | |
| R-POL-7 Wildcard-Enforcer | ✅ | E-4 offen. |
| R-POL-8 Warnung bei `Enabled=false` | ⛔ | |
| R-POL-9 POL-15 | ✅ | |
| R-POL-10 Sensitivität zentral | ✅ | D-4. |
| R-POL-11, R-POL-12 | ⛔ | |
| R-EXT-1 dbt-Status „Approved“ ohne Wirkung | ⛔ | |
| R-EXT-2 `TableMetadata`-Kopie | ✅ | D-5. |
| R-DEP-1 Schlüssellänge an Verbrauchern | ⛔ | |
| R-ERR-1 `INVALID_QUERY`-Text | ✅ | Generischer Text außerhalb Development (`4852f55`). |
| R-API-1 Migrationshinweis | ⛔ | |
| R-API-2 FinOps-Fallback | ✅ | |
| R-GQL-1 maskierte Typen | ✅ | G-8; HMAC-Spalten im Schema als String typisiert; maskierte Nicht-String-Felder liefern null mit Fehlercode MASKED. |
| R-GQL-2 Kollisionen | ✅ | G-2; Kollidierende Tabellen ausgelassen, kanonische Tabellen bleiben stabil, Fehler wird geloggt. |
| R-GQL-3 Memo pro Verbindung | ✅ | G-3, G-4, G-7, D-7; `ClearOperation` im Request-Middleware nach Request-Ende, FIFO-Eviction als Fallback. |
| R-GQL-4 Budget/Offset | ✅ | D-8; Zeilenbudget und MaxOffset in Optionen und Dienst durchgesetzt. |
| R-GQL-5 verschachteltes offset | ✅ | |
| R-GQL-6 Katalog aufzählbar | ✅ | `GraphQlEnumerationShieldMiddleware` vereinheitlicht Validierungs-, `ACCESS_DENIED`- und `INVALID_QUERY`-Fehler; `EnableSchemaRequests=false` ohne Introspection (`4852f55`). |
| R-GQL-7 N² Relationen | ✅ | |
| R-GQL-8 ✔ | ✅ | Subjektprüfung strikt für alle Tenanten; Token ohne SID abgewiesen. |
| R-GQL-9 | ⛔ | |
| R-GQL-10 Datum/Decimal | 🟡 | G-1; SQLite-Format. |
| R-GQL-11 Null-Semantik | 🟡 | G-9. |
| R-GQL-12 TypesChanged | ⛔ | |
| R-GQL-13 nicht abfragbare Spalten | ⛔ | |
| R-GQL-14 SID im Baumpfad | ✅ | |
| R-GQL-15 Sortierrichtung | 🟡 | G-5. |

## 5. Umsetzungsplan

Regeln für alle Phasen:
- Jede Phase endet mit grünem `dotnet build` und `dotnet test`. Erst danach wird gepusht.
- Ein Thema pro Commit, mit aussagekräftiger Commit-Message.
- **TDD:** Jeder Sicherheitstest muss **ohne** den Fix fehlschlagen. Vor dem Commit den Fix kurz zurücknehmen und prüfen, dass der Test rot wird.
- Einen Pull Request nach `main` öffnen, damit die CI läuft.
- Datenbankspezifisches Verhalten (Transaktionen, GUCs, Reader) braucht Integrationstests gegen echte Datenbanken (Testcontainers für PostgreSQL und SQL Server). SQLite allein deckt D-1 nicht auf.

Aufwand: S = bis 2 h, M = halber bis ganzer Tag, L = mehrere Tage.

### Phase 0 – Build und Tests bestätigen (S)

1. `dotnet build` und `dotnet test` im Container. Rote Tests beheben.
2. PR nach `main` öffnen und die CI beobachten.

### Phase 1 – Fail-open und Laufzeitfehler mit Sicherheitsbezug (M–L)

1. **D-1 (Code ✅ d77d650):** Integrationstest WebSQL-SELECT gegen PostgreSQL (Testcontainers) nachziehen; D-10 (Transaktion nach Commit freigeben).
2. **Casbin-Rest (E-1/E-2/E-3 mit 5c795e4 und 8636038 umgesetzt, aktueller Rest):**
   - **F-3:** `TenantId` wieder streng, Wildcard nur intern; Probe W6; Testfall `*`.
   - **F-1:** Mandanten-Datei ohne `p`-Regel ablehnen, wenn vorher welche da waren; Test.
   - **F-2, F-5:** Prüfungen präzisieren.
   - **F-4:** Thread-Sicherheit von `Enforce` klären, gegebenenfalls Lock.
   - **E-4:** `GetOrCreateEnforcer` nicht öffentlich.
   - **C-2-Rest:** Simulation mit konfiguriertem Modell, gemeinsamem Parser und echtem `SecurityEvaluationContext`; Vergleichstest.
   - **Tests:** Test01 so umbauen, dass er ohne Fix rot ist; Test04, Test05, Test07 ergänzen.
   - **F-6:** Doku korrigieren.
   - Die folgenden Unterpunkte sind der Stand vor 5c795e4 und nur noch zur Nachverfolgung:
   - E-1: Datei ohne `p`-Regel ablehnen, solange `p`-Regeln aktiv sind; Test mit Datei nur aus `g`-Zeilen.
   - E-2: Pro Snapshot neue Enforcer bauen, laufende nie ändern; Test „Wildcard-Allow global entfernt, Mandant mit eigener Datei verliert Zugriff“.
   - Fehlende Tests zu C-2: Modell ohne Wildcard-Klausel lehnt `*` ab; Simulation und Enforcement liefern dasselbe.
   - E-3 bis E-6 niedrig, mit Phase 7.
3. **R-POL-5-Rest, F-7, R-POL-8:** Policy-Datei beim Start probeweise parsen (gemeinsamer Parser); `ModelPath` auch bei `Enabled=false` prüfen oder Service eager auflösen; Warnung bei `Enabled=false`. (Modellprüfung beim Start ✅ 8636038.)
4. **S-1, D-2:** Ersatz-Executor entfernen; `null` gilt überall als Produktion, kein `"Test"`. Die betroffenen Tests mit Development-Environment oder injiziertem Executor bauen. Den Test `R_SQL_1_…GeneratesSyntheticData_InDevelopmentOrUnitTests` umkehren.
5. **S-2:** ✅ behoben (`GatewayNotImplementedException` mit generischem Text auf 501, Parserfehler auf 400).
6. **G-1:** Kulturunabhängige Konvertierung; Test unter de-DE.
7. **R-GQL-8:** ✅ behoben (Bei HTTP-Anmeldung und abweichendem Subjekt immer abweisen, Token ohne SID abweisen).
8. **D-4:** Entscheidung zur Sensitivitäts-Einstufung festhalten (MEDIUM, CONFIDENTIAL, PII), Tests für die Wertetabelle, Migrationshinweis.

### Phase 2 – Tests für die behobenen Befunde (M)

Jeder Test muss ohne Fix rot werden:

| Befund | Test |
|---|---|
| SQL-1 | `EnforceReadOnlyQueries = false`, `EnforceWithCheckOption = true`; UPDATE und INSERT mit `TenantId` in beiden Engines; Multi-Table-Fall. |
| SQL2-1 | Maskierte Spalte, als `clear` deklariert, bleibt Mask. |
| POL-2 / D-4 | Repository-Tests SQLite und PG; Wertetabelle für `IsSensitivityHigh`. |
| POL-3 | `ActivateConsentAsync` legt Spaltenregeln an; spätere Spalte ist nicht Clear. |
| POL-4 | Lakehouse- und DeltaLake-Executor: verschiedene Pseudonyme je Mandant, keine Doppelmaskierung. |
| POL-7 | API ergibt 400; Userset `group:x#member` funktioniert weiter. |
| POL-15 | PG-Aktivierung für Gruppe und Service-Principal setzt `grantee_sid`. |
| EXT-2 / R-EXT-2 | Batch und Webhook behalten `SourceName`; dbt-Approve behält alle Eigenschaften. |
| API-4 / R-API-2 | Anonym wird nicht verbucht; ungültiger Mandanten-Claim wirft nicht. |
| DEP-6 | 31 Byte wirft, 32 Byte ok, Development ok; ForwardAuth; `ColumnMaskingProvider`. |
| R-SQL-3 | PostgreSQL: `set_config(…, true)` in derselben Transaktion wie die Abfrage. |
| R-SQL-5 | Drosselung ergibt 429 mit `Retry-After`. |
| R-SQL-6 | Prozeduraufruf setzt die Sitzungsvariablen über den Initializer. |
| R-SQL-7 | Rollback-Fehler verdeckt die Original-Exception nicht. |
| R-SQL-11 | Ungültiger Mandant ergibt 403 ohne Claim-Text in der Antwort (D-6). |
| R-POL-4 | Reload-Fehler wird geloggt, Event mit `"*"` wirft nicht. |
| R-POL-6 | Default-Methoden werfen. |
| D-8 ✔ | ✅ Gesamt-Zeilenbudget überschritten ergibt `INVALID_QUERY`, im Dienst und Builder validiert. |

Den irreführenden Test `DbSessionContextInitializer_RollsBackTransaction_OnError` umbenennen oder ersetzen.

### Phase 3 – GraphQL fertigstellen (L)

1. **G-2 (R-GQL-2) ✔:** ✅ Stabile Namensvergabe; Fehler pro Tabelle loggen und die Tabelle auslassen; Executor-Test, dass das Schema mit Kollisionen startet.
2. **D-8 (SQL2-7) ✔:** ✅ Zeilenbudget und `MaxOffset` konfigurierbar und im Dienst prüfen.
3. **G-8 (R-GQL-1) ✔:** ✅ HMAC-Spalten als String oder Code `MASKED`; Executor-Tests mit maskierter Int- und Bool-Spalte (SQLite).
4. **G-3, G-4, G-7, D-7 ✔:** ✅ Memo pro Operation bereinigt, opId thread-safe im Resolver/Interceptor, eine `ExecuteAsync` mit Pflicht-`operationId` und `CatalogOperationCleanupMiddleware`.
5. **R-GQL-6, R-ERR-1:** Unbekannte und gesperrte Felder einheitlich melden; `EnableSchemaRequests = false`; generischer Text für `INVALID_QUERY` ✅ `4852f55`.
6. **R-GQL-9:** `SecurityPrincipalContext.TenantId` als Quelle in den Mutationen; Admin-Ausnahme einheitlich.
7. **R-GQL-10, G-9, G-5:** SQLite-Datumsformat, Null-Semantik, Variablen in `orderBy`.
8. **R-GQL-12, R-GQL-13, G-6:** Schema-Reload an die Katalog-Epoch koppeln; nicht abfragbare Spalten und Dialekte auslassen; gemeinsamer Typklassifizierer.
9. **SQL2-11, SQL2-12:** Parameter-Präfix pro Tabelle; gemeinsame HMAC-Normalisierung mit REST.
10. **Tests laut Übergabe:** Ende-zu-Ende mit SQLite über `WebApplicationFactory`; gleiche Zeilen wie OData und WebSQL; Abnahme `fms/air1` unter 2 s.

### Phase 4 – SQL, Sitzungskontext, Prozeduren (M)

1. **R-SQL-4:** `readOnly` im Initializer für den Row-Scope.
2. **D-3:** Transaktionsverantwortung in `ExecuteDmlInTransactionAsync` klären, Freigabe in `finally`.
3. **D-6, D-9:** Generische Fehlertexte; `TenantId.TryParse` im Prozeduraufruf.
4. **R-SQL-8 / SQL2-8:** `SequentialAccess` mit Restbudget, Base64-Faktor, eigenes Limit.
5. **R-SQL-10:** Test und ADR-018.
6. **R-SQL-12, R-SQL-13:** Reste von `SingleQueryAstCompiler`, `GetSessionInitializationSql` und `SqlFilterProvider` entfernen.
7. **SQL-2, SQL-3, SQL-5:**
   - Policy-Maps pro Dialekt gefaltet.
   - Factory wirft bei unbekanntem Dialekt.
   - Katalogteil dreiteiliger Namen umschreiben.
8. **SQL2-9:** DuckDB `Interrupt` und Generatoren sperren.
9. **D-5:** Logging von `TableMetadata` prüfen.

### Phase 5 – Offene mittlere Befunde aus dem Gesamt-Review (L)

1. **R-EXT-1:** dbt-Approve für RLS_FILTER/CASBIN_ROLES ablehnen oder eigener Status.
2. **INF-1:** Redis-Fehler bei der Epoch-Prüfung als „degraded“ behandeln.
3. **MCP-1, MCP-5:** Spaltenfilter für MCP-Ressourcen und Golden Queries.
4. **POL-6:** ReBAC zentral in `ResolveTableAccessAsync` (oder Geltungsbereich dokumentieren).
5. **GQL-3, GQL-4:** Audit und Limits für Subscriptions.
6. **POL-5:** Art.-9-Tags auf Spaltenebene.
7. **EXT-5:** Allowlists für Lakehouse-Container und -Buckets.
8. **EXT-3 / API-12:** Header-Allowlist beim Shadowing.
9. **API-2, API-3:** Envoy-JSON-Modus und Schema-Contracts klären.
10. **R-DEP-1:** Längenprüfung an den Verbrauchern, keine Cross-Secret-Aliase.
11. **DEP-7, DEP-8, DEP-5:** TLS-Prüfung in Produktion; Serilog-Overrides und Query-Strings redigieren; Image-Name.

### Phase 6 – Laufzeitfehler (M–L)

1. **DEP-4:** ✅ behoben (`HTTPS_PORTS` weg, `/app/data` mit `chown` und als VOLUME, Startvalidierung & automatische Verzeichniserstellung).
2. **SQL2-2:** ✅ behoben (OLAP-Kapazität: dynamischer Ceiling in SqlConnector, Staging + 1 Anforderung und deterministischer 400-Abbruch bei Überschreitung).
3. **GQL-2:** WebSocket-Header.
4. **SQL2-4:** ✅ behoben (keine Doppelmaskierung: ConnectorRowMasker und CrossDomainJoinEngine prüfen InDbColumnMaskingExecuted).
5. **EXT-4:** Stubs außerhalb von Development ablehnen.
6. **MCP-2, MCP-3:** async Store, ein Writer pro SSE-Session, Subscription freigeben.
7. Danach: SQL-4, API-7, API-14, API-15, INF-4, INF-5, EXT-8, GQL-8, GQL-11, SQL2-17, SQL-7, SQL-8.

### Phase 7 – Niedrige Befunde und Architektur (L, fortlaufend)

- Niedrige Befunde gebündelt nach Bereich (CI/Lieferkette DEP-9 bis -16, MCP, API, Richtlinien).
- **Architektur 1:** Eine zentrale Zugriffsentscheidung (`ITableAccessResolver`); löst POL-6, SQL2-6 und API-10 strukturell.
- **Architektur 2:** Gemeinsamer Lesepfad für Konnektoren (`GovernedConnectorReader`).
- **Architektur 3:** Restlichen toten Code entfernen.
- **Architektur 6:** Doku an die Optionsklassen angleichen, dazu die Migrationshinweise (R-API-1, Casbin `Enabled`, 32-Byte-Schlüssel, D-4 Sensitivität).

### Parallel – PoC (sofort, außerhalb dieses Repos)

DEP-1/2/3 mit dem Owner des PoC-Repos abstimmen:
- Passwörter und HMAC-Schlüssel rotieren.
- Defaults aus `compose.yaml` entfernen (`${VAR:?}`).
- Ports nur an 127.0.0.1 binden.
- Test-DB oder Read-only-Replikat statt LWETEM_PROD.
