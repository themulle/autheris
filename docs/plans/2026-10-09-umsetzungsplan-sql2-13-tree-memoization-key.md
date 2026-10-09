# Umsetzungsplan: Tree-Memoization-Key Härtung in GovernedTreeQueryService (SQL2-13)

**Referenz:** [security-review-2026-10-07.md](security-review-2026-10-07.md), Befund SQL2-13; Architektur-Leitfaden `csharp-architect`.  
**Datum:** 2026-10-09  
**Branch:** `feat/ast-target-dialect-generator`

---

## 1. Problemstellung & Ist-Zustand (Befund SQL2-13)

In `src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs`:
- Zeile 82: `private readonly ConcurrentDictionary<string, OperationMemo> _memos = new();`
- Zeile 52: `public ConcurrentDictionary<TableIdentifier, ResolvedTableAccess> AccessByTable { get; } = new();`
- Zeile 53: `public ConcurrentDictionary<(TableIdentifier Table, bool Allowed), byte> Audited { get; } = new();`

Wenn ein Client denselben `operationId`-Wert (z. B. eine deterministische Query-ID, Subscription-ID oder statischen Header) wiederverwendet, oder wenn Requests verschiedener Mandanten/Benutzer auf denselben InMemory-Memo-Cache treffen:
1. `AccessByTable` schlägt für eine Tabelle nach, ohne Mandant (`TenantId`) oder Benutzer (`Sid`) im Cache-Key zu berücksichtigen. Ein Benutzer B könnte die `ResolvedTableAccess`-Entscheidung (inklusive Zeilenfilter und Spaltenfreigaben) von Benutzer A erhalten (Cross-Tenant / Cross-Principal Leakage).
2. `Audited` dedubliziert Audit-Einträge nur auf Basis von `(Table, Allowed)`. Der Zugriff von Benutzer B würde im Audit-Log unterschlagen, wenn Benutzer A zuvor dieselbe Tabelle im selben `operationId`-Kontext abgefragt hat.

---

## 2. Ziel-Architektur & Defense-in-Depth

1. **Zweistufige Isolation (Defense-in-Depth):**
   - **Ebene 1 (OperationMemo-Scope):**
     Der globale Cache `_memos` wird von `string` (nur `operationId`) auf einen zusammengesetzten Schlüssel erweitert:
     `(string OperationId, string Tenant, string UserSid)` (bzw. formatiert als `operationId + "|" + tenant + "|" + userSid`).
     Dadurch teilen verschiedene Mandanten oder Benutzer niemals dasselbe `OperationMemo`-Objekt.
   - **Ebene 2 (AccessByTable & Audited im Memo):**
     - `AccessByTable` speichert Einträge mit dem Key:
       `(TableIdentifier Table, string Tenant, string UserSid)`.
     - `Audited` speichert Einträge mit dem Key:
       `(TableIdentifier Table, string Tenant, string UserSid, bool Allowed)`.
     - Auch innerhalb eines Memos ist jede Tabellen-Entscheidung und jeder Audit-Datensatz strikt an Tenant und UserSid gebunden.

2. **Zero-Trust & Fail-Closed Guardrails:**
   - Wenn `principal` fehlt oder keine gültige User-SID vorhanden ist, wird die Auflösung verweigert (Authentifizierung erforderlich).
   - `ResolveOnceAsync` und `AuditOnceAsync` werten `tenantId` und `userSid` strikt aus und sichern atomare Nebenläufigkeit über das `SemaphoreSlim`-Lock ab.

---

## 3. TDD-Schritte (Rot -> Grün -> Refactor)

1. **Rot (Failing Tests):**
   - Erstellung von Tests in `tests/Autheris.Tests.Unit/Tree/GovernedTreeQueryServiceIsolationTests.cs`:
     - Test 1: `ExecuteAsync_WhenSameOperationIdUsedByDifferentPrincipals_ResolvesTableAccessSeparatelyPerUser` (verifiziert, dass `ResolveTableAccessAsync` für jeden Benutzer separat aufgerufen wird und kein Cache-Leck auftritt).
     - Test 2: `ExecuteAsync_WhenSameOperationIdUsedByDifferentTenants_AuditsSeparatelyPerTenant` (verifiziert, dass für verschiedene Mandanten getrennte Audit-Logs aufgezeichnet werden).
2. **Grün (Implementation):**
   - Anpassung von `OperationMemo` und Schlüssel-Strukturen in `GovernedTreeQueryService.cs`.
   - Anpassung von `_memos.GetOrAdd`, `ResolveOnceAsync` und `AuditOnceAsync`.
3. **Refactor & Verifikation:**
   - Ausführung aller Unit-Tests (`dotnet test tests/Autheris.Tests.Unit`).
   - `dotnet build Autheris.sln /warnaserror` (0 Warnings, 0 Errors).
   - Ein Commit: `fix(tree): isolate OperationMemo cache key by tenant and user SID (SQL2-13)`.
