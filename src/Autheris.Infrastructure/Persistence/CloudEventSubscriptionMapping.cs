namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Events.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Dialect-neutral helpers shared by the three <c>ICloudEventSubscriptionStore</c> repository partials.</summary>
internal static class CloudEventSubscriptionMapping
{
    /// <summary>Column order read by <see cref="Read"/>.</summary>
    public const string Columns =
        "id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms";

    public const string InsertParameters =
        "@id, @tenant, @url, @filter, @ops, @secret, @enabled, @headers, @sid, @groups, @roles, @created";

    public static string? OperationsJson(IReadOnlyList<CdcOperation> ops) =>
        ops.Count == 0 ? null : JsonSerializer.Serialize(ops.Select(o => (int)o));

    public static string? HeadersJson(IReadOnlyDictionary<string, string>? headers) =>
        headers is { Count: > 0 } ? JsonSerializer.Serialize(headers) : null;

    public static string? ListJson(IReadOnlyList<string>? values) =>
        values is null ? null : JsonSerializer.Serialize(values);

    public static long CreatedMs(CloudEventWebhookSubscription s) =>
        (s.CreatedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

    /// <summary>Rejects values the table cannot hold, so a violation fails before touching the database.</summary>
    public static void Validate(CloudEventWebhookSubscription s)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrWhiteSpace(s.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(s.Id);
        if (s.Id.Length > CloudEventOutboxMessageFactory.MaxSubscriptionIdLength || s.TenantId.Length > 128
            || (s.TargetUrl?.Length ?? 0) > 2048 || (s.FilterTable?.Length ?? 0) > 512 || (s.CreatedBySid?.Length ?? 0) > 256)
        {
            throw new ArgumentException("Subscription field exceeds its storage bound.", nameof(s));
        }

        if ((s.CreatedByRoles?.Count ?? 0) > 256 || (s.CreatedByRoles?.Any(r => r is { Length: > 256 }) ?? false)
            || (s.CreatedByGroupSids?.Count ?? 0) > 4096
            || (HeadersJson(s.CustomHeaders)?.Length ?? 0) > 16384)
        {
            throw new ArgumentException("Subscription snapshot or headers exceed their storage bound.", nameof(s));
        }
    }

    /// <summary>Reads the current row; a row with malformed JSON columns is skipped (<c>null</c>) so one bad row cannot break the whole tenant.</summary>
    public static CloudEventWebhookSubscription? TryRead(DbDataReader r)
    {
        try
        {
            return Read(r);
        }
        catch (Exception ex) when (ex is JsonException or InvalidCastException or ArgumentException or FormatException or NullReferenceException)
        {
            return null;
        }
    }

    public static CloudEventWebhookSubscription Read(DbDataReader r)
    {
        var ops = r.IsDBNull(4)
            ? []
            : (IReadOnlyList<CdcOperation>)JsonSerializer.Deserialize<int[]>(r.GetString(4))!.Select(i => (CdcOperation)i).ToList();
        var headers = r.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(7));
        return new CloudEventWebhookSubscription(
            Id: r.GetString(0),
            TenantId: r.GetString(1),
            TargetUrl: r.GetString(2),
            FilterTable: r.GetString(3),
            FilterOperations: ops,
            HmacSecret: r.IsDBNull(5) ? string.Empty : r.GetString(5),
            IsEnabled: Convert.ToInt64(r.GetValue(6)) != 0,
            CustomHeaders: headers,
            CreatedBySid: r.IsDBNull(8) ? null : r.GetString(8),
            CreatedByGroupSids: r.IsDBNull(9) ? null : JsonSerializer.Deserialize<string[]>(r.GetString(9)),
            CreatedByRoles: r.IsDBNull(10) ? null : JsonSerializer.Deserialize<string[]>(r.GetString(10)),
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(11)));
    }

    public static IReadOnlyList<CloudEventWebhookSubscription> Filter(
        IEnumerable<CloudEventWebhookSubscription> enabled, TableIdentifier table, CdcOperation op) =>
        enabled.Where(s => CloudEventSubscriptionMatcher.Matches(s, table, op)).ToList();

    public static IReadOnlyList<CloudEventWebhookSubscription> Filter(
        IEnumerable<CloudEventWebhookSubscription> enabled, string tableName, CdcOperation op) =>
        enabled.Where(s => CloudEventSubscriptionMatcher.Matches(s, tableName, op)).ToList();

    public static InvalidOperationException Duplicate() => new("A webhook subscription with this id already exists.");

    public static InvalidOperationException QuotaExceeded(string tenantId, int max) =>
        new($"Maximum webhook subscriptions ({max}) reached for tenant '{tenantId}'.");
}
