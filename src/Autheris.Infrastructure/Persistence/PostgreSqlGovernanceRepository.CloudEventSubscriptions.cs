namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Npgsql;

/// <summary>PostgreSQL <see cref="ICloudEventSubscriptionStore"/> (WP-ARC-A-03). Every statement is scoped by <c>tenant_id</c>.</summary>
public partial class PostgreSqlGovernanceRepository : ICloudEventSubscriptionStore
{
    public async ValueTask RegisterSubscriptionAsync(CloudEventWebhookSubscription subscription, int maxSubscriptionsPerTenant, CancellationToken ct = default)
    {
        CloudEventSubscriptionMapping.Validate(subscription);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Serialises concurrent registrations of one tenant so the count-then-insert quota is atomic under READ COMMITTED.
        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@key));";
            lockCmd.Parameters.AddWithValue("@key", "cloudevent_subs:" + subscription.TenantId);
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int affected;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                INSERT INTO CLOUDEVENT_SUBSCRIPTIONS ({CloudEventSubscriptionMapping.Columns})
                SELECT {CloudEventSubscriptionMapping.InsertParameters}
                 WHERE (SELECT COUNT(*) FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant) < @max
                ON CONFLICT (id) DO NOTHING;";
            cmd.Parameters.AddWithValue("@id", subscription.Id);
            cmd.Parameters.AddWithValue("@tenant", subscription.TenantId);
            cmd.Parameters.AddWithValue("@url", subscription.TargetUrl);
            cmd.Parameters.AddWithValue("@filter", subscription.FilterTable);
            cmd.Parameters.AddWithValue("@ops", NpgsqlTypes.NpgsqlDbType.Text, (object?)CloudEventSubscriptionMapping.OperationsJson(subscription.FilterOperations) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@secret", NpgsqlTypes.NpgsqlDbType.Text, string.IsNullOrEmpty(subscription.HmacSecret) ? DBNull.Value : subscription.HmacSecret);
            cmd.Parameters.AddWithValue("@enabled", subscription.IsEnabled);
            cmd.Parameters.AddWithValue("@headers", NpgsqlTypes.NpgsqlDbType.Text, (object?)CloudEventSubscriptionMapping.HeadersJson(subscription.CustomHeaders) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sid", NpgsqlTypes.NpgsqlDbType.Text, (object?)subscription.CreatedBySid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@groups", NpgsqlTypes.NpgsqlDbType.Text, (object?)CloudEventSubscriptionMapping.ListJson(subscription.CreatedByGroupSids) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@roles", NpgsqlTypes.NpgsqlDbType.Text, (object?)CloudEventSubscriptionMapping.ListJson(subscription.CreatedByRoles) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created", CloudEventSubscriptionMapping.CreatedMs(subscription));
            cmd.Parameters.AddWithValue("@max", maxSubscriptionsPerTenant);
            affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (affected == 1)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        await using var probe = conn.CreateCommand();
        probe.Transaction = tx;
        probe.CommandText = "SELECT 1 FROM CLOUDEVENT_SUBSCRIPTIONS WHERE id = @id;";
        probe.Parameters.AddWithValue("@id", subscription.Id);
        throw await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null
            ? CloudEventSubscriptionMapping.Duplicate()
            : CloudEventSubscriptionMapping.QuotaExceeded(subscription.TenantId, maxSubscriptionsPerTenant);
    }

    public async ValueTask<bool> RemoveSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND id = @id;";
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        cmd.Parameters.AddWithValue("@id", subscriptionId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
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

    private async Task<IReadOnlyList<CloudEventWebhookSubscription>> QuerySubscriptionsAsync(string tenantId, string? id, bool enabledOnly, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {CloudEventSubscriptionMapping.Columns} FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant"
                          + (id is null ? string.Empty : " AND id = @id")
                          + (enabledOnly ? " AND is_enabled = TRUE" : string.Empty) + " ORDER BY created_ms, id;";
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
    }
}
