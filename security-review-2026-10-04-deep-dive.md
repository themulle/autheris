# Security Deep-Dive — weitere Findings (2026-10-04)

**Stand:** Commit `ca53775`, zusätzlich zu `security-review-2026-10-04.md` und `…-recheck.md`. Bereits dort gelistete Punkte sind hier nicht wiederholt.

**Schwerpunkt:** Bereiche, die im ersten Review nur oberflächlich geprüft wurden:
- Query-Engine und Datenpfade (Vector/RAG, Lakehouse, Streaming, Masking)
- GraphQL-Resolver, Cache, Audit-Trail, HitL, ReBAC, FinOps
- restliche Endpoints und Middleware, Startup-Guards, Kryptografie

**Methode:** Statisches Code-Review. Die wichtigsten Punkte (E-1, E-2, E-3, E-4, E-7) habe ich selbst am Code nachgeprüft. Es lief kein Build und kein Test.

| Schwere | Anzahl |
|---|---|
| High | 2 |
| Medium | 13 |
| Low | 30 |

---

## High

### E-1: Vector-/RAG-Suche ignoriert Spalten-Consent und Masking
**Ort:**
- `src/Autheris.Application/Security/ChunkPiiRedactor.cs:23-70`
- `src/Autheris.Application/Kernel/GovernedExecutionKernel.cs:322, 419`

**Problem:**
- `RedactChunk(chunk, metadata, maskingProvider)` nimmt `metadata` und `maskingProvider` entgegen, benutzt sie aber nicht.
- Es gibt nur zwei Schutzmechanismen:
  - eine Key-Denylist (`ssn`, `secret`, `password`) auf den Metadaten,
  - Regexe für E-Mail, Kreditkarte und API-Keys im Text.
- Deny- und Mask-Spalten, `IsSensitive` und `ColumnMaskingRules` greifen nicht.

**Wer betroffen ist:** Jeder MCP-Nutzer mit Tabellen-Consent auf eine Vector-Collection. Der Weg ist das Tool `search_rag_context`, auch über den Semantic Cache.

**Fix:**
- Metadaten-Keys und `content_text` als Katalogspalten behandeln.
- Keys ohne Katalogeintrag verwerfen.
- `decision.GetEffectiveColumnAccess` und `MaskValue` anwenden, mit tenant-scoped Regeln wie im Tabellenpfad.
- Die Regex-Redaktion nur noch als zusätzliche Schicht verwenden.

### E-2: Mehrere Replikas teilen sich keine Governance-Datenbank
**Ort:**
- `GatewayServiceCollectionExtensions.cs:131-133`: Es ist nur `Provider == "Sqlite"` erlaubt.
- `appsettings.json:29`: `Data Source=governance.db`.
- `docs/configuration-guide.md:205, 740`: Die Doku beschreibt `SqlServer` und `replicas: 3`.

**Problem:** Der Multi-Node-Modus verlangt nur Redis. Die Datenbank ist trotzdem eine lokale SQLite-Datei pro Pod. Folgen:
- Freigaben, Widerrufe, Delegationen und die Audit-Kette landen nur auf dem Pod, der die Anfrage bearbeitet hat.
- Ein Widerruf auf Pod A erhöht zwar die Redis-Epoch. Pod B lädt danach aber den noch aktiven Consent aus seiner eigenen DB. Der Zugriff bleibt dort bis `ValidTo` bestehen.
- Vier-Augen-Freigaben können sich über Pods verteilen.
- Auf einem geteilten Netzlaufwerk gilt: SQLite-Locking über NFS/SMB, `SemaphoreSlim` im Prozess und `_lastAuditHash` im Speicher sind nicht sicher.

**Voraussetzung:** Mehr als eine Replika. Bitte bestätigen, wie ihr tatsächlich deployt.

**Fix:**
- Den Start verweigern, wenn `MultiNodeClusterMode` aktiv oder `replicas > 1` ist und der Provider SQLite ist.
- Einen gemeinsamen RDBMS-Provider implementieren.
- Bis dahin nur eine schreibende Replika betreiben.

---

## Medium

### E-3: ReBAC `batch-check` prüft fremde Tenants, und der Cache ist unbegrenzt
**Ort:** `RebacEndpoints.cs:163-190`, `ZanzibarRebacEvaluator.cs:147-160`

**Problem:**
- Der Endpoint vergleicht nur das äußere `batchCheck.TenantId`. Jedes Element trägt aber ein eigenes `TenantId`, und genau dieses wird ausgewertet.
- Nicht-Admins können damit für ihre eigene SID in fremden Tenants prüfen.
- Tenant-Admins (`GovernanceAdmin`/`SecurityAdmin`) können beliebige Nutzer und Objekte in jedem Tenant prüfen.
- Jede beliebige Kombination erzeugt einen Eintrag in `_cache`. Das ist ein Speicher-DoS.

**Fix:** Das `TenantId` der Elemente serverseitig mit dem validierten Tenant überschreiben. Den Cache als LRU begrenzen.

### E-4: `REGEX`-Masking gibt Klartext zurück, wenn das Muster nicht passt
**Ort:** `ColumnMaskingProvider.cs:208-213`

**Problem:** `Regex.Replace` liefert die Eingabe unverändert zurück, wenn es keinen Treffer gibt. Andere Formate kommen deshalb ungemaskt durch, zum Beispiel:
- Leerzeichen oder andere Trennzeichen,
- Kleinschreibung.

Das betrifft alle Pfade, die den Provider nutzen.

**Fix:** Wenn `!Regex.IsMatch(...)`, komplett redigieren. Verankerte Muster (`^…$`) erzwingen.

### E-5: Der Tenant-Filter greift nur bei einer Spalte namens `tenant_id`
**Ort:** `SqlDataSourceExecutor.cs:190-199`, `GovernedSqlExecutionService.cs:408` (`HasColumn("tenant_id")`)

**Problem:**
- Andere Pfade erwarten andere Spaltennamen:
  - CDC: `TenantId`
  - Lakehouse: `tenantId`
- Eine MSSQL-Tabelle mit `TenantId` bekommt deshalb kein Tenant-Prädikat.
- Session-RLS gibt es nur für PostgreSQL.
- Die Isolation hängt dann allein am Consent-Row-Filter. Bei Gruppen-Consents ohne Filter gibt es keine Isolation.

**Fix:** Ein explizites `TenantColumn` in den Tabellen-Metadaten führen. Für Multi-Tenant-Tabellen ist es Pflicht, sonst fail-closed. Alle Pfade nutzen dieselbe Definition.

### E-6: RLS im Speicher filtert lockerer als SQL
**Ort:**
- `GatewayExecutionService.FilterRows` (`:456-525`, `DataTable.Select`, `CaseSensitive=false`): betrifft REST-, Plugin-, Lakehouse- und synthetische Quellen.
- `StreamingRowFilterAstEvaluator` (`:279, 313, 363, 557, 575`): betrifft CDC und den Vector-Post-Filter.

**Abweichungen von SQL:**
- Vergleiche ignorieren Groß- und Kleinschreibung.
- Strings werden in Zahlen umgewandelt: `'007' = '7'`.
- `*` und `[` wirken bei `DataTable` als `LIKE`-Wildcard.
- Eine fehlende Spalte zählt als NULL, und `IS NULL` greift dann. CT-Delete-Events enthalten nur den Primärschlüssel.

**Fix:**
- Einen einzigen Evaluator verwenden, mit Ordinal-Vergleich.
- Typen nur anhand des Katalogs umwandeln.
- Bei fehlender Spalte deny.
- `DataTable.Select` entfernen.

### E-7: dbt-Endpoints – jeder `DataOwner` wirkt auf alle Tabellen
**Ort:**
- `DbtEndpoints.cs:20-49` (sync), `:170-197` (run-results), `:224-245` (health/reset)
- `DbtHealthCircuitBreaker.cs:123-186`

**Problem:** Die Endpoints prüfen nur die Rolle, nicht das Ownership. Dadurch kann ein `DataOwner`:
- mit einem präparierten `run_results.json` beliebige Tabellen in Quarantäne setzen (DoS für alle Tenants),
- jede Quarantäne aufheben,
- Beschreibungen beliebiger Tabellen überschreiben. Diese Texte landen in MCP-Tool-Beschreibungen, also ein indirekter Prompt-Injection-Pfad zu KI-Agenten.

**Fix:** Die Endpoints einer Service-Identität oder globalen Admins vorbehalten. Alternativ Ownership pro betroffener Tabelle prüfen.

### E-8: Token-Widerruf ist nicht an einen Tenant gebunden
**Ort:** `TokenRevocationEndpoints.cs:29-63`

**Problem:**
- Nur die Policy `GovernanceAdmin` wird geprüft. Damit lässt sich jedes `subjectOrJti` bis zu 30 Tage sperren.
- Zusammen mit M-1 (noch offen) heißt das: Tenant-Admins können ClusterAdmins und Nutzer fremder Tenants aussperren.

**Fix:**
- Nicht-kanonische Admins dürfen nur Subjects des eigenen Tenants sperren.
- Einen ClusterAdmin darf nur ein ClusterAdmin sperren.

### E-9: GraphQL kann Anträge freigeben, die auf ITSM warten (Regression aus `ca53775`)
**Ort:**
- `SqliteGovernanceRepository.Consent.cs:910-915, 932-937`: Der Status `PENDING_EXTERNAL_APPROVAL` ist neu freigebbar.
- `MutationTypes.cs:330-405`

**Problem:**
- Bei aktivem ITSM kann ein Data Owner oder Delegate per `approveConsentRequest` das Change-Board umgehen.
- Eine spätere ITSM-Ablehnung bleibt dann wirkungslos.
- Vier-Augen lässt sich aus einer ITSM- und einer GraphQL-Freigabe zusammensetzen.

**Fix:** `PENDING_EXTERNAL_APPROVAL` darf nur über den verifizierten ITSM-Webhook weiterlaufen.

### E-10: Wichtige Entscheidungen fehlen in der Audit-Kette oder haben falschen Actor oder Tenant
**Ort:** `SqliteGovernanceRepository.Consent.cs:905-1022, 1181-1186, 1335-1347, 1419-1456`

**Was nicht in der Hash-Kette landet:**
- Freigabeschritte und Ablehnungen; sie stehen nur in der veränderbaren Tabelle `APPROVAL_STEPS`
- HitL-Entscheidungen
- Änderungen an ReBAC-Tupeln
- Verlängerungen der Consent-Laufzeit
- Schema-Reloads und Katalog-Syncs
- Fehlgeschlagene Logins

**Was falsch erfasst wird:**
- `CONSENT_GRANTED` speichert als Actor den Grantee statt des Approvers.
- `CONSENT_REVOKED` speichert keinen `TenantId`. Tenant-gefilterte DSGVO-Berichte übersehen den Eintrag.

**Fix:** Die fehlenden Events in derselben Transaktion schreiben. Den Approver als Actor eintragen und immer den Tenant setzen.

### E-11: Die Audit-Manipulationserkennung wird nie ausgeführt
**Ort:** `VerifyAuditHashChainAsync` und `IAuditWormExportService` haben keinen Aufrufer zur Laufzeit. Relevant sind außerdem `SqliteGovernanceRepository.Audit.cs:615-636` und `AuditWormExportService.cs` (~290).

**Problem:**
- Der Anker liegt per Default neben der DB. Wer Zugriff aufs Volume hat, kann nach einem Neustart ein älteres, konsistentes Paar aus DB und Anker zurückspielen.
- Die HMAC-Schlüssel liegen in der App. Ist die App kompromittiert, lässt sich die Kette neu schreiben.
- Der lokale „WORM"-Export setzt nur das ReadOnly-Attribut.

**Fix:**
- Prüfung und Export regelmäßig planen. Bei Verletzung alarmieren und die Readiness auf fehlerhaft setzen.
- Den Anker auf separatem WORM-Speicher ablegen (S3 Object Lock) und monoton halten.
- Mit einem asymmetrischen KMS/HSM-Schlüssel signieren.

### E-12: Anonymer Readiness-Probe nutzt die SQLite-Verbindung ohne Lock (Bestätigung nötig)
**Ort:** `GatewayHealthCheckService.cs:43-48`, `HealthEndpoints.cs:50-104` (`AllowAnonymous`)

**Problem:**
- `SqliteConnection` ist nicht thread-safe.
- Läuft gerade eine Transaktion, schlägt der Probe fehl. Die Readiness flattert, und Pods fallen aus dem Load-Balancer.
- Das lässt sich anonym auslösen.

**Fix:** Eine `PingAsync()`-Methode hinter `_lock` oder eine separate Read-only-Verbindung verwenden.

### E-13: Anonymer CPU-DoS und ungedrosseltes Passwort-Spraying über Probe-Pfade
**Ort:** `RateLimitingMiddleware.cs:38-44`, `BasicAuthenticationHandler.cs:97-121`

**Problem:**
- `/health` und `/metrics` sind vom IP-Limiter ausgenommen.
- Die Authentifizierung läuft trotzdem auf jeder Anfrage. Jeder `Basic`-Header kostet also PBKDF2 mit 210k Iterationen, etwa 0,1 s CPU.
- Die Sperre greift pro (User, IP). Mit wechselnden Benutzernamen umgeht man sie.
- `/metrics` antwortet mit 200 oder 401. Das ist ein Orakel für gültige Passwörter.

**Voraussetzung:** `BasicAuth.Enabled=true` (nicht der Default).

**Fix:**
- Den IP-Limiter vor der Authentifizierung auf allen Pfaden einsetzen, oder die Probes ohne Authentifizierung laufen lassen.
- Eine globale Grenze für fehlgeschlagene Versuche pro IP einführen.

### E-14: FinOps-Budget gilt pro Replika, wird nie zurückgesetzt, und ein Nutzer blockiert den ganzen Tenant
**Ort:** `FocusCostAccountingService.cs:26, 65, 115-130`, `FinOpsBudgetMiddleware.cs:45-104`

**Problem:**
- Das Limit ist effektiv N × Budget und wird bei jedem Neustart zurückgesetzt.
- Ist das Budget erreicht, bekommt der ganze Tenant 429.
- Offene SSE- oder WebSocket-Verbindungen werden als Rechenzeit abgerechnet. Damit treibt ein einzelner Nutzer den Verbrauch schnell hoch.

**Fix:**
- Den Zähler in Redis führen, pro Tenant und Monat.
- Sub-Budgets pro Principal einführen.
- Langlebige Verbindungen nicht abrechnen oder pro Request deckeln.

### E-15: Lakehouse-Isolation nur auf Dateiebene, und Zeilen werden umetikettiert (latent)
**Ort:**
- `LakehouseDataSourceExecutor.cs:244-247, 282-284`
- `DeltaLakeDataSourceExecutor.cs:83-108`

**Problem:**
- Beide Executors setzen `row[TenantColumn] = request.TenantId`.
- Iceberg akzeptiert Min/Max-Grenzen als „Beweis" für den Tenant. Eine Datei mit mehreren Tenants wird deshalb komplett gelesen.
- Delta hat gar kein Tenant-Prädikat.
- Heute werden die Zeilen noch synthetisch erzeugt. Mit einem echten Parquet-Reader kämen fremde Zeilen zurück, beschriftet mit dem Tenant des Aufrufers.

**Fix:**
- Die Tenant-Spalte nie überschreiben.
- Nach dem Scan pro Zeile filtern, fail-closed.
- Das Tenant-Prädikat in Delta einbauen.

---

## Low / Härtung

**Masking und Datenpfade**

1. **HMAC-Pseudonyme nicht tenant-scoped.** Betrifft Iceberg/Delta, `StreamRlsPolicyEnforcer:262-265`, `ConnectorRowMasker` und den Kernel-Post-Pass. Dadurch lassen sich Werte über Tenants hinweg korrelieren.
2. **Falsche Zugriffsfunktion.** `LoadInvoiceItemsBatchAsync` und `PushdownPlanner` nutzen `GetColumnAccess` statt `GetEffectiveColumnAccess`. Der Invoice-Loader wendet RLS außerdem erst nach dem Masking an.
3. **Streaming löst Tenant und IP anders auf als der Query-Pfad.**
   - Die Reihenfolge der Tenant-Claims unterscheidet sich (`StreamRlsPolicyEnforcer:77-80` gegenüber `Sid.GetTenantId`).
   - Der `ip`-Claim wird dort weiter vertraut (`:343-346`). Der H-4-Fix hat das an dieser Stelle nicht erfasst.
4. **Synthetische Daten in Produktion möglich.** `new SqlDataSourceExecutor()` hat kein Environment, und `null` wird als Dev behandelt (`SqlDataSourceExecutor.cs:96`, `GatewayExecutionService.cs:39`).
5. **Teilmasken zeigen zu viel.** `MaskPhone`, `MASK_LAST_FOUR` und `MASK_IBAN` lassen bei kurzen Werten bis zu 6 von 9 Zeichen sichtbar.
6. **Latente Escaping-Lücken bei Vector-Pushdown.**
   - `BuildMilvusFilter` escaped `\` nicht.
   - `BuildPgVectorQuery` bettet das RLS-SQL ungeprüft ein.
7. **`SingleQueryAstCompiler` und `CompiledSqlQueryPlanCache` noch nicht verdrahtet.** Vor dem Aktivieren beheben:
   - Projektionen ohne Deny/Mask.
   - Der Cache-Key enthält die Spaltenentscheidung nicht.
8. **MSSQL-CT-Poller nutzt immer die erste SQL-Server-Verbindung.** In Setups mit mehreren Servern landen Events bei der falschen Tabelle.
9. **ReBAC-Cache ohne Epoch-CAS.** Eine Entscheidung von vor einem Widerruf kann nachträglich in den Cache geschrieben werden.
10. **Masking-Ersatztext nur mit `''` escaped** (`SqlDataSourceExecutor.cs:391`). Databricks interpretiert zusätzlich Backslashes.

**Governance und Workflow**

11. **Delegationen.**
    - Sie überdauern die Deaktivierung des Owners.
    - Es gibt keine Prüfung von Datumsfolge oder Maximaldauer.
    - Selbst- und Weiterdelegation werden nicht verhindert, und es wird nichts auditiert.
    - Derzeit nutzt keine API diesen Pfad.
12. **Approve, Reject und Consent-Erzeugung nicht atomar.** Bei geteilter DB kann `REJECTED` mit aktivem Consent entstehen.
13. **Revoke überschreibt einen bestehenden Widerruf.** Es fehlt `AND is_revoked = 0`.
14. **Existenz-Orakel.** Bei fremden IDs kommt `NOT_FOUND`, bei fremdem Tenant `FORBIDDEN`.
15. **Lineage nutzt die literale Rolle `ClusterAdmin` statt `IsCanonicalClusterAdmin`** (`QueryTypes.cs:387`, `LineageImpactAnalyzerService.cs:64`).
16. **HitL.**
    - Kein Status-CAS im Cluster.
    - Wenn das Lock fehlschlägt, läuft es ohne Lock weiter.
    - Das Rejection-Reason ist unbegrenzt.
    - Bei App-only-MCP-Sessions ist der Requester die ServicePrincipal-ID. Der Owner des Agenten kann dann womöglich selbst genehmigen (Bestätigung nötig).
17. **Canary-Routing.** `X-Feature-Variant` wird vor Rolle und Tenant geprüft. Das ist latent, weil keine Regeln aktiv sind.
18. **ReBAC-Subject in GraphQL anders als in REST** (`NameIdentifier`/`sub`/`Name` statt `UserSid`). Außerdem ist `TenantResolutionMiddleware` toter Code, der Tenant-Wechsel per Header erlaubt. Den Code löschen.
19. **Tenant-scoped `GovernanceAdmin` gilt als global.** In `SemanticMcpCompiler.cs:283-301` sehen solche Admins MCP-Resources aller Tabellen. Deny-Consents werden dort ignoriert.
20. **DP-`perturb` verbraucht das Budget beliebiger `clientId`s** (`GovernanceEndpoints.cs:253-275`). Gleiche Ursache wie M-4.

**Konfiguration und Härtung**

21. **Die Umgebungsnamen „Testing"/„Test" schwächen still die Sicherheit.** Betroffen sind:
    - der hartkodierte Audit-HMAC-Key `AutherisAuditLog…` (`SqliteGovernanceRepository.cs:103-127`),
    - Redis ohne Passwort,
    - Garnet auf dem Netzwerk,
    - synthetische Daten.

    Fix: Nur `IsDevelopment()` gilt als unsicher, und es gibt keinen Fallback-Key.
22. **Schema-Contracts.** Der Client wählt sie per Header oder Query (`SchemaContractMiddleware.cs:41-62`), und sie werden nirgends durchgesetzt.
23. **`GraphQL.EnableIntrospection=true` ist in Produktion ohne Guard oder Warnung möglich** (`GatewayServiceCollectionExtensions.cs:830`).
24. **`Accept: text/event-stream` entgeht den Resource-Group-Limits.** Der MCP-Pfad ist außerdem als `/mcp` hartkodiert (`ResourceGroupMiddleware.cs:183-190`).
25. **Mutationen von Egress-Interceptoren werden verworfen** (`ExtensibilityPipeline.cs:41-70`). Ein künftiger Redaction-Plugin wäre damit fail-open.
26. **`GET /api/v1/queries/` liefert `RawSql`, `DataSource` und `ReferencedTables` aller Endpoints an jeden authentifizierten Nutzer.**
27. **`/metrics` ist für jeden authentifizierten Nutzer lesbar.**
28. **`warn_allow_all_cors_origins` schaltet den CSRF-Origin-Check komplett ab,** ist aber nur als WARN eingestuft und damit in Produktion erlaubt. Als DANGER einstufen.
29. **`RequireHttpsMetadata=false` für EntraId/ADFS wird außerhalb von Development nicht blockiert.** Die OIDC-Keys könnten dann per MITM ausgetauscht werden.
30. **Swagger UI wird von unpkg.com ohne SRI geladen,** und die CSP erlaubt die ganze Domain (`ODataEndpoints.cs:211-413`).

---

## Was geprüft wurde und in Ordnung ist
- **SQL-Ausführung:**
  - Alle Werte im SQL-Pfad sind gebunden.
  - Filter auf Nicht-Clear-Spalten werden abgelehnt.
  - Es gibt kein GraphQL-ORDER-BY oder -GROUP-BY. Im WebSQL-Pfad laufen Sortierung und Gruppierung auf bereits gemaskten Werten.
- **`SqlSecurityValidator` und `SingleQueryAstCompiler`:** Identifier werden validiert und gequotet, RLS ist auf jeder Ebene Pflicht.
- **Streaming-Evaluator:**
  - Dreiwertige Logik ist korrekt.
  - Unbekannte Operatoren werden beim Kompilieren abgelehnt.
  - Laufzeitfehler führen zu deny.
- **Consent-Cache:**
  - Keys enthalten Tenant, SID, Gruppen-Hash und Tabelle.
  - Epoch-CAS, und die L2-Einträge sind per HMAC gebunden.
- **Vier-Augen:** Der Unique-Index `UX_APPROVAL_STEPS_REQ_STEP` verhindert parallele Freigaben im selben Schritt.
- **Audit-Hashes:** HMAC über escapte Felder, lückenlose Sequenznummern, JSON-Details. Log-Injection ist dort nicht möglich.
- **Anonyme Endpoints:** DevPortal liefert außerhalb von Development 404 (Nonce-CSP, kein reflektiertes Input). Health zeigt in Produktion nur den Status.
- **TrustedDocumentsOnly:** greift in der HotChocolate-Pipeline, also auch für GET, Batching und WebSocket.
- **OData `$metadata`:** pro Tabelle nach Consent gefiltert.
- **Middleware-Reihenfolge:**
  - `UseForwardedHeaders` läuft vor aller IP-Logik.
  - Authentifizierung und Autorisierung laufen vor Tenant, FinOps und Resource Groups.
  - Kein Swallow-and-continue.
- **Pfadprüfungen:** nutzen `StartsWithSegments`. `%2F` wird nicht dekodiert.
- **Kryptografie:**
  - `FixedTimeEquals` bei allen Secret-Vergleichen.
  - Kein MD5, SHA1, ECB oder unauthentifiziertes CBC.
  - Kein nicht-kryptografischer Zufall für Tokens.
- **Login:** gleiche Fehlermeldung und gleiche Laufzeit bei unbekanntem Nutzer und falschem Passwort.

## Empfohlene Reihenfolge
1. **E-1 (RAG-Masking) und E-2 (Replika/SQLite):** Mindestens den Start-Guard für E-2 einbauen.
2. **Kleine Fixes mit großer Wirkung:** E-3, E-4, E-8 und E-9.
3. **Zusammen mit M-1 und N-1 aus dem Recheck:** M-1 vergrößert E-8 und Low-19.
4. **Rest der Mediums:** E-5 und E-6 (einheitliche Tenant- und RLS-Semantik), E-10 und E-11 (Audit), E-7.
5. **Lows:** zuerst 21 (Test-Umgebungsnamen), 28 (CORS) und 29 (RequireHttpsMetadata).
