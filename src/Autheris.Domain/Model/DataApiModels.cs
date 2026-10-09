namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;

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

public sealed record RequestContext(
    ClaimsPrincipal User,
    TenantId TenantId,
    string? ClientIp = null)
{
    public RequestContext() : this(new ClaimsPrincipal(new ClaimsIdentity()), TenantId.LegacySingleTenant) { }

    public static RequestContext FromUser(ClaimsPrincipal user, TenantId? tenantId = null, string? clientIp = null) =>
        new(user, tenantId ?? TenantId.LegacySingleTenant, clientIp);
}
