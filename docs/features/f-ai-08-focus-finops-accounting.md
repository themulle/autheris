# F-AI-08: FOCUS-konformes FinOps Accounting für Token & Compute

**Status:** **100% (GA) ✅ (Implementiert & Security-Audited 2026-10-02)**  
**Komponenten:** [`IFinOpsAccountingService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/FinOps/Interfaces/IFinOpsAccountingService.cs), [`FocusCostAccountingService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/FinOps/Services/FocusCostAccountingService.cs), [`FinOpsBudgetMiddleware.cs`](file:///root/lis-git/gql/gql/src/Autheris.Api/Middleware/FinOpsBudgetMiddleware.cs), [`FinOpsEndpoints.cs`](file:///root/lis-git/gql/gql/src/Autheris.Api/Endpoints/FinOpsEndpoints.cs), [`FinOpsModels.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Model/FinOpsModels.cs), [`FinOpsOptions.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Options/GatewayOptions.cs)  
**Referenzen:** [`implementation-plan-welle-2-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-2-2026-10-02.md), [`security-review-welle-2-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/threat-model/security-review-welle-2-2026-10-02.md), [FinOps Foundation FOCUS Specification](https://focus.finops.org/)

---

## 1. Übersicht & Problemstellung
Autonome KI-Agenten (über Model Context Protocol / MCP) lösen durch mehrstufiges Reasoning unkontrollierte Kaskaden von Backend-Abfragen und LLM-Inferenz aus ("Denial of Wallet"). Plattform-Teams in Konzernen können diese Kosten weder transparent messen noch verursachergerecht auf Abteilungen oder Mandanten umlegen (Chargeback / Showback).

## 2. Architektur & Umsetzung
- **Standardisiertes FOCUS v1.2 Kostenmodell:** Kontinuierliche Aggregation von Verbrauchsdaten in standardisierte FOCUS Cost Records (`BilledCost`, `EffectiveCost`, `ConsumedQuantity`, `ConsumedUnit`, `SubAccountId`, `PricingCategory`).
- **Mikro-Abrechnung für Token & Rechenzeit:** Exakte `decimal`-Berechnung für Prompt-Tokens, Completion-Tokens und Backend-CPU-Millisekunden.
- **Budget-Governance & Denial-of-Wallet Schutz (`FinOpsBudgetMiddleware`):**
  - **Soft Cap:** Sendet `X-FinOps-Budget-Warning: true` Header an Clients, wenn der definierte Schwellenwert (z. B. 80%) erreicht ist.
  - **Hard Cap:** Blockiert weitere Anfragen des Mandanten sofort mit `429 Too Many Requests` (`FINOPS_BUDGET_EXCEEDED`).
- **Sichere REST-Endpunkte:**
  - `GET /api/v1/finops/focus`: Liefert FOCUS-Datensätze als JSON oder CSV (mit integriertem CSV-Formula-Injection-Schutz CWE-1236).
  - `GET /api/v1/finops/budget/{tenantId}`: IDOR-geschützte Budgetabfrage mit strikter Mandanten-Isolation.

## 3. Konfigurationsbeispiel (`appsettings.json`)
```json
{
  "Gateway": {
    "FinOps": {
      "Enabled": true,
      "DefaultMonthlyBudget": 100.0,
      "SoftCapRatio": 0.8,
      "PricePerThousandPromptTokens": 0.003,
      "PricePerThousandCompletionTokens": 0.015,
      "PricePerComputeSecond": 0.0001,
      "TenantMonthlyBudgets": {
        "tenant-finance": 500.0,
        "tenant-marketing": 50.0
      }
    }
  }
}
```

## 4. Business Value
- **Vollständige Kostenwahrheit:** Präzise Unit Economics und interne Weiterverrechnung (Showback / Chargeback) von KI- und Gateway-Workloads.
- **Schutz vor Denial of Wallet:** Schützt das Budget vor Amok laufenden KI-Agenten und unkontrollierten Batch-Jobs.
