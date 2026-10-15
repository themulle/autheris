namespace Autheris.Application.Streaming.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Envelope encryption of stream events and row images plus the keyed key fingerprint (PLAN-CDC-99 D-A7..D-A9).
/// Failures throw <see cref="CdcStreamKeyUnavailableException"/> (fail closed).
/// </summary>
public interface ICdcStreamCrypto
{
    ValueTask<CdcProtectedBlob> ProtectEventAsync(CdcStreamId id, long offset, CdcStreamEventKind kind, CdcTenantScope scope, string? tenantId, string? priorTenantId, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);

    ValueTask<byte[]> UnprotectEventAsync(CdcStreamEventRecord record, CancellationToken ct = default);

    ValueTask<CdcProtectedBlob> ProtectImageAsync(CdcStreamId id, byte[] fingerprint, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);

    ValueTask<byte[]> UnprotectImageAsync(CdcStreamId id, CdcRowImageRecord image, CancellationToken ct = default);

    ValueTask<byte[]> FingerprintAsync(CdcStreamId id, IReadOnlyList<object?> keyValues, CancellationToken ct = default);
}
