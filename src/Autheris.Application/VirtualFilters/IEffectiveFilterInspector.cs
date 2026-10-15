namespace Autheris.Application.VirtualFilters;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>Raw query values of the effective-filters inspection (parsed and validated by the inspector).</summary>
public sealed record EffectiveFilterQuery(string? User, string? Groups, string? Roles, string? Tenant, string? Table, string? Page, string? PageSize);

public enum EffectiveFilterInspectionKind
{
    Forbidden,
    BadRequest,
    NotFound,
    SingleTable,
    Page
}

/// <summary>One line of the paged list mode.</summary>
public sealed record EffectiveFilterListItem(string Table, string Decision, IReadOnlyList<string> AppliedFilters, string? Reason);

public sealed record EffectiveFilterInspection
{
    public required EffectiveFilterInspectionKind Kind { get; init; }
    public string? Error { get; init; }
    public string? User { get; init; }
    public TableMetadata? Table { get; init; }
    public MandatoryFilterExplanation? Explanation { get; init; }
    public IReadOnlyList<EffectiveFilterListItem> Items { get; init; } = [];
    public int PageNo { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
}

/// <summary>WP-ARC-B-07: authorization, audit and resolution of the effective-filters inspection (phase 6 of the virtual filters).</summary>
public interface IEffectiveFilterInspector
{
    Task<EffectiveFilterInspection> InspectAsync(SecurityPrincipalContext caller, EffectiveFilterQuery query, CancellationToken ct);
}
