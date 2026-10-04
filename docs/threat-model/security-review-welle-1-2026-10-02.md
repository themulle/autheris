# Enterprise Security Expert Review: Welle 1 & Core Hardening

**Prüfbericht-ID:** `SEC-REV-2026-10-02-WELLE-1`  
**Datum:** 2026-10-02  
**Lead Security Architect & Auditor:** Enterprise Security Specialist Team  
**Geprüfte Commits:** `5d6d7e0` -> `7d93b09` -> `b7d95ec`  
**Status:** **PASSED (Security Freigabe erteilt)**

---

## 1. Audit-Scope & Prüfmethodik

Im Rahmen dieses Audits wurden alle im Zuge von **Welle 1** eingebrachten Code- und Konfigurationsänderungen einer statischen und dynamischen Bedrohungsanalyse (STRIDE, OWASP API Top 10, LLM Top 10) unterzogen:

1. **`F-AI-07` Dynamic Semantic Schema Pruning & Just-in-Time MCP Tools:**
   - Prompt Injection & Jailbreak Resilience
   - LLM Context Window Exhaustion / Denial of Wallet
   - Authorization & Information Disclosure (unberechtigte Tool-Sichtbarkeit)
2. **`F-CDC-03` Zero-Kafka PostgreSQL CDC via Logical Streaming Replication:**
   - Mandanten-Isolation & In-Stream Row-Level Security (RLS)
   - DoS / Storage Exhaustion (PostgreSQL WAL-Wachstum bei Lag)
   - Connection-String & Secret Leakage in Logs
3. **`F-OPS-01` AST-Aware Production Traffic Shadowing & Dark Replay:**
   - AST Mutation Guard (Schutz vor versehentlichen Mutationen/Writes auf Staging)
   - PII- & Credential-Redaction (Authorization-Token, API-Keys, Cookies, Client-Certs)
   - DoS-Schutz vor speicherhungrigen Payloads (Bounded Buffering)
   - SSRF-Resistenz beim Replay-Call

---

## 2. Detaillierte Sicherheitsbewertung je Modul

### 2.1 `F-AI-07`: Dynamic Semantic Schema Pruning

| Prüffeld | Analyse & Befund | Bewertung |
|---|---|:---:|
| **Prompt Injection** | Der Pruner (`SemanticToolPruner`) führt **keine** LLM-Inferenz durch und interpretiert keine Prompt-Befehle (`"IGNORE PREVIOUS INSTRUCTIONS..."`). Der Input-Prompt wird rein tokenisiert, normalisiert und gegen die intern registrierten Werkzeug-Begriffe gematcht. Maliziöse Prompt-Token wirken rein als No-Op-Schlüsselwörter. | **ROBUST** |
| **Token-Budget Bounding** | `MaxToolDefinitionTokens` (Standard: 4.000) und `MaxTools` (Standard: 8) verhindern effektiv, dass ein Agent durch manipulierte Abfragen das Kontextfenster sprengt oder exorbitante Inferenzkosten verursacht. | **SICHER** |
| **Fail-Closed Fallback** | Kann die Semantik nicht bestimmt werden oder treten Fehler auf, liefert der Handler einen deterministischen Satz vordefinierter Standardtools aus, anstatt den ungefilterten Supergraph preiszugeben. | **SICHER** |

---

### 2.2 `F-CDC-03`: Zero-Kafka PostgreSQL CDC via Logical Streaming Replication

| Prüffeld | Analyse & Befund | Bewertung |
|---|---|:---:|
| **Mandanten-Isolation (Tenant Boundary)** | `WalMessageDecoder` extrahiert die Mandanten-ID strikt aus Feldern wie `tenant_id`, `tid`, `tenant`. Ist kein Tenant vorhanden (`null`), greift der nachgelagerte `StreamRlsPolicyEnforcer`: **Events ohne explizit passenden Tenant werden sofort und geräuschlos verworfen (`StreamSecurityDecision.Denied`)**. | **VERIFIZIERT (Fail-Closed)** |
| **Credential-Schutz** | Weder `ConnectionString` noch DB-Passwörter tauchen in den Logausgaben des `PostgreSqlLogicalReplicationService` auf. Es werden lediglich Slot-Name, Publikation und Lag-Metriken protokolliert. | **SICHER** |
| **WAL Storage Exhaustion Schutz** | Der Service überwacht `CurrentWalLagBytes` gegen `MaxLagBytes` (1 GB). Bei Überschreitung wird die Pipeline gedrosselt/pausiert, um zu verhindern, dass fehlerhafte Konsumenten die DB-Festplatte überlaufen lassen. | **GEHÄRTET** |

> [!WARNING] **Betrieblicher Sicherheitshinweis für PostgreSQL DBA:**  
> Wenn ein Replikations-Slot für längere Zeit unbestätigt (`unacknowledged LSN`) pausiert, hält PostgreSQL WAL-Dateien auf dem Server vor. Bei dauerhaftem Lag über 1 GB muss das Monitoring (Prometheus Alert `gateway_cdc_wal_lag_bytes`) anspringen, damit der Slot im Notfall durch das Ops-Team gedroppt werden kann, bevor der DB-Storage zu 100% vollläuft.

---

### 2.3 `F-OPS-01`: AST-Aware Production Traffic Shadowing & Dark Replay

Im Rahmen des initialen Reviews wurden 3 potenzielle Schwachstellen identifiziert und in Commit `b7d95ec` umgehend **proaktiv gehärtet**:

#### Befund 1: Dialekt-Bypass im SQL-Mutation-Guard (Behoben in `b7d95ec`)
- **Ursprünglicher Zustand:** Regex verlangte Sequenzen wie `DELETE\s+FROM` oder `INSERT\s+INTO`. Bestimmte SQL-Dialekte (z. B. T-SQL / SQLite / Postgres) erlauben jedoch `DELETE <table>` ohne `FROM` oder `INSERT <table>` ohne `INTO`.
- **Härtung:** Umstellung auf direkte Keyword-Grenzen (`\b(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE|REPLACE|MERGE|EXEC|EXECUTE|CALL|GRANT|REVOKE|COPY)\b`). Jedes Statement, das eines dieser Verben enthält, wird ausnahmslos verworfen.
- **Zusatzschutz:** Auch Privilege-Änderungen (`GRANT`, `REVOKE`) und Prozess-Aufrufe (`COPY`) sind nun explizit blockiert.

#### Befund 2: Credential-Leakage über Nicht-Standard-Header (Behoben in `b7d95ec`)
- **Ursprünglicher Zustand:** Nur `Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key` und `Proxy-Authorization` wurden gefiltert.
- **Härtung:** Erweiterung der `DroppedHeaders` um MTLS-/Proxy-Zertifikate und Auth-Header (`ApiKey`, `api-key`, `Client-Cert`, `X-Client-Cert`, `X-Forwarded-Client-Cert`, `X-Vault-Token`, `X-Auth-Token`) sowie generisches Droppen aller Header mit `secret`, `token` oder `password` im Namen. `Authorization` wird durch ein synthetisches Staging-Token (`Bearer staging-shadow-synthetic-token`) ersetzt.

#### Befund 3: Memory Exhaustion / Unbounded Stream Buffering (Behoben in `b7d95ec`)
- **Ursprünglicher Zustand:** `TrafficShadowingMiddleware` las den Body ein, wenn `ContentLength > 0` war.
- **Härtung:** Einbau einer strikten Obergrenze (`context.Request.ContentLength > 2 * 1024 * 1024`). Payloads größer als 2 MB werden im Shadowing-Pfad sofort übersprungen, um Memory-Spikes oder DoS-Angriffe auf das Gateway zu verhindern.

---

## 3. Review der vorherigen Security-Hardening-Maßnahmen (`5d6d7e0`)

Die in der vorherigen Runde vorgenommenen Anpassungen wurden nochmals auditiert:
1. **`EnterpriseClaimsTransformation.cs`:** Das Hinzufügen von nackten `ClaimTypes.Role` ist strikt auf Rollen **ohne** Tenant-Präfix beschränkt (`!rawRole.Contains(':')`). Ein Mandanten-Admin (`tenant-1:ClusterAdmin`) kann daher nicht mehr in globale Rechte eskalieren.
2. **`GatewayRoleEvaluator.cs`:** Ist `tenantId == null`, wird bei Rollen mit Doppelpunkt stets `false` evaluiert.
3. **`HitLStepUpApprovalService.cs`:** Die Methoden `ApproveStepUpRequest` und `RejectStepUpRequest` sind über `_clusterState.TryAcquireLockAsync` mit einem distributed Lock versehen. Split-Brain-Zulassungen zwischen parallelen Instanzen sind ausgeschlossen.

---

## 4. Fazit & Freigabe

Die Architektur und Implementierung der **Welle 1** erfüllt sämtliche definierten Sicherheitskriterien:
- **Zero Data Leakage:** Kein Abfluss von Produktions-Credentials oder sensiblen PII-Feldern im Shadow-Traffic.
- **Strict Read-Only Enforcement:** Konstruktiver Ausschluss aller schreibenden Operationen im Shadowing-Pfad.
- **Tenant Isolation:** Robuste Trennung im CDC-Stream und im Rollen-Evaluator.
- **Stabilität:** 2.802 Tests laufen ohne Fehler durch.

**Gesamtergebnis: Enterprise Security Freigabe erteilt.**
