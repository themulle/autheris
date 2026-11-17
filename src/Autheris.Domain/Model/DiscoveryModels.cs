namespace Autheris.Domain.Model;

public enum DiscoveryObjectKind { Table, View, Procedure }
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
    string? LongDescription = null)
{
    public IReadOnlyList<DiscoveryColumnClassification> Classifications { get; init; } = Classifications ?? [];
    public IReadOnlyList<string> ClearRequestable { get; init; } = ClearRequestable ?? [];
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
