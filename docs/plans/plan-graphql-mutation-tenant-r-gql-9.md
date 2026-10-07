# Architecture Implementation Plan: GraphQL Mutation Tenant Resolution & ClusterAdmin Unification (R-GQL-9)

## 1. Problem Description (R-GQL-9)
In `MutationTypes.cs`, four mutations manage table access requests and consents:
1. `requestTableAccess` (lines 193–213)
2. `approveAccessRequest` (lines 383–403)
3. `rejectAccessRequest` (lines 552–572)
4. `revokeTableConsent` (lines 654–674)

Two critical bugs / regressions exist across these methods:
1. **ClusterAdmin regression on `requestTableAccess`**:
   - In `approveAccessRequest`, `rejectAccessRequest`, and `revokeTableConsent`, `!isCrossTenantAdmin` is checked before forbidding tenant mismatch.
   - In `requestTableAccess` (lines 204–210), `isCrossTenantAdmin` is not checked at all. A ClusterAdmin who selects a tenant via `X-Tenant-ID` header receives `FORBIDDEN` ("Mandantenübergreifender Zugriff verboten").
2. **Inverted tenant precedence (`Claim` overrides `SecurityPrincipalContext`)**:
   - In all four mutations, the active tenant is resolved as:
     `var tenantId = principalTenant != TenantId.LegacySingleTenant ? principalTenant : contextTenant;`
   - When a ClusterAdmin or authorized user specifies `X-Tenant-ID: tenantB`, the HTTP pipeline resolves and validates `SecurityPrincipalContext.TenantId = tenantB` (`contextTenant`).
   - However, the mutation code prefers `principalTenant` (from the token/claim) over `contextTenant`. The chosen tenant is silently discarded, and the claim tenant is used instead. When checking `req.TenantId != tenantId`, the operation fails or binds the consent to the wrong tenant.

---

## 2. Architectural Design

### 2.1 Unified Helper `ResolveMutationTenantId`
Introduce a centralized private static helper method in `MutationTypes`:
```csharp
private static TenantId ResolveMutationTenantId(
    IHttpContextAccessor? httpContextAccessor,
    ClaimsPrincipal? principal,
    bool isCrossTenantAdmin)
{
    var principalTenant = principal?.GetTenantId() ?? TenantId.LegacySingleTenant;
    var contextTenant = TenantId.LegacySingleTenant;

    if (httpContextAccessor?.HttpContext?.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var secObj) == true &&
        secObj is SecurityPrincipalContext secCtx)
    {
        contextTenant = secCtx.TenantId;
    }
    else if (httpContextAccessor?.HttpContext?.Items.TryGetValue("TenantId", out var tidObj) == true &&
             tidObj is TenantId tid)
    {
        contextTenant = tid;
    }

    if (principalTenant != TenantId.LegacySingleTenant &&
        contextTenant != TenantId.LegacySingleTenant &&
        principalTenant != contextTenant &&
        !isCrossTenantAdmin)
    {
        throw new GraphQLException(ErrorBuilder.New()
            .SetCode("FORBIDDEN")
            .SetMessage("Mandantenübergreifender Zugriff verboten: Token-Mandant stimmt nicht mit dem Mandanten des Verbindungskontexts überein.")
            .Build());
    }

    // SecurityPrincipalContext.TenantId is the canonical source of truth for the active request tenant;
    // fall back to the token principal tenant if no context tenant is established.
    return contextTenant != TenantId.LegacySingleTenant ? contextTenant : principalTenant;
}
```

### 2.2 Application in Mutation Handlers
Replace the duplicated blocks in:
- `requestTableAccess`
- `approveAccessRequest`
- `rejectAccessRequest`
- `revokeTableConsent`

Each handler uses `isCrossTenantAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal)` and calls `ResolveMutationTenantId`.

---

## 3. TDD Plan

1. **Unit Tests in `tests/Autheris.Tests.Unit/GraphQL/MutationTypesTests.cs` (or `CatalogGraphQlSchemaTests.cs`)**:
   - `RequestTableAccess_WithClusterAdminAndDifferentContextTenant_SucceedsAndUsesContextTenant`:
     Verifies a ClusterAdmin with token tenant `tenantA` and `SecurityPrincipalContext.TenantId = tenantB` can request table access, and the created `ConsentRequest.TenantId` is `tenantB`.
   - `ApproveAccessRequest_WithClusterAdminAndDifferentContextTenant_UsesContextTenant`:
     Verifies `approveAccessRequest` uses `SecurityPrincipalContext.TenantId`.
   - `RequestTableAccess_WithNonAdminAndTenantMismatch_ThrowsForbidden`:
     Verifies non-admin with mismatched context and principal tenant is rejected with `FORBIDDEN`.
2. **Execute tests to verify Red phase**.
3. **Apply the fix in `src/Autheris.GraphQL/Types/MutationTypes.cs`**.
4. **Execute tests to verify Green phase**.
5. **Run all unit, integration, and architecture tests**.
6. **Update status plan and commit**.
