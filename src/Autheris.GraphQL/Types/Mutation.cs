using System;
using System.Collections.Generic;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

namespace Autheris.GraphQL.Types;

public sealed class OpenMetadataSyncPayload
{
    public bool Success { get; init; }
    public int SyncedTables { get; init; }
    public int SyncedConsents { get; init; }
    public int SyncedMaskingRules { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class DataCatalogSyncPayload
{
    public bool Success { get; init; }
    public int SyncedTablesCount { get; init; }
    public int SyncedColumnsCount { get; init; }
    public int MaskedColumnsCount { get; init; }
    public int Art9ProtectedTablesCount { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class ConsentRequestPayload
{
    public Guid RequestId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Description { get; init; }
}

public sealed partial class Mutation
{
    private static Sid GetAuthenticatedUserSid(IHttpContextAccessor httpContextAccessor)
    {
        var httpContext = httpContextAccessor?.HttpContext;
        if (httpContext?.User?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required.")
                .Build());
        }

        var userSid = httpContext.User.GetUserSid();
        if (userSid == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("No valid user SID in the authentication token.")
                .Build());
        }

        return userSid.Value;
    }

    private static TenantId GetContextTenantId(IHttpContextAccessor? httpContextAccessor)
    {
        TenantId contextTenant = default; // DT-A-3: no sentinel; unset means "no context tenant" (ResolveTenantId falls back to the claim)
        if (httpContextAccessor?.HttpContext?.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var secObj) == true && secObj is SecurityPrincipalContext secCtx)
        {
            contextTenant = secCtx.ResolvedTenant ?? default;
        }
        else if (httpContextAccessor?.HttpContext?.Items.TryGetValue("TenantId", out var tidObj) == true && tidObj is TenantId tid)
        {
            contextTenant = tid;
        }

        return contextTenant;
    }
}
