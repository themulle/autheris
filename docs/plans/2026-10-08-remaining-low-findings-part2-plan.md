# Architectural Implementation Plan: Remaining Low & Usability Findings (Part 2)

**Target Branch:** `feat/ast-target-dialect-generator`  
**Author:** Solution Architect (`csharp-architect`)  
**Methodology:** Strict Test-Driven Development (TDD: Red $\rightarrow$ Green $\rightarrow$ Refactor)  
**Compatibility Guardrail:** Trino SQL engine compatibility preserved; extensions well-isolated.

---

## 1. Executive Summary & Scope

Following the successful resolution of Wünsche 1, 2, 6, 7, 9, 10, 11, 12 (Part 1), and 13, this architectural plan targets the remaining findings from [`docs/plans/2026-10-08-befund-autheris.md`](2026-10-08-befund-autheris.md) and related security reviews:

1. **Wunsch 8 (Befund 3.6) — Prohibit WHERE / HAVING Filtering on Denied/Masked Columns in WebSQL**:
   - *Problem*: In WebSQL, denied/masked columns in `WHERE` or `HAVING` clauses are replaced by `NULL` or `'***'`. Text comparisons silently return 0 rows; numeric/date comparisons fail with database 500 type conversion errors. In contrast, GraphQL explicitly rejects queries filtering on non-accessible columns with a clean 400/403.
   - *Solution*: Extract filter column references in `SqlQueryAnalyzer` and validate them in `GovernedSqlExecutionService`. Reject queries filtering on `Deny` or statically masked columns with `WebSqlPolicyException` (HTTP 403 Forbidden and a diagnostic message).
2. **Wunsch 12 Part 2 — Dynamic Declared Query Registration via API (`POST /api/v1/queries`)**:
   - *Problem*: Declared queries can currently only be ingested from directory files via `SqlEndpointLoader`. Frontends and automated agents require an authenticated administrative API to register or update declared queries at runtime.
   - *Solution*: Add `POST /api/v1/queries` endpoint in `SqlEndpointRoutes.cs` (restricted to `IsListAdmin`), parsing SQL/metadata and registering into `ISqlEndpointRegistry`.
3. **Wunsch 3 — WebSQL Data Source to Connection Mapping (`WebSqlOptions.DataSourceMappings`)**:
   - *Problem*: Catalog table queries use domain names (e.g. `finance.public.invoices` or `crm.dbo.orders`), but physical connection pools in `DataSources.Connections` may be named differently (e.g. `governancedb`, `crmdb`). Without alias mapping, queries fail with missing connection errors.
   - *Solution*: Add `DataSourceMappings` dictionary in `WebSqlOptions`, resolving catalog data source names to configured connection names.
4. **Hardening & Usability (R-SQL-7 & Error Handling)**:
   - *Problem*: Database rollback in exception handlers must never mask the original exception, and unhandled transaction disposals must remain strictly inside `finally` blocks.
   - *Solution*: Audit and enforce try/catch/finally safety around all transaction commits and rollbacks.

---

## 2. Architectural Design & Pragmatic Decisions

### Anti-Overengineering & Principles
- **Leverage Existing ANTLR AST Visitor**: Extend `SqlQueryAnalyzer` to capture WHERE/HAVING column identifiers during AST analysis rather than adding separate parsing steps or regexes.
- **Fail-Closed by Default**: If a column has `ColumnAccessLevel.Deny` or static redaction (non-HMAC), attempting to filter on it must be prohibited before the query reaches the physical database.
- **No Over-Abstraction**: Use existing ASP.NET Core Minimal API conventions in `SqlEndpointRoutes` for the new `POST` endpoint; reuse `SqlEndpointLoader.ParseSqlContent` logic.

---

## 3. Phased Implementation Plan

### Phase 1: Wunsch 8 — Guardrail Against Filtering on Denied / Statically Masked Columns
- **Red Phase**:
  - Create `tests/Autheris.Tests.Unit/WebSqlFilterGuardrailTests.cs`.
  - Test: Query with `WHERE denied_col = 1` throws `WebSqlPolicyException` with message identifying the column.
  - Test: Query with `WHERE masked_col > 100` (static redaction) throws `WebSqlPolicyException`.
  - Test: Query with `WHERE hmac_col = 'xyz'` (deterministic HMAC) is permitted.
  - Test: Query with `WHERE clear_col = 'test'` is permitted.
- **Green Phase**:
  - Update `src/TrinoSqlEngine/Analysis/ISqlQueryAnalyzer.cs` to add `IReadOnlyList<JoinColumnReference>? FilterColumnReferences` to `SqlQueryMetadata`.
  - In `src/TrinoSqlEngine/Analysis/SqlQueryAnalyzer.cs`, override `EnterWhereClause` / inspect `querySpecification.where` to extract referenced column identifiers into `_filterColumnReferences`.
  - In `src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs`, validate `metadata.FilterColumnReferences` against `lvl == ColumnAccessLevel.Deny` or non-HMAC `ColumnAccessLevel.Mask`, throwing `WebSqlPolicyException`.
- **Refactor Phase**:
  - Ensure zero allocations on queries without WHERE clauses.

---

### Phase 2: Wunsch 3 — WebSQL Data Source to Connection Mapping
- **Red Phase**:
  - Create `tests/Autheris.Tests.Unit/WebSqlDataSourceMappingTests.cs`.
  - Test: When `WebSqlOptions.DataSourceMappings` maps `"finance"` to `"governancedb"`, query for table `finance.public.invoices` resolves connection `"governancedb"`.
  - Test: Unmapped data sources use their original name.
- **Green Phase**:
  - Add `public Dictionary<string, string> DataSourceMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);` to `src/Autheris.Domain/Options/GatewayOptions.cs` (`WebSqlOptions`).
  - Update `src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs` when resolving `connOptions` from `_options.Value.DataSources?.Connections` to check `DataSourceMappings`.
- **Refactor Phase**:
  - Validate case-insensitivity.

---

### Phase 3: Wunsch 12 (Part 2) — Dynamic Declared Query Registration API
- **Red Phase**:
  - Create `tests/Autheris.Tests.Unit/SqlEndpointDynamicRegistrationTests.cs`.
  - Test: `POST /api/v1/queries` with JSON `{ "name": "activeUsers", "sql": "-- @summary: Active users\nSELECT id FROM users" }` registers query into `ISqlEndpointRegistry`.
  - Test: Subsequent `GET /api/v1/queries/activeUsers` executes the registered query.
  - Test: Non-admin caller receives HTTP 403 Forbidden.
- **Green Phase**:
  - In `src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs`, add `group.MapPost("/", HandleRegisterEndpoint).RequireAuthorization();`.
  - Verify caller is admin via `IsListAdmin(context.User)`.
  - Parse request body DTO and call `registry.Register(endpointDefinition)`.
- **Refactor Phase**:
  - Return HTTP 201 Created with `Location: /api/v1/queries/{name}`.

---

### Phase 4: Full Verification & Status Documentation
- Run full test suite (`dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`).
- Update [`docs/plans/2026-10-08-befund-autheris.md`](2026-10-08-befund-autheris.md).
- Clean git commit.
