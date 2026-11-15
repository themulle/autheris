namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Builds the paged effective-access report for a subject across matching catalog objects using
/// <see cref="IAccessDecisionPoint.ExplainAsync"/> plus structural provenance and flag analysis.
/// </summary>
public sealed class EffectiveAccessService(
    IAccessDecisionPoint decisionPoint,
    ITableMetadataRepository tables,
    ICatalogVisibilityService visibility,
    IDataOwnershipRepository owners,
    IConsentRepository consents,
    IExplainSubjectResolver subjectResolver,
    IAuditWriter audit,
    IVirtualFilterSnapshotProvider? filterSnapshots = null,
    ICatalogVersionProvider? catalogVersionProvider = null,
    TimeProvider? timeProvider = null) : IEffectiveAccessService
{
    private const int MaxSubjectLength = 256;
    private static readonly Regex SubjectIdFormat = new(
        @"^[A-Za-z0-9._:@\-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    private readonly IAccessDecisionPoint _decisionPoint = decisionPoint ?? throw new ArgumentNullException(nameof(decisionPoint));
    private readonly ITableMetadataRepository _tables = tables ?? throw new ArgumentNullException(nameof(tables));
    private readonly ICatalogVisibilityService _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
    private readonly IDataOwnershipRepository _owners = owners ?? throw new ArgumentNullException(nameof(owners));
    private readonly IConsentRepository _consents = consents ?? throw new ArgumentNullException(nameof(consents));
    private readonly IExplainSubjectResolver _subjectResolver = subjectResolver ?? throw new ArgumentNullException(nameof(subjectResolver));
    private readonly IAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly IVirtualFilterSnapshotProvider? _filterSnapshots = filterSnapshots;
    private readonly ICatalogVersionProvider? _catalogVersionProvider = catalogVersionProvider;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<EffectiveAccessReportResult> GetEffectiveAccessAsync(
        SecurityPrincipalContext caller,
        EffectiveAccessReportQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(query);

        var (outcome, report, subjectSid) = await GetEffectiveAccessCoreAsync(caller, query, ct).ConfigureAwait(false);
        await AuditAsync(caller, query.ObjectPattern, subjectSid, outcome, report?.Objects.Count ?? 0, ct).ConfigureAwait(false);
        return new EffectiveAccessReportResult(outcome, outcome == EffectiveAccessOutcome.Ok ? report : null);
    }

    private async Task<(EffectiveAccessOutcome Outcome, EffectiveAccessReport? Report, string? SubjectSid)> GetEffectiveAccessCoreAsync(
        SecurityPrincipalContext caller,
        EffectiveAccessReportQuery query,
        CancellationToken ct)
    {
        var sidText = query.SubjectSid?.Trim();
        if (string.IsNullOrEmpty(sidText) ||
            sidText.Length > MaxSubjectLength ||
            !SubjectIdFormat.IsMatch(sidText))
        {
            return (EffectiveAccessOutcome.BadRequest, null, null);
        }

        if (!EffectiveAccessAnalyzer.TryParseObjectPattern(query.ObjectPattern, out var normalizedPattern, out var isWildcard) ||
            !EffectiveAccessAnalyzer.TryDecodeCursor(query.Cursor, out var cursorAfter))
        {
            return (EffectiveAccessOutcome.BadRequest, null, sidText);
        }

        var pageSize = query.PageSize ?? EffectiveAccessDefaults.DefaultPageSize;
        if (pageSize <= 0)
        {
            return (EffectiveAccessOutcome.BadRequest, null, sidText);
        }

        pageSize = Math.Min(pageSize, EffectiveAccessDefaults.MaxPageSize);

        if (caller.ResolvedTenant is not { Value: not null } tenant)
        {
            return (EffectiveAccessOutcome.NotFound, null, sidText);
        }

        var isSelf = caller.UserSid.Equals(new Sid(sidText));
        var isAdminOrAuditor = caller.HasRole(GatewayRole.GovernanceAdmin) || caller.HasRole(GatewayRole.SecurityAuditor);

        List<(TableMetadata Target, TableMetadata Raw)> scopedTables;
        try
        {
            if (normalizedPattern is not null && !isWildcard && TableIdentifier.TryParse(normalizedPattern, out var exactId))
            {
                var found = await FindVisibleSingleTableAsync(caller, tenant, exactId, isSelf && !isAdminOrAuditor, ct).ConfigureAwait(false);
                if (found is null)
                {
                    return (EffectiveAccessOutcome.NotFound, null, sidText);
                }

                if (!isSelf && !isAdminOrAuditor &&
                    !await _owners.IsAuthorizedApproverForTableAsync(exactId, caller.UserSid, ct).ConfigureAwait(false))
                {
                    return (EffectiveAccessOutcome.Forbidden, null, sidText);
                }

                scopedTables = [found.Value];
            }
            else
            {
                scopedTables = await FindVisiblePatternTablesAsync(caller, tenant, normalizedPattern, isSelf && !isAdminOrAuditor, ct).ConfigureAwait(false);
                if (!isSelf && !isAdminOrAuditor)
                {
                    var owned = new List<(TableMetadata Target, TableMetadata Raw)>(scopedTables.Count);
                    foreach (var pair in scopedTables)
                    {
                        if (await _owners.IsAuthorizedApproverForTableAsync(pair.Raw.Identifier, caller.UserSid, ct).ConfigureAwait(false))
                        {
                            owned.Add(pair);
                        }
                    }

                    if (owned.Count == 0)
                    {
                        return (EffectiveAccessOutcome.Forbidden, null, sidText);
                    }

                    scopedTables = owned;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (EffectiveAccessOutcome.NotFound, null, sidText);
        }

        SecurityPrincipalContext? subject;
        try
        {
            subject = isSelf
                ? caller
                : await _subjectResolver.ResolveAsync(sidText, upn: null, tenant, ct).ConfigureAwait(false);
            if (subject is null)
            {
                return (EffectiveAccessOutcome.BadRequest, null, sidText);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (EffectiveAccessOutcome.NotFound, null, sidText);
        }

        var ordered = scopedTables
            .OrderBy(static pair => pair.Raw.Identifier.ToString(), StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrEmpty(cursorAfter))
        {
            ordered = ordered
                .Where(pair => string.Compare(pair.Raw.Identifier.ToString(), cursorAfter, StringComparison.Ordinal) > 0)
                .ToList();
        }

        var hasMore = ordered.Count > pageSize;
        var pageItems = ordered.Take(pageSize).ToList();
        var nextCursor = hasMore && pageItems.Count > 0
            ? EffectiveAccessAnalyzer.EncodeCursor(pageItems[^1].Raw.Identifier.ToString())
            : null;

        var now = _time.GetUtcNow();
        var subjectSids = BuildAllSubjectSids(subject);
        IReadOnlyList<Consent> subjectConsents = [];
        try
        {
            subjectConsents = await _consents.GetAllActiveConsentsForSubjectsAsync(subjectSids, roles: null, now, tenant, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (EffectiveAccessOutcome.NotFound, null, sidText);
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

        long? catalogEpoch = null;
        if (_catalogVersionProvider is not null)
        {
            try
            {
                var catVer = await _catalogVersionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
                catalogEpoch = catVer.StructureVersion + catVer.ContentVersion;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                catalogEpoch = null;
            }
        }

        var entries = new List<EffectiveAccessObjectEntry>(pageItems.Count);
        foreach (var (targetMeta, rawMeta) in pageItems)
        {
            var explanation = await _decisionPoint.ExplainAsync(
                new AccessRequest(subject, targetMeta, AccessOperation.Read, AccessTransport.Rest, Trace: true),
                ct).ConfigureAwait(false);

            var activeForTable = await ResolveActiveAllowConsentsForTableAsync(
                subjectConsents,
                subjectSids,
                rawMeta.Identifier,
                tenant,
                now,
                ct).ConfigureAwait(false);

            var contributing = BuildContributingGrants(explanation, activeForTable, subject);
            var contributedEntities = contributing
                .Select(c => activeForTable.FirstOrDefault(a => a.Id == c.GrantId))
                .Where(static a => a is not null)
                .Cast<Consent>()
                .ToList();

            var redundant = EffectiveAccessAnalyzer.FindRedundantGrants(
                contributedEntities.Count >= 2 ? contributedEntities : activeForTable,
                rawMeta);
            var (hardPolicies, emptyHardIntersection) = EffectiveAccessAnalyzer.AnalyzeHardPoliciesForObject(
                snapshot,
                tenant,
                subject,
                rawMeta);

            var columns = explanation.Columns
                .Select(static c => new EffectiveColumnState(
                    c.ColumnName,
                    c.Level,
                    EffectiveAccessAnalyzer.ToColumnStateName(c.Level),
                    c.MaskType))
                .ToList();

            entries.Add(new EffectiveAccessObjectEntry(
                rawMeta.Identifier.ToString(),
                explanation.Allowed,
                columns,
                contributing,
                hardPolicies,
                redundant,
                emptyHardIntersection,
                explanation.PolicyEpoch,
                catalogEpoch));
        }

        var resolved = subject.GroupResolutionStatus == GroupResolutionStatus.Complete;
        var report = new EffectiveAccessReport(subject.UserSid.Value, entries, nextCursor, hasMore, resolved);
        return (EffectiveAccessOutcome.Ok, report, subject.UserSid.Value);
    }

    private async Task<(TableMetadata Target, TableMetadata Raw)?> FindVisibleSingleTableAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        TableIdentifier id,
        bool useVisibleMetadata,
        CancellationToken ct)
    {
        var metadata = await _tables.GetTableMetadataAsync(id, ct).ConfigureAwait(false);
        if (metadata is null)
        {
            metadata = await CatalogTablePageReader.FindFirstAsync(
                _tables,
                new CatalogTableQuery(
                    DataSource: SafeFilter(id.Domain),
                    Schema: SafeFilter(id.Schema),
                    NamePrefix: SafeFilter(id.TableName),
                    IncludeInactive: false),
                t => t.Table.IsActive && t.Identifier == id,
                ct: ct).ConfigureAwait(false);
        }

        if (metadata is null ||
            metadata.Table.DiscoveryHidden ||
            (metadata.OwnerTenantId is { } ownerTenant && !string.Equals(ownerTenant, tenant.Value, StringComparison.Ordinal)))
        {
            return null;
        }

        var visible = await _visibility.FindVisibleAsync(new CatalogCaller(caller.Principal, tenant, CatalogChannel.Rest), id, ct).ConfigureAwait(false);
        if (visible is not { Table: { } visibleTable })
        {
            return null;
        }

        return (useVisibleMetadata ? visibleTable : metadata, metadata);
    }

    private static string? SafeFilter(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CatalogTableQuery.MaxFilterLength &&
        value.Trim().Length == value.Length &&
        value.IndexOfAny(['\0', '\r', '\n']) < 0
            ? value
            : null;

    private async Task<List<(TableMetadata Target, TableMetadata Raw)>> FindVisiblePatternTablesAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        string? normalizedPattern,
        bool useVisibleMetadata,
        CancellationToken ct)
    {
        var all = await CatalogTablePageReader.ReadAllAsync(
            _tables,
            new CatalogTableQuery(IncludeInactive: false),
            t => t.Table.IsActive &&
                 !t.Table.DiscoveryHidden &&
                 (t.OwnerTenantId is not { } ownerTenant || string.Equals(ownerTenant, tenant.Value, StringComparison.Ordinal)) &&
                 EffectiveAccessAnalyzer.MatchesObjectPattern(normalizedPattern, t.Identifier),
            ct: ct).ConfigureAwait(false);
        var catalogCaller = new CatalogCaller(caller.Principal, tenant, CatalogChannel.Rest);
        var list = new List<(TableMetadata Target, TableMetadata Raw)>();

        foreach (var meta in all)
        {
            var visible = await _visibility.FindVisibleAsync(catalogCaller, meta.Identifier, ct).ConfigureAwait(false);
            if (visible is { Table: { } visibleTable })
            {
                list.Add((useVisibleMetadata ? visibleTable : meta, meta));
            }
        }

        return list;
    }

    private async Task<List<Consent>> ResolveActiveAllowConsentsForTableAsync(
        IReadOnlyList<Consent> cachedAllConsents,
        IReadOnlySet<Sid> subjectSids,
        TableIdentifier table,
        TenantId tenant,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var source = cachedAllConsents;
        if (source.Count == 0)
        {
            source = await _consents.GetActiveConsentsForSubjectsAsync(subjectSids, table, now, tenant, ct).ConfigureAwait(false);
        }

        return source
            .Where(c =>
                c.Effect == ConsentEffect.Allow &&
                c.TenantId == tenant &&
                c.IsActive(now) &&
                ConsentResolutionService.IsTableTargetMatch(c, table) &&
                GranteeMatcher.MatchesGrant(c.GranteeType, c.GranteeSid, subjectSids.First(), subjectSids, subjectSids))
            .ToList();
    }

    private static List<EffectiveContributingGrant> BuildContributingGrants(
        AccessExplanation explanation,
        IReadOnlyList<Consent> activeForTable,
        SecurityPrincipalContext subject)
    {
        var allSids = BuildAllSubjectSids(subject);
        var byId = activeForTable.ToDictionary(static c => c.Id.ToString("D"), StringComparer.OrdinalIgnoreCase);
        var result = new List<EffectiveContributingGrant>();
        var seen = new HashSet<Guid>();

        foreach (var grantRef in explanation.Grants)
        {
            if (byId.TryGetValue(grantRef.GrantId, out var consent))
            {
                if (seen.Add(consent.Id))
                {
                    result.Add(ToContributingGrant(consent, subject, allSids));
                }
            }
            else if (Guid.TryParse(grantRef.GrantId, out var parsedId) && seen.Add(parsedId))
            {
                result.Add(new EffectiveContributingGrant(parsedId, grantRef.Source.ToString(), Array.Empty<EffectiveRowScopeSummary>()));
            }
        }

        if (result.Count == 0 && explanation.Allowed && activeForTable.Count > 0)
        {
            foreach (var consent in activeForTable.OrderBy(static c => c.Id))
            {
                if (seen.Add(consent.Id))
                {
                    result.Add(ToContributingGrant(consent, subject, allSids));
                }
            }
        }

        return result;
    }

    private static EffectiveContributingGrant ToContributingGrant(
        Consent consent,
        SecurityPrincipalContext subject,
        IReadOnlySet<Sid> allSids)
    {
        var via = EffectiveAccessAnalyzer.ResolveProvenanceVia(consent, subject.UserSid, subject.GroupSids, allSids);
        var rowScope = consent.RowScope
            .Select(static r => new EffectiveRowScopeSummary(
                r.PolicyName,
                EffectiveAccessAnalyzer.CountParameters(r.Parameters),
                r.Uncovered))
            .ToList();
        return new EffectiveContributingGrant(consent.Id, via, rowScope);
    }

    private static HashSet<Sid> BuildAllSubjectSids(SecurityPrincipalContext subject)
    {
        var set = new HashSet<Sid> { subject.UserSid };
        foreach (var g in subject.GroupSids)
        {
            set.Add(g);
        }

        return set;
    }

    private Task AuditAsync(
        SecurityPrincipalContext caller,
        string? objectPattern,
        string? subjectSid,
        EffectiveAccessOutcome outcome,
        int objectCount,
        CancellationToken ct)
    {
        // Never logs SQL predicates or parameter values: only ids, counts, and outcome.
        var details = JsonSerializer.Serialize(new
        {
            subject = subjectSid,
            outcome = outcome.ToString(),
            objectCount
        });

        var target = TableIdentifier.TryParse(objectPattern, out var id)
            ? id.ToString()
            : string.Empty;

        return _audit.RecordAsync(new AuditLogEntry
        {
            TenantId = caller.ResolvedTenant ?? default,
            OccurredAt = _time.GetUtcNow(),
            EventType = AuditEventTypes.AuthzEffectiveAccessReport,
            ActorSid = caller.UserSid,
            TargetTable = target,
            Decision = outcome == EffectiveAccessOutcome.Ok ? "ALLOW" : "DENY",
            DetailsJson = details
        }, ct);
    }
}
