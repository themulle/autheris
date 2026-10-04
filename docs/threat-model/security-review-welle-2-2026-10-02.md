# Enterprise Security Expert Review: Welle 2 (FinOps Accounting & Dynamic Schema Contracts)

**Prüfbericht-ID:** `SEC-REV-2026-10-02-WELLE-2`  
**Datum:** 2026-10-02  
**Lead Security Architect & Auditor:** Enterprise Security Specialist Team  
**Geprüfte Features:** `F-AI-08` (FOCUS FinOps Accounting) & `F-GOV-08` (Dynamic Schema Contracts)  
**Status:** **PASSED (Security Freigabe nach Härtung erteilt)**

---

## 1. Audit-Scope & Prüffokus

Im Rahmen von **Welle 2** wurden die Architektur und Implementierung der beiden Module folgenden Sicherheitsprüfungen unterzogen (STRIDE, OWASP Top 10 API Security, FinOps/Billing Integrity):

1. **`F-AI-08` FOCUS FinOps Accounting for Token & Compute:**
   - **Denial of Wallet & Runaway Agent Protection:** Wirksamkeit von Soft- & Hard-Cap-Sperren bei KI-Inferenz und Datenbank-Rechenzeit.
   - **Multi-Tenant Isolation & IDOR-Prävention:** Berechtigungsprüfungen beim Abruf von Budget- und Verbrauchsdaten.
   - **Export-Sicherheit:** Schutz vor CSV Formula Injection (CWE-1236) beim Datenexport für Enterprise-Finanzsysteme.
   - **DoS / Memory Exhaustion:** Begrenzung von In-Memory-Verbrauchspuffern (`MaxInMemoryRecords`).

2. **`F-GOV-08` Dynamic Schema Contracts & Tag-Based Projection:**
   - **Information Disclosure / Zero-Leakage:** Zuverlässiges Entfernen von `@inaccessible`- und ausgeschlossenen `@tag`-Feldern/-Typen vor Client-Auslieferung.
   - **Orphan Type Pruning:** Unterdrückung leerer Typ-Hüllen, um keine internen Typnamen zu leaken.
   - **ReDoS Resilience:** Absicherung der SDL-Filter-Regexes gegen algorithmische Komplexitätsangriffe (CWE-1333).
   - **Metadata Enumeration Defense:** Vermeidung von Informationsabfluss über registrierte interne Schema-Verträge in Fehlermeldungen.

---

## 2. Identifizierte Schwachstellen & Durchgeführte Härtungsmaßnahmen

Während des Reviews wurden 4 konkrete Angriffsvektoren identifiziert und unmittelbar im Code behoben:

### Befund 1: Insecure Direct Object Reference (IDOR) im Budget-Endpunkt (Behoben in `FinOpsEndpoints.cs`)
- **Schwachstelle:** In `GET /api/v1/finops/budget/{tenantId}` wurde zwar geprüft, ob der Anrufer die Rolle `BillingAdmin` besitzt, jedoch nicht, ob die übergebene `tenantId` mit dem Mandanten des Anrufers übereinstimmt. Ein Mandanten-BillingAdmin von Mandant A konnte somit die Budgetgrenzen, den aktuellen Verbrauch und den Budgetstatus von Mandant B ausspähen.
- **Härtungsmaßnahme:** Einbau einer strikten Mandanten-Grenzprüfung:
  ```csharp
  var isClusterAdmin = GatewayPolicies.HasAnyRole(user, ["ClusterAdmin", "GovernanceAdmin"]);
  if (!isClusterAdmin && (string.IsNullOrWhiteSpace(callerTenant) || !string.Equals(callerTenant, tenantId, StringComparison.OrdinalIgnoreCase)))
  {
      return Results.StatusCode(StatusCodes.Status403Forbidden);
  }
  ```
  Nur globale `ClusterAdmin` oder `GovernanceAdmin` dürfen fremde Mandanten-Budgets abfragen.

### Befund 2: CSV Formula Injection beim FOCUS-Export (Behoben in `FinOpsEndpoints.cs`)
- **Schwachstelle (CWE-1236):** Beim Export von Verbrauchsdatensätzen (`GET /api/v1/finops/focus?format=csv`) wurden Felder wie `SubAccountId`, `ResourceId` (Operation Name) oder `ServiceName` ohne Neutralisierung führender Formelzeichen in die CSV geschrieben. Ein Angreifer, der eine GraphQL-Operation z. B. `=cmd|' /C calc'!A0` benannte, konnte beim Öffnen des Reports in Tabellenkalkulationen (Excel, Calc) Remote-Code-Execution auslösen.
- **Härtungsmaßnahme:** Einführung der Methode `EscapeCsvField`:
  - Alle Werte werden in Anführungszeichen gekapselt und enthaltene Quotes verdoppelt (`""`).
  - Werte, die mit `=`, `+`, `-`, `@`, `\t` oder `\r` beginnen, werden mit einem führenden Single-Quote `'` neutralisiert.

### Befund 3: ReDoS-Risiko in SchemaContractFilter (Behoben in `SchemaContractFilter.cs`)
- **Schwachstelle (CWE-1333):** Die regulären Ausdrücke für `@tag` und `@inaccessible` verfügten über kein Ausführungs-Timeout. Zudem waren ungenutzte statische Regexes deklariert.
- **Härtungsmaßnahme:**
  - `TagRegex` und `InaccessibleRegex` wurden mit einem strikten `TimeSpan.FromSeconds(1)` Timeout versehen.
  - Die ungenutzten Regexes `TypeBlockRegex` und `FieldLineRegex` wurden entfernt.

### Befund 4: Information Disclosure über Contract-Enumeration (Behoben in `SchemaContractMiddleware.cs`)
- **Schwachstelle (CWE-209):** Bei Angabe eines ungültigen Vertrags-Headers (`X-Gateway-Contract: foo`) gab die Middleware im 400-Bad-Request-Body die vollständige Liste aller registrierten internen Schema-Verträge (`Available contracts: [public, partner, b2b-confidential, ...]`) zurück. Dies ermöglichte externen Angreifern die Rekognoszierung interner Schnittstellen-Tiers.
- **Härtungsmaßnahme:** Die Fehlermeldung wurde auf `Unknown schema contract '{contractName}'.` bereinigt, ohne die Liste der verfügbaren Verträge offenzulegen.

---

## 3. Sicherheitsbewertung je Modul nach Härtung

### 3.1 `F-AI-08`: FOCUS FinOps Accounting
| Prüffeld | Befund & Kontrollmechanismus | Bewertung |
|---|---|:---:|
| **Denial of Wallet** | `FinOpsBudgetMiddleware` stoppt Anfragen bei `budgetStatus.IsExceeded` deterministisch mit HTTP 429 (`FINOPS_BUDGET_EXCEEDED`). Ein Schwellenwert-Header `X-FinOps-Budget-Warning: true` warnt bei Erreichen der Soft-Cap (z. B. 80%). | **EXZELLENT** |
| **Mandantentrennung** | Strikte Filterung nach `SubAccountId` / `tenant_id` im Service und den Endpunkten; unberechtigte Cross-Tenant-Abfragen werden mit HTTP 403 abgewiesen. | **VERIFIZIERT** |
| **Speicherverbrauch** | FIFO-Eviction bei Erreichen von `MaxInMemoryRecords = 10000` schützt vor OOM-Angriffen durch massenhafte Accounting-Events. | **ROBUST** |
| **Integrität der Berechnung** | Vollständige `decimal`-Arithmetik mit Rundung auf 6 Dezimalstellen verhindert Rundungsverluste bei Kleinstbeträgen. | **SICHER** |

### 3.2 `F-GOV-08`: Dynamic Schema Contracts
| Prüffeld | Befund & Kontrollmechanismus | Bewertung |
|---|---|:---:|
| **Schema-Isolation** | `@inaccessible`-Typen und -Felder werden restlos entfernt, bevor der SDL-String den Gateway verlässt. Orphan-Typen (ohne verbleibende Felder) werden automatisch gepruned. | **VERIFIZIERT** |
| **Tag-Projektion** | Mandanten sehen ausschließlich Felder, die ihren `IncludedTags` entsprechen oder nicht durch `ExcludedTags` gesperrt sind. | **SICHER** |
| **Direktiven-Säuberung** | Interne Direktiven (`@tag(...)`, `@inaccessible`) werden aus dem bereinigten SDL entfernt; keine Offenlegung von Governance-Metadaten. | **GEHÄRTET** |

---

## 4. Test- und Regressionsstatus

- **Unit-Tests (FinOps & Contracts):** Alle Tests inkl. neuer Sicherheits-Tests für CSV-Neutralisierung und IDOR-Schutz erfolgreich (`1551 / 1551` Tests bestanden).
- **Gesamtlösung:** Keine Regressionen in bestehenden Sicherheits- und Authentifizierungs-Pipelines.

---

## 5. Freigabebeschluss

Die Implementierung von **Welle 2** (`F-AI-08` und `F-GOV-08`) erfüllt in der gehärteten Fassung die strengen Anforderungen an Enterprise-Mandantensicherheit, Denial-of-Wallet-Schutz und Zero Information Leakage.

**Gesamtergebnis: Enterprise Security Freigabe für Welle 2 vollumfänglich erteilt.**
