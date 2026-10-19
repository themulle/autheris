namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Events and image writes of one append, with offsets <c>head + 1 .. NewHead</c>.</summary>
public sealed record CdcBuiltBatch(IReadOnlyList<CdcStreamEventRecord> Events, IReadOnlyList<CdcRowImageWrite> ImageWrites, long NewHead);

/// <summary>
/// PLAN-CDC-99 D-A4 step 2, D-A6, D-A9, SEC-CDC99-01/-03: turns source rows into stream event records. Fingerprints each key per
/// stream, supplies the before-image from the last-image store (or marks it unknown), attributes every image to its OWN tenant,
/// serialises and encrypts with the offset bound into the AAD. Offsets are assigned here, before encryption. Nothing is written; a
/// failure (unsupported type, key unavailable) leaves the stream untouched.
/// </summary>
public sealed class CdcChangeEventBuilder
{
    private static readonly Counter<long> UnresolvedTenant = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_cdc_stream_unresolved_tenant_total",
        description: "CDC events whose tenant could not be attributed; they are delivered to nobody");

    private readonly ICdcStreamStore _store;
    private readonly ICdcStreamCrypto _crypto;
    private readonly ICdcPayloadCodec _codec;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;

    public CdcChangeEventBuilder(
        ICdcStreamStore store,
        ICdcStreamCrypto crypto,
        ICdcPayloadCodec codec,
        IOptionsMonitor<GatewayOptions> options,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<CdcBuiltBatch> BuildChangesAsync(
        CdcActivation activation, long headOffset, IReadOnlyList<CdcSourceRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return new CdcBuiltBatch([], [], headOffset);
        }

        var streamId = activation.Stream.Id;
        var maxBytes = Math.Max(1, _options.CurrentValue.CdcStream.MaxEventPayloadBytes);

        // fingerprints of every key of the batch, then the last images in one call
        var fingerprints = new byte[rows.Count][];
        for (var i = 0; i < rows.Count; i++)
        {
            fingerprints[i] = await _crypto.FingerprintAsync(streamId, rows[i].KeyValues, ct).ConfigureAwait(false);
        }

        var state = new Dictionary<string, ImageState>(StringComparer.Ordinal);
        var distinct = fingerprints.GroupBy(Convert.ToHexString).Select(g => g.First()).ToList();
        var stored = await _store.GetRowImagesAsync(streamId, distinct, ct).ConfigureAwait(false);
        foreach (var record in stored)
        {
            var plaintext = await _crypto.UnprotectImageAsync(streamId, record, ct).ConfigureAwait(false);
            state[Convert.ToHexString(record.KeyFingerprint)] = new ImageState(_codec.DeserializeImage(plaintext));
        }

        var now = _time.GetUtcNow();
        var events = new List<CdcStreamEventRecord>(rows.Count);
        var latest = new Dictionary<string, (long Offset, IReadOnlyDictionary<string, object?>? Image, bool Delete, CdcTenantScope Scope, string? Tenant)>(StringComparer.Ordinal);
        var offset = headOffset;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var fp = Convert.ToHexString(fingerprints[i]);
            state.TryGetValue(fp, out var previous);
            offset++;

            var before = row.Operation == CdcOperation.Insert ? null : previous?.Image;
            var beforeUnknown = row.Operation != CdcOperation.Insert && before is null;

            // each image is attributed to its OWN tenant (SEC-CDC99-01)
            CdcTenantScope scope;
            string? tenant;
            string? prior = null;
            if (row.Operation == CdcOperation.Delete)
            {
                (scope, tenant) = before is not null ? ImageTenant(activation, before) : DeleteTenantWithoutImage(activation, row);
            }
            else
            {
                (scope, tenant) = ImageTenant(activation, row.Row!);
            }

            if (before is not null && scope == CdcTenantScope.Row && row.Operation != CdcOperation.Delete)
            {
                var (beforeScope, beforeTenant) = ImageTenant(activation, before);
                if (beforeScope != CdcTenantScope.Row)
                {
                    // never expose an image whose tenant is unknown
                    before = null;
                    beforeUnknown = true;
                }
                else if (!string.Equals(beforeTenant, tenant, StringComparison.Ordinal))
                {
                    prior = beforeTenant;
                }
            }

            if (scope == CdcTenantScope.Unresolved)
            {
                UnresolvedTenant.Add(1);
            }

            var payload = new CdcChangePayload(row.Key, before, row.Row, beforeUnknown, PayloadOversize: false);
            var bytes = Serialize(payload);
            var oversize = bytes.Length > maxBytes;
            if (oversize)
            {
                bytes = Serialize(new CdcChangePayload(new Dictionary<string, object?>(), null, null, false, PayloadOversize: true));
            }

            var blob = await _crypto.ProtectEventAsync(streamId, offset, CdcStreamEventKind.Change, scope, tenant, prior, bytes, ct).ConfigureAwait(false);
            events.Add(new CdcStreamEventRecord(streamId, offset, row.Version, CdcStreamEventKind.Change, row.Operation, scope, tenant, prior, blob, now));

            // the row image after this change (a later row of the same key in this batch sees it as its before-image)
            var isDelete = row.Operation == CdcOperation.Delete;
            IReadOnlyDictionary<string, object?>? image = isDelete || oversize ? null : row.Row;
            state[fp] = new ImageState(image);
            latest[fp] = (offset, image, image is null, scope, tenant);
        }

        var writes = new List<CdcRowImageWrite>(latest.Count);
        foreach (var (fp, entry) in latest)
        {
            var fingerprint = Convert.FromHexString(fp);
            CdcProtectedBlob? blob = null;
            if (entry.Image is not null)
            {
                blob = await _crypto.ProtectImageAsync(streamId, fingerprint, SerializeImage(entry.Image), ct).ConfigureAwait(false);
            }

            writes.Add(new CdcRowImageWrite(fingerprint, entry.Scope, entry.Tenant, blob, entry.Offset));
        }

        return new CdcBuiltBatch(events, writes, offset);
    }

    /// <summary>A control event (<c>WatermarkExpired</c>, <c>Resync</c>): never row data; tenant scope <see cref="CdcTenantScope.Shared"/>.</summary>
    public async Task<CdcStreamEventRecord> BuildControlAsync(
        CdcActivation activation, long offset, CdcStreamEventKind kind, CdcControlPayload payload, long changeVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(payload);
        var bytes = _codec.SerializeControl(payload);
        var streamId = activation.Stream.Id;
        var blob = await _crypto.ProtectEventAsync(streamId, offset, kind, CdcTenantScope.Shared, null, null, bytes, ct).ConfigureAwait(false);
        return new CdcStreamEventRecord(streamId, offset, changeVersion, kind, null, CdcTenantScope.Shared, null, null, blob, _time.GetUtcNow());
    }

    private byte[] Serialize(CdcChangePayload payload)
    {
        try
        {
            return _codec.SerializeChange(payload);
        }
        catch (NotSupportedException)
        {
            throw new CdcSourceConfigurationException(CdcActivationCodes.UnsupportedType);
        }
    }

    private byte[] SerializeImage(IReadOnlyDictionary<string, object?> image)
    {
        try
        {
            return _codec.SerializeImage(image);
        }
        catch (NotSupportedException)
        {
            throw new CdcSourceConfigurationException(CdcActivationCodes.UnsupportedType);
        }
    }

    /// <summary>
    /// Tenant of one image, with the read path's rules (D-A6): a tenant column attributes the row (<c>Row</c>; a null, empty or malformed
    /// value is <c>Unresolved</c>, byte-exact after <see cref="TenantId.TryParse"/>, SEC-CDC99-03); a table without one belongs to its
    /// owner tenant (<c>Owner</c>) or is <c>Shared</c>.
    /// </summary>
    private static (CdcTenantScope Scope, string? Tenant) ImageTenant(CdcActivation activation, IReadOnlyDictionary<string, object?> image)
    {
        if (activation.TenantColumn is null)
        {
            return NoTenantColumn(activation);
        }

        return image.TryGetValue(activation.TenantColumn, out var value) ? ParseTenant(value) : (CdcTenantScope.Unresolved, null);
    }

    /// <summary>A delete without a last image: the tenant is only known when the tenant column is part of the primary key.</summary>
    private static (CdcTenantScope Scope, string? Tenant) DeleteTenantWithoutImage(CdcActivation activation, CdcSourceRow row)
    {
        if (activation.TenantColumn is null)
        {
            return NoTenantColumn(activation);
        }

        return row.Key.TryGetValue(activation.TenantColumn, out var value) ? ParseTenant(value) : (CdcTenantScope.Unresolved, null);
    }

    private static (CdcTenantScope Scope, string? Tenant) NoTenantColumn(CdcActivation activation)
    {
        if (string.IsNullOrWhiteSpace(activation.OwnerTenantId))
        {
            return (CdcTenantScope.Shared, null);
        }

        return TenantId.TryParse(activation.OwnerTenantId, out var owner) ? (CdcTenantScope.Owner, owner.Value) : (CdcTenantScope.Unresolved, null);
    }

    private static (CdcTenantScope Scope, string? Tenant) ParseTenant(object? value)
    {
        var text = value switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        return TenantId.TryParse(text, out var tenant) ? (CdcTenantScope.Row, tenant.Value) : (CdcTenantScope.Unresolved, null);
    }

    private sealed record ImageState(IReadOnlyDictionary<string, object?>? Image);
}
