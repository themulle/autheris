namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Data.SqlClient;

public partial class GovernanceRepository : ICloudEventSubscriptionStore
{
    private const string InsertPg = @"
                INSERT INTO CLOUDEVENT_SUBSCRIPTIONS (id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms)
                SELECT @id, @tenant, @url, @filter, @ops, @secret, @enabled, @headers, @sid, @groups, @roles, @created
                 WHERE (SELECT COUNT(*) FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant) < @max
                ON CONFLICT (id) DO NOTHING;";

    private const string InsertSs = @"
            INSERT INTO dbo.CLOUDEVENT_SUBSCRIPTIONS (id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms)
            SELECT @id, @tenant, @url, @filter, @ops, @secret, @enabled, @headers, @sid, @groups, @roles, @created
             WHERE NOT EXISTS (SELECT 1 FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WITH (UPDLOCK, HOLDLOCK) WHERE id = @id)
               AND (SELECT COUNT(*) FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant) < @max;";

    private const string SelectPgAll = "SELECT id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant ORDER BY created_ms, id;";
    private const string SelectPgEnabled = "SELECT id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND is_enabled = TRUE ORDER BY created_ms, id;";
    private const string SelectPgById = "SELECT id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND id = @id ORDER BY created_ms, id;";
    private const string SelectPgByIdEnabled = "SELECT id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND id = @id AND is_enabled = TRUE ORDER BY created_ms, id;";

    private const string SelectSsAll = "SELECT id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant ORDER BY created_ms, id;";
    private const string SelectSsEnabled = "SELECT id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND is_enabled = 1 ORDER BY created_ms, id;";
    private const string SelectSsById = "SELECT id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND id = @id ORDER BY created_ms, id;";
    private const string SelectSsByIdEnabled = "SELECT id, tenant_id, target_url, filter_table, filter_operations, hmac_secret_protected, is_enabled, custom_headers_json, created_by_sid, created_by_groups_json, created_by_roles_json, created_ms FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND id = @id AND is_enabled = 1 ORDER BY created_ms, id;";

    public async ValueTask RegisterSubscriptionAsync(CloudEventWebhookSubscription subscription, int maxSubscriptionsPerTenant, CancellationToken ct = default)
    {
        CloudEventSubscriptionMapping.Validate(subscription);

        var isPg = _dialect is PostgreSqlGovernanceDialect;
        if (isPg)
        {
            await RegisterSubscriptionPgAsync(subscription, maxSubscriptionsPerTenant, ct).ConfigureAwait(false);
        }
        else
        {
            await RegisterSubscriptionSsAsync(subscription, maxSubscriptionsPerTenant, ct).ConfigureAwait(false);
        }
    }

    private async Task RegisterSubscriptionPgAsync(CloudEventWebhookSubscription subscription, int maxSubscriptionsPerTenant, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@key));";
            lockCmd.AddBound("@key", "cloudevent_subs:" + subscription.TenantId);
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int affected;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = InsertPg;
            BindSubscriptionParameters(cmd, subscription, maxSubscriptionsPerTenant);
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
        probe.AddBound("@id", subscription.Id);
        throw await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null
            ? CloudEventSubscriptionMapping.Duplicate()
            : CloudEventSubscriptionMapping.QuotaExceeded(subscription.TenantId, maxSubscriptionsPerTenant);
    }

    private async Task RegisterSubscriptionSsAsync(CloudEventWebhookSubscription subscription, int maxSubscriptionsPerTenant, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;";
            var pRes = lockCmd.CreateParameter();
            pRes.ParameterName = "@res";
            pRes.Value = "cloudevent_subs:" + subscription.TenantId;
            lockCmd.Parameters.Add(pRes);
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int affected;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = InsertSs;
            BindSubscriptionParameters(cmd, subscription, maxSubscriptionsPerTenant);
            affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            throw CloudEventSubscriptionMapping.Duplicate();
        }

        if (affected == 1)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        await using var probe = conn.CreateCommand();
        probe.Transaction = tx;
        probe.CommandText = "SELECT 1 FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WHERE id = @id;";
        probe.AddBound("@id", subscription.Id);
        throw await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null
            ? CloudEventSubscriptionMapping.Duplicate()
            : CloudEventSubscriptionMapping.QuotaExceeded(subscription.TenantId, maxSubscriptionsPerTenant);
    }

    private static void BindSubscriptionParameters(DbCommand cmd, CloudEventWebhookSubscription subscription, int maxSubscriptionsPerTenant)
    {
        cmd.AddBound("@id", subscription.Id);
        cmd.AddBound("@tenant", subscription.TenantId);
        cmd.AddBound("@url", subscription.TargetUrl);
        cmd.AddBound("@filter", subscription.FilterTable);
        cmd.AddBound("@ops", (object?)CloudEventSubscriptionMapping.OperationsJson(subscription.FilterOperations) ?? DBNull.Value);
        cmd.AddBound("@secret", string.IsNullOrEmpty(subscription.HmacSecret) ? DBNull.Value : subscription.HmacSecret);
        cmd.AddBound("@enabled", subscription.IsEnabled);
        cmd.AddBound("@headers", (object?)CloudEventSubscriptionMapping.HeadersJson(subscription.CustomHeaders) ?? DBNull.Value);
        cmd.AddBound("@sid", (object?)subscription.CreatedBySid ?? DBNull.Value);
        cmd.AddBound("@groups", (object?)CloudEventSubscriptionMapping.ListJson(subscription.CreatedByGroupSids) ?? DBNull.Value);
        cmd.AddBound("@roles", (object?)CloudEventSubscriptionMapping.ListJson(subscription.CreatedByRoles) ?? DBNull.Value);
        cmd.AddBound("@created", CloudEventSubscriptionMapping.CreatedMs(subscription));
        cmd.AddBound("@max", maxSubscriptionsPerTenant);
    }

    public async ValueTask<bool> RemoveSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var isPg = _dialect is PostgreSqlGovernanceDialect;
        var sql = isPg
            ? "DELETE FROM CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND id = @id;"
            : "DELETE FROM dbo.CLOUDEVENT_SUBSCRIPTIONS WHERE tenant_id = @tenant AND id = @id;";

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@tenant", tenantId);
        cmd.AddBound("@id", subscriptionId);
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
        var isPg = _dialect is PostgreSqlGovernanceDialect;
        string sql;
        if (isPg)
        {
            sql = (id, enabledOnly) switch
            {
                (not null, true) => SelectPgByIdEnabled,
                (not null, false) => SelectPgById,
                (null, true) => SelectPgEnabled,
                (null, false) => SelectPgAll,
            };
        }
        else
        {
            sql = (id, enabledOnly) switch
            {
                (not null, true) => SelectSsByIdEnabled,
                (not null, false) => SelectSsById,
                (null, true) => SelectSsEnabled,
                (null, false) => SelectSsAll,
            };
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@tenant", tenantId);
        if (id is not null)
        {
            cmd.AddBound("@id", id);
        }

        var rows = new List<CloudEventWebhookSubscription>();
        await using var reader = await ExecuteReaderAsync(cmd, "CloudEvents.Query", ct).ConfigureAwait(false);
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
