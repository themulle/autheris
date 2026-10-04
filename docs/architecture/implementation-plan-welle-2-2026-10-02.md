# Master-Implementierungsplan: Welle 2 (FinOps Accounting & Dynamic Schema Contracts)

**Dokument-ID:** `IMPL-PLAN-2026-10-02-WELLE-2`  
**Datum:** 2026-10-02  
**Status:** Genehmigt (Architektur-Freigabe)  
**Autor:** Principal Enterprise Software & Security Architect  
**Geltungsbereich:** `gql` (Core Gateway), `gql_sqlparser`, `gql_extensions`  
**Referenzen:** [`marktanalyse.md`](file:///root/lis-git/gql/gql/marktanalyse.md), [`implementation-plan-welle-1-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-1-2026-10-02.md), [FinOps FOCUS Specification v1.2](https://focus.finops.org/)

---

## 1. Executive Summary & Strategischer Kontext

Nach der erfolgreichen Fertigstellung von **Welle 1** (`F-AI-07`, `F-CDC-03`, `F-OPS-01`) adressiert **Welle 2 (Wave 2)** die beiden höchstbewerteten Themen der **Phase 2 (Strategische Skalierung & Enterprise Governance)** aus der Marktanalyse:

```
+---------------------------------------------------------------------------------------------------+
|                                      WELLE 2 LEISTUNGSUMFANG                                      |
+----------------------------------------------------+----------------------------------------------+
| F-AI-08: FOCUS FinOps Accounting für Token/Compute | F-GOV-08: Dynamic Schema Contracts (@tag)    |
| -> Standardisiertes FinOps-Modell (FOCUS v1.2/1.4) | -> Multi-Tenant Schema-Projektion via @tag   |
| -> Schutz vor "Denial of Wallet" durch Budget-Caps | -> Isolierte Introspektion für Partner & Web |
| -> Granulares Chargeback/Showback je Mandant/Agent | -> Single Source of Truth ohne Subgraph-Drift|
+----------------------------------------------------+----------------------------------------------+
                                          |
                                          v
+---------------------------------------------------------------------------------------------------+
|                           ARCHITEKTONISCHES FUNDAMENT & ENABLER                                  |
|  - F-AI-07 Semantic Pruner: Liefert gefilterte Tool-Tokens direkt in das FinOps-Accounting         |
|  - K-K10 Unified GatewayRole / Tenant Evaluator: Verhindert Privilegien-Bypass bei Schema-Contracts |
|  - K-K14 Distributed Cluster State: Verteiltes Budget-Tracking & Quoten-Synchronisation via Redis   |
+---------------------------------------------------------------------------------------------------+
```

---

## 2. Feature-Spezifikationen & Technisches Design

### 2.1 `F-AI-08` FOCUS-konformes FinOps Accounting für Token & Compute

#### 2.1.1 Problem & Schmerzpunkt
1. Autonome Agenten führen mehrstufige Planungs- und Abfrageschleifen aus. Einzelne fehlgeleitete Agenten-Sessions können hunderte Dollar an LLM- und Backend-Kosten in wenigen Minuten verursachen.
2. Bestehende API-Gateways (Apollo, Kong, Hasura) erfassen HTTP-Metriken (Status, Latenz), aber keine **FOCUS-kompatiblen Kosteneinheiten** (`BilledCost`, `EffectiveCost`, `TokenCount`, `DB-IOPS`).
3. Interne Verrechnung (Chargeback/Showback) scheitert an fehlender Standardisierung.

#### 2.1.2 Architektur & Lösungsansatz
1. **FOCUS Datenmodell:** Implementierung der standardisierten Spaltenstruktur gemäß *FinOps Open Cost and Usage Specification (FOCUS v1.2)*:
   - `ChargePeriodStart`, `ChargePeriodEnd`
   - `BilledCost`, `EffectiveCost`, `Currency` (z. B. EUR / USD)
   - `ConsumedQuantity`, `ConsumedUnit` (`Tokens`, `ComputeMilliseconds`, `MegaBytes`)
   - `SubAccountId` (`TenantId`), `ResourceId` (Operation/Tool), `ServiceName` (`Autheris`)
   - `ProviderName`, `PricingCategory` (`AI-Inference`, `DatabaseCompute`)
2. **In-Flight Accounting Interceptor:**
   - Erfasst bei jedem GraphQL- / REST- / MCP-Aufruf:
     - LLM Prompt Tokens, Completion Tokens (aus MCP-Header / Proxy)
     - Gateway Compute Millisekunden & SQL Pushdown Query Duration
     - Assoziierter Mandant (`TenantId`), Service Principal und Agent Session ID
3. **Budget Enforcement Engine (Soft & Hard Caps):**
   - **Soft Cap (z. B. 80% des Monatsbudgets):** Löst Alerting via OpenTelemetry Metrik und Webhook-Event (`finops.budget.warning`) aus.
   - **Hard Cap (100% des Budgets):** Verweigert nachfolgende Aufrufe mit `429 Too Many Requests` bzw. GraphQL-Fehlercode `FINOPS_BUDGET_EXCEEDED`.
   - Cluster-weites Tracking über `IDistributedClusterStateProvider` (Redis/Garnet Atomic Increment).
4. **Multi-Format Export & OpenTelemetry:**
   - REST API `/api/v1/finops/focus` mit Content-Negotiation (`application/json`, `text/csv`, `application/vnd.apache.parquet`).
   - Prometheus/OTel Metriken: `gateway_finops_billed_cost_total`, `gateway_finops_consumed_tokens_total`.

#### 2.1.3 Kernschnittstellen
```csharp
namespace Autheris.Application.FinOps.Model;

public sealed record FocusCostRecord(
    string ChargePeriodStart,
    string ChargePeriodEnd,
    decimal BilledCost,
    decimal EffectiveCost,
    string Currency,
    double ConsumedQuantity,
    string ConsumedUnit,
    string SubAccountId, // TenantId
    string ResourceId,   // Operation/Tool Name
    string ServiceName,  // "Autheris"
    string PricingCategory,
    IReadOnlyDictionary<string, string>? Tags = null
);

public interface IFinOpsAccountingService
{
    ValueTask RecordUsageAsync(
        string tenantId,
        string principalId,
        string operationName,
        string category,
        long tokens,
        long computeMs,
        CancellationToken ct = default);

    ValueTask<BudgetStatus> CheckBudgetAsync(string tenantId, CancellationToken ct = default);
    IAsyncEnumerable<FocusCostRecord> GetRecordsAsync(DateTimeOffset from, DateTimeOffset to, string? tenantId = null, CancellationToken ct = default);
}

public sealed record BudgetStatus(bool IsExceeded, bool IsWarning, decimal CurrentSpend, decimal BudgetLimit);
```

---

### 2.2 `F-GOV-08` Dynamic Schema Contracts & Tag-basierte Projektion (`@tag` / `@inaccessible`)

#### 2.2.1 Problem & Schmerzpunkt
1. Großunternehmen betreiben oft parallele Gateway-Instanzen für interne Systeme, B2B-Kunden, mobile Applikationen und externe Partner.
2. Dies führt zu Schemadrift, hohem Betriebsaufwand und der Gefahr, dass vertrauliche Enterprise-Felder versehentlich über externe APIs sichtbar werden.
3. Apollo GraphOS Contracts lösen dieses Problem nur als Cloud-SaaS-Funktion im teuersten Enterprise-Tarif.

#### 2.2.2 Architektur & Lösungsansatz
1. **Deklarative Schema-Annotation via GraphQL Direktiven:**
   - `@tag(name: String!)`: Wiederholbare Direktive auf ObjectTypes, Fields, Interfaces, Enums und Unions.
   - `@inaccessible`: Globale Ausschlusssperre für vertrauliche oder unfertige Schnittstellenelemente.
2. **Contract Projection Engine (`SchemaContractFilter`):**
   - Vorkompilierung separater Schema-Repräsentationen zur Gateway-Start- oder Reload-Zeit (z. B. `Contract: partner`, `Contract: public`, `Contract: mobile`).
   - Algorithmus zur Bereinigung von Waisentypen (Orphan Types): Werden alle Felder eines Types weggefiltert, wird der gesamte Typ samt Referenzen aus dem Vertragsschema entfernt.
3. **Strikte Introspektions-Isolation:**
   - Führt ein Client auf dem `partner`-Endpunkt eine Introspektion durch (`__schema`, `__type`), sieht er **ausschließlich** die für `partner` freigegebenen Typen und Felder.
   - Es existiert keinerlei Information Leakage über unveröffentlichte Schema-Bereiche.
4. **Dynamic Contract Routing Middleware (`SchemaContractMiddleware`):**
   - Ermittelt den zutreffenden Vertrag aus:
     1. HTTP-Header `X-Gateway-Contract: <name>`
     2. Query-Parameter `?contract=<name>`
     3. URL-Pfad `/graphql/contracts/<name>`
     4. ClaimsPrincipal Claim `contract`
   - Bindet den Request an die vorkompilierte `ISchema`-Instanz des entsprechenden Vertrags.
   - **Fail-Closed:** Versucht ein Client, ein nicht im Vertrag enthaltenes Feld abzufragen, schlägt die AST-Validierung mit `Field "..." does not exist` fehl, genau wie bei einem nicht existierenden Feld.

#### 2.2.3 Kernschnittstellen
```csharp
namespace Autheris.Application.Governance.Contracts;

public sealed class SchemaContractDefinition
{
    public string Name { get; init; } = string.Empty;
    public HashSet<string> IncludedTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExcludedTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ExcludeInaccessible { get; init; } = true;
}

public interface ISchemaContractManager
{
    bool HasContract(string contractName);
    IReadOnlyList<string> GetAvailableContracts();
    string FilterSchemaSdl(string originalSdl, SchemaContractDefinition contract);
}
```

---

## 3. Datenfluss & Komponenten-Architektur (Welle 2)

```
+-----------------------------------------------------------------------------------------------+
| Client Request (GraphQL / MCP / REST)                                                         |
| Headers: Authorization: Bearer ..., X-Gateway-Contract: partner                               |
+-----------------------------------------------------------------------------------------------+
                                                |
                                                v
+-----------------------------------------------------------------------------------------------+
| Pipeline Stage 1: Contract Resolution (F-GOV-08)                                              |
| [ SchemaContractMiddleware ]                                                                  |
|   -> Liest Contract 'partner' aus Header / Claim                                              |
|   -> Validiert gegen SchemaContractManager                                                    |
|   -> Wählt vorkompiliertes 'Partner'-Schema (Alle internen Typen / Felder getilgt)            |
+-----------------------------------------------------------------------------------------------+
                                                |
                                                v
+-----------------------------------------------------------------------------------------------+
| Pipeline Stage 2: FinOps Pre-Check (F-AI-08)                                                  |
| [ FinOpsBudgetMiddleware / FinOpsBudgetEnforcer ]                                             |
|   -> Fragt BudgetStatus für Tenant ab (Redis Atomic Hash)                                     |
|   -> Bei Hard-Cap Überschreitung: 429 Too Many Requests (FINOPS_BUDGET_EXCEEDED)              |
+-----------------------------------------------------------------------------------------------+
                                                |
                                                v
+-----------------------------------------------------------------------------------------------+
| Pipeline Stage 3: Governed Execution & Pushdown                                               |
| [ GatewayExecutionService ] -> Single-Query Pushdown / MCP Tool Registry                      |
+-----------------------------------------------------------------------------------------------+
                                                |
                                                v
+-----------------------------------------------------------------------------------------------+
| Pipeline Stage 4: FinOps Post-Execution Metering (F-AI-08)                                    |
| [ FocusCostAccountingService ]                                                                |
|   -> Erfasst CPU-Zeit, DB-I/O und Prompt/Completion Tokens                                    |
|   -> Berechnet BilledCost & EffectiveCost nach FOCUS v1.2 Spezifikation                       |
|   -> Schreibt in Bounded Channel -> Asynchroner Flush in Storage / Parquet                    |
|   -> Aktualisiert Redis / OTel Counter (gateway_finops_billed_cost_total)                     |
+-----------------------------------------------------------------------------------------------+
```

---

## 4. Phasierungs- und Meilensteinplan für Welle 2

Die Realisierung erfolgt in **4 strukturierten Sprints**:

| Meilenstein / Sprint | Dauer | Schwerpunkte & Arbeitspakete | Betroffene Komponenten | Quality Gate / Deliverable |
|---|:---:|---|---|---|
| **Sprint 1: F-AI-08 FinOps Core** | 3 Tage | **FOCUS Accounting Engine (`F-AI-08`)**<br>- `FocusCostRecord` & `IFinOpsAccountingService`<br>- In-Memory & Redis Usage Aggregator<br>- Token- und Latenz-Extraktor im MCP- und GraphQL-Pfad<br>- FinOps Formel-Kalkulator (Token-to-Cost, Compute-to-Cost) | `Autheris.Application.FinOps`<br>`Autheris.Domain` | Unit-Tests für FOCUS-Mapping; genaue Kostenberechnung nachgewiesen |
| **Sprint 2: FinOps Enforcement** | 3 Tage | **Budget Caps & Export (`F-AI-08`)**<br>- `FinOpsBudgetEnforcer` & Budget-Middleware<br>- Distributed Atomic Spend Tracker via Redis `K-K14`<br>- REST Export Endpoint `/api/v1/finops/focus` (CSV/JSON/Parquet)<br>- OTel Metriken & Webhooks | `Autheris.Api`<br>`Autheris.Infrastructure` | Hard Cap Test: Blockiert Abfragen bei 100% Budget mit 429; Export valide |
| **Sprint 3: F-GOV-08 Schema Contracts** | 3 Tage | **Tag-basierte Projektion (`F-GOV-08`)**<br>- Parser für `@tag` und `@inaccessible`<br>- `SchemaContractFilter` mit Orphan-Type Bereinigung<br>- Dynamic SDL Compiler für Schema-Slices | `Autheris.Application.Governance.Contracts`<br>`gql_sqlparser` | Unit-Tests: `@inaccessible` und nicht-ge-taggte Felder werden restlos getilgt |
| **Sprint 4: Contract Runtime & E2E** | 3 Tage | **Routing, Introspection & Härtung**<br>- `SchemaContractMiddleware`<br>- Introspektions-Isolation (`__schema` / `__type`)<br>- Integration mit `GatewayRoleEvaluator` und Security-Audits<br>- End-to-End Tests & Performance-Messung | `Autheris.Api.Middleware`<br>`tests/Autheris.Tests.Unit` | Introspection Test: 0 Informationsleck; 100% Testabdeckung |

---

## 5. Sicherheits-, Governance- und Compliance-Leitplanken

1. **Strikte Introspektions-Quarantäne (`F-GOV-08`):**
   - Es ist sicherzustellen, dass keine Fragmente, Direktiven oder Typdefinitionen unberechtigter Verträge in `__schema` oder `__type` auftauchen.
   - Fehlermeldungen bei ungültigen Feldern dürfen keine Hinweise auf die Existenz des Feldes in einem anderen Vertrag geben (standardmäßiges `FieldNotFound`).
2. **Fail-Closed Budget Enforcement (`F-AI-08`):**
   - Bei Ausfall des Redis-Cluster-Status greift für Tenants mit konfiguriertem Hard-Cap ein Fail-Safe-Modus (Sperre oder konservatives lokales In-Memory-Tracking), um unbegrenztes unbemerktes Weiterbrennen von Budget zu verhindern.
3. **Manipulationssichere FOCUS-Exporte:**
   - Der Zugriff auf `/api/v1/finops/focus` erfordert strikt `GovernanceAdmin`, `BillingAdmin` oder `ClusterAdmin` Rolle.
   - Tenants können nur ihre eigenen FOCUS-Datensätze abrufen (`TenantId` Isolation).

---

## 6. Traceability-Matrix Welle 2

| Feature-ID | Name | Priorität | Test-Suite | Dokumentation |
|---|---|:---:|---|---|
| **`F-AI-08`** | FOCUS FinOps Accounting für Token & Compute | Hoch (RICE-4) | `FocusCostAccountingTests.cs` | [`f-ai-08-focus-finops-accounting.md`](file:///root/lis-git/gql/gql/docs/features/f-ai-08-focus-finops-accounting.md) |
| **`F-GOV-08`** | Dynamic Schema Contracts (`@tag`) | Hoch (RICE-5) | `DynamicSchemaContractTests.cs` | [`f-gov-08-schema-contracts-tag-projection.md`](file:///root/lis-git/gql/gql/docs/features/f-gov-08-schema-contracts-tag-projection.md) |
