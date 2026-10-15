namespace Autheris.Application.Streaming.Interfaces;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Plaintext serialisation of stream payloads and row images (PLAN-CDC-99 D-A7). The producer serialises with it before
/// encryption; the shipped implementation is the closed-tag <c>CdcPayloadCodec</c> (SEC-CDC99-16). Implementations throw
/// <see cref="NotSupportedException"/> for a CLR value type outside the tag set; the producer then fails the whole batch
/// (<see cref="CdcActivationCodes.UnsupportedType"/>, nothing is written).
/// </summary>
public interface ICdcPayloadCodec
{
    byte[] SerializeChange(CdcChangePayload payload);

    byte[] SerializeControl(CdcControlPayload payload);

    /// <summary>Serialises one full row image (the last-image store of a key).</summary>
    byte[] SerializeImage(IReadOnlyDictionary<string, object?> row);

    IReadOnlyDictionary<string, object?> DeserializeImage(ReadOnlySpan<byte> plaintext);
}
