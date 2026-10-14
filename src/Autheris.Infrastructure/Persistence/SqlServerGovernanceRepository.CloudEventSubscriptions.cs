namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Data.SqlClient;

/// <summary>SQL Server <see cref="ICloudEventSubscriptionStore"/> (WP-ARC-A-03). Every statement is scoped by <c>tenant_id</c>.</summary>
public partial class SqlServerGovernanceRepository : ICloudEventSubscriptionStore
{
    public async ValueTask RegisterSubscriptionAsync(CloudEventWebhookSubscription subscription, int maxSubscriptionsPerTenant, CancellationToken ct = default)
    {
        CloudEventSubscriptionMapping.Validate(subscription);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        // One statement: the HOLDLOCK range lock on the tenant rows makes count-then-insert atomic; PK guards the id.
        cmd.CommandText = $@"
            INSERT INTO dbo.CLOUDEVENT_SUBSCRIPTIONS ({CloudEventSubscriptionMapping.Columns})
            SELECT {CloudEventSubscriptionMapping.InsertParameters}
             WHERE NOT EXISTS (SELECT 1 FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WITH (UPDLOCK, HOLDLOCK) WHERE id = @id)
               AND (SELECT COUNT(*) FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant) < @max;";
        cmd.Parameters.AddWithValue("@id", subscription.Id);
        cmd.Parameters.AddWithValue("@tenant", subscription.TenantId);
        cmd.Parameters.AddWithValue("@url", subscription.TargetUrl);
        cmd.Parameters.AddWithValue("@filter", subscription.FilterTable);
        cmd.Parameters.AddWithValue("@ops", (object?)CloudEventSubscriptionMapping.OperationsJson(subscription.FilterOperations) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@secret", string.IsNullOrEmpty(subscription.HmacSecret) ? DBNull.Value : subscription.HmacSecret);
        cmd.Parameters.AddWithValue("@enabled", subscription.IsEnabled);
        cmd.Parameters.AddWithValue("@headers", (object?)CloudEventSubscriptionMapping.HeadersJson(subscription.CustomHeaders) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sid", (object?)subscription.CreatedBySid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@groups", (object?)CloudEventSubscriptionMapping.ListJson(subscription.CreatedByGroupSids) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@roles", (object?)CloudEventSubscriptionMapping.ListJson(subscription.CreatedByRoles) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", CloudEventSubscriptionMapping.CreatedMs(subscription));
        cmd.Parameters.AddWithValue("@max", maxSubscriptionsPerTenant);

        int affected;
        try
        {
            affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            throw CloudEventSubscriptionMapping.Duplicate();
        }

        if (affected == 1)
        {
            return;
        }

        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WHERE id = @id;";
        probe.Parameters.AddWithValue("@id", subscription.Id);
        throw await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null
            ? CloudEventSubscriptionMapping.Duplicate()
            : CloudEventSubscriptionMapping.QuotaExceeded(subscription.TenantId, maxSubscriptionsPerTenant);
    }

    public async ValueTask<bool> RemoveSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND id = @id;";
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
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {CloudEventSubscriptionMapping.Columns} FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant"
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
            rows.Add(CloudEventSubscriptionMapping.Read(reader));
        }

        return rows;
    }
}
