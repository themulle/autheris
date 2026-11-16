namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Evaluates non-blocking approval-time structural effects (matched objects, future object inclusion,
/// redundant grants, and empty hard-policy intersections) and audits every preview call.
/// </summary>
public sealed class GrantEffectPreviewService(
    ITableMetadataRepository tables,
    IConsentRepository consents,
    IAuditWriter audit,
    IVirtualFilterSnapshotProvider? filterSnapshots = null,
    TimeProvider? timeProvider = null) : IGrantEffectPreview
{
    private readonly ITableMetadataRepository _tables = tables ?? throw new ArgumentNullException(nameof(tables));
    private readonly IConsentRepository _consents = consents ?? throw new ArgumentNullException(nameof(consents));
    private readonly IAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly IVirtualFilterSnapshotProvider? _filterSnapshots = filterSnapshots;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<GrantEffectPreviewResult> PreviewAsync(
        SecurityPrincipalContext subject,
        IReadOnlyList<GrantPreviewRequestedItem> requestedItems,
        CancellationToken ct = default) =>
        PreviewAsync(subject, subject, requestedItems, ct);

    public async Task<GrantEffectPreviewResult> PreviewAsync(
        SecurityPrincipalContext actor,
        SecurityPrincipalContext subject,
        IReadOnlyList<GrantPreviewRequestedItem> requestedItems,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(requestedItems);

        var result = await EvaluateNonBlockingAsync(subject, requestedItems, ct).ConfigureAwait(false);
        await RecordPreviewAuditAsync(actor, subject, requestedItems, result, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<GrantEffectPreviewResult> EvaluateNonBlockingAsync(
        SecurityPrincipalContext subject,
        IReadOnlyList<GrantPreviewRequestedItem> requestedItems,
        CancellationToken ct)
    {
        bool includesFutureObjects = requestedItems.Any(static item =>
            !string.IsNullOrWhiteSpace(item.Target) &&
            item.Target.Contains('*', StringComparison.Ordinal));

        if (requestedItems.Count == 0 || subject.ResolvedTenant is not { Value: not null } tenant)
        {
            return new GrantEffectPreviewResult(0, includesFutureObjects, Array.Empty<RedundantGrantFlag>(), false, Array.Empty<string>());
        }

        IReadOnlyList<TableMetadata> allTables;
        try
        {
            allTables = await CatalogTablePageReader.ReadAllAsync(
                _tables,
                new CatalogTableQuery(IncludeInactive: false),
                static t => t.Table.IsActive,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GrantEffectPreviewResult(0, includesFutureObjects, Array.Empty<RedundantGrantFlag>(), false, Array.Empty<string>());
        }

        var tenantTables = allTables
            .Where(t => !t.Table.DiscoveryHidden &&
                        (t.OwnerTenantId is null || string.Equals(t.OwnerTenantId, tenant.Value, StringComparison.Ordinal)))
            .OrderBy(t => t.Identifier.ToString(), StringComparer.Ordinal)
            .ToList();

        var now = _time.GetUtcNow();
        var subjectSids = BuildSubjectSids(subject);
        IReadOnlyList<Consent> existingConsents = [];
        try
        {
            existingConsents = await _consents.GetAllActiveConsentsForSubjectsAsync(subjectSids, roles: null, now, tenant, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            existingConsents = Array.Empty<Consent>();
        }

        VirtualFilterSnapshot? snapshot = null;
        if (_filterSnapshots is not null)
        {
            try
            {
                snapshot = await _filterSnapshots.GetAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                snapshot = null;
            }
        }

        var affectedNames = new SortedSet<string>(StringComparer.Ordinal);
        var redundantFlags = new List<RedundantGrantFlag>();
        var seenRedundant = new HashSet<(Guid GrantId, Guid CoveredBy)>();
        bool emptyHardIntersection = false;

        foreach (var table in tenantTables)
        {
            var matchingSynthetics = new List<Consent>();
            foreach (var item in requestedItems)
            {
                if (!MatchesRequestedTarget(item.Target, table.Identifier, out var isPattern))
                {
                    continue;
                }

                matchingSynthetics.Add(ToSyntheticConsent(item, table.Identifier, tenant, subject.UserSid, isPattern, now));
            }

            if (matchingSynthetics.Count == 0)
            {
                continue;
            }

            affectedNames.Add(table.Identifier.ToString());

            var activeForTable = await LoadTableAllowsAsync(existingConsents, subjectSids, subject.UserSid, table.Identifier, tenant, now, ct).ConfigureAwait(false);
            foreach (var flag in EffectiveAccessAnalyzer.FindRedundantAgainstExisting(matchingSynthetics, activeForTable, table))
            {
                if (seenRedundant.Add((flag.GrantId, flag.CoveredBy)))
                {
                    redundantFlags.Add(flag);
                }
            }

            var (_, tableEmptyIntersection) = EffectiveAccessAnalyzer.AnalyzeHardPoliciesForObject(snapshot, tenant, subject, table);
            if (tableEmptyIntersection)
            {
                emptyHardIntersection = true;
            }
        }

        return new GrantEffectPreviewResult(
            affectedNames.Count,
            includesFutureObjects,
            redundantFlags,
            emptyHardIntersection,
            affectedNames.ToList());
    }

    private async Task<List<Consent>> LoadTableAllowsAsync(
        IReadOnlyList<Consent> cachedAll,
        IReadOnlySet<Sid> subjectSids,
        Sid userSid,
        TableIdentifier table,
        TenantId tenant,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var source = cachedAll;
        if (source.Count == 0)
        {
            try
            {
                source = await _consents.GetActiveConsentsForSubjectsAsync(subjectSids, table, now, tenant, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                source = Array.Empty<Consent>();
            }
        }

        return source
            .Where(c =>
                c.Effect == ConsentEffect.Allow &&
                c.TenantId == tenant &&
                c.IsActive(now) &&
                ConsentResolutionService.IsTableTargetMatch(c, table) &&
                GranteeMatcher.MatchesGrant(c.GranteeType, c.GranteeSid, userSid, subjectSids, subjectSids))
            .ToList();
    }

    private static bool MatchesRequestedTarget(string? target, TableIdentifier table, out bool isPattern)
    {
        isPattern = false;
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (target.Contains('*', StringComparison.Ordinal))
        {
            isPattern = true;
            return ObjectPatternSpec.TryParse(target, out var spec, out _) && spec!.Matches(table);
        }

        return TableIdentifier.TryParse(target, out var exact) && exact == table;
    }

    private static Consent ToSyntheticConsent(
        GrantPreviewRequestedItem item,
        TableIdentifier table,
        TenantId tenant,
        Sid subjectSid,
        bool isPattern,
        DateTimeOffset now) =>
        new()
        {
            Id = item.ItemId ?? Guid.NewGuid(),
            TenantId = tenant,
            TableIdentifier = isPattern ? default : table,
            ObjectPattern = isPattern ? item.Target : null,
            ColumnSetMode = item.ColumnSetMode,
            Effect = item.Effect,
            GranteeType = GranteeType.User,
            GranteeSid = subjectSid,
            ValidFrom = now.AddMinutes(-1),
            ValidTo = now.AddDays(30),
            ColumnRules = item.ColumnRules ?? [],
            RowScope = item.RowScope ?? [],
            WriteRules = item.WriteRules ?? []
        };

    private static HashSet<Sid> BuildSubjectSids(SecurityPrincipalContext subject)
    {
        var set = new HashSet<Sid> { subject.UserSid };
        foreach (var g in subject.GroupSids)
        {
            set.Add(g);
        }

        return set;
    }

    private Task RecordPreviewAuditAsync(
        SecurityPrincipalContext actor,
        SecurityPrincipalContext subject,
        IReadOnlyList<GrantPreviewRequestedItem> requestedItems,
        GrantEffectPreviewResult result,
        CancellationToken ct)
    {
        // Never includes predicate SQL or parameter values: counts, flags, and ids only.
        var details = JsonSerializer.Serialize(new
        {
            subject = subject.UserSid.Value,
            itemCount = requestedItems.Count,
            matchedObjectCount = result.MatchedObjectCount,
            includesFutureObjects = result.IncludesFutureObjects,
            redundantGrantCount = result.RedundantGrants.Count,
            emptyHardIntersection = result.EmptyHardIntersection
        });

        var firstTarget = requestedItems.Count == 1 && TableIdentifier.TryParse(requestedItems[0].Target, out var id)
            ? id.ToString()
            : string.Empty;

        return _audit.RecordAsync(new AuditLogEntry
        {
            TenantId = actor.ResolvedTenant ?? subject.ResolvedTenant ?? default,
            OccurredAt = _time.GetUtcNow(),
            EventType = AuditEventTypes.AuthzEffectPreview,
            ActorSid = actor.UserSid,
            TargetTable = firstTarget,
            Decision = "ALLOW",
            DetailsJson = details
        }, ct);
    }
}
