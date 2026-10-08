# Architektonischer Umsetzungsplan: Bereich 3 – Container- & Demo-Seed (Standard ReBAC-Tuples & Iceberg Metadaten für PoC/Dev)

**Dokument-ID:** `PLAN-BEREICH-3-CONTAINER-DEMO-SEED-2026-10-08`  
**Datum:** 2026-10-08  
**Rolle:** Solution Architect (`csharp-architect`)  
**Status:** Genehmigungsreif / Bereit für TDD-Umsetzung  
**Bezug:** [`docs/plans/status-und-umsetzungsplan-2026-10-07.md`](status-und-umsetzungsplan-2026-10-07.md) (Abschnitt 3: Container- & Demo-Seed).

---

## 1. Architektonische Leitplanken & Problemstellung

1. **Problemstellung (Out-of-the-Box PoC-Fähigkeit):**
   - Wenn ReBAC (`Gateway:Rebac:Enabled = true`) aktiv ist, verlangt der Unified Policy Decision Point (PDP) für DuckDB OLAP standardmäßig die Relation `can_query` auf Tabellenobjekten (`table:domain.schema.table`).
   - Die Arrow IPC Export API (`/api/v1/export/arrow`) verlangt über `RequireRebac` die Relation `viewer` auf dem Tabellenobjekt.
   - Da `ZanzibarRebacEvaluator` bisher keine Vererbung von `can_query` auf `viewer`/`editor`/`owner` registriert hatte und weder in `InsecureGettingStarted` noch in `deploy/containers/` Standard-ReBAC-Tuples für Demo-Benutzer wie `user:david` existierten, schlugen OLAP- und Arrow-Abfragen im PoC/Container-Setup mit HTTP 403 Forbidden fehl.
   - Ebenso fehlten in `seed_governance.py` direkte Consents für `user:david` (`S-1-5-21-LWE-DAVID`) auf der Iceberg-Tabelle `lakehouse.dbo.orders` und `sales.public.orders`.

2. **Leitplanken nach `csharp-architect`:**
   - **KISS & YAGNI:** Keine komplexe graphische Rechteverwaltung erzwingen; Vererbungsregel `can_query <- viewer, editor, owner` in `ZanzibarRebacEvaluator.InitializeDefaultInheritance()` verankern.
   - **Dev- & PoC-Ergonomie:**
     - `RebacOptions.SeedTuples`: Option zur deklarativen Angabe von Initial-Tuples in Konfigurationen (`appsettings.Development.json`).
     - `InsecureGettingStartedOptions.danger_bypass_rebac`: Dedizierter Switch zum gezielten Abschalten von ReBAC-Prüfungen in Schnelleinstiegs-/Test-Szenarien analog zu `danger_bypass_consent_checks` und `danger_bypass_lakehouse_auth`.
     - Automatisches Seeding von Standard-Tuples für `InMemoryRebacStore` bei `IsDevelopment()` bzw. `InsecureGettingStarted`.
   - **Container-Seed-Parität:** `deploy/containers/governance-seed/seed_governance.py` um Consents und Redis-ReBAC-Tuples für `user:david` / `S-1-5-21-LWE-DAVID` erweitern.

---

## 2. Komponenten-Design & Spezifikation

### 2.1 ReBAC-Vererbung (`ZanzibarRebacEvaluator.cs`)
In `InitializeDefaultInheritance()`:
```csharp
RegisterInheritance("viewer", "editor", "owner");
RegisterInheritance("editor", "owner");
RegisterInheritance("can_query", "viewer", "editor", "owner");
```
Damit autorisiert jeder `viewer` (sowie `editor` und `owner`) automatisch den `can_query`-Zugriff auf Tabellenobjekte.

### 2.2 ReBAC-Optionen & Insecure-Bypass (`GatewayOptions.cs`)
1. In `RebacOptions`:
   ```csharp
   public List<RebacTuple> SeedTuples { get; init; } = [];
   ```
2. In `InsecureGettingStartedOptions`:
   ```csharp
   /// <summary>
   /// [DANGER] Umgeht ReBAC (Relationship-Based Access Control) Prüfungen für Schnelleinstieg / PoC.
   /// </summary>
   public bool danger_bypass_rebac { get; init; } = false;
   ```
3. In `GatewayOptions`:
   ```csharp
   public bool IsRebacBypassed => Insecure.danger_bypass_rebac;
   ```
4. Guardrails in `RebacEndpointFilter.cs` und `TableAccessPolicy.cs`:
   - Wenn `IsRebacBypassed == true`, wird die ReBAC-Prüfung übersprungen (Allow).

### 2.3 Store-Seeding in DI (`GatewayServiceCollectionExtensions.cs`)
Beim Auflösen von `IRebacStore`:
- Wenn `SeedTuples` in `gatewayOptions.Rebac.SeedTuples` hinterlegt sind oder im Development-Modus / Insecure-Modus gearbeitet wird, werden Standard-Demo-Tuples geladen:
  - `(Tenant: "default", User: "user:david", Relation: "viewer", Object: "table:lakehouse.dbo.orders")`
  - `(Tenant: "default", User: "S-1-5-21-LWE-DAVID", Relation: "viewer", Object: "table:lakehouse.dbo.orders")`
  - `(Tenant: "default", User: "user:david", Relation: "viewer", Object: "table:sales.public.orders")`
  - `(Tenant: "default", User: "S-1-5-21-LWE-DAVID", Relation: "viewer", Object: "table:sales.public.orders")`
  - `(Tenant: "tenant_lwe", User: "user:david", Relation: "viewer", Object: "table:lakehouse.dbo.orders")`
  - `(Tenant: "tenant_lwe", User: "S-1-5-21-LWE-DAVID", Relation: "viewer", Object: "table:lakehouse.dbo.orders")`

### 2.4 Container-Seed (`deploy/containers/governance-seed/seed_governance.py`)
1. Consents für `user:david` / `S-1-5-21-LWE-DAVID` in SQLite anlegen (auf `lakehouse.dbo.orders` und `sales.public.orders`).
2. Optionales Redis-Seeding: Wenn `redis` erreichbar ist, die entsprechenden ReBAC-Hashes (`autheris:rebac:default:tuples` und `autheris:rebac:tenant_lwe:tuples`) mit den Demo-Tuples befüllen.

---

## 3. TDD-Testplan

1. `ZanzibarRebacEvaluatorTests`:
   - `ViewerRelation_Inherits_CanQuery_OnTableObject`: Überprüfung, dass ein Benutzer mit Tuple `("user:david", "viewer", "table:lakehouse.dbo.orders")` die Abfrage nach `can_query` erfolgreich besteht.
2. `TableAccessPolicyRebacTests`:
   - `TableAccessPolicy_WithDangerBypassRebac_AllowsQueryEvenWithoutTuples`: Wenn `danger_bypass_rebac = true`, wird der ReBAC-Gate-Check übersprungen.
3. `ArrowExportRebacIntegrationTests`:
   - `ArrowExport_WithSeededRebacViewer_AllowsExport`: Exportanfrage für `user:david` auf `lakehouse.dbo.orders` liefert HTTP 200 (statt HTTP 403).
4. `InsecureGettingStartedIntegrationTests`:
   - Testfall zur Überprüfung des ReBAC-Bypass und Seeding-Verhaltens.
