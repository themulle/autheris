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
    string? LongDescription = null)
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
    DiscoveryWriteState? Write = null)
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
