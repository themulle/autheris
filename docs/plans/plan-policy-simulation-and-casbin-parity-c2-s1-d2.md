# Implementierungsplan: Policy-Simulation-Harmonisierung (C-2), Fail-Closed DemoData (S-1/D-2) und Snapshot-Test-Härtung

**Datum:** 2026-10-07  
**Ziel-Branch:** `feat/ast-target-dialect-generator`  
**Referenzdokument:** `docs/plans/status-und-umsetzungsplan-2026-10-07.md` (Punkte C-2, S-1/D-2, Testlücken E-2)

---

## 1. Problemanalyse & Architektur-Kontext

### 1.1 C-2: Diskrepanzen zwischen PolicySimulationService und CasbinEnforcementService
1. **Modell-Divergenz:**
   `PolicySimulationService` instanziiert ein Casbin-Modell fest aus `CasbinEnforcementService.DefaultModelText`. Eine in `GatewayOptions.Casbin.ModelPath` konfigurierte Modelldatei oder ein im System aktiver Casbin-Vertrag wird ignoriert.
2. **Parser-Divergenz & Fail-Open-Risiken:**
   `PolicySimulationService` splittet Zeilen per naivem `.Split(',')`. Kommas in Anführungszeichen (z. B. in ABAC-Ausdrücken wie `r.ctx.Attributes.Contains("foo,bar")`) werden zerschnitten. Zudem wird `NormalizeEffect` nicht aufgerufen – ungültige oder fehlerhafte `eft`-Werte fallen auf Allow zurück oder werden still fehlinterpretiert, anstatt wie in der Durchsetzung mit `FormatException` abgewiesen zu werden.
3. **Kontext-Divergenz:**
   Beim Aufruf von `enforcer.Enforce` übergibt `PolicySimulationService` ein anonymes Objekt `new { Tenant = tenantStr, UserSid = actor }`. `CasbinEnforcementService` übergibt hingegen den vollständigen `SecurityEvaluationContext` (mit `UserSid`, `Tenant`, `TargetTable`, `Timestamp`, `Attributes`, etc.). Regeln mit ABAC-Klauseln auf `r.ctx` verhalten sich in der Simulation daher völlig anders als im produktiven Gateway.
4. **Fehlende Wildcard-Prüfung:**
   Wenn ein Modell konfiguriert ist, das laut Probe W1 keine Wildcards unterstützt, lehnt die Durchsetzung `*`-Regeln ab. Die Simulation akzeptiert sie bisher still.
5. **Fehlende Paritäts-Tests:**
   Es existiert kein vergleichender Test, der sicherstellt, dass `PolicySimulationService` und `CasbinEnforcementService` für identische Regelsätze und Audit-Ereignisse exakt dieselbe Entscheidung fällen.

### 1.2 S-1 / D-2: Fail-Closed DemoDataSwitch
In `Autheris.Domain.Options.DemoDataSwitch`:
```csharp
public static bool Resolve(GatewayOptions options, string? environmentName)
{
    if (options.GovernanceDb.SeedDemoData.HasValue)
        return options.GovernanceDb.SeedDemoData.Value;

    return string.IsNullOrEmpty(environmentName) ||
           string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
}
```
Wenn kein `ASPNETCORE_ENVIRONMENT` gesetzt ist (`environmentName == null`), wird dies als `Development` gewertet und Beispieldaten werden geladen. In Produktions-Containern, bei denen versehentlich kein Environment deklariert ist, führt dies zu Datenleck-Risiken (S-1 / D-2). Die Standardannahme muss strikt Fail-Closed (`false`) sein.

### 1.3 Snapshot-Testlücken (E-2)
In `CasbinSnapshotImmutabilityTests.cs`:
- `Test01`: Bisher nur über `EvaluatePolicyAsync` geprüft. Dort fing der Gateway-Matcher die Regel bereits ab, sodass der interne Zustand des Casbin-Enforcers nicht direkt verifiziert wurde.
- `Test04`: Gegenprobe nach dem Entfernen der globalen `g`-Zeile fehlte.
- `Test07`: Nach einer durch `FormatException` abgelehnten Datei fehlte die Verifikation, dass der alte Snapshot und Enforcer unverändert aktiv bleiben.

---

## 2. Lösungsdesign & Umsetzungsstrategie

### 2.1 C-2: Einheitliche Casbin-Model- & Parser-Nutzung in PolicySimulationService
1. **DI-Erweiterung:**
   `PolicySimulationService` erhält im Konstruktor optional:
   - `IPolicyEnforcementService? policyEnforcementService = null`
   - `IOptions<GatewayOptions>? options = null`
2. **Modell- & Capability-Auflösung:**
   - Wenn `policyEnforcementService is CasbinEnforcementService casbin`: Bezug von `casbin.ModelText` und `casbin.ModelSupportsWildcardTenant`.
   - Andernfalls: Wenn `options?.Value?.Casbin?.ModelPath` konfiguriert ist und existiert, Datei einlesen; sonst Fallback auf `CasbinEnforcementService.DefaultModelText`. Prüfung der Fähigkeiten über `CasbinModelContract.Verify(modelText)`.
3. **Wiederverwendung des zentralen Parsers:**
   Nutzung von `CasbinEnforcementService.ParsePolicyText(draftCsv, defaultTenant: tenantStr, modelSupportsWildcardTenant: modelSupportsWildcard)`.
   Damit werden Quoting, `NormalizeEffect`, `ValidateSubRuleTokens` und Wildcard-Validierung 1:1 identisch zur Durchsetzung ausgeführt.
4. **SecurityEvaluationContext für die Simulation:**
   Erstellung einer echten `SecurityEvaluationContext`-Instanz mit:
   - `UserSid = entry.ActorSid`
   - `Tenant = effectiveTenant`
   - `TargetTable = TableIdentifier.TryParse(table, out var tid) ? tid : new TableIdentifier("default", "public", table)`
   - `Timestamp = entry.OccurredAt`
   - `Attributes = { ["tenant"] = tenantStr, ["user_sid"] = entry.ActorSid.Value }`

### 2.2 S-1 / D-2: Fail-Closed DemoDataSwitch
Änderung in `src/Autheris.Domain/Options/DemoDataSwitch.cs`:
```csharp
public static bool Resolve(GatewayOptions options, string? environmentName)
{
    ArgumentNullException.ThrowIfNull(options);

    if (options.GovernanceDb.SeedDemoData.HasValue)
    {
        return options.GovernanceDb.SeedDemoData.Value;
    }

    return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
}
```
Damit ist `null` oder ein leerer String per Default `false` (Produktion).

### 2.3 Snapshot-Testvervollständigung (E-2)
Erweiterung der bestehenden Tests in `CasbinSnapshotImmutabilityTests.cs`:
- Direkte `GetEnforcer(TenantA).Enforce(...)`-Prüfung in `Test01`.
- Gegenprobe in `Test04`: Nach Entfernen der `g`-Regel muss `alice` abgewiesen werden.
- Verifikation in `Test07`: Nach fehlerhafter Datei muss die vorherige Policy intakt bleiben.

---

## 3. Test- & Verifikationsplan (TDD)

1. **Unit-Tests für DemoDataSwitch:**
   - `DemoDataSwitch_NullEnvironment_ReturnsFalse`
   - `DemoDataSwitch_EmptyEnvironment_ReturnsFalse`
   - `DemoDataSwitch_DevelopmentEnvironment_ReturnsTrue`
   - `DemoDataSwitch_ExplicitOverride_TakesPrecedence`
2. **Paritäts-Tests (PolicySimulationParityTests):**
   - Parität bei einfacher Allow-Regel
   - Parität bei explizitem Deny (`eft = deny`)
   - Parität bei Wildcard-Mandant `*`
   - Parität bei ABAC-Subregeln auf `r.ctx`
   - Parität bei Syntaxfehlern (`FormatException` bei ungültigem `eft`)
3. **Casbin-Snapshot-Tests:**
   - Test01, Test04, Test07 aktualisieren und ausführen.
4. **Gesamte Test-Suite:**
   - Ausführen von `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`.
