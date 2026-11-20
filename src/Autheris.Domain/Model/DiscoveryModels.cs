namespace Autheris.Domain.Model;

public enum DiscoveryObjectKind { Table, View, Procedure, MetricView }
public enum MetricViewColumnRole { Dimension, Measure }

public static class DiscoveryObjectKindExtensions
{
    public static string ToWireName(this DiscoveryObjectKind kind) => kind switch
    {
        DiscoveryObjectKind.Table => "table",
        DiscoveryObjectKind.View => "view",
        DiscoveryObjectKind.Procedure => "procedure",
        DiscoveryObjectKind.MetricView => "metric_view",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown DiscoveryObjectKind.")
    };

    public static string ToWireName(this MetricViewColumnRole role) => role switch
    {
        MetricViewColumnRole.Dimension => "dimension",
        MetricViewColumnRole.Measure => "measure",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown MetricViewColumnRole.")
    };
}
public enum DiscoveryAccessState { None, Requested, Granted, Expired }
public enum DiscoveryRowScope { Full, RowRestricted }
public enum DiscoveryColumnState { NoAccess, Masked, Clear }

public sealed record DiscoveryOwner(string DisplayName, string? Email);

public sealed record DiscoveryWriteState(
    bool CanInsert,
    bool CanUpdate,
    bool CanDelete,
    WriteRowScope? UpdateRowScope,
    IReadOnlyList<string> InsertColumns,
    IReadOnlyList<string> UpdateColumns,
    bool Requestable)
{
    public static readonly DiscoveryWriteState None = new(
        CanInsert: false,
        CanUpdate: false,
        CanDelete: false,
        UpdateRowScope: null,
        InsertColumns: [],
        UpdateColumns: [],
        Requestable: false);
}

public sealed record DiscoveryColumnClassification(string Code, string DisplayName);

/// <summary>A linked document returned with a discovery object; <paramref name="Trust"/> is <c>official</c> or <c>untrusted</c>.</summary>
public sealed record DiscoveryDocumentRef(
    string AppId, string Category, string DocId, string Title, string? Summary, bool Official, string Trust, DateTimeOffset UpdatedAt);

/// <summary>An application linked to a discovery object ("program based on this data").</summary>
public sealed record DiscoveryApplicationRef(string AppId, string? Name, int DocumentCount);

public sealed record DiscoveryColumn(
    string Name,
    string? DisplayName,
    string DataType,
    string? Description,
    bool Pii,
    bool ClearAccessRequiresSeparateApproval,
    DiscoveryColumnState State,
    DiscoveryColumnStatistics? Statistics,
    bool ClearInSomeRows = false,
    bool WriteProtected = false,
    IReadOnlyList<DiscoveryColumnClassification>? Classifications = null,
    IReadOnlyList<string>? ClearRequestable = null,
    string? LongDescription = null,
    MetricViewColumnRole? Role = null)
{
    public IReadOnlyList<DiscoveryColumnClassification> Classifications { get; init; } = Classifications ?? [];
    public IReadOnlyList<string> ClearRequestable { get; init; } = ClearRequestable ?? [];

    /// <summary>Documents linked to this column that the caller may see (at most 5, newest first).</summary>
    public IReadOnlyList<DiscoveryDocumentRef> Documents { get; init; } = [];
}

public sealed record DiscoveryObject(
    DiscoveryObjectKind Kind,
    string Domain,
    string Schema,
    string Name,
    string? DisplayName,
    string? Description,
    string Sensitivity,
    IReadOnlyList<string> Tags,
    IReadOnlyList<DiscoveryOwner> Owners,
    DiscoveryAccessState Access,
    DiscoveryRowScope? RowScope,
    IReadOnlyList<DiscoveryColumn> Columns,
    DiscoveryTableStatistics? Statistics,
    bool SamplesAvailable,
    bool IsHidden,
    DiscoveryWriteState? Write = null,
    DiscoveryQueryShape? QueryShape = null)
{
    /// <summary>Internal shaping input: names of Masked columns whose catalog text was withheld; never part of a client contract.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlySet<string> HiddenTextColumns { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Documents linked to this object that the caller may see (at most 10, newest first).</summary>
    public IReadOnlyList<DiscoveryDocumentRef> Documents { get; init; } = [];

    /// <summary>Applications linked to this object that the caller may see.</summary>
    public IReadOnlyList<DiscoveryApplicationRef> Applications { get; init; } = [];

    /// <summary>True when more documents were linked than the output cap allows.</summary>
    public bool DocumentsTruncated { get; init; }

    /// <summary>Number of visible documents linked to this object before the output cap.</summary>
    public int DocumentCount { get; init; }
}

/// <summary>
/// Issues #805/#807: the declared primary key (key order with directions), the declared indexes and the required filter columns of a
/// discovery object as this caller may see them. A key or an index is present only when every one of its columns is listed with state
/// <see cref="DiscoveryColumnState.Clear"/>; a required filter column only when it is listed with access, a group only when every
/// member is (never a partial key or group). Index names are never part of it.
/// </summary>
public sealed record DiscoveryQueryShape(
    IReadOnlyList<string> PrimaryKeyColumns,
    IReadOnlyList<KeyColumnDirection> PrimaryKeyDirections,
    IReadOnlyList<DatasetIndexInfo> Indexes,
    IReadOnlyList<string> RequiredFilterColumns,
    IReadOnlyList<IReadOnlyList<string>> RequiredFilterColumnGroups)
{
    /// <summary>Nothing visible: the fail-closed shape.</summary>
    public static DiscoveryQueryShape Empty { get; } = new([], [], [], [], []);
}

public sealed record DiscoveryTableStatistics(
    long? ApproxRowCount,
    long? SizeBytes,
    DateTimeOffset? LastUpdatedAt,
    IReadOnlyList<string>? PartitionColumns,
    DateTimeOffset UploadedAt);

public sealed record DiscoveryColumnStatistics(
    bool? Nullable,
    double? NullRatio,
    long? ApproxDistinctCount,
    int? MinLength,
    int? MaxLength,
    string? MinValue,
    string? MaxValue,
    IReadOnlyList<string>? TopValues);

public sealed record DiscoveryArtifacts(
    Guid TableId,
    string TenantId,
    string? SamplesJson,
    string? StatisticsJson,
    string ContentSha256,
    string UploadedBy,
    DateTimeOffset UploadedAt,
    int SchemaVersion = 1);
