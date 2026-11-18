namespace Autheris.Application.Products.Sync;

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;

public enum ProductSyncWebhookStatus { Accepted, Duplicate, BadRequest, TooLarge, Unauthorized, Unavailable }

/// <summary>The signed headers of a delivery; every value is untrusted until the signature has been verified.</summary>
public sealed record ProductSyncWebhookDelivery(string? Timestamp, string? DeliveryId, string? Signature);

/// <summary>
/// The signed webhook of the Git synchronisation. The tenant is bound by the route and its secret, never by the payload. A delivery is
/// verified before its body is parsed: size cap, timestamp window, HMAC-SHA256 over <c>timestamp "." raw body</c> compared in constant
/// time with the per-tenant secret, then a replay cache on the delivery id. An accepted delivery only records the trigger (audited); the
/// repository state itself arrives through the plan and apply routes of the sync identity.
/// </summary>
public sealed class ProductSyncWebhookService(
    IKeyVaultSecretProvider? secrets,
    IDistributedClusterStateProvider? state,
    IAuditWriter audit,
    TimeProvider? timeProvider = null)
{
    public const int MaxBodyBytes = 256 * 1024;
    public static readonly TimeSpan TimestampWindow = TimeSpan.FromMinutes(5);
    internal const string SecretRefPrefix = "products-sync-webhook-";
    internal const string ReplayKeyPrefix = "products:sync:webhook:";
    private const int MaxDeliveryIdLength = 100;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>The secret reference of a tenant (the secret store holds one secret per tenant).</summary>
    public static string SecretRef(TenantId tenant) => SecretRefPrefix + tenant.Value;

    /// <summary>Computes the signature header value a sender must present.</summary>
    public static string Sign(ReadOnlySpan<byte> secret, string timestamp, ReadOnlySpan<byte> body)
    {
        using var hmac = new HMACSHA256(secret.ToArray());
        hmac.TransformBlock(Encoding.ASCII.GetBytes(timestamp + "."), 0, timestamp.Length + 1, null, 0);
        var buffer = body.ToArray();
        hmac.TransformFinalBlock(buffer, 0, buffer.Length);
        return "sha256=" + Convert.ToHexStringLower(hmac.Hash!);
    }

    public async Task<ProductSyncWebhookStatus> HandleAsync(
        TenantId tenant, ReadOnlyMemory<byte> body, ProductSyncWebhookDelivery delivery, CancellationToken ct = default)
    {
        if (body.Length > MaxBodyBytes)
        {
            return await RejectAsync(tenant, ProductSyncWebhookStatus.TooLarge, "TOO_LARGE", ct).ConfigureAwait(false);
        }

        if (!TryVerify(tenant, body.Span, delivery, out var reason))
        {
            return await RejectAsync(tenant, ProductSyncWebhookStatus.Unauthorized, reason, ct).ConfigureAwait(false);
        }

        // Only a verified body is parsed.
        if (!TryParseBody(body.Span, out var commit))
        {
            return ProductSyncWebhookStatus.BadRequest;
        }

        if (state is null)
        {
            return ProductSyncWebhookStatus.Unavailable;
        }

        long? seen;
        try
        {
            seen = await state.IncrementAsync(ReplayKeyPrefix + tenant.Value + ":" + delivery.DeliveryId, 1, TimestampWindow * 2 + TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        }
        catch (ClusterStateUnavailableException)
        {
            return ProductSyncWebhookStatus.Unavailable;
        }

        if (seen is null)
        {
            return ProductSyncWebhookStatus.Unavailable;
        }

        if (seen > 1)
        {
            return await RejectAsync(tenant, ProductSyncWebhookStatus.Duplicate, "REPLAY", ct).ConfigureAwait(false);
        }

        await audit.RecordRequiredAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.ProductSyncWebhookReceived,
            TenantId = tenant,
            TargetTable = "products.sync",
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new { delivery = delivery.DeliveryId, commit }),
            OccurredAt = _time.GetUtcNow()
        }, ct).ConfigureAwait(false);
        return ProductSyncWebhookStatus.Accepted;
    }

    private bool TryVerify(TenantId tenant, ReadOnlySpan<byte> body, ProductSyncWebhookDelivery delivery, out string reason)
    {
        reason = "SIGNATURE";
        if (string.IsNullOrWhiteSpace(delivery.DeliveryId) || delivery.DeliveryId.Length > MaxDeliveryIdLength ||
            !long.TryParse(delivery.Timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            reason = "HEADERS";
            return false;
        }

        var age = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253402300799));
        if (age > TimestampWindow || age < -TimestampWindow)
        {
            reason = "TIMESTAMP";
            return false;
        }

        byte[] secret;
        try
        {
            secret = secrets?.GetSecretBytes(SecretRef(tenant)) ?? [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            secret = [];
        }

        if (secret.Length == 0 || delivery.Signature is not { Length: > 7 } header || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var presented = Convert.FromHexString(header["sha256=".Length..]);
            var expected = Convert.FromHexString(Sign(secret, delivery.Timestamp!, body)["sha256=".Length..]);
            return CryptographicOperations.FixedTimeEquals(presented, expected);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Parses a verified body: false when it is not a bounded JSON object; the commit id is hex only and bounded, otherwise null.</summary>
    private static bool TryParseBody(ReadOnlySpan<byte> body, out string? commit)
    {
        commit = null;
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var key in new[] { "commit", "after" })
            {
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: >= 7 and <= 64 } id && IsHex(id))
                {
                    commit = id;
                    break;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<ProductSyncWebhookStatus> RejectAsync(TenantId tenant, ProductSyncWebhookStatus status, string reason, CancellationToken ct)
    {
        try
        {
            await audit.RecordBestEffortAsync(new AuditLogEntry
            {
                EventType = AuditEventTypes.ProductSyncWebhookRejected,
                TenantId = tenant,
                TargetTable = "products.sync",
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new { reason }),
                OccurredAt = _time.GetUtcNow()
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // a rejected delivery stays rejected when its best-effort audit fails
        }

        return status;
    }
}
