namespace Autheris.Application.Streaming.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;

/// <summary>Admin view of a stream: no payload, fingerprint, wrapped key or key material (SEC-CDC99-11).</summary>
public sealed record CdcStreamAdminView(
    string Id, string Source, string Table, string Status, string? ActivationCode, long HeadOffset, long TailOffset,
    long? SourceWatermark, DateTimeOffset? LastPolledAt, long? SourceLagVersions);

public sealed record CdcStreamListing(IReadOnlyList<CdcStreamAdminView> Streams, IReadOnlyDictionary<string, int> WrappedKeysByKek);

public enum CdcResyncStatus
{
    Done = 1,
    NotFound = 2,
    Unavailable = 3
}

public sealed record CdcResyncOutcome(CdcResyncStatus Status, int DiscardedEvents);

/// <summary>Operations behind the stream admin endpoints (PLAN-CDC-99 D-A14, SEC-CDC99-21).</summary>
public sealed class CdcStreamAdminService(
    ICdcStreamStore store, ICdcStreamKeyStore keyStore, CdcStreamKeyring keyring, ICdcStreamRestarter? restarter = null)
{
    public async Task<CdcStreamListing> ListAsync(CancellationToken ct = default)
    {
        var streams = await store.ListStreamsAsync(ct).ConfigureAwait(false);
        var counts = await keyStore.CountByKekAsync(ct).ConfigureAwait(false);
        return new CdcStreamListing(
            [.. streams.Select(s => new CdcStreamAdminView(
                s.Id.Value, s.Source, s.Table.ToString(), s.Status.ToString(), s.ActivationCode, s.HeadOffset, s.TailOffset,
                s.SourceWatermark, s.LastPolledAt, null))],
            counts);
    }

    /// <summary>
    /// Restarts the stream at the current source version. With <paramref name="discardUnreadable"/> the events, images and keys that can no
    /// longer be decrypted (KEK loss) are removed first; consumers then observe a gap (SEC-CDC99-18).
    /// </summary>
    public async Task<CdcResyncOutcome> ResyncAsync(CdcStreamId id, bool discardUnreadable, CancellationToken ct = default)
    {
        if (await store.GetStreamAsync(id, ct).ConfigureAwait(false) is null)
        {
            return new CdcResyncOutcome(CdcResyncStatus.NotFound, 0);
        }

        if (restarter is null)
        {
            return new CdcResyncOutcome(CdcResyncStatus.Unavailable, 0);
        }

        var discarded = 0;
        if (discardUnreadable)
        {
            var unreadable = new List<string>();
            foreach (var key in await keyStore.GetKeysAsync(id, ct).ConfigureAwait(false))
            {
                try
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(await keyring.GetDekAsync(id, key.DekId, ct).ConfigureAwait(false));
                }
                catch (CdcStreamKeyUnavailableException)
                {
                    unreadable.Add(key.DekId);
                }
            }

            if (unreadable.Count > 0)
            {
                discarded = await store.DiscardUnreadableAsync(id, unreadable, ct).ConfigureAwait(false);
            }
        }

        return await restarter.RestartAsync(id, ct).ConfigureAwait(false)
            ? new CdcResyncOutcome(CdcResyncStatus.Done, discarded)
            : new CdcResyncOutcome(CdcResyncStatus.Unavailable, discarded);
    }

    /// <summary>Rotates the image key: new image writes use a fresh key, existing images stay readable under their recorded key.</summary>
    public async Task<bool> RotateImageKeyAsync(CdcStreamId id, CancellationToken ct = default)
    {
        if (await store.GetStreamAsync(id, ct).ConfigureAwait(false) is null)
        {
            return false;
        }

        await keyring.RotateImageKeyAsync(id, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// SEC-CDC101-06: Resets the multi-watermark vector state of a governed stream with an audited administrative justification.
    /// </summary>
    public async Task<bool> ResetWatermarkAsync(
        CdcStreamId id, IReadOnlyDictionary<string, string> dimensions, string justification, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);
        ArgumentNullException.ThrowIfNull(dimensions);

        if (await store.GetStreamAsync(id, ct).ConfigureAwait(false) is null)
        {
            return false;
        }

        var vector = new MultiWatermarkVector(dimensions);
        await store.ResetWatermarkAsync(id, vector.ToJson(), ct).ConfigureAwait(false);
        return true;
    }
}
