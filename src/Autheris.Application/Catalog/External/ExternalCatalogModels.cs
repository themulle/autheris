namespace Autheris.Application.Catalog.External;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed record CatalogObjectVersionRow(
    string ObjectId,
    string ContentHash,
    long Seq,
    bool Removed);

public sealed record CatalogVersionUpsert(
    string ObjectId,
    string ContentHash,
    bool Removed);

public sealed record CatalogChangeRecord(
    string ObjectId,
    string Kind,
    long Seq,
    DateTimeOffset ChangedAt);

public sealed record CatalogChangesFeedResult(
    IReadOnlyList<CatalogChangeRecord> Changes,
    long NextSince);

/// <summary>
/// Persistence contract for tenant-partitioned catalog object version hashes and monotonic change feed rows.
/// </summary>
public interface ICatalogChangeStore
{
    Task<IReadOnlyDictionary<string, CatalogObjectVersionRow>> GetObjectVersionsAsync(
        TenantId tenantId,
        CancellationToken ct = default);

    Task<long> ApplyVersionChangesAsync(
        TenantId tenantId,
        IReadOnlyList<CatalogVersionUpsert> upserts,
        DateTimeOffset changedAt,
        CancellationToken ct = default);

    Task<CatalogChangesFeedResult> ReadCatalogChangesAsync(
        TenantId tenantId,
        long sinceSeq,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<TenantId>> ListKnownTenantIdsAsync(CancellationToken ct = default);
}

/// <summary>
/// Coalescing in-process signal waking <see cref="CatalogChangeIndexerHostedService"/> when catalog metadata changes.
/// </summary>
public interface ICatalogChangedSignal
{
    void Signal();
    ValueTask WaitAsync(CancellationToken ct = default);
}

public sealed class CatalogChangedSignal : ICatalogChangedSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Signal() => _channel.Writer.TryWrite(true);

    public async ValueTask WaitAsync(CancellationToken ct = default)
    {
        await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
        while (_channel.Reader.TryRead(out _))
        {
        }
    }
}

public sealed record ExternalCatalogListQuery(
    string? Kind = null,
    string? Domain = null,
    string? Schema = null,
    string? Owner = null,
    string? Tag = null,
    string? Sensitivity = null,
    bool? Pii = null,
    long? ChangedSince = null,
    string? Cursor = null,
    int PageSize = 50);

public sealed record ExternalCatalogOwnerDto(
    string DisplayName,
    string? Email);

public sealed record ExternalCatalogCallerStateDto(
    string Access,
    string? RowScope,
    DiscoveryWriteState? Write);

public sealed record ExternalCatalogColumnDto(
    string Name,
    string? DisplayName,
    string DataType,
    string? Description,
    IReadOnlyList<string> Tags,
    string Sensitivity,
    bool Pii,
    string DefaultMaskingState,
    bool ColumnSecurity,
    bool Hidden,
    bool WriteProtected,
    string? AccessState = null,
    bool ClearInSomeRows = false,
    DiscoveryColumnStatistics? Statistics = null,
    IReadOnlyList<DiscoveryColumnClassification>? Classifications = null,
    IReadOnlyList<string>? ClearRequestable = null);

public sealed record ExternalCatalogObjectDto(
    string ObjectId,
    string Kind,
    string Domain,
    string Schema,
    string Name,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string> Tags,
    string Sensitivity,
    IReadOnlyList<ExternalCatalogOwnerDto> Owners,
    IReadOnlyList<string> LinkedDocuments,
    DiscoveryWriteState Write,
    bool StatisticsAvailable,
    DiscoveryTableStatistics? Statistics,
    bool SamplesAvailable,
    bool Hidden,
    ExternalCatalogCallerStateDto? CallerState,
    IReadOnlyList<ExternalCatalogColumnDto> Columns,
    long? ChangeSeq);

public sealed record ExternalCatalogPageDto(
    IReadOnlyList<ExternalCatalogObjectDto> Items,
    string? NextCursor,
    int TotalCount);

public sealed record ExternalCatalogChangeDto(
    string ObjectId,
    string Kind,
    long Seq,
    DateTimeOffset ChangedAt);

public sealed record ExternalCatalogChangesResponseDto(
    IReadOnlyList<ExternalCatalogChangeDto> Changes,
    long NextSince);

public sealed record ExternalCatalogError(
    int StatusCode,
    string Code,
    string Detail);

public sealed record ExternalCatalogResult<T>(
    T? Value,
    ExternalCatalogError? Error)
{
    public bool IsSuccess => Error is null && Value is not null;
}

public static class ExternalCatalogResult
{
    public static ExternalCatalogResult<T> Ok<T>(T value) => new(value, null);

    public static ExternalCatalogResult<T> Fail<T>(int statusCode, string code, string detail) =>
        new(default, new ExternalCatalogError(statusCode, code, detail));
}
