namespace Autheris.Infrastructure.Streaming;

using System;
using Autheris.Domain.Model;

/// <summary>
/// Structural checks of a <see cref="CdcAppendBatch"/> shared by every stream store: offsets must be dense
/// (<c>ExpectedHeadOffset + 1 .. ExpectedHeadOffset + n</c>), events must belong to the batch's stream and stored kinds only.
/// A violation is a programming error of the producer and is thrown before anything is written.
/// </summary>
internal static class CdcAppendBatchValidation
{
    public const int FingerprintLength = 32;

    public static void Validate(CdcAppendBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Events);
        ArgumentNullException.ThrowIfNull(batch.ImageWrites);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.Source);
        if (batch.StreamId.Value is null)
        {
            throw new ArgumentException("The batch has no stream id.", nameof(batch));
        }

        if (batch.ExpectedHeadOffset < 0)
        {
            throw new ArgumentException("The expected head offset must not be negative.", nameof(batch));
        }

        if (batch.FencingToken < 0)
        {
            throw new ArgumentException("The fencing token must not be negative.", nameof(batch));
        }

        var newHead = batch.ExpectedHeadOffset + batch.Events.Count;
        for (var i = 0; i < batch.Events.Count; i++)
        {
            var e = batch.Events[i];
            if (e.StreamId != batch.StreamId)
            {
                throw new ArgumentException("An event belongs to a different stream than the batch.", nameof(batch));
            }

            if (e.Offset != batch.ExpectedHeadOffset + 1 + i)
            {
                throw new ArgumentException("Event offsets must be dense and start at the expected head offset + 1.", nameof(batch));
            }

            if (e.Kind == CdcStreamEventKind.Gap || !Enum.IsDefined(e.Kind))
            {
                throw new ArgumentException("Only stored event kinds can be appended (Gap is synthesised, never stored).", nameof(batch));
            }

            if (!Enum.IsDefined(e.TenantScope))
            {
                throw new ArgumentException("An event has an invalid tenant scope.", nameof(batch));
            }

            ValidateBlob(e.Payload);
        }

        foreach (var image in batch.ImageWrites)
        {
            if (image.KeyFingerprint is not { Length: FingerprintLength })
            {
                throw new ArgumentException("A row image fingerprint must be exactly 32 bytes.", nameof(batch));
            }

            if (!Enum.IsDefined(image.TenantScope))
            {
                throw new ArgumentException("A row image has an invalid tenant scope.", nameof(batch));
            }

            if (image.Offset <= batch.ExpectedHeadOffset || image.Offset > newHead)
            {
                throw new ArgumentException("A row image offset must reference an event of the batch.", nameof(batch));
            }

            if (image.Image is not null)
            {
                ValidateBlob(image.Image);
            }
        }
    }

    public static void ValidateFingerprint(byte[] fingerprint)
    {
        if (fingerprint is not { Length: FingerprintLength })
        {
            throw new ArgumentException("A row image fingerprint must be exactly 32 bytes.", nameof(fingerprint));
        }
    }

    private static void ValidateBlob(CdcProtectedBlob blob)
    {
        if (blob is null || string.IsNullOrEmpty(blob.DekId) || blob.Data is null || blob.Data.Length == 0)
        {
            throw new ArgumentException("A protected blob needs a DEK id and data.");
        }
    }
}
