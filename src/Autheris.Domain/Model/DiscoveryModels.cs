namespace Autheris.Domain.Model;

public enum DiscoveryObjectKind { Table, View, Procedure }
public enum DiscoveryAccessState { None, Requested, Granted, Expired }
public enum DiscoveryRowScope { Full, RowRestricted }
public enum DiscoveryColumnState { NoAccess, Masked, Clear }

public sealed record DiscoveryOwner(string DisplayName, string? Email);

public sealed record DiscoveryColumn(
    string Name,
    string? DisplayName,
    string DataType,
    string? Description,
    bool Pii,
    bool ClearAccessRequiresSeparateApproval,
    DiscoveryColumnState State,
    DiscoveryColumnStatistics? Statistics,
    bool ClearInSomeRows = false);

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
    bool IsHidden);

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
