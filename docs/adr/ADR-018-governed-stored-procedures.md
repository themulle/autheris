# ADR-018: Stored Procedures as Governed Endpoints with Database-Side RLS via SESSION_CONTEXT

**Date:** 2026-10-05 (Updated: 2026-11-24)
**References:** [F-SQL-02](../features/f-sql-02-governed-stored-procedures.md), [ADR-001](ADR-001-trusted-subsystem.md)

## Context

The gateway cannot rewrite the body of a stored procedure, so AST-based row-level security (RLS) and masking injection (F-DATA-02 / F-SQL-01) do not apply inside procedure bodies. [ADR-001](ADR-001-trusted-subsystem.md) defines a read-only (`SELECT`) service principal for table/view queries.

## Decision

1. **Separate Execute-Only Service Principal:** Stored procedure endpoints use a dedicated technical connection (`SqlEndpoints:Procedures:ConnectionName`, or an explicitly admitted entry in `SqlEndpoints:Procedures:AllowedDataSources`) granted **exclusively `EXECUTE`** on approved schemas (`SqlEndpoints:Procedures:AllowedSchemas`). It holds no `SELECT`, no table DML, and no `VIEW DEFINITION` / object-definition read privileges. Access to underlying tables occurs solely through database ownership chaining inside the procedure.
2. **Database-Side Row-Level Security:** Before each invocation, the gateway writes tenant, user SID, and purpose into read-only `SESSION_CONTEXT` (`autheris.tenant_id`, `autheris.user_sid`, `autheris.purpose`, `@read_only = 1` on SQL Server; transaction-local `set_config(..., true)` on PostgreSQL). Database `SECURITY POLICY` filter predicates evaluate these context values inside the engine, supplemented by optional gateway-side secondary `row_scope_key` filtering.
3. **Fail-Closed Registration via Declared-Only Governance (Catalog-Mode Validator Retired):**
   - **Retirement of `StoredProcedureCatalogValidator`:** The originally drafted `validation: catalog` check (`StoredProcedureCatalogValidator`, which proposed querying `sys.sql_modules`, `sys.dm_sql_referenced_entities`, `sys.dm_exec_describe_first_result_set_for_object`, `sys.parameters`, `sys.columns`, and `SECURITY POLICY` definitions from the source database) was **never built and is permanently retired**. Reading database object definitions or module dependencies at runtime directly contradicts Decision 1's execute-only, zero-definition-read principle. The legacy `AllowDeclaredValidation` configuration toggle has also been removed; declared-only governance is always active.
   - **Shipped Declared-Only Contract Validation:** Procedure declarations (`*.proc.yaml` / `*.proc.yml`; legacy `*.proc.sql` header files are rejected at startup) are loaded by `ProcedureDefinitionLoader` and activated by `ProcedureRegistrationService.ValidateDueAsync` at startup, hot-reload rescan, and periodic revalidation intervals:
     - Enforces `SqlEndpoints:Procedures:AllowedSchemas`, rejects `sys.*`, `INFORMATION_SCHEMA.*`, `sp_*`, `xp_*`, and three-part object names, and checks data-source/dialect admission (`ProcedureConnectionProvider.CheckAdmission`).
     - Enforces `Gateway:SqlEndpoints:Procedures:RequireDdlHashForDeclared` (`null` resolves to required in every environment except `Development`): declarations missing `integrity.ddl_hash` are rejected fail-closed when required.
     - Requires non-empty self-declared `result_table`, `outputs`, and `referenced_tables` (or `tables`), verifying that every declared table is registered with columns in the local Autheris catalog (`TABLE_NOT_REGISTERED` keeps the endpoint in `Pending`, returning `503`).
     - Enforces `ROW_SCOPE_REQUIRED` when active virtual row filters exist for any declared table under `rls: session-context` without a `row_scope_key`, disabling the endpoint (`503`) and recording a `PROCEDURE_DISABLED` audit event.
   - **Authoring & Pipeline Responsibility:** Because the runtime gateway cannot inspect procedure bodies, parameter signatures, result shapes, or database `SECURITY POLICY` definitions under the execute-only boundary, Autheris **never** verifies parameter signature parity, result-set structure, or completeness of `referenced_tables` against the database at runtime. Ensuring complete `referenced_tables`, accurate `outputs` / column provenance (`source_table` / `source_column`), active database `SECURITY POLICY` predicates, and current `integrity.ddl_hash` values is the responsibility of the procedure author and release/authoring pipeline (which operate with separate authoring/DBA credentials). Teams can supply machine-derived `*.proc.yaml` contracts generated from real build/lineage tooling via Git-based data product sync (`POST /api/v1/products/sync/plan` and `POST /api/v1/products/sync/apply`).
4. **Gateway Column Governance on Result Sets:** On returned result sets, consent `Deny` removes the column, `Mask` masks the value, and undeclared columns are pruned (except synthetic/computed columns declared in `cleared_columns`). A masked source column remains masked even when listed in `cleared_columns` (R-SQL-10 / SQL2-1; see the 2026-10-08 addendum below).
5. **Consent Row-Filter Handling:** Direct consent row filters cannot be pushed into an opaque stored procedure body and in-memory filtering diverges from SQL semantics. Procedures requiring gateway-side row-scope narrowing use `row_scope_key` against the result set, and active virtual row filters without `row_scope_key` are rejected fail-closed (`ROW_SCOPE_REQUIRED`).
6. **Synchronous Fail-Closed Auditing:** Calls audit `PROCEDURE_EXECUTE`, `PROCEDURE_DENIED`, `PROCEDURE_REJECTED`, and `PROCEDURE_FAILED`. Parameter values are logged only as truncated SHA-256 hashes.
7. **Typed Stored Procedure Invocation:** Calls run strictly via `CommandType.StoredProcedure` with typed parameters (or positional parameterized calls for TVFs). Context-bound parameters (`context:` bindings) cannot be supplied by clients and are rejected with `400` like any unknown parameter.

## Consequences

- (+) Row, aggregate, and join filtering inside procedures are enforced by database `SECURITY POLICY` predicates without exposing table `SELECT` or `VIEW DEFINITION` privileges to the procedure login.
- (+) The execute-only principal cannot issue ad-hoc SQL or inspect procedure source definitions.
- (+) Connection pooling remains safe because session context is bound per invocation (`sp_reset_connection` on SQL Server; uncommitted or session-scoped transaction handling on PostgreSQL).
- (−) DBAs must maintain database `SECURITY POLICY` predicates alongside procedures.
- (−) Authoring and CI/release pipelines (not the runtime gateway) must guarantee `referenced_tables` completeness, `outputs` accuracy, and `integrity.ddl_hash` integrity.
- (−) Nested calls or `EXECUTE AS` inside procedures remain an authoring/DBA review responsibility.

## Addendum 2026-10-05: YAML Contracts, TVFs, and Supported Databases

Following the 2026-10-05 security review (P-1 … P-8), the YAML contract and execution rules enforce:

- **Declared Contract Completeness:** Every `*.proc.yaml` contract requires `result_table`, a non-empty `outputs` list, and a non-empty `tables` / `referenced_tables` list. Result columns run through the central consent and masking decision point (`IAccessDecisionPoint`) for `result_table` and referenced tables; missing consent denies the call.
- **Typed `outputs` (YAML):** Output entries may be plain column names, `{name, type, source / source_table / source_column}` mappings, or `<name>: <type>` pairs. Types populate `type`, `format`, and `x-sql-type` in `/api/v1/procedures/openapi.json` for contract documentation and are neither checked nor coerced at runtime (`int`, `bigint`, `smallint`, `tinyint`, `bit`, `decimal`, `numeric`, `float`, `real`, `money`, `smallmoney`, `nvarchar`, `varchar`, `nchar`, `char`, `uniqueidentifier`, `date`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset`, `time`).
- **Table-Valued Functions (TVFs):** Invoked positionally (`kind: tvf`). Arguments bind in declared file order, so `context:` bindings must be declared inline inside `parameters` in the exact function signature order.
- **Database Engines:** With `rls: session-context`, only SQL Server (read-only `SESSION_CONTEXT`) and PostgreSQL (transaction-local `set_config(..., true)` inside an uncommitted read transaction or transaction-scoped write wrapper) are permitted. PostgreSQL has no read-only session setting, so procedure bodies must not overwrite those settings (DBA review). Oracle, Databricks, and SQLite are permitted only with `rls: none`, which is restricted to `Development`.
- **Fail-Closed YAML Parsing & Hot Reload:** Unknown keys, duplicate mapping keys, unknown `context:` values, reserved `limit` parameter names, and invalid identifiers raise `FormatException` and skip the file without terminating the host. Renaming or deleting files unregisters removed endpoints; duplicate endpoint names across files are rejected; symbolic links / reparse points are never followed; activation and deactivation use compare-and-swap on the exact validated `ProcedureDefinition` instance.

## Addendum 2026-10-08: Stored Procedure Result Column Masking and `clear` Semantics (R-SQL-10 / SQL2-1)

Following the security review rechecks and remediation of SQL governance gaps (R-SQL-10 / SQL2-1), the column governance of stored procedure results enforces the following fail-closed rules:

1. **Masked Columns Remain Masked Despite `cleared_columns` (`clear` Declaration)**:
   - When a result column is mapped to an underlying table column (via declared `ResultColumnSource` provenance or `result_table` metadata) that is subject to data catalog masking or consent policy masking, listing the column in `cleared_columns` **does not unmask** explicit consent masking.
   - Only catalog-sensitivity-derived masks without an overriding explicit policy mask can be lifted via `cleared_columns` for genuine synthetic, aggregated, or calculated columns.
2. **Provenance Verification (Bypass Prevention)**:
   - The gateway inspects `ResultColumnSource` metadata when evaluating result columns.
   - If source provenance for a declared `clear` column is missing or unresolvable so the gateway cannot verify that it does not originate from a governed masked column, the column is **removed / redacted** fail-closed (`continue` in governance planning).

## Write Procedures (`mode: write`)

Write stored procedures (`kind: procedure` only; TVFs are excluded) require an explicit data-product binding (`schema` and `owner_group_sid` or `product_owner`) and are authorized against `AccessOperation.ExecuteWrite` with optional declared business `error_codes` (`number` 50000–59999 or five-character `sqlstate`).
