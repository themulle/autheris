using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using HotChocolate;
using Microsoft.AspNetCore.Http;

namespace Autheris.GraphQL.Types;

public sealed class TableRecordPayload
{
    public string TableName { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public IReadOnlyList<string> JsonRows { get; init; } = [];
}

public sealed class TableMetadataDto
{
    public string Domain { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string TableName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Sensitivity { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? AccessStatus { get; init; }
    public string? DataOwner { get; init; }
    public IReadOnlyList<string>? Delegates { get; init; }
    public string? Language { get; init; }
}

public sealed partial class Query
{
    /// <summary>The item key written by <c>DomainScopeRewriteMiddleware</c> (kept equal by a unit test; the GraphQL project cannot reference the API project).</summary>
    private const string DomainScopeItemKey = "Autheris.DomainScope";

    /// <summary>#341: table arguments are caller text; a blank or malformed component is a client error (never an existence signal).</summary>
    private static GraphQLException InvalidTableArgument() => new(ErrorBuilder.New()
        .SetCode("BAD_REQUEST")
        .SetMessage("The table identifier is invalid.")
        .Build());

    /// <summary>
    /// The caller context of an authenticated principal. An unauthenticated or SID-less caller is refused first (UNAUTHORIZED with
    /// <paramref name="authenticationRequiredMessage"/>), then a caller without a valid tenant (never a default tenant, DT-A-3).
    /// </summary>
    private static CallerSecurityContext GetCallerSecurityContext(IHttpContextAccessor httpContextAccessor, string authenticationRequiredMessage)
    {
        var httpContext = httpContextAccessor?.HttpContext;
        var principal = httpContext?.User ?? new ClaimsPrincipal();
        if (principal.Identity?.IsAuthenticated != true || principal.GetUserSid() is not { } userSid || userSid.Value == "S-1-5-21-ANONYMOUS")
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage(authenticationRequiredMessage)
                .Build());
        }

        var groupSids = principal.GetGroupSids().ToList();
        var roles = principal.GetUserRoles().ToList();

        TenantId tenantId;
        if (httpContext?.Items.TryGetValue("TenantId", out var tidObj) == true && tidObj is TenantId tid)
        {
            tenantId = tid;
        }
        else
        {
            try
            {
                tenantId = principal.GetTenantId();
            }
            catch (Autheris.Domain.Exceptions.GatewaySecurityException)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("UNAUTHORIZED")
                    .SetMessage("No valid tenant in the authentication token.")
                    .Build());
            }
        }

        bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
        bool isClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);

        return new CallerSecurityContext(
            userSid,
            groupSids,
            roles,
            tenantId,
            isGovAdmin,
            isClusterAdmin);
    }
}
