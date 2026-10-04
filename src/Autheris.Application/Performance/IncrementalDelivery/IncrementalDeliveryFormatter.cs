namespace Autheris.Application.Performance.IncrementalDelivery;

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// F-PERF-12: Standardized GraphQL Incremental Delivery Multipart Chunk Formatter.
/// Serializes initial and deferred payload chunks into compliant multipart/mixed streams.
/// </summary>
public interface IIncrementalDeliveryFormatter
{
    string Boundary { get; }
    string ContentType { get; }

    ValueTask WriteInitialChunkAsync(Stream output, string jsonPayload, bool hasNext, CancellationToken ct = default);

    ValueTask WriteIncrementalChunkAsync(Stream output, string jsonPayload, bool hasNext, CancellationToken ct = default);

    ValueTask WriteFinalBoundaryAsync(Stream output, CancellationToken ct = default);
}

public sealed class IncrementalDeliveryFormatter : IIncrementalDeliveryFormatter
{
    public const string DefaultBoundary = "-";
    public string Boundary => DefaultBoundary;
    public string ContentType => $"multipart/mixed; boundary=\"{DefaultBoundary}\"";

    private const string Cr = "\r\n";
    private readonly byte[] _chunkHeaderBytes;
    private readonly byte[] _finalBoundaryBytes;

    public IncrementalDeliveryFormatter()
    {
        var header = $"{Cr}--{DefaultBoundary}{Cr}Content-Type: application/json; charset=utf-8{Cr}{Cr}";
        _chunkHeaderBytes = Encoding.UTF8.GetBytes(header);

        var final = $"{Cr}--{DefaultBoundary}--{Cr}";
        _finalBoundaryBytes = Encoding.UTF8.GetBytes(final);
    }

    public async ValueTask WriteInitialChunkAsync(Stream output, string jsonPayload, bool hasNext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(jsonPayload);

        await output.WriteAsync(_chunkHeaderBytes, ct).ConfigureAwait(false);
        var payloadBytes = Encoding.UTF8.GetBytes(jsonPayload);
        await output.WriteAsync(payloadBytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask WriteIncrementalChunkAsync(Stream output, string jsonPayload, bool hasNext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(jsonPayload);

        await output.WriteAsync(_chunkHeaderBytes, ct).ConfigureAwait(false);
        var payloadBytes = Encoding.UTF8.GetBytes(jsonPayload);
        await output.WriteAsync(payloadBytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask WriteFinalBoundaryAsync(Stream output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteAsync(_finalBoundaryBytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
