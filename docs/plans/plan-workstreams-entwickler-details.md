# Entwickler-Leitfaden: Detaillierte Workstreams & TDD-Spezifikation (Plan 8)

**Dokument-ID:** `PLAN-DEV-DETAILS-08`  
**Referenzen:** [Hauptplan 8](plan-vollstaendige-daten-api-und-mcp-bereitstellung.md), [Feature Request R-54 bis R-66](2026-10-09-feature-request-admin-datenquellen-und-mcp.md), [Gesamtübersicht](00-gesamtplan-uebersicht.md)  
**Rolle:** C# & .NET Solution Architect  
**Zielgruppe:** Entwickler-Agents (dotnet-developer) für autonome, testgetriebene Umsetzung (TDD)  

---

## 1. Architektonische Leitplanken & Invarianten

1. **Clean Architecture Grenzen:**
   - `Autheris.Domain`: Nur POCOs, Records, Enums, Value Objects. **Null externe Abhängigkeiten.**
   - `Autheris.Application`: Use-Case-Services, Interfaces, Business Logik, ReBAC/ABAC-Aufrufe.
   - `Autheris.Infrastructure`: Persistenz, Externe HTTP-Clients, Redis/Cluster-State, Secret Vaults.
   - `Autheris.Api`: Minimal API Endpoints, ASP.NET Core Middleware, DI-Composition-Root Extensions.
2. **Qualitäts-Invarianten (Build & Style):**
   - **0 Build-Warnungen, 0 Fehler** (`/warnaserror` muss durchlaufen).
   - **Maximale Dateilänge:** Jede Quelldatei muss strikt $\le 800$ Zeilen bleiben.
   - **Keine Secrets in Logs, Responses oder Audits:** Geheimnisse werden niemals im Klartext serialisiert.
   - **Async all the way:** Keine blockierenden `.Result` oder `.GetAwaiter().GetResult()`. `CancellationToken` durchgängig durchreichen.
3. **Test-Driven Development (TDD) Mandat:**
   - Zuerst fehlschlagende Unit-Tests (Red) implementieren.
   - Minimale Implementierung schreiben, bis alle Tests grün sind (Green).
   - Refactoring unter Wahrung der Testabdeckung (Refactor).

---

## 2. Workstream A: Governed REST Data API & Virtuelle System-Tabellen

### 2.1 Ziel & Verantwortlichkeiten
Bereitstellung einer leichtgewichtigen, hochperformanten REST-Data-API (`/api/v1/data/*`) für beliebige Geschäfts- und interne System-Tabellen unter 100%iger Durchsetzung von ReBAC, Column-Masking und Row-Level-Security.

### 2.2 Domänen-Modelle & Verträge

```csharp
// Pfad: src/Autheris.Domain/Model/DataApiModels.cs
namespace Autheris.Domain.Model;

public sealed record DatasetQueryRequest(
    TableIdentifier Table,
    IReadOnlyList<string>? SelectColumns = null,
    string? FilterExpression = null,
    string? OrderBy = null,
    int Limit = 50,
    int Offset = 0);

public sealed record DatasetColumnInfo(
    string Name,
    string Type,
    bool Masked);

public sealed record DatasetQueryEnvelope(
    string Dataset,
    int Count,
    int Offset,
    int Limit,
    bool HasMore,
    IReadOnlyList<DatasetColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Data);
```

```csharp
// Pfad: src/Autheris.Application/Data/Interfaces/IGovernedDataQueryService.cs
namespace Autheris.Application.Data.Interfaces;

public interface IGovernedDataQueryService
{
    Task<DatasetQueryEnvelope> ExecuteQueryAsync(
        DatasetQueryRequest request,
        RequestContext context,
        CancellationToken ct = default);
}
```

### 2.3 Virtuelle System-Tabellen (`governance.system.*`)
In `ITableMetadataRepository` werden virtuelle System-Entitäten registriert, damit sie transparent über dieselbe REST-Data-API abfragbar sind:
1. `governance.system.datasources`: `id`, `name`, `domain`, `type`, `base_url`, `is_configured`, `status`, `created_at`, `updated_at`.
2. `governance.system.policies`: `id`, `table_id`, `column_name`, `sensitivity`, `masking_type`, `classification_tags`.
3. `governance.system.rebac_tuples`: `user`, `relation`, `object`.
4. `governance.system.virtual_filters`: `id`, `table_id`, `principal`, `filter_expression`, `valid_until`.
5. `governance.system.audit_trail`: `id`, `timestamp`, `actor_sid`, `channel`, `action`, `target`, `correlation_id`, `worm_signature`.

### 2.4 Endpunkte (`src/Autheris.Api/Endpoints/DatasetDataEndpoints.cs`)
- `GET /api/v1/data/{domain}/{schema}/{table}`
- `GET /api/v1/data/{datasetId}`
- `GET /api/v1/data/{domain}/{schema}/{table}/{id}`
- Streaming-Serialisierung via `Utf8JsonWriter` direkt in den Response-Body.

### 2.5 TDD Test-Spezifikation (`tests/Autheris.Tests.Unit/DataApi/GovernedDataQueryServiceTests.cs`)
- **Test 1:** `ExecuteQueryAsync_ValidRequest_AppliesMaskingAndLimits`:
  - Arrange: Mock-Tabelle mit maskierten Spalten (`email`) und Klartext (`id`).
  - Act: Abfrage mit `Limit = 10`.
  - Assert: Maskierte Spalte enthält maskierten Wert (`d***@...`), `Columns[1].Masked == true`.
- **Test 2:** `ExecuteQueryAsync_ForbiddenTable_ThrowsForbiddenException`:
  - Arrange: Aufrufer ohne Leserecht (`can_query` verweigert).
  - Act & Assert: Wirft `UnauthorizedAccessException` bzw. Result `403 Forbidden`.
- **Test 3:** `ExecuteQueryAsync_FilterOnMaskedColumn_RejectsWithBadRequest`:
  - Arrange: Filter auf maskierte Spalte (`filter=email eq 'secret'`).
  - Act & Assert: Wirft Validierungsfehler (Verhinderung von Oracle-Inference-Angriffen).

---

## 3. Workstream B: Catalog & Discovery API & Datasource Onboarding (R-54..58, R-61)

### 3.1 Ziel & Verantwortlichkeiten
Bereitstellung von Discovery- und Self-Service-APIs (`/api/v1/catalog/*`, `/api/v1/governance/*`) und vollständige Umsetzung von R-54 bis R-58 (Swagger/OpenAPI Ingestion mit Credentials im Vault, Paging-Metadaten, Inaktiv-Start und Principal-Resolution).

### 3.2 Domänen-Modelle & Verträge

```csharp
// Pfad: src/Autheris.Domain/Model/CatalogApiModels.cs
namespace Autheris.Domain.Model;

public sealed record CatalogDatasetSummary(
    string DatasetId,
    string Domain,
    string Schema,
    string Table,
    string SourceType,
    string Sensitivity,
    string? Description,
    bool IsActive);

public sealed record CatalogColumnDetail(
    string Name,
    string Type,
    string Sensitivity,
    string MaskingState,
    bool IsPrimaryKey,
    bool IsPiiIndicator);

public sealed record CatalogDatasetDetail(
    string DatasetId,
    string Domain,
    string Schema,
    string Table,
    IReadOnlyList<CatalogColumnDetail> Columns,
    IReadOnlyList<string> PrimaryKeys,
    string Sensitivity,
    bool IsActive);

public sealed record PrincipalResolutionItem(
    string Sid,
    string DisplayName,
    string PrincipalType,
    bool ExactMatch,
    IReadOnlyList<string> Groups);

public sealed record DatasourceRegistrationRequest(
    string Name,
    string Domain,
    string? SpecContent = null,
    string? SpecUrl = null,
    string? BaseUrl = null,
    DatasourceAuthDto? Auth = null,
    bool DryRun = false);
```

```csharp
// Pfad: src/Autheris.Application/Catalog/Interfaces/ICatalogDiscoveryService.cs
namespace Autheris.Application.Catalog.Interfaces;

public interface ICatalogDiscoveryService
{
    Task<IReadOnlyList<CatalogDatasetSummary>> ListDatasetsAsync(RequestContext context, CancellationToken ct = default);
    Task<CatalogDatasetDetail?> GetDatasetDetailAsync(TableIdentifier table, RequestContext context, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogDatasetSummary>> SearchCatalogAsync(string query, string? domain, RequestContext context, CancellationToken ct = default);
}

// Pfad: src/Autheris.Application/Catalog/Interfaces/IPrincipalResolverService.cs
namespace Autheris.Application.Catalog.Interfaces;

public interface IPrincipalResolverService
{
    Task<IReadOnlyList<PrincipalResolutionItem>> ResolvePrincipalAsync(string query, RequestContext context, CancellationToken ct = default);
}
```

### 3.3 TDD Test-Spezifikation (`tests/Autheris.Tests.Unit/Catalog/CatalogDiscoveryServiceTests.cs`)
- **Test 1:** `ListDatasetsAsync_FiltersUnpermittedDatasets`:
  - Aufrufer sieht nur Datensätze, für die ReBAC `can_query` gilt.
- **Test 2:** `ResolvePrincipalAsync_AmbiguousMatches_ReturnsAllSuggestions`:
  - Suche nach „philipp“ liefert Treffer mit `ExactMatch = false` bei Mehrdeutigkeit.
- **Test 3:** `RegisterDatasource_NeverLeaksSecretInResponseOrAudit`:
  - Anlegen einer Quelle speichert Secret im `IKeyVaultSecretProvider`. Die Antwort enthält nur `isConfigured: true`.

---

## 4. Workstream C: RFC 6238 TOTP 2FA Engine & Step-Up HitL Integration

### 4.1 Ziel & Verantwortlichkeiten
Bereitstellung eines universellen TOTP-2FA-Dienstes (RFC 6238) für Google Authenticator, Microsoft Authenticator und 1Password zur Absicherung von Human-in-the-Loop-Freigaben (ADR-05, R-62).

### 4.2 Domänen-Modelle & Verträge

```csharp
// Pfad: src/Autheris.Domain/Model/TotpModels.cs
namespace Autheris.Domain.Model;

public sealed record TotpEnrollmentResult(
    string SecretBase32,
    string QrCodeUri,
    string FormattedKey,
    IReadOnlyList<string> RecoveryCodes);

public sealed record TotpVerifyRequest(string TotpCode);

public sealed record TotpVerifyResult(
    bool IsValid,
    string? ErrorMessage = null);
```

```csharp
// Pfad: src/Autheris.Application/Security/Totp/Interfaces/ITotpVerificationService.cs
namespace Autheris.Application.Security.Totp.Interfaces;

public interface ITotpVerificationService
{
    TotpEnrollmentResult GenerateEnrollment(string userSid, string email, string issuer = "Autheris");
    string BuildQrCodeUri(string issuer, string user, string secretBase32);
    bool VerifyTotp(string secretBase32, string totpCode, int toleranceSteps = 1);
    Task<bool> VerifyAndConsumeTotpAsync(string userSid, string secretBase32, string totpCode, CancellationToken ct = default);
}
```

### 4.3 Replay-Schutz & Cluster-Synchronisation
- Jeder verbrauchte TOTP-Zeitstempel (`epochStep = unixTime / 30`) wird im verteilten Zustand (`IDistributedClusterStateProvider`) als `totp:consumed:{userSid}:{epochStep}` mit TTL von 90 Sekunden hinterlegt.
- Wiederholte Nutzung desselben Einmalcodes innerhalb des Toleranzfensters wird strikt abgewiesen (`ReplayDetectedException`).

### 4.4 Erweiterung HitL-Service & Endpunkte
- `IHitLStepUpApprovalService.ApproveStepUpRequestAsync(string approvalId, HitLApproverContext approver, string? totpCode, CancellationToken ct)`
- `POST /api/governance/hitl/tickets/{ticketId}/approve` nimmt `{ "totpCode": "..." }` entgegen.
- `POST /api/v1/governance/2fa/enroll` und `POST /api/v1/governance/2fa/verify-enrollment`.

### 4.5 TDD Test-Spezifikation (`tests/Autheris.Tests.Unit/Security/TotpVerificationServiceTests.cs`)
- **Test 1:** `GenerateEnrollment_GeneratesValidBase32AndUri`:
  - Prüft, dass Secret Base32-konform ist und URI `otpauth://totp/Autheris:...` matcht.
- **Test 2:** `VerifyTotp_ValidCode_ReturnsTrue`:
  - Erzeugt gültigen TOTP für aktuellen Zeitstempel $\rightarrow$ Verifikation erfolgreich.
- **Test 3:** `VerifyTotp_ExpiredCode_ReturnsFalse`:
  - Zeitverschiebung $> 60$ Sekunden $\rightarrow$ Verifikation schlägt fehl.
- **Test 4:** `VerifyAndConsumeTotpAsync_ReplayAttempt_RejectsSecondUse`:
  - Erster Aufruf `true`, zweiter Aufruf mit identischem Code `false` (Replay Prevention).

---

## 5. Workstream D: Hybrid MCP Tools, Resources & Prompts

### 5.1 Ziel & Verantwortlichkeiten
Bereitstellung aller Autheris-Funktionen über das Model Context Protocol (MCP) für LLMs und KI-Agenten: High-Level-Tools, universeller API-Dispatcher, Ressourcen und Prompts.

### 5.2 MCP Werkzeug-Definitionen (`McpDatasetTools.cs`)
1. `query_sql`: `{"query": string}` $\rightarrow$ führt Governed SQL aus.
2. `query_dataset`: `{"dataset": string, "select": string[], "filter": string, "limit": int, "offset": int}`
3. `search_catalog`: `{"query": string, "domain": string?}`
4. `get_my_permissions`: `{"dataset": string}`
5. `list_datasources`: `{"status": string?}`
6. `get_data_lineage`: `{"dataset": string}`
7. `describe_api`: `{"endpoint": string, "method": string?}`
8. `invoke_api`: `{"endpoint": string, "method": string, "parameters": object?, "body": object?}`

### 5.3 MCP Resources & Prompts (`GatewayMcpServer.cs`)
- Resources: `autheris://catalog/summary`, `autheris://catalog/datasets/{datasetId}/schema`, `autheris://catalog/datasources`, `autheris://governance/my-access`, `autheris://api/openapi.json`, `autheris://api/docs/endpoints`, `autheris://api/docs/mcp-tools`.
- Prompts: `explore_dataset`, `audit_access_compliance`.

### 5.4 TDD Test-Spezifikation (`tests/Autheris.Tests.Unit/Mcp/McpHybridToolsTests.cs`)
- **Test 1:** `InvokeTool_QuerySql_ExecutesUnderCallerContext`:
  - Mock ReBAC-Identity $\rightarrow$ Ergebnis enthält maskierte Spalten.
- **Test 2:** `InvokeTool_DescribeApi_ReturnsEndpointSchema`:
  - Abfrage für `/api/v1/data/{domain}/{table}` liefert Parameter-Dokumentation.
- **Test 3:** `GetResource_CatalogSummary_ReturnsMarkdownRepresentation`:
  - Liefert Markdown aller zugänglichen Datensätze.

---

## 6. Workstream E: Admin MCP Tools, Access Planning & Two-Phase Confirmation

### 6.1 Ziel & Verantwortlichkeiten
Umsetzung der administrativen Kontrollwerkzeuge (R-60 bis R-64) mit strikter Zwei-Phasen-Freigabe (Human-in-the-Loop) und 2FA TOTP Step-Up.

### 6.2 Domänen-Modelle & Workflow

```csharp
// Pfad: src/Autheris.Domain/Model/AccessPlanningModels.cs
namespace Autheris.Domain.Model;

public sealed record ColumnAccessGrant(
    string Column,
    string AccessLevel); // "clear", "mask", "deny"

public sealed record PrincipalAccessGrant(
    string Principal,
    IReadOnlyDictionary<string, string> Columns,
    string? RowFilter = null,
    DateTimeOffset? ValidUntil = null);

public sealed record AdminPlanAccessRequest(
    string DatasetId,
    IReadOnlyList<PrincipalAccessGrant> Grants,
    string Reason);

public sealed record AccessPlanDiffItem(
    string Principal,
    string Column,
    string BeforeState,
    string AfterState,
    bool IsPii);

public sealed record AdminPlanAccessResult(
    string PlanId,
    string DatasetId,
    IReadOnlyList<AccessPlanDiffItem> Diffs,
    IReadOnlyList<string> Warnings,
    DateTimeOffset ExpiresAt);

public sealed record AdminConfirmPlanRequest(
    string PlanId,
    string TotpCode);

public sealed record AdminConfirmPlanResult(
    string ConfirmationToken,
    DateTimeOffset ExpiresAt);

public sealed record AdminApplyAccessRequest(
    string PlanId,
    string ConfirmationToken);
```

### 6.3 Interaktionsfluss (Two-Phase Confirmation mit 2FA)
1. **Planung:** LLM ruft `admin_plan_access` auf. Autheris generiert Plan-Diff und Warnungen (z.B. personenbezogene Spalten) ohne Seiteneffekte.
2. **Bestätigung (HitL):** Administrator prüft Plan in Web-UI und sendet `POST /api/governance/plans/{planId}/confirm` mit `totpCode` (aus Microsoft Authenticator, Google Authenticator oder 1Password).
3. **Ausführung:** Autheris validiert TOTP, erzeugt HMAC-signiertes `confirmationToken`. Erst mit diesem Token kann `admin_apply_access` ausgeführt werden.
4. **WORM-Audit:** Mutation wird mit Akteur, PlanId, Kanal (MCP) und kryptografischer Signatur im Audit-Log gesichert.

### 6.4 TDD Test-Spezifikation (`tests/Autheris.Tests.Unit/Mcp/AdminMcpTwoPhaseTests.cs`)
- **Test 1:** `AdminPlanAccess_GeneratesDiffWithoutModifyingState`:
  - Prüft, dass keine ReBAC-Tupel oder Maskierungsregeln vorab geändert werden.
- **Test 2:** `AdminApplyAccess_WithoutConfirmationToken_FailsWithForbidden`:
  - Direkter Aufruf von `admin_apply_access` ohne Token wird mit 403 abgewiesen.
- **Test 3:** `AdminConfirmPlan_ValidTotp_IssuesConfirmationToken`:
  - Validiert TOTP und stellt kurzlebiges Token aus.
- **Test 4:** `AdminApplyAccess_ValidConfirmationToken_AppliesMutationAndAudits`:
  - Wendet Änderungen an und erzeugt WORM-Audit-Eintrag.

---

## 7. Parallelisierungs-Matrix für Entwickler-Agents

| Workstream | Fokus-Dateien (Schreibrechte) | Externe Abhängigkeiten | Parallel ausführbar mit |
|---|---|---|---|
| **Track A** (Data API) | `src/Autheris.Domain/Model/DataApiModels.cs`<br/>`src/Autheris.Application/Data/*`<br/>`src/Autheris.Api/Endpoints/DatasetDataEndpoints.cs`<br/>`tests/Autheris.Tests.Unit/DataApi/*` | `GovernedSqlExecutionService`<br/>`TableAccessPolicy` | Track B, Track C |
| **Track B** (Catalog & Discovery) | `src/Autheris.Domain/Model/CatalogApiModels.cs`<br/>`src/Autheris.Application/Catalog/*`<br/>`src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs`<br/>`tests/Autheris.Tests.Unit/Catalog/*` | `ITableMetadataRepository`<br/>`OpenApiIngestionService` | Track A, Track C |
| **Track C** (TOTP 2FA & HitL) | `src/Autheris.Domain/Model/TotpModels.cs`<br/>`src/Autheris.Application/Security/Totp/*`<br/>`src/Autheris.Application/Mcp/Services/HitLStepUpApprovalService.cs`<br/>`src/Autheris.Api/Endpoints/HitLEndpoints.cs`<br/>`tests/Autheris.Tests.Unit/Security/*` | `IDistributedClusterStateProvider` | Track A, Track B |
| **Track D** (Hybrid MCP Tools) | `src/Autheris.Application/Mcp/Tools/McpDatasetTools.cs`<br/>`src/Autheris.Application/Mcp/GatewayMcpServer.cs`<br/>`src/Autheris.Application/Mcp/Services/ApiDispatcherService.cs`<br/>`tests/Autheris.Tests.Unit/Mcp/*` | Output von Track A & B | Track E |
| **Track E** (Admin MCP & HitL 2FA) | `src/Autheris.Domain/Model/AccessPlanningModels.cs`<br/>`src/Autheris.Application/Governance/AccessPlanningService.cs`<br/>`src/Autheris.Application/Mcp/Tools/McpAdminTools.cs`<br/>`tests/Autheris.Tests.Unit/Mcp/AdminMcpTwoPhaseTests.cs` | Output von Track B & C | Track D |

---

## 8. Abnahme-Kriterien für alle Tracks

1. Jeder Track startet mit **Red Tests** (fehlschlagende Unit-Tests).
2. `dotnet build /warnaserror` kompiliert mit **0 Warnungen und 0 Fehlern**.
3. `dotnet test` führt alle neuen und bestehenden Tests erfolgreich aus (100% grün).
4. Keine Datei überschreitet die Grenze von **800 Zeilen**.
5. Git-Commits sind semantisch und modular je Track geschnitten.
