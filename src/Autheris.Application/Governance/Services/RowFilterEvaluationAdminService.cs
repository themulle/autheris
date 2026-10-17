namespace Autheris.Application.Governance.Services;

using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Audit;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-8, SEC-RF107-14, SEC-RF107-06e): validation, persistence and audit of the row filter evaluation settings for the
/// REST endpoints and the MCP tools. The repository bumps the policy epoch in the transaction of the settings change and invalidates the epoch
/// cache after the commit; this service records the domain audit entry (actor, tenant, target table, rule id, before/after, no data values)
/// through the HMAC-chained audit writer with the dedicated event types. The caps are read per call, so a lowered cap applies at once.
/// </summary>
public sealed class RowFilterEvaluationAdminService : IRowFilterEvaluationAdminService
{
    private readonly IConsentApprovalRepository _consents;
    private readonly ITableMetadataRepository _tables;
    private readonly IAuditWriter _audit;
    private readonly IOptionsMonitor<GatewayOptions> _options;

    public RowFilterEvaluationAdminService(
        IConsentApprovalRepository consents,
        ITableMetadataRepository tables,
        IAuditWriter audit,
        IOptionsMonitor<GatewayOptions> options)
    {
        _consents = consents ?? throw new ArgumentNullException(nameof(consents));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    private RowFilterKeySetCacheOptions Caps => _options.CurrentValue.RowFilters.KeySetCache;

    public async Task<RowFilterEvaluationView?> GetRuleAsync(Guid rowFilterId, TenantId tenant, CancellationToken ct)
    {
        var row = await FindRuleAsync(rowFilterId, tenant, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var stored = StoredOf(row.Value.Filter);
        var meta = await _tables.GetTableMetadataAsync(row.Value.Table, ct).ConfigureAwait(false);
        return new RowFilterEvaluationView(stored, RowFilterEvaluationResolver.Resolve(stored, meta?.RowFilterEvaluation, Caps));
    }

    public async Task<RowFilterEvaluationPreview?> ValidateRuleAsync(
        Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var row = await FindRuleAsync(rowFilterId, tenant, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        ThrowIfInvalid(RowFilterEvaluationResolver.Validate(settings, Caps, row.Value.Filter, row.Value.Effect));
        return new RowFilterEvaluationPreview(row.Value.Table, StoredOf(row.Value.Filter));
    }

    public async Task<RowFilterEvaluationChange?> SetRuleAsync(
        Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, Sid actor, CancellationToken ct)
    {
        if (await ValidateRuleAsync(rowFilterId, tenant, settings, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var change = await _consents.UpdateRowFilterEvaluationAsync(rowFilterId, tenant, settings, ct).ConfigureAwait(false);
        if (change is null)
        {
            return null;
        }

        await AuditAsync(AuditEventTypes.RowFilterEvaluationChanged, "rule", change, tenant, actor).ConfigureAwait(false);
        return change;
    }

    public async Task<RowFilterEvaluationView?> GetObjectAsync(TableIdentifier table, CancellationToken ct)
    {
        var meta = await _tables.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return null;
        }

        var stored = meta.RowFilterEvaluation ?? RowFilterEvaluationSettings.Inherit;
        return new RowFilterEvaluationView(stored, RowFilterEvaluationResolver.Resolve(null, stored, Caps));
    }

    public async Task<RowFilterEvaluationPreview?> ValidateObjectAsync(TableIdentifier table, RowFilterEvaluationSettings? settings, CancellationToken ct)
    {
        if (settings is not null)
        {
            ThrowIfInvalid(RowFilterEvaluationResolver.Validate(settings, Caps, null, null));
        }

        var meta = await _tables.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        return meta is null ? null : new RowFilterEvaluationPreview(table, meta.RowFilterEvaluation ?? RowFilterEvaluationSettings.Inherit);
    }

    public async Task<RowFilterEvaluationChange?> SetObjectAsync(
        TableIdentifier table, RowFilterEvaluationSettings? settings, Sid actor, TenantId tenant, CancellationToken ct)
    {
        if (settings is not null)
        {
            ThrowIfInvalid(RowFilterEvaluationResolver.Validate(settings, Caps, null, null));
        }

        var change = await _tables.SetRowFilterEvaluationAsync(table, settings, actor, ct).ConfigureAwait(false);
        if (change is null)
        {
            return null;
        }

        await AuditAsync(AuditEventTypes.TableRowFilterStrategyChanged, "object", change, tenant, actor).ConfigureAwait(false);
        return change;
    }

    // Not found and other tenant are indistinguishable (SEC-RF107-14a).
    private async Task<(ConsentRowFilter Filter, ConsentEffect Effect, TenantId Tenant, TableIdentifier Table)?> FindRuleAsync(
        Guid rowFilterId, TenantId tenant, CancellationToken ct)
    {
        var row = await _consents.GetRowFilterAsync(rowFilterId, ct).ConfigureAwait(false);
        return row is { } found && string.Equals(found.Tenant.Value, tenant.Value, StringComparison.Ordinal) ? found : null;
    }

    private static RowFilterEvaluationSettings StoredOf(ConsentRowFilter filter)
        => new(filter.KeySetStrategy, filter.KeySetDurationSeconds, filter.KeySetMaxKeys);

    private static void ThrowIfInvalid(string? reason)
    {
        if (reason is not null)
        {
            throw new RowFilterEvaluationValidationException(reason);
        }
    }

    private static object Describe(RowFilterEvaluationSettings s) => new
    {
        strategy = RowFilterEvaluationInput.Format(s.Strategy),
        durationSeconds = s.DurationSeconds,
        maxKeys = s.MaxKeys
    };

    // Post-commit and required: an unavailable audit store surfaces as AuditUnavailableException (fail closed, like the key definition change).
    private Task AuditAsync(string eventType, string level, RowFilterEvaluationChange change, TenantId tenant, Sid actor)
        => _audit.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = eventType,
            ActorSid = actor,
            TargetTable = change.TargetTable.ToString(),
            Decision = "ALLOW",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                level,
                ruleId = change.RowFilterId,
                before = Describe(change.Before),
                after = Describe(change.After),
                epoch = change.NewEpoch
            })
        }, CancellationToken.None);
}
