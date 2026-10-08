# Architektonischer Umsetzungsplan: Bereich 4 – Offene Integrationstests aus Phase 2

**Dokument-ID:** `PLAN-BEREICH-4-INTEGRATIONSTESTS-2026-10-08`  
**Datum:** 2026-10-08  
**Rolle:** Solution Architect (`csharp-architect`)  
**Status:** Genehmigungsreif / Bereit für TDD-Umsetzung  
**Bezug:** [`docs/plans/status-und-umsetzungsplan-2026-10-07.md`](status-und-umsetzungsplan-2026-10-07.md) (Abschnitt 2: Tests nachziehen).

---

## 1. Übersicht der offenen Testfälle

1. **`D-1 / R-SQL-3` (WebSQL PostgreSQL Transaction & Reader Lifecycle):**
   - **Fokus:** Integrationstest für WebSQL SELECT gegen PostgreSQL (`Testcontainers`):
   - **Prüfkriterien:**
     - Reader wird vor dem Commit der Transaktion ordnungsgemäß geschlossen/disposed (`await using (var reader = ...)`).
     - Session-Kontext wird via `set_config(..., true)` (transaktionslokal) in exakt derselben Transaktion initialisiert.
     - Wenn Docker nicht verfügbar ist, sauber überspringen (`SkipException` / frühes Return).
   - **Ort:** `tests/Autheris.Tests.Integration/PostgreSqlWebSqlTransactionTests.cs`.

2. **`POL-15` (PostgreSQL Governance Aktivierung setzt `grantee_sid`):**
   - **Fokus:** In `PostgreSqlGovernanceRepository.Consent.cs` bei `ActivateConsentRequestAsync`:
   - **Prüfkriterien:**
     - Bei Aktivierung für `GranteeType.Group` und `GranteeType.ServicePrincipal` wird `grantee_sid` persistiert.
     - Bei `GranteeType.Role` wird `role_name` persistiert und `grantee_sid` bleibt null.
   - **Ort:** Ergänzung in `tests/Autheris.Tests.Integration/PostgreSqlGovernanceRepositoryTests.cs` (bzw. `PostgreSqlVirtualFilterContractTests.cs`).

3. **`R-SQL-6` (Stored Procedure Session Variables via Initializer):**
   - **Fokus:** `MssqlProcedureInvoker` und `SqlProcedureRowScopeResolver` rufen `IDbSessionContextInitializer.InitializeSessionAsync` auf.
   - **Prüfkriterien:**
     - Bei Aufruf einer Stored Procedure werden Session-Variablen (`autheris.tenant_id`, `autheris.user_sid`, etc.) über den zentralen `IDbSessionContextInitializer` auf der Verbindung gesetzt.
   - **Ort:** `tests/Autheris.Tests.Unit/Procedures/ProcedureSessionContextInitializerTests.cs`.

4. **`GraphQL E2E SQLite` (`fms/air1` Latency & Correctness):**
   - **Fokus:** End-to-End-Test mit SQLite über `WebApplicationFactory`:
   - **Prüfkriterien:**
     - Dieselbe Zeilenergebnis-Menge wie bei OData und WebSQL.
     - Abnahme `fms/air1` unter 2 s Latenz.
   - **Ort:** `tests/Autheris.Tests.Integration/GraphQLAir1EndToEndTests.cs`.

---

## 2. Test-Spezifikationen & Ablauf

Jeder Test wird sauber aufgesetzt, verifiziert und gegen Regressionen abgesichert.

---

## 3. Umsetzungs- & Testergebnisse

Alle 4 Testfälle wurden erfolgreich implementiert und verifiziert (Stand 2026-10-08):

1. **`D-1 / R-SQL-3` (`PostgreSqlWebSqlTransactionTests.cs`):**
   - Gegen echten PostgreSQL 16 Alpine Testcontainer ausgeführt.
   - Initialisiert DB-Session mit GUC `set_config('autheris.tenant_id', ..., true)`.
   - Schließt `DbDataReader` vor Commit der Transaktion.
   - Status: ✅ Bestanden (1/1 Tests bestanden).

2. **`POL-15` (`PostgreSqlVirtualFilterContractTests.cs`):**
   - Methode: `POL_15_ActivateConsent_ForGroupAndServicePrincipal_PersistsGranteeSid`.
   - Testet Aktivierung für Gruppe (`GranteeType.Group`), Service-Principal (`GranteeType.ServicePrincipal`) und Rolle (`GranteeType.Role`).
   - Verifiziert, dass `grantee_sid` für Gruppen/Principals persistiert wird und für Rollen `role_name` gesetzt wird.
   - Status: ✅ Bestanden (4/4 Tests im Vertragstest bestanden).

3. **`R-SQL-6` (`ProcedureSessionContextInitializerTests.cs`):**
   - Verifiziert, dass `MssqlProcedureInvoker.InvokeProcedureAsync` vor Ausführung den `IDbSessionContextInitializer.InitializeSessionAsync` mit Tenant-ID, User-SID und Zweck aufruft.
   - Verifiziert Fail-Closed-Verhalten bei Fehlern der Session-Initialisierung.
   - Status: ✅ Bestanden (2/2 Tests bestanden).

4. **`GraphQL E2E SQLite` (`RowFilterChannelParityTests.cs`):**
   - Methode: `GraphQl_E2E_SQLite_Air1_ReturnsSameRowsAsODataAndWebSql_UnderTwoSeconds`.
   - Verifiziert `fms/air1` Parität zwischen GraphQL, OData und WebSQL Endpunkten bei voller RLS-Filterung.
   - Gemessene Ausführungszeit: weit unter den geforderten 2000 ms.
   - Status: ✅ Bestanden (13/13 Paritätstests bestanden).

---

## 4. Code & Security Review

### Code Quality (`csharp-code-reviewer`):
- **Idiomatisches C# 12 / .NET 10:** Verwendung von File-scoped Namespaces, Target-typed `new()`, Primären Konstruktoren und Pattern Matching.
- **Ressourcen-Hygiene:** Striktes Asynchrones Disposing (`await using`) aller SQL-Verbindungen, Commands und Readers sowie der Testcontainers-Instanzen via `IAsyncLifetime`.
- **Entkopplung:** Mocks via NSubstitute mit klaren Assertions auf Aufrufargumente.

### Security Quality (`csharp-security-expert`):
- **Fail-Closed bei Session-Initialisierung:** Wenn `InitializeSessionAsync` fehlschlägt, wird der Aufruf sofort abgebrochen; keine unauthorisierte Ausführung mit Default-Kontext.
- **SQL-Injection-Prävention:** Alle Parameter werden sauber typisiert und parameterisiert übergeben.
- **GUC-Isolation:** Session-Variablen in PostgreSQL werden mit `is_local = true` transaktionsgebunden gesetzt, sodass Verbindungs-Pooling im Gateway sicher vor Session-Bleed geschützt ist.
- **RBAC/ABAC Grantee Isolation (POL-15):** Exakte Trennung zwischen Security Identifiers (`grantee_sid`) und Rollennamen (`role_name`) im PostgreSQL Governance Schema.

