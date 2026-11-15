namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

public enum DiscoveryArtifactOutcome
{
    Ok = 0,
    NoContent = 1,
    Unauthenticated = 2,
    NotFound = 3,
    Forbidden = 4,
    InvalidArtifact = 5,
    Unavailable = 6
}

public sealed record DiscoveryArtifactWriteResult(
    DiscoveryArtifactOutcome Outcome,
    string? DatasetId = null,
    string? ViolationCode = null,
    string? Message = null,
    string? ContentSha256 = null,
    int SampleRowCount = 0,
    int StatisticsColumnCount = 0);

public sealed record DiscoverySamplesResponse(
    bool Available,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Samples);

public sealed record DiscoverySamplesReadResult(
    DiscoveryArtifactOutcome Outcome,
    DiscoverySamplesResponse? Payload = null,
    string? ErrorCode = null,
    string? Message = null);

public interface IDiscoveryArtifactService
{
    Task<DiscoveryArtifactWriteResult> UpsertAsync(
        ClaimsPrincipal? principal,
        string? datasetId,
        ReadOnlyMemory<byte> rawBody,
        string source,
        CancellationToken ct = default);

    Task<DiscoveryArtifactWriteResult> DeleteAsync(
        ClaimsPrincipal? principal,
        string? datasetId,
        string source,
        CancellationToken ct = default);

    Task<DiscoverySamplesReadResult> GetSamplesAsync(
        ClaimsPrincipal? principal,
        string? datasetId,
        int? limit = null,
        DiscoverySurface surface = DiscoverySurface.Rest,
        CancellationToken ct = default);
}
