# Implementation Plan: Remaining Low & Usability Findings

**Date:** 2026-10-08  
**Author:** Solution Architect  
**Scope:** Remediation of remaining open low and usability findings from `docs/plans/2026-10-08-befund-autheris.md` (Wunsch 11 and Wunsch 6).  
*(Note: Wunsch 7 [WebSQL truncated & LIMIT clamping] and Wunsch 12 [Declared queries response format] are already implemented and tested).*

---

## 1. Architectural Principles & Anti-Overengineering Review

Per `csharp-architect` principles:
- **Pragmatic Consistency**: Maintain backward compatibility while providing unified metadata contracts across endpoints.
- **Fail-Closed & Defense-in-Depth**: Ensure cost limits and query limits default safely when unconfigured.

---

## 2. Findings Analysis & Target Architecture

### 2.1 Wunsch 11 (Befund 3.7): Uniform Masking Forms Across All Paths
- **Current Problem**:
  - WebSQL and GraphQL in-SQL masking default to `'***'` for all non-HMAC masking rules (`GovernedSqlExecutionService.cs:1519`).
  - Stored procedures and memory projections mask via `ColumnMaskingProvider.cs:297-340`, producing `o***@***.local` for `MASK_EMAIL` and `DE89***1234` for `MASK_IBAN`.
  - Clients observe different masked values depending on whether a query runs via WebSQL or via procedures.
- **Architectural Solution**:
  - Extend `GetMaskExpressionForRule` in `GovernedSqlExecutionService.cs` to generate SQL expressions matching `ColumnMaskingProvider` algorithms:
    - `NULLIFY` -> `NULL`
    - `MASK_EMAIL` -> Dialect-aware SQL string expression (e.g. `SUBSTRING(col, 1, 1) || '***@***.' || ...` or canonical `'***@***'`) or unified standard mask.
    - `MASK_IBAN` -> Dialect-aware SQL string expression keeping country code + last 4 digits (e.g. `SUBSTRING(col, 1, 4) || '***' || SUBSTRING(col, LENGTH(col)-3, 4)`).
    - `REDACT` -> Custom replacement string or `'[REDACTED]'`.
    - `HMAC` -> Keyed HMAC expression or `'[PSEUDONYMIZED]'`.

### 2.2 Wunsch 6: Configurable GraphQL Cost Limits & API Key Configuration
- **Current Problem**:
  - Tier policies in `ClientTierModels.cs:19-26` are hardcoded constants (Standard = 250 cost limit).
  - `ClientTierResolver.RegisterApiKey` exists but cannot be configured via options/appsettings.
- **Architectural Solution**:
  - In `GraphQLOptions`, add optional configurable `MaxAllowedCostPerQuery` and role-to-tier mappings (`RoleTierMappings: { "ClusterAdmin": "Internal", "DataOwner": "Enterprise", ... }`).
  - Add `ApiKeys` configuration section (`Gateway:Authentication:ApiKeys`) allowing static API key registration with assigned tiers (`Free`, `Standard`, `Enterprise`, `Internal`).
  - In `ClientTierResolver.ResolveAsync`, check user roles against configured role-to-tier mappings and inspect configured static API keys.

---

## 3. Step-by-Step Implementation Sequence

1. **Phase 1: Uniform SQL Masking Expressions (Wunsch 11)**
   - Enhance `GetMaskExpressionForRule` in `GovernedSqlExecutionService.cs` with SQL expressions for `MASK_EMAIL` and `MASK_IBAN` matching `ColumnMaskingProvider`.
   - Add unit tests verifying uniform output.

2. **Phase 2: GraphQL Cost Limits & API Key Configuration (Wunsch 6)**
   - Add `Gateway:Authentication:ApiKeys` options and role-tier mapping.
   - Wire static API keys into `ClientTierResolver`.
   - Add unit tests verifying role-based tier assignment and configured API key resolution.

3. **Phase 3: Verification & Documentation**
   - Run complete test suite (`dotnet test`).
   - Update `docs/plans/2026-10-08-befund-autheris.md`.
