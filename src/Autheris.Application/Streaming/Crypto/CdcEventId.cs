namespace Autheris.Application.Streaming.Crypto;

using System;
using System.Globalization;
using Autheris.Domain.Model;

/// <summary>
/// Opaque, deterministic receiver-facing event id (PLAN-CDC-99 D-A10, SEC-CDC99-19): <c>evt_{streamId}_{offset}</c>.
/// It is derived only from the stream id and the offset and therefore contains no key value, fingerprint or change version.
/// </summary>
public static class CdcEventId
{
    public const string Prefix = "evt_";

    public static string Format(CdcStreamId streamId, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, 1L);
        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}{streamId.Value}_{offset}");
    }
}
