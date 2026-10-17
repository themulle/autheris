namespace Autheris.Application.Governance.Interfaces;

using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Issue #111 (PLAN-111 C-2): one edit of the admin-managed catalog settings of a dataset. A null member is "unchanged", never a reset;
/// an empty <see cref="ReplaceTags"/> list replaces the admin tags with none. <see cref="Visibility"/> is the requested name (Hidden, Summary, Full).
/// </summary>
public sealed record DatasetCatalogEdit(
    IReadOnlyList<string>? ReplaceTags,
    IReadOnlyList<string>? AddTags,
    IReadOnlyList<string>? RemoveTags,
    string? Visibility)
{
    /// <summary>A request body that could not be bound (missing or wrong shape); answered as <see cref="DatasetCatalogOutcome.InvalidRequest"/> after authorization.</summary>
    public bool Malformed { get; init; }
}

/// <summary>
/// Result classes of <see cref="IDatasetCatalogAdminService"/>. <see cref="NotFound"/> is identical for unknown, inactive and non-manageable datasets.
/// For reads, <see cref="Unchanged"/> means "found and returned".
/// </summary>
public enum DatasetCatalogOutcome
{
    Applied,
    Unchanged,
    Forbidden,
    NotFound,
    InvalidTag,
    TagLimitExceeded,
    InvalidVisibility,
    InvalidRequest,
    Unavailable
}

/// <summary>Outcome plus, on success, the resolved dataset, its settings after the edit and whether the tier is effective for it.</summary>
public sealed record DatasetCatalogResult(
    DatasetCatalogOutcome Outcome,
    TableIdentifier? Table = null,
    DatasetCatalogSettings? Settings = null,
    bool TierEffective = true)
{
    public bool IsSuccess => Outcome is DatasetCatalogOutcome.Applied or DatasetCatalogOutcome.Unchanged;
}

/// <summary>
/// The only writer of dataset tags and visibility tiers (REST, declarative manifest and the MCP admin tool all call it). It authorizes from the
/// authenticated principal through <c>CallerScopeResolver</c> and the ownership store, never from request fields (PLAN-111 A-5).
/// </summary>
public interface IDatasetCatalogAdminService
{
    /// <summary><paramref name="source"/> is <c>admin</c>, <c>manifest</c> or <c>mcp</c> (audit detail only).</summary>
    Task<DatasetCatalogResult> ApplyAsync(ClaimsPrincipal? caller, string datasetId, DatasetCatalogEdit edit, string source, CancellationToken ct);

    Task<DatasetCatalogResult> GetAsync(ClaimsPrincipal? caller, string datasetId, CancellationToken ct);
}
