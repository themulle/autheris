# Security-Nachprüfung, Stand 2026-10-02 (nach SQ-Fixes, EX-01/13 und Extension-Umbau)

**Scope:**
1. Die SQ-01 bis SQ-16-Fixes in `gql_sqlparser` (Commit `4283f17`) und in `GovernedSqlExecutionService` (Commit `0fd2cc7`).
2. Der Umbau „alle Anbindungen nach gql_extensions“ einschließlich Egress-Allowlist.
3. Die Fixes für EX-01 und EX-13.
4. Der Status der übrigen EX-Befunde.

**Methode:** Statische Analyse von Diff und aktuellem Code, keine Laufzeittests. Bei Parser-Differentials wird nur der Mechanismus beschrieben, Payloads stehen bewusst nicht im Dokument.

---

## 1. Kurzfazit

- **SQ-01 und SQ-02 (Lexer-Differentials) sind im Gateway-Pfad wirksam geschlossen.** Kommentare werden abgelehnt (doppelt geprüft), Backslashes in Strings werden abgelehnt, verschachtelte Kommentare werden erkannt, und für SQL Server ist `$$` gesperrt. Die Prüfungen hängen aber an einer Bedingung, die mit der Tiefenprüfung abgeschaltet wird (P-03).
- **SQ-08 (Parsen mit Zeitbudget und eigenem Stack) ist nicht umgesetzt.** SQ-06 ist weiterhin nur eine Denylist. SQ-11, SQ-13 und SQ-15 sind im Gateway nicht aktiv.
- **Neu im Parser:** Methodenaufruf-Syntax (`x.f()`, `T::f()`) umgeht die Funktions-Policy (P-01). Der Dialekt wird aus dem Katalog statt aus der Verbindung bestimmt (P-05).
- **Beim Umbau gab es keine Regression.** Alle verschobenen Sicherheitsfunktionen sind erhalten, die DI-Registrierungen sind korrekt, und jeder Extension-Client läuft durch den SSRF-Handler.
- **Die neue Egress-Allowlist ist die Hauptschwachstelle des Umbaus:** keine Validierung der Netze, global für alle Clients statt pro Integration, Redirects und DNS-Rebinding werden nicht abgefangen (E-01 bis E-03).
- **EX-01 ist für Production wirksam** (DANGER bricht den Start ab), widerruft aber bestehende Auto-Consents nicht (E-05). **EX-13 und EX-15 sind behoben.** Die übrigen EX-Befunde sind offen.

---

## 2. Status SQ-01 bis SQ-16

| ID | Status | Begründung |
|---|---|---|
| SQ-01 | ✅ wirksam | `FastSqlEngine.cs:225` lehnt Backslashes in jedem STRING-Token ab, `:232-240` lehnt `E'…'` ab. **Offen:** `standard_conforming_strings=on` wird pro Session nicht erzwungen (zweite Verteidigungslinie fehlt). |
| SQ-02 | ✅ wirksam (Abhängigkeit siehe P-03) | Kommentare: `FastSqlEngine.cs:215` und `RlsListener.cs:103-113`. Verschachtelung: `:206-213`. `$$` bei T-SQL: `:374`. Fremde Whitespace-Zeichen und NUL führen zu UNRECOGNIZED, also Parse-Fehler (fail-closed). |
| SQ-03 | ✅ wirksam | `RlsListener.cs:921-941`: ColumnReference und Dereference in SET/WHERE, auch in Subqueries. |
| SQ-04 | ✅ weitgehend | Nur noch aufgelöste Namen bzw. Kurznamen, wenn der Name im SQL tatsächlich unqualifiziert steht. Kein Rückfall auf Kurznamen im Provider. Rest: Mehrdeutige Kurznamen werden nicht ausdrücklich abgelehnt (fail-closed, aber inkonsistent). |
| SQ-05 | 🟡 teilweise | LIMIT-Werte sind numerisch und damit injektionsfrei. Bei T-SQL ergeben `OFFSET` zusammen mit LIMIT ein doppeltes OFFSET, und `FETCH FIRST` wird nicht umgebaut. Der Alias aus Roh-Text bricht bei `"a.b"`. Alles fail-closed, aber funktional falsch. |
| SQ-06 | ❌ unwirksam als Allowlist | Nur die Denylist wurde erweitert (`SqlFunctionPolicy.cs:30-57`). `AllowedFunctions` wird im Gateway nie gesetzt; keine Unterscheidung nach Dialekt. Lücken siehe P-02. |
| SQ-07 | ✅ wirksam | `RlsListener.cs:470-477`, `GSES:364-365, 472-474` |
| SQ-08 | ❌ unwirksam | `FastSqlEngine.cs:70-124`: kein eigener Thread, kein maxStackSize, kein Timeout. `MaxNestingDepth` steht weiter auf 200. Nur `EnsureSufficientExecutionStack` am Einstieg, das schützt nicht innerhalb der ATN-Rekursion. |
| SQ-09 | ✅ wirksam | Namen mit 4 oder mehr Teilen werden abgelehnt (`SqlQueryAnalyzer.cs:372-375`). Der Katalogteil wird gegen die Datenquelle geprüft (`GSES:287-293`). |
| SQ-10 | ✅ wirksam | Case-Folding nur für ASCII (`ZeroCopyCaseInsensitiveStream.cs:61,72`) |
| SQ-11 | ❌ im Gateway inaktiv | Schalter vorhanden (Default false), das Gateway setzt ihn nicht |
| SQ-12 | ❌ offen | Resolver ohne dialektgerechtes Case-Folding |
| SQ-13 | ❌ im Gateway inaktiv | `RejectTimeTravelQueries` steht auf Default false. Es greift nur das alte, zufällig fail-closed Verhalten. |
| SQ-14 | ✅ wirksam | `ParserPooledObjectPolicy.cs:39` |
| SQ-15 | ❌ offen | `MaxAffectedRows = 0` (unbegrenzt) steht nicht in der Bypass-Liste |
| SQ-16 | ❌ offen | `SqlParameterExtractor` unverändert |

---

## 3. Neue Befunde – Parser / WebSQL

### P-01 🟠 Methodenaufruf-Syntax umgeht die Funktions-Policy *(per Test verifizieren)*
`SqlBase.g4:653-654`; `RlsListener.cs:182`; `SqlQueryAnalyzer.cs:124`
```antlr
| qualifiedName '::' methodName '(' (argument (',' argument)*)? ')'   #staticMethodCall
| primaryExpression '.' methodName '(' (argument (',' argument)*)? ')' #methodCall
```
Listener und Analyzer prüfen nur `FunctionCallContext`. Für `a.f(x)` gewinnt bei der Mehrdeutigkeit zwar die frühere Alternative `functionCall`. Aufrufe auf nicht-triviale Ausdrücke wie `(…).f(…)` oder `x[1].f(…)` sowie `T::f(…)` werden aber als `methodCall`/`staticMethodCall` geparst und gehen **ohne Policy-Prüfung** an die DB.
- **Relevanz:** vor allem SQL Server, das Methoden auf XML- und CLR-Typen kennt (`.value()`, `.query()`, `.nodes()`, statische Methoden auf CLR-Typen). PostgreSQL und SQLite lehnen die Syntax ab.
- **Fix:** `methodCall` und `staticMethodCall` in Listener und Analyzer grundsätzlich ablehnen; Regressionstests für qualifizierte Namen und Ausdrucksketten.

### P-02 🟡 Denylist weiter lückenhaft (SQ-06)
`SqlFunctionPolicy.cs:17-57`. Beispiele, die fehlen:
- **PostgreSQL:** eine Textsuche-Statistikfunktion, die SQL-Text als Argument ausführt (`ts_stat`; Wirkung wie `query_to_xml`, also RLS-Bypass), `setval`/`nextval` (Seiteneffekte), `pg_logical_slot_*`, `has_*_privilege`, `to_regclass`, `inet_client_addr`, `current_database`.
- **SQL Server:** `SESSION_CONTEXT`, `CONTEXT_INFO`, `OBJECT_DEFINITION`, `ORIGINAL_LOGIN`, `HOST_NAME`.
- **SQLite:** `load_extension`, `readfile`/`writefile`, `zeroblob`/`randomblob` (Speicher-DoS).

**Fix:** Allowlist pro Dialekt im Gateway (Standard-Skalar-, Aggregat- und Fensterfunktionen), Denylist nur als zweite Linie. DB-Login ohne Superuser-, Replikations-, Datei- oder `VIEW SERVER STATE`-Rechte.

### P-03 🟡 Alle Lexer-Sperren hängen an `MaxNestingDepth > 0`
`FastSqlEngine.cs:186`
```csharp
if (MaxNestingDepth <= 0) return;
tokens.Fill();
```
Die Prüfungen für SQ-01, -02, -10, -11 und -13 liegen in derselben Methode wie die Tiefenprüfung. Wer die Tiefenprüfung abschaltet (laut Doku bedeutet 0 „aus“), schaltet damit stillschweigend auch die Differential-Sperren ab (Backslash, `E'…'`).
**Fix:** Token-Prüfungen in eine eigene Methode auslagern, die immer läuft.

### P-04 🟢 Sicherheitsschalter als veränderlicher Instanzzustand
`FastSqlEngine.cs:359-396`: `RewriteRls` setzt `RejectComments` usw. auf die Engine-Instanz und stellt sie im `finally` zurück. Bei gemeinsam genutzter Engine (Singleton, andere Nutzer der Bibliothek) kann ein paralleler Aufruf die Schalter des anderen auf false zurücksetzen (fail-open). Im Gateway ist der Service Scoped, deshalb derzeit kaum ausnutzbar.
**Fix:** Optionen als Parameter an `Parse` durchreichen, keinen gemeinsamen Zustand verwenden.

### P-05 🟡 Dialekt kommt aus dem Katalog der ersten Tabelle, nicht aus der Verbindung
`GSES:276, 437-443`
```csharp
targetDatabaseDialect ??= tableMeta.Dialect;
... _ => TargetSqlDialect.Ansi
```
- Die erste Tabelle bestimmt den Dialekt. Abweichende Dialekte weiterer Tabellen werden nicht abgelehnt.
- Unbekannte Dialekte (Databricks, Oracle) fallen auf Ansi zurück, also ohne `$$`-Sperre und ohne T-SQL-Rewrite.
- Die Verbindungskonfiguration (`DataSourceConnectionOptions`) wird nicht gegengeprüft.

**Fix:** Dialekt aus der Datenquellen-Konfiguration ableiten, jede Tabelle dagegen prüfen, unbekannte Dialekte ablehnen. Bei PostgreSQL `SET standard_conforming_strings = on` pro Session.

### P-06 🟢 Unsichere Defaults der neuen Schalter in der Bibliothek
`RejectComments`, `RejectDollarQuoting`, `RejectNonAscii…`, `RejectDotsInQuotedIdentifiers` und `RejectTimeTravelQueries` stehen standardmäßig auf false (`IRlsPolicyProvider.cs:235ff`, `FastSqlEngine.cs:56-68`). Im Gateway sind die meisten fest verdrahtet, SQ-11 und SQ-13 aber nicht. Andere Nutzer der Bibliothek sind standardmäßig ungeschützt.
**Fix:** sichere Defaults (true). Im Gateway zusätzlich `RejectDotsInQuotedIdentifiers` und `RejectTimeTravelQueries` setzen. `MaxAffectedRows = 0` als DANGER listen (SQ-15).

---

## 4. Umbau „Anbindungen nach gql_extensions“ – Regressionen

| Prüfpunkt | Ergebnis |
|---|---|
| ITSM-Webhook (HMAC pro Instanz, Zeitfenster, Replay-Cache, tenantgebundener Ticket-Lookup) | ✅ erhalten, nur Namespace/usings geändert |
| Jira/ServiceNow (Secret fail-closed, Auth, keine Retries) | ✅ erhalten |
| Katalog-Sync inkl. Ratchet | ✅ erhalten (Vergleich per `diff -w`) |
| Byte-Limits | ✅ verbessert (Purview, Collibra, Alation, Purview-Token: 10 MB, auch chunked) |
| OpenLineage, OpenJEV, Backstage, CDC | ✅ erhalten |
| SSRF-Handler je Client | ✅ vollständig (inkl. Purview-Token-Endpoint, OpenJEV named client, S3/Azure) |
| DI: genau eine Implementierung, Lifetimes, keine Captive Dependencies | ✅ |
| Hintergrunddienste nur bei aktivierter Option | ✅ 1:1 wie vorher |
| Endpoints (Auth, Body-Limits) | ✅ unverändert |

Kleinere Punkte: Der Registrierungstest prüft die Handler-Kette der named clients S3, Azure, AuditWorm und CDN nicht (Testlücke).

---

## 5. Neue Befunde – Egress-Allowlist und Extensions

### E-01 🟡 Allowlist ohne Validierung, unvollständige Sperren im Trusted-Pfad
`gql/src/Autheris.Application/Security/SsrfProtectionHandler.cs:40-46, 100-118`
- **Zu große Netze:** `0.0.0.0/0`, `::/0` und `::ffff:0:0/96` werden akzeptiert. Damit ist die Private-IP-Prüfung für alle Clients ausgeschaltet.
- **Lückenhafte Sperren:** Im Trusted-Pfad werden nur Loopback und Link-Local gesperrt. Nicht gesperrt sind `0.0.0.0`/`::` (unter Linux = localhost), 100.64/10 (Alibaba-IMDS 100.100.100.200), fc00::/7 (AWS-IMDS über IPv6 `fd00:ec2::254`), Multicast und Broadcast.
- **Stille Fehler:** Ungültige CIDRs werden ohne Warnung ignoriert (fail-closed). Die Allowlist erscheint nicht in der Bypass-Liste bzw. im Health-Check.

**Fix:**
- Netze beim Start validieren: IPv4 mindestens /8, IPv6 mindestens /32; Netze ablehnen, die 0/8, 127/8, 169.254/16, 100.64/10, fc00::/7 oder `::ffff:0:0/96` enthalten. Ungültige Einträge brechen den Start ab.
- Im Trusted-Pfad `IsRestrictedIp` anwenden und nur RFC 1918 innerhalb der vertrauten Netze ausnehmen. Sperrliste für Metadaten-IPs (100.100.100.200, fd00:ec2::254, 168.63.129.16).
- Eine aktive Allowlist als `WARN:` melden.

### E-02 🟡 Globale Allowlist öffnet Lakehouse-Manifest-URLs ins interne Netz
`SsrfProtectionHandler.cs:100` in Verbindung mit `S3LakehouseStorageProvider.cs:163-175, 213-218`
- Die Allowlist gilt für jeden Client, nicht pro Integration.
- Ohne konfigurierten `S3Endpoint` akzeptiert der S3-Provider jeden `*.amazonaws.com`-Host aus Iceberg-Manifesten, also aus **Daten der Producer**. Interne AWS-Namen (ELB, EC2) lösen in der VPC auf private IPs auf. Liegen diese in `TrustedInternalNetworks` (z. B. 10.0.0.0/8 wegen On-Prem-Jira), gehen SigV4-signierte Requests an interne Dienste.
- Vor der Allowlist hat die Private-IP-Prüfung das blockiert.

**Fix:** Allowlist pro Integration (z. B. `Egress:Itsm:…`, `Egress:Catalog:…`), Lakehouse ausnehmen. S3-Host per Regex auf `s3[.-]…amazonaws.com` beschränken (EX-12).

### E-03 🟡 Redirects und DNS-Rebinding umgehen die Allowlist
`SsrfProtectionHandler.cs:49-63, 84-101`
- **Redirects:** Für die Extension-Clients ist kein Primary Handler konfiguriert, es gilt also der Default `HttpClientHandler` mit `AllowAutoRedirect=true`. Redirects folgt der Primary Handler intern, der DelegatingHandler sieht nur die erste URL.
  - Ein vertrauter oder kompromittierter Host bzw. ein Open Redirect kann auf beliebige interne HTTPS-Ziele umleiten (z. B. `kubernetes.default.svc`).
  - Bei 307/308 wird ein POST-Body erneut gesendet (ITSM-Ticketinhalte).
  - Mildernd: kein Redirect von HTTPS auf HTTP, `Authorization` wird beim Redirect entfernt.
- **DNS-Rebinding:** Prüfung und Verbindung lösen den Namen getrennt auf. Anders als bei `DeclarativeHttp` (`ConnectCallback` mit IP-Pinning) wird die geprüfte IP nicht festgehalten. Ein vertrauter Hostname mit DNS-Fehler (leere Antwort) wird ohne Adressprüfung zugelassen.

**Fix:** Für alle Extension-Clients `ConfigurePrimaryHttpMessageHandler` mit `SocketsHttpHandler { AllowAutoRedirect = false }` und einem `ConnectCallback`, der Allowlist und Sperrliste auf die tatsächlich verbundene IP anwendet. Gemeinsame Factory-Methode in Application; DNS-Fehler bei vertrauten Hosts fail-closed.

### E-04 🟢 Alation-`TOKEN`-Header wird bei Redirects mitgesendet
`AlationCatalogClient.cs:74`. .NET entfernt beim Redirect nur `Authorization`, eigene Header gehen an jedes Redirect-Ziel mit.
**Fix:** wie E-03 (Redirects abschalten).

### E-05 🟡 EX-01 widerruft bestehende Auto-Consents nicht
`OpenMetadataSyncService.cs:299-307, 317-335, 360-364`
1. Wer `AutoCreateConsents` bisher als WARN in Production betrieben hat, muss die Option jetzt abschalten. Die bereits angelegten Allow-Consents bleiben aber in `desired` und werden **nicht widerrufen**. Sie gelten bis zu einem Jahr weiter.
2. `knownRoles` enthält nur noch die Werte der Map. Alt-Consents auf rohe OM-Rollennamen werden nicht mehr geladen und damit nie widerrufen.
3. Übersprungene Four-Eyes- bzw. Art.-9-Consents bleiben ebenfalls in `desired`. Bereits angelegte bleiben damit aktiv.

**Fix:** Alle aktiven Consents mit `OpenMetadataSyncConsentMarker` laden, ohne Filter auf Subjekt. Alles widerrufen, was nicht tatsächlich angelegt würde. Einmalige Migration: Marker-Consents mit Effect Allow widerrufen.

### E-06 🟢 Art.-9-Ausnahme im EX-01-Fix greift faktisch nie
`OpenMetadataSyncService.cs:328-331, 618-619`. Der OM-Pfad setzt `Sensitivity` nur auf `HIGH` oder `NORMAL`, die geprüften Werte „ART9“ und „ARTICLE_9“ kommen nie vor. `Catalog.GdprArticle9Tags` wird nicht ausgewertet, `RESTRICTED` ist nicht ausgenommen. Wegen DANGER derzeit nur in Development relevant.
**Fix:** Art.-9- und PII-Tags an Tabelle und Spalten auf `HIGH` plus `RequiresFourEyes` abbilden, wie im Kern-Katalog-Sync.

### E-07 🟢 Vermischte Schlüssel bei der SID-Zuordnung
`OpenMetadataSyncService.cs:132-145, 210-213`. Teamname, FQN und Id liegen in einer gemeinsamen Map ohne Groß-/Kleinschreibung. Ein Team, dessen Name der GUID eines gemappten Teams entspricht, erbt dessen SID. Bei E-Mails, die sich nur in der Schreibweise unterscheiden, gilt dasselbe (die Map ist ordinal, der Lookup nicht).
**Fix:** getrennte Maps je Schlüsselart; den Grantee direkt aus `TryResolve*Sid` übernehmen.

### E-08 🟢 Alation-Client: Token-Log, Fallback, Paging, Geister-Domains
`AlationCatalogClient.cs:66, 104-121, 161-165`
- Die Exception-Message des Secret-Providers enthält die Referenz bzw. den Rohwert, und sie wird geloggt (EX-16-Muster).
- Bei leeren Secret-Bytes oder einem Fehler in Development wird der Referenzstring als Token gesendet.
- Kein Paging, nach 250 Tabellen ist Schluss.
- Die Domain `ds_<id>` passt zu keiner Gateway-Tabelle, dadurch entstehen aktive Geister-Tabellen.

**Fix:** Exception ohne Message loggen; fail-closed bei leerem Secret; Paging; explizite Map `ds_id` → Gateway-Domain.

### E-09 🟢 Katalog-Provider OpenMetadata ignoriert ServiceFilter und legt aktive Tabellen an
`DataCatalogSyncService.cs:46`, `OpenMetadataCatalogAdapter.cs:32, 59-61`. Seit der Umstellung kommen alle Seiten aller OM-Services, auch außerhalb von `OpenMetadata.ServiceFilter`. Neue Tabellen werden mit `IsActive=true` und `NORMAL` angelegt. Die Datenbank fehlt in der Identität (wie EX-06).
**Fix:** ServiceFilter bzw. Domain-Map im Adapter anwenden; neue Tabellen inaktiv anlegen.

### E-10 ℹ️ Info
- **Sammel-Flag für Webhook-Bypass:** `IsWebhookSignatureBypassed` fasst die Bypass-Flags aller Systeme zusammen. Der dbt-Bypass schaltet damit in Development auch die Signaturprüfung für ITSM, OM und Katalog ab. Ein aktives `AutoCreateConsents` lockert in Development die Subgraph-SSRF-Prüfung (`HasAnyDangerBypassActive`). Fix: pro System eigene Flags auswerten.
- **IP-Literale:** Purview, Collibra und Alation blocken private IP-Literale schon per `ValidateUrl`, bevor der Handler greift. Die Allowlist wirkt dort nur für Hostnamen (inkonsistent, fail-closed).
- **Purview (übernommener Altcode):** Ohne Credentials wird auch in Production ein Mock-Token gesendet. Purview und Collibra liefern bei HTTP-Fehlern `[]` statt den Fehler zu propagieren.

---

## 6. Status EX-01 bis EX-18

| ID | Status | Bemerkung |
|---|---|---|
| EX-01 | 🟡 für Production behoben, inhaltlich teilweise | DANGER bricht den Start ab. S-1-Fallback und Name-Lookup sind entfernt, `RoleToGatewayRoleMap` ist da. Offen: E-05, E-06, E-07. |
| EX-02 | ❌ offen (durch E-05 verschärft) | Reconcile nur für Tabellen des laufenden Syncs; nach einer Exception trotzdem `Success=true` |
| EX-03 | ❌ offen | OM-Consents ohne `TenantId` |
| EX-04 | ❌ offen | Lakehouse-RLS auf maskierten Werten |
| EX-05 | ❌ offen | OData `$metadata` ohne Rechtefilter |
| EX-06 | ❌ offen | Datenbank fehlt in der OM-Identität; Webhook ohne ServiceFilter (siehe auch E-09) |
| EX-07 | ❌ offen | DisplayName, Spaltennamen und DataType aus OM ungeprüft (Prompt-Injection in MCP) |
| EX-08 | ❌ offen | dbt-`/sync` weiter für DataOwner ohne Ownership-Prüfung |
| EX-09 bis EX-11 | ❌ offen | Lakehouse Tenant-Bounds, `?`/`#` im Key, Präfix-Ableitung |
| EX-12 | 🟡 teilweise | Handler an allen Clients. Redirects, `ConnectCallback` und S3-Host-Regex fehlen (E-03). |
| EX-13 | ✅ behoben | `GatewayOptions.cs:81` (Nebenwirkung E-10) |
| EX-14 | ❌ offen | Katalog nutzt ersatzweise das OM-Secret, Dedup vor der Verarbeitung, voller Sync pro Event, EventGrid-Handshake |
| EX-15 | ✅ behoben | Duplikate gelöscht |
| EX-16 | ❌ offen, repliziert | OM-Client und jetzt auch Alation (E-08) |
| EX-17, EX-18 | ❌ offen | Partitions-Orakel; `MaxScanRowsLimit` ungenutzt |

---

## 7. Empfohlene Reihenfolge

1. **Sofort:**
   - P-01 (Methodenaufrufe sperren und testen)
   - P-03 (Lexer-Sperren unabhängig von der Tiefenprüfung)
   - E-01 (Allowlist validieren, Sperrliste im Trusted-Pfad)
   - E-05 (Auto-Consents widerrufen, Migration)
2. **Kurzfristig:**
   - E-03 (Redirects aus, `ConnectCallback` mit IP-Pinning für alle Extension-Clients)
   - E-02 (Allowlist pro Integration, S3-Host-Regex)
   - P-05 (Dialekt aus der Verbindung, `standard_conforming_strings`)
   - SQ-08 (Parsen mit eigenem Stack und Zeitbudget)
   - SQ-06/P-02 (Funktions-Allowlist pro Dialekt)
3. **Danach:**
   - EX-02 bis EX-08 (OM-Reconcile, Mandant, Lakehouse-RLS, OData `$metadata`, OM-Identität, Metadaten → MCP, dbt-Ownership)
   - SQ-11/13/15 aktivieren
   - E-06 bis E-09
