namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;

internal enum RecertificationDialect
{
    Sqlite,
    PostgreSql,
    SqlServer
}

internal sealed class RecertificationScope(DbConnection connection, Func<ValueTask> dispose) : IAsyncDisposable
{
    public DbConnection Connection { get; } = connection;
    public ValueTask DisposeAsync() => dispose();
}

internal sealed class RecertificationStore(
    RecertificationDialect dialect,
    Func<bool, CancellationToken, Task<RecertificationScope>> open)
{
    public RecertificationDialect Dialect => dialect;

    // ---------------------------------------------------- CAMPAIGNS

    public async Task CreateCampaignAsync(RecertificationCampaign campaign, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO RECERT_CAMPAIGNS (tenant_id, campaign_id, name, recert_class, status, deadline, created_at)
VALUES (@tenantId, @campaignId, @name, @recertClass, @status, @deadline, @createdAt)";
        AddParam(cmd, "@tenantId", campaign.TenantId);
        AddParam(cmd, "@campaignId", campaign.CampaignId);
        AddParam(cmd, "@name", campaign.Name);
        AddParam(cmd, "@recertClass", campaign.RecertClass.ToString());
        AddParam(cmd, "@status", campaign.Status.ToString());
        AddParam(cmd, "@deadline", FormatIso(campaign.Deadline));
        AddParam(cmd, "@createdAt", FormatIso(campaign.CreatedAt));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecertificationCampaign>> GetCampaignsAsync(TenantId tenantId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, campaign_id, name, recert_class, status, deadline, created_at FROM RECERT_CAMPAIGNS WHERE tenant_id = @tenantId";
        AddParam(cmd, "@tenantId", tenantId.Value);

        var list = new List<RecertificationCampaign>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadCampaign(reader));
        }

        return list;
    }

    public async Task<RecertificationCampaign?> GetCampaignByIdAsync(TenantId tenantId, string campaignId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, campaign_id, name, recert_class, status, deadline, created_at FROM RECERT_CAMPAIGNS WHERE tenant_id = @tenantId AND campaign_id = @campaignId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@campaignId", campaignId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadCampaign(reader);
        }

        return null;
    }

    public async Task UpdateCampaignStatusAsync(TenantId tenantId, string campaignId, RecertCampaignStatus status, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "UPDATE RECERT_CAMPAIGNS SET status = @status WHERE tenant_id = @tenantId AND campaign_id = @campaignId";
        AddParam(cmd, "@status", status.ToString());
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@campaignId", campaignId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------- ITEMS

    public async Task CreateItemAsync(RecertificationItem item, CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO RECERT_ITEMS (tenant_id, campaign_id, item_id, asset_type, asset_key, assigned_to, reviewer_id, decision, decision_at, decision_by, comments)
VALUES (@tenantId, @campaignId, @itemId, @assetType, @assetKey, @assignedTo, @reviewerId, @decision, @decisionAt, @decisionBy, @comments)";
        AddParam(cmd, "@tenantId", item.TenantId);
        AddParam(cmd, "@campaignId", item.CampaignId);
        AddParam(cmd, "@itemId", item.ItemId);
        AddParam(cmd, "@assetType", item.AssetType);
        AddParam(cmd, "@assetKey", item.AssetKey);
        AddParam(cmd, "@assignedTo", item.AssignedTo);
        AddParam(cmd, "@reviewerId", item.ReviewerId);
        AddParam(cmd, "@decision", item.Decision.ToString());
        AddParam(cmd, "@decisionAt", item.DecisionAt.HasValue ? FormatIso(item.DecisionAt.Value) : null);
        AddParam(cmd, "@decisionBy", item.DecisionBy);
        AddParam(cmd, "@comments", item.Comments);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecertificationItem>> GetItemsAsync(TenantId tenantId, string campaignId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = @"
SELECT tenant_id, campaign_id, item_id, asset_type, asset_key, assigned_to, reviewer_id, decision, decision_at, decision_by, comments
FROM RECERT_ITEMS
WHERE tenant_id = @tenantId AND campaign_id = @campaignId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@campaignId", campaignId);

        var list = new List<RecertificationItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadItem(reader));
        }

        return list;
    }

    public async Task<RecertificationItem?> GetItemByIdAsync(TenantId tenantId, string campaignId, string itemId, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = @"
SELECT tenant_id, campaign_id, item_id, asset_type, asset_key, assigned_to, reviewer_id, decision, decision_at, decision_by, comments
FROM RECERT_ITEMS
WHERE tenant_id = @tenantId AND campaign_id = @campaignId AND item_id = @itemId";
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@campaignId", campaignId);
        AddParam(cmd, "@itemId", itemId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadItem(reader);
        }

        return null;
    }

    public async Task<IReadOnlyList<RecertificationItem>> GetPendingItemsPastDeadlineAsync(DateTimeOffset deadlineUtc, CancellationToken ct = default)
    {
        await using var scope = await open(false, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = @"
SELECT i.tenant_id, i.campaign_id, i.item_id, i.asset_type, i.asset_key, i.assigned_to, i.reviewer_id, i.decision, i.decision_at, i.decision_by, i.comments
FROM RECERT_ITEMS i
INNER JOIN RECERT_CAMPAIGNS c ON i.tenant_id = c.tenant_id AND i.campaign_id = c.campaign_id
WHERE i.decision = 'Pending' AND c.deadline <= @deadlineUtc";
        AddParam(cmd, "@deadlineUtc", FormatIso(deadlineUtc));

        var list = new List<RecertificationItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadItem(reader));
        }

        return list;
    }

    public async Task<bool> TryUpdateItemDecisionAsync(
        TenantId tenantId,
        string campaignId,
        string itemId,
        RecertDecision expected,
        RecertDecision decision,
        string decisionBy,
        string? comments = null,
        CancellationToken ct = default)
    {
        await using var scope = await open(true, ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = @"
UPDATE RECERT_ITEMS
SET decision = @decision,
    decision_at = @decisionAt,
    decision_by = @decisionBy,
    comments = @comments
WHERE tenant_id = @tenantId AND campaign_id = @campaignId AND item_id = @itemId AND decision = @expected";
        AddParam(cmd, "@expected", expected.ToString());
        AddParam(cmd, "@decision", decision.ToString());
        AddParam(cmd, "@decisionAt", FormatIso(DateTimeOffset.UtcNow));
        AddParam(cmd, "@decisionBy", decisionBy);
        AddParam(cmd, "@comments", comments);
        AddParam(cmd, "@tenantId", tenantId.Value);
        AddParam(cmd, "@campaignId", campaignId);
        AddParam(cmd, "@itemId", itemId);

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    // ---------------------------------------------------- HELPERS

    private static RecertificationCampaign ReadCampaign(DbDataReader reader)
    {
        var tenantId = reader.GetString(0);
        var campaignId = reader.GetString(1);
        var name = reader.GetString(2);
        var recertClass = Enum.Parse<RecertClass>(reader.GetString(3), ignoreCase: true);
        var status = Enum.Parse<RecertCampaignStatus>(reader.GetString(4), ignoreCase: true);
        var deadline = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var createdAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        return new RecertificationCampaign(tenantId, campaignId, name, recertClass, status, deadline, createdAt);
    }

    private static RecertificationItem ReadItem(DbDataReader reader)
    {
        var tenantId = reader.GetString(0);
        var campaignId = reader.GetString(1);
        var itemId = reader.GetString(2);
        var assetType = reader.GetString(3);
        var assetKey = reader.GetString(4);
        var assignedTo = reader.GetString(5);
        var reviewerId = reader.GetString(6);
        var decision = Enum.Parse<RecertDecision>(reader.GetString(7), ignoreCase: true);
        var decisionAt = reader.IsDBNull(8) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var decisionBy = reader.IsDBNull(9) ? null : reader.GetString(9);
        var comments = reader.IsDBNull(10) ? null : reader.GetString(10);

        return new RecertificationItem(tenantId, campaignId, itemId, assetType, assetKey, assignedTo, reviewerId, decision, decisionAt, decisionBy, comments);
    }

    private static string FormatIso(DateTimeOffset dt) => dt.ToUniversalTime().ToString("O");

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
