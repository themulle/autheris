namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite <see cref="ICloudEventSubscriptionStore"/> (WP-ARC-A-03). Every statement runs through
/// <see cref="WithSubscriptionsConnectionAsync{T}"/>, the only place in this file that touches the shared connection. Every
/// statement is scoped by <c>tenant_id</c>.
/// </summary>
public partial class SqliteGovernanceRepository : ICloudEventSubscriptionStore
{
    private async Task<T> WithSubscriptionsConnectionAsync<T>(Func<SqliteConnection, Task<T>> op, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await op(_connection).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask RegisterSubscriptionAsync(CloudEventWebhookSubscription subscription, int maxSubscriptionsPerTenant, CancellationToken ct = default)
    {
        CloudEventSubscriptionMapping.Validate(subscription);
        await WithSubscriptionsConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            // The quota check and the insert are one statement under the connection lock; ON CONFLICT keeps another row intact.
            cmd.CommandText = $@"
                INSERT INTO CLOUDEVENT_SUBSCRIPTIONS ({CloudEventSubscriptionMapping.Columns})
                SELECT {CloudEventSubscriptionMapping.InsertParameters}
                 WHERE (SELECT COUNT(*) FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant) < @max
                ON CONFLICT (id) DO NOTHING;";
            Bind(cmd, subscription);
            cmd.Parameters.AddWithValue("@max", maxSubscriptionsPerTenant);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
            {
                return true;
            }

            await using var probe = conn.CreateCommand();
            probe.CommandText = "SELECT 1 FROM CLOUDEVENT_SUBSCRIPTIONS WHERE id = @id;";
            probe.Parameters.AddWithValue("@id", subscription.Id);
            throw await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null
                ? CloudEventSubscriptionMapping.Duplicate()
                : CloudEventSubscriptionMapping.QuotaExceeded(subscription.TenantId, maxSubscriptionsPerTenant);
        }, ct).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand cmd, CloudEventWebhookSubscription s)
    {
        cmd.Parameters.AddWithValue("@id", s.Id);
        cmd.Parameters.AddWithValue("@tenant", s.TenantId);
        cmd.Parameters.AddWithValue("@url", s.TargetUrl);
        cmd.Parameters.AddWithValue("@filter", s.FilterTable);
        cmd.Parameters.AddWithValue("@ops", (object?)CloudEventSubscriptionMapping.OperationsJson(s.FilterOperations) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@secret", string.IsNullOrEmpty(s.HmacSecret) ? DBNull.Value : s.HmacSecret);
        cmd.Parameters.AddWithValue("@enabled", s.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@headers", (object?)CloudEventSubscriptionMapping.HeadersJson(s.CustomHeaders) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sid", (object?)s.CreatedBySid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@groups", (object?)CloudEventSubscriptionMapping.ListJson(s.CreatedByGroupSids) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@roles", (object?)CloudEventSubscriptionMapping.ListJson(s.CreatedByRoles) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", CloudEventSubscriptionMapping.CreatedMs(s));
    }

    public async ValueTask<bool> RemoveSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        return await WithSubscriptionsConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND id = @id;";
            cmd.Parameters.AddWithValue("@tenant", tenantId);
            cmd.Parameters.AddWithValue("@id", subscriptionId);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<CloudEventWebhookSubscription?> GetSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        var rows = await QuerySubscriptionsAsync(tenantId, subscriptionId, enabledOnly: false, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> ListSubscriptionsAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return await QuerySubscriptionsAsync(tenantId, null, enabledOnly: false, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> GetSubscriptionsAsync(string tenantId, string tableName, CdcOperation operation, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var rows = await QuerySubscriptionsAsync(tenantId, null, enabledOnly: true, ct).ConfigureAwait(false);
        return CloudEventSubscriptionMapping.Filter(rows, tableName, operation);
    }

    public async ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> GetSubscriptionsAsync(string tenantId, TableIdentifier table, CdcOperation operation, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var rows = await QuerySubscriptionsAsync(tenantId, null, enabledOnly: true, ct).ConfigureAwait(false);
        return CloudEventSubscriptionMapping.Filter(rows, table, operation);
    }

    private Task<IReadOnlyList<CloudEventWebhookSubscription>> QuerySubscriptionsAsync(string tenantId, string? id, bool enabledOnly, CancellationToken ct) =>
        WithSubscriptionsConnectionAsync<IReadOnlyList<CloudEventWebhookSubscription>>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {CloudEventSubscriptionMapping.Columns} FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant"
                              + (id is null ? string.Empty : " AND id = @id")
                              + (enabledOnly ? " AND is_enabled = 1" : string.Empty) + " ORDER BY created_ms, id;";
            cmd.Parameters.AddWithValue("@tenant", tenantId);
            if (id is not null)
            {
                cmd.Parameters.AddWithValue("@id", id);
            }

            var rows = new List<CloudEventWebhookSubscription>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (CloudEventSubscriptionMapping.TryRead(reader) is { } parsed)
            {
                rows.Add(parsed);
            }
            }

            return rows;
        }, ct);
}
