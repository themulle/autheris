namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Assignment of data-source owners. The caller (GovernanceAdmin or TenantAdmin of the tenant, checked by the route) is the control
/// plane; every assignment and removal is audited before it is reported as done.
/// </summary>
public sealed partial class DataSourceOwnerService(IDataProductRepository products, IAuditWriter audit, TimeProvider? timeProvider = null)
{
    private readonly IDataProductRepository _products = products ?? throw new ArgumentNullException(nameof(products));
    private readonly IAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_.-]{0,126}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SourceRegex();

    public Task<IReadOnlyList<DataSourceOwner>> ListAsync(TenantId tenant, string dataSource, CancellationToken ct = default) =>
        _products.GetDataSourceOwnersAsync(tenant, Source(dataSource), ct);

    /// <summary>Assigns the owner; false when already assigned.</summary>
    public async Task<bool> AssignAsync(TenantId tenant, Sid actor, string dataSource, Sid owner, CancellationToken ct = default)
    {
        var source = Source(dataSource);
        if (string.IsNullOrWhiteSpace(owner.Value) || owner.Value.Length > 256)
        {
            throw new ProductException(ProductFailure.Invalid, ProductErrorCodes.DefinitionInvalid);
        }

        var now = _time.GetUtcNow();
        var added = await _products.AddDataSourceOwnerAsync(new DataSourceOwner(tenant, source, owner, actor, now), ct).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.DataSourceOwnerAssigned, tenant, actor, source, owner, added, now, ct).ConfigureAwait(false);
        return added;
    }

    public async Task<bool> RemoveAsync(TenantId tenant, Sid actor, string dataSource, Sid owner, CancellationToken ct = default)
    {
        var source = Source(dataSource);
        var removed = await _products.RemoveDataSourceOwnerAsync(tenant, source, owner, ct).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.DataSourceOwnerRemoved, tenant, actor, source, owner, removed, _time.GetUtcNow(), ct).ConfigureAwait(false);
        return removed;
    }

    private static string Source(string dataSource)
    {
        var normalized = (dataSource ?? string.Empty).Trim().ToLowerInvariant();
        return SourceRegex().IsMatch(normalized) ? normalized : throw new ProductException(ProductFailure.Invalid, ProductErrorCodes.DefinitionInvalid);
    }

    private Task AuditAsync(string eventType, TenantId tenant, Sid actor, string source, Sid owner, bool changed, DateTimeOffset now, CancellationToken ct) =>
        _audit.RecordRequiredAsync(new AuditLogEntry
        {
            EventType = eventType,
            TenantId = tenant,
            ActorSid = actor,
            TargetTable = $"datasource.{source}",
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new { dataSource = source, owner = owner.Value, changed }),
            OccurredAt = now
        }, ct);
}
