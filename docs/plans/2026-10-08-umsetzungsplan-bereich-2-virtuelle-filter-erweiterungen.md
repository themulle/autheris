# Architektonischer Umsetzungsplan: Bereich 2 – Virtuelle Filter Erweiterungen (`RequireApproval` & `config-sync/status`)

**Dokument-ID:** `PLAN-BEREICH-2-VIRTUELLE-FILTER-2026-10-08`  
**Datum:** 2026-10-08  
**Rolle:** Solution Architect (`csharp-architect`)  
**Status:** Genehmigungsreif / Bereit für TDD-Umsetzung  
**Bezug:** [`docs/plans/2026-10-08-umsetzungsplan-virtuelle-filter.md`](2026-10-08-umsetzungsplan-virtuelle-filter.md) (Abschnitt Stand der Umsetzung: Offene Punkte 4.2 und `RequireApproval`).

---

## 1. Architektonische Leitplanken & Anti-Overengineering (nach `csharp-architect`)

1. **YAGNI & KISS:**
   - Kein komplexes Workflow-Engine-Subsystem für `RequireApproval`. Stattdessen konsequente Nutzung des bestehenden `VirtualFilterActor`-Modells mit Vier-Augen-Validierung (`ApproverSid` != `UserSid`).
   - `config-sync/status` aggregiert vorhandene Metadaten des aktuellen `VirtualFilterSnapshot` (Generation, ManagedBy-Commits, Zeitstempel, Anzahl aktiver Bindungen), ohne eine neue Tabelle oder Datenbankmigration anzulegen.
2. **Fail-Closed & Segregation of Duties (SoD):**
   - Wenn `VirtualFilterOptions.RequireApproval = true` gesetzt ist, werden Änderungen über die interaktive Administrations-API blockiert, wenn kein eigenständiger, vom Ersteller abweichender Zweitprüfer (`ApproverSid`) benannt ist (`InvalidOperationException`).
   - GitOps-Synchronisationen (`actor.IsSync = true`) mit signierten Git-Commits sind davon ausgenommen, da der Review- und Freigabeprozess bereits im vorgelagerten GitHub-Pull-Request stattfand.
3. **Audit-Integrität:**
   - Jede Freigabe und jeder Sync-Status wird im WORM-Audit-Log mit Ersteller- und Genehmiger-Identität verankert.

---

## 2. Komponenten-Design

### 2.1 Modell-Erweiterung (`VirtualFilterModels.cs` / `GatewayOptions.cs`)
1. In `VirtualFilterOptions`:
   ```csharp
   public bool RequireApproval { get; init; } = false;
   ```
2. In `VirtualFilterActor`:
   ```csharp
   public sealed record VirtualFilterActor(Sid? UserSid, bool IsSync = false, Sid? ApproverSid = null);
   ```

### 2.2 Administrations-Service (`VirtualFilterAdministrationService.cs`)
In `SaveFilterAsync`, `DeleteFilterAsync`, `SaveProfileAsync`, `DeleteProfileAsync`:
```csharp
if (_options.RequireApproval && !actor.IsSync)
{
    if (actor.ApproverSid == null || actor.ApproverSid == actor.UserSid)
    {
        throw new InvalidOperationException("Modifications to virtual filters and access profiles require four-eyes approval by a distinct approver.");
    }
}
```

### 2.3 Status-Endpunkt (`VirtualFilterEndpoints.cs`)
Zwei Routen registrieren:
- `GET /api/v1/governance/config-sync/status`
- `GET /api/v1/governance/virtual-filters/sync/status`

Rückgabe:
```json
{
  "generation": 12,
  "status": "InSync",
  "lastSyncAt": "2026-10-08T15:30:00Z",
  "lastCommit": "6830f0c",
  "filterCount": 5,
  "profileCount": 2,
  "managedFilterCount": 5,
  "managedProfileCount": 2,
  "maxRemovals": 10,
  "requireApproval": false
}
```

---

## 3. TDD-Testplan

1. `VirtualFilterAdministrationTests.cs`:
   - `SaveFilter_WithRequireApproval_WithoutDistinctApprover_ThrowsInvalidOperationException()`
   - `SaveFilter_WithRequireApproval_WithDistinctApprover_Succeeds()`
   - `SaveFilter_WithRequireApproval_SyncActor_BypassesLocalApproval()`
2. `VirtualFilterEndpointsTests.cs` / `EndToEndSqliteTests.cs`:
   - `ConfigSyncStatus_ReturnsCurrentGenerationAndMetadata()`:
     Aufruf `GET /api/v1/governance/config-sync/status` liefert HTTP 200 OK mit `generation`, `filterCount` und `requireApproval`.
