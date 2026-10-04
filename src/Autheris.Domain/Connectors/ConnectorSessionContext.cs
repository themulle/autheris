using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

namespace Autheris.Domain.Connectors;

public sealed record ConnectorSessionContext(
    ClaimsPrincipal Principal,
    TenantId? Tenant,
    TableAccessDecision AccessDecision,
    IReadOnlyList<string> ProjectedColumns,
    IReadOnlyDictionary<string, object?> Arguments,
    string? PushdownFilterSql = null,
    int? Limit = null,
    int? Offset = null,
    IReadOnlyDictionary<string, string[]>? RequestHeaders = null,
    IDictionary<string, object?>? Items = null)
{
    public IDictionary<string, object?> Items { get; init; } = Items ?? new Dictionary<string, object?>();
}
