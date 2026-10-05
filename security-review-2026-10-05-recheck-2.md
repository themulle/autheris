# Nachprüfung 2 – Security Review (2026-10-05)

**Geprüfter Stand:** `a10a0a6`, verglichen mit `ca53775`. Dazwischen liegen drei Commits:
- `1bf80f5` fix(security) follow-up
- `171ba78` perf(sqlite): WAL, L1-Metadaten-Cache, Audit-Batching
- `dae958c` Benchmarks

**Methode:**
- Den vollständigen Diff aller Quell-, CI- und Deploy-Änderungen gelesen.
- Für alle offenen Findings geprüft, ob die betroffenen Dateien überhaupt geändert wurden.
- Kein Build und kein Testlauf.

## Ergebnis
**Nein, es sind noch nicht alle Lücken geschlossen.**
- `1bf80f5` behebt den Großteil der offenen Punkte aus der ersten Nachprüfung.
- Die 15 Medium/High-Findings aus dem Deep-Dive (E-1 … E-15) und dessen 30 Lows wurden **nicht angefasst**. Keine der betroffenen Dateien ist geändert.
- Der Performance-Commit `171ba78` bringt drei neue Medium-Findings.

| Bereich | Behoben | Teilweise | Offen | Neu |
|---|---|---|---|---|
| Erste Nachprüfung (M-1, M-2, M-3, M-4, M-9, M-10, N-1, N-2, H-2-Nebenwirkung) | 6 | 2 | 1 | – |
| Lows aus dem ersten Review (17) | 3 | 1 | 13 | – |
| Deep-Dive E-1 … E-15 | 0 | 0 | 15 | – |
| Deep-Dive Lows (30) | 0 | 0 | 30 | – |
| Neu durch `1bf80f5`/`171ba78` | – | – | – | 3 Medium, 3 Low |

---

## 1. Status der Punkte aus der ersten Nachprüfung

| ID | Status | Begründung |
|---|---|---|
| M-1 Admin-Aliase | ✅ | `TenantAdmin` impliziert nicht mehr `GovernanceAdmin` (`GatewayRole.cs:78-82`). |
| N-1 ITSM-Präfix-Bypass | ✅ | Präfix-Prüfung entfernt. Dafür gibt es eine neue Schwäche in der Identitätsauflösung, siehe R2-1. |
| N-2 Re-Aktivierung nach Widerruf | ✅ | Jede `consent_request_id` kann nur noch einmal aktiviert werden. |
| H-2 Nebenwirkung (WebSocket-JWT) | ✅ | Signing-Keys werden über den `ConfigurationManager` geladen. |
| M-4 DP-Budget | ✅ | Client-IDs werden serverseitig auf `tenant:clientId` gebunden, auch bei `perturb` und GET. Nebenbei: Der EU-AI-Act-Exporter liest weiter den ungebundenen Key. Das ist funktional falsch, aber kein Sicherheitsproblem. |
| M-10 NuGet | ✅ | Lock-Files und CI-Schritt sind ergänzt. Der Schritt `dotnet list package --vulnerable` scheitert allerdings nicht bei Funden. Er ist nur informativ, der Build bricht über NuGetAudit trotzdem ab. |
| **M-3 Selbstgenehmigung** | ❌ **wirkungslos** | Siehe Abschnitt unten. |
| M-2 ITSM-SoD | ⚠️ | Siehe Abschnitt unten. |
| M-9 Query-Kosten | ⚠️ | Unpaginierte Relationslisten werden nur pauschal mit ×2 gewichtet, nicht mit der tatsächlichen Zeilenzahl. |

**M-3 im Detail:**
- `ConsentRequest.RequesterIdentifiers` wird beim Antrag befüllt.
- Das Feld wird aber **nicht in SQLite gespeichert und nicht wieder geladen**. Weder `CREATE`/`INSERT` noch `SELECT` in `SqliteGovernanceRepository.Consent.cs` kennen eine solche Spalte.
- Beim Genehmigen kommt der Antrag aus der Datenbank, die Liste ist also leer.
- `MutationTypes.IsSameIdentity` und `IsSelfApproval` vergleichen damit faktisch wieder nur `RequesterSid`.

**M-2 im Detail:** Die ITSM-Selbstgenehmigung wird nur erkannt, wenn das ITSM-System die SID des Antragstellers meldet. Der Vergleich mit E-Mail oder UPN scheitert an demselben Persistenzproblem wie bei M-3.

## 2. Lows aus dem ersten Review

**Behoben:**
- Low-1: Federation nutzt jetzt `SecureOutboundHttp`.
- Low-9: Das Flight-SQL-Secret ist zufällig statt `"default-secret"`.
- Low-12: Incremental Delivery hat Slots pro Nutzer.

**Teilweise:** Low-14. `permissions:` ist ergänzt, die Actions sind aber weiter nur per Tag statt per SHA gepinnt.

**Offen:** alle übrigen, nämlich 2–8, 10, 11, 13 und 15–17. Ebenso die Restpunkte zu C-1 und H-3: Edge-Stripping von `x-autheris-*` fehlt, und `LoadTable` prüft keinen Consent.

## 3. Deep-Dive: alles offen
Keine der betroffenen Dateien wurde geändert:
- `ChunkPiiRedactor.cs`
- `RebacEndpoints.cs`
- `ColumnMaskingProvider.cs`
- `SqlDataSourceExecutor.cs`
- `StreamingRowFilterAstEvaluator.cs`
- `DbtEndpoints.cs`
- `TokenRevocationEndpoints.cs`
- `GatewayHealthCheckService.cs`
- `RateLimitingMiddleware.cs`
- `FocusCostAccountingService.cs`
- die Lakehouse-Executors
- die Startup-Validierung in `GatewayServiceCollectionExtensions.cs`

Damit sind weiterhin offen:
- **High:** E-1 (RAG ignoriert Masking) und E-2 (SQLite pro Replika).
- **Medium:** E-3 bis E-15.
- **Low:** alle 30.

E-9 bleibt ebenfalls bestehen: GraphQL kann `PENDING_EXTERNAL_APPROVAL` weiterhin genehmigen. Die Statusprüfung im nicht-ITSM-Pfad ist unverändert.

---

## 4. Neue Findings

### R2-1 (Medium): Genehmiger-Identität wird über den Text nach dem letzten `:` aufgelöst
**Ort:** `SqliteGovernanceRepository.Catalog.cs:706-737` (`IsAuthorizedApproverForTableInternalAsync`)

```csharp
var candidateAccount = colonIdx >= 0 ? sidVal[(colonIdx + 1)..] : sidVal;
... AND (o.ad_sid = @apprSid OR o.ad_account = @candidateAccount OR o.email = @candidateAccount)
... OR rm.member_sid = @candidateAccount   -- GovernanceAdmin/ClusterAdmin
```

**Problem:**
- Die Methode wird für **alle** Genehmigungen genutzt, auch für die GraphQL-Mutation mit der SID des eingeloggten Nutzers.
- Jede Identität, deren SID ein `:` enthält, gilt als der Account oder die E-Mail hinter dem letzten Doppelpunkt.
- Sie kann damit als Data Owner, als Delegate oder als `GovernanceAdmin`-Mitglied durchgehen.

**Voraussetzung (Bestätigung nötig):** Ein Authentifizierungsweg liefert eine SID mit `:` und einem vom Nutzer beeinflussbaren Teil dahinter, zum Beispiel:
- ForwardAuth-Benutzernamen mit `:`,
- Basic-Auth-SIDs aus der Konfiguration,
- `sub`/`NameIdentifier` bei IdPs mit frei wählbaren Kennungen.

Bei ForwardAuth schützt aktuell nur zufällig, dass der Name in Großbuchstaben umgewandelt wird. SQLite vergleicht ohne `COLLATE NOCASE` binär.

**Fix:**
- Den ITSM-Approver als **separaten, typisierten Parameter** übergeben.
- Für den GraphQL-Pfad nur `ad_sid = @apprSid` zulassen.
- Keine String-Zerlegung von Identitäten.

### R2-2 (Medium): Der L1-Metadaten-Cache kann veraltete Masking- und Sensitivitätsregeln dauerhaft festhalten
**Ort:** `SqliteGovernanceRepository.Catalog.cs:41-163, 283, 538, 573, 615`

**Problem 1 – Race Condition:**
- `UpsertTableMetadataAsync` löscht den Cache-Eintrag **vor** dem Warten auf `_lock`.
- Ein Leser, der den Lock vorher bekommt, liest noch die alten Daten aus der DB und schreibt sie zurück in den Cache.
- Danach committet das Upsert, und es kommt keine weitere Invalidierung.
- Es gibt **keine TTL**. Eine verschärfte Masking-Regel oder ein neues `IsSensitive` greift deshalb bis zum nächsten Neustart oder Epoch-Bump nicht.

**Problem 2 – nur lokal:** Der Cache wird nur prozesslokal invalidiert, nicht über die Redis-Epoch. Bei mehreren Replikas verschärft das E-2.

**Fix:**
- Nach dem Commit **unter dem Lock** invalidieren.
- Einträge mit der Tabellen-Epoch versionieren und bei jedem Zugriff gegen `IEpochValidationService` prüfen.
- Eine kurze TTL als Fallback setzen.

### R2-3 (Medium): Das Audit für Datenzugriffe ist jetzt asynchron und kann still verloren gehen
**Ort:** `SqliteGovernanceRepository.Audit.cs:17-140`, `SqliteGovernanceRepository.cs:53-60`

**Problem:**
- Die Events `TABLE_QUERY`/`WEBSQL_QUERY` mit `ALLOW` gehen in einen Channel mit 100.000 Plätzen. Die Daten sind beim Client, bevor der Audit-Eintrag gespeichert ist.
- Schlägt das Batch-Schreiben fehl, wird der Fehler **nur geloggt und der Batch verworfen** (`catch … LogError`, kein Retry).
- Bei Crash, OOM-Kill oder `kill -9` gehen alle Einträge in der Queue verloren.
- Vorher war das Audit synchron und fail-closed.

**Folge:** Für Nachweise nach DSGVO („wer hat welche Daten gesehen") ist das Protokoll nicht mehr vollständig, und eine Lücke ist nicht erkennbar. Die Sequenznummern bleiben lückenlos, weil verlorene Events nie eine bekommen.

**Fix:**
- Bei einem Batch-Fehler erneut versuchen, sonst Readiness auf fehlerhaft setzen und neue Tier-B-Events ablehnen.
- Eine kleine Queue mit Backpressure verwenden.
- Optional die Events vor der Auslieferung in ein lokales Append-Only-Log schreiben.
- Den Kompromiss in der Doku festhalten.

### R2-4 (Low): `PRAGMA synchronous = NORMAL` im WAL-Modus
**Ort:** `SqliteGovernanceRepository.Schema.cs:18-33`

**Problem:** Bei Stromausfall oder OS-Crash können die letzten Commits verloren gehen, darunter **Widerrufe** und Tier-A-Audit-Einträge. Ein widerrufener Consent ist danach wieder aktiv. Außerdem werden Fehler beim Setzen der PRAGMAs verschluckt.

**Fix:** `synchronous = FULL` für die Governance-DB verwenden, oder gezielt bei Widerruf und Freigabe. PRAGMA-Fehler außerhalb von Development nicht ignorieren.

### R2-5 (Low): ITSM-Freigabe ohne benannten Genehmiger überspringt die Owner-Prüfung
**Ort:** `SqliteGovernanceRepository.Consent.cs:946-978`

**Problem:** Die Prüfung entfällt komplett (fail-open), wenn mindestens eine der beiden Bedingungen zutrifft:
- Das ITSM-System meldet keinen `Approver` (weniger als zwei `:` im Actor).
- Für die Tabelle sind keine Owner konfiguriert.

**Fix:** Ohne auflösbaren Genehmiger ablehnen, oder dies explizit pro ITSM-Instanz konfigurierbar machen.

### R2-6 (Low): Persistenz von `RequesterIdentifiers` fehlt
Das ist die Ursache für das Scheitern von M-3 und M-2.

**Fix:** Spalte `requester_identifiers_json` in `CONSENT_REQUESTS` anlegen, beim Insert schreiben und beim Laden lesen.

---

## 5. Empfohlene Reihenfolge
1. **E-1** (RAG/Masking) und **Start-Guard für E-2**. Beide sind High und weiterhin offen.
2. **R2-6/M-3** (Persistenz) und **R2-1** (Identitätsauflösung). Beide sind klein und betreffen die Funktionstrennung.
3. **R2-2** (Cache-Race) und **R2-3** (Audit-Verlust). Beide sind durch den Performance-Commit entstanden.
4. **E-3, E-4, E-8, E-9:** kleine Fixes.
5. Danach die übrigen Medium-Findings aus dem Deep-Dive und die Lows.
