namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

public enum DocLinkOutcome { Ok, Unauthenticated, Forbidden, NotFound, InvalidRequest, Unavailable }

public sealed record DocLinkResult(DocLinkOutcome Outcome, int LinkCount = 0, string? Message = null)
{
    public bool Succeeded => Outcome == DocLinkOutcome.Ok;
}

/// <summary>Links documents and applications to discoverable catalog objects and hides documents from discovery.</summary>
public interface IDocLinkService
{
    Task<DocLinkResult> ReplaceDocumentLinksAsync(ClaimsPrincipal? principal, DocCaller caller, string appId, DocCategory category, string docId, IReadOnlyList<DocLinkTarget> targets, CancellationToken ct = default);

    Task<DocLinkResult> ReplaceApplicationLinksAsync(ClaimsPrincipal? principal, DocCaller caller, string appId, IReadOnlyList<DocLinkTarget> targets, CancellationToken ct = default);

    Task<DocLinkResult> SetDocumentHiddenAsync(ClaimsPrincipal? principal, DocCaller caller, string appId, DocCategory category, string docId, bool hidden, CancellationToken ct = default);
}

/// <summary>
/// Authorization, validation and fail-closed audit for document links. Every target must be discoverable for the actor
/// (otherwise 404, identical to unknown); the document writer or the owner/administrator of the targets may change links.
/// </summary>
public sealed class DocLinkService : IDocLinkService
{
    private readonly ITableMetadataRepository _metadata;
    private readonly IDataOwnershipRepository _owners;
    private readonly IDocLinkRepository _links;
    private readonly IDocumentRepository _documents;
    private readonly IAuditWriter _audit;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly TimeProvider _time;

    public DocLinkService(
        ITableMetadataRepository metadata,
        IDataOwnershipRepository owners,
        IDocLinkRepository links,
        IDocumentRepository documents,
        IAuditWriter audit,
        IOptions<GatewayOptions> gatewayOptions,
        TimeProvider? time = null)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _links = links ?? throw new ArgumentNullException(nameof(links));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _time = time ?? TimeProvider.System;
    }

    public async Task<DocLinkResult> ReplaceDocumentLinksAsync(ClaimsPrincipal? principal, DocCaller caller, string appId, DocCategory category, string docId, IReadOnlyList<DocLinkTarget> targets, CancellationToken ct = default)
    {
        var actor = ResolveActor(principal);
        if (actor is null) return Fail(DocLinkOutcome.Unauthenticated);
        if (Validate(targets) is { } invalid) return invalid;
        var doc = await FindDocumentAsync(actor, appId, category, docId, ct).ConfigureAwait(false);
        if (doc is null) return Fail(DocLinkOutcome.NotFound);
        var isWriter = IsWriter(caller, appId, doc.TenantId);
        if (doc.DiscoveryHidden && !isWriter) return Fail(DocLinkOutcome.NotFound);
        return await ReplaceAsync(actor, caller, DocLinkSource.ForDocument(new DocKey(doc.TenantId, appId, category, docId)), isWriter, targets, ct).ConfigureAwait(false);
    }

    public async Task<DocLinkResult> ReplaceApplicationLinksAsync(ClaimsPrincipal? principal, DocCaller caller, string appId, IReadOnlyList<DocLinkTarget> targets, CancellationToken ct = default)
    {
        var actor = ResolveActor(principal);
        if (actor is null) return Fail(DocLinkOutcome.Unauthenticated);
        if (!DocumentRules.IsValidAppId(appId)) return Fail(DocLinkOutcome.InvalidRequest, "Invalid application id.");
        if (Validate(targets) is { } invalid) return invalid;
        return await ReplaceAsync(actor, caller, DocLinkSource.ForApplication(appId), DocAccessPolicy.ResolveWriter(caller, appId, out _) is not null, targets, ct).ConfigureAwait(false);
    }

    public async Task<DocLinkResult> SetDocumentHiddenAsync(ClaimsPrincipal? principal, DocCaller caller, string appId, DocCategory category, string docId, bool hidden, CancellationToken ct = default)
    {
        var actor = ResolveActor(principal);
        if (actor is null) return Fail(DocLinkOutcome.Unauthenticated);
        var doc = await FindDocumentAsync(actor, appId, category, docId, ct).ConfigureAwait(false);
        if (doc is null) return Fail(DocLinkOutcome.NotFound);
        var isWriter = IsWriter(caller, appId, doc.TenantId);
        if (doc.DiscoveryHidden && !isWriter) return Fail(DocLinkOutcome.NotFound);
        var key = new DocKey(doc.TenantId, appId, category, docId);
        if (!isWriter)
        {
            var existing = await _links.ListForSourceAsync(actor.Tenant.Value, DocLinkSource.ForDocument(key), ct).ConfigureAwait(false);
            if (existing.Count == 0 || !await ManagesAllAsync(actor, existing.Select(l => l.Target).ToList(), ct).ConfigureAwait(false))
                return Fail(DocLinkOutcome.Forbidden);
        }

        var previous = await _documents.SetDiscoveryHiddenAsync(key, hidden, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (previous is null) return Fail(DocLinkOutcome.NotFound);
        try
        {
            await _audit.RecordRequiredAsync(NewEntry(actor, AuditEventTypes.DocVisibilityChanged, new { appId, category = category.ToString(), docId, official = doc.TenantId is null, before = previous.Value, after = hidden }), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            using var rollback = new CancellationTokenSource(RollbackTimeout);
            await _documents.SetDiscoveryHiddenAsync(key, previous.Value, _time.GetUtcNow(), rollback.Token).ConfigureAwait(false);
            if (ex is OperationCanceledException) throw;
            return Fail(DocLinkOutcome.Unavailable, "Audit persistence failed; the change was rolled back.");
        }

        return new DocLinkResult(DocLinkOutcome.Ok);
    }

    /// <summary>The compensating write runs on its own bounded token so a cancelled request still undoes an unaudited change.</summary>
    private static readonly TimeSpan RollbackTimeout = TimeSpan.FromSeconds(10);

    private async Task<DocLinkResult> ReplaceAsync(Actor actor, DocCaller caller, DocLinkSource source, bool isWriter, IReadOnlyList<DocLinkTarget> targets, CancellationToken ct)
    {
        var resolved = new List<(DocLinkTarget Target, bool Manages)>(targets.Count);
        foreach (var t in targets.Distinct())
        {
            var gate = await ResolveTargetAsync(actor, t, ct).ConfigureAwait(false);
            if (gate is null) return Fail(DocLinkOutcome.NotFound);
            resolved.Add(gate.Value);
        }

        if (!isWriter && (resolved.Count == 0 || resolved.Any(r => !r.Manages))) return Fail(DocLinkOutcome.Forbidden);

        var tenant = actor.Tenant.Value;
        var before = await _links.ListForSourceAsync(tenant, source, ct).ConfigureAwait(false);
        var final = resolved.Select(r => r.Target).ToList();
        if (!isWriter)
        {
            foreach (var kept in before.Select(l => l.Target).Where(t => !final.Contains(t)))
            {
                if (!await ManagesAllAsync(actor, [kept], ct).ConfigureAwait(false)) final.Add(kept);
            }
        }

        if (final.Count > DocLinkLimits.MaxTargetsPerSource) return Fail(DocLinkOutcome.InvalidRequest, "Too many link targets.");
        var beforeTargets = before.Select(l => l.Target).ToList();
        await _links.ReplaceForSourceAsync(tenant, source, final, actor.Sid.Value, ct).ConfigureAwait(false);
        try
        {
            await _audit.RecordRequiredAsync(NewEntry(actor, AuditEventTypes.DocLinksChanged, new
            {
                source = source.Kind.ToString(),
                appId = source.AppId,
                category = source.Category?.ToString(),
                docId = source.DocId,
                official = source.Kind == DocLinkSourceKind.Document && source.DocTenantId is null,
                before = beforeTargets.Select(Describe).ToList(),
                after = final.Select(Describe).ToList()
            }), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            using var rollback = new CancellationTokenSource(RollbackTimeout);
            await _links.ReplaceForSourceAsync(tenant, source, beforeTargets, actor.Sid.Value, rollback.Token).ConfigureAwait(false);
            if (ex is OperationCanceledException) throw;
            return Fail(DocLinkOutcome.Unavailable, "Audit persistence failed; the change was rolled back.");
        }

        return new DocLinkResult(DocLinkOutcome.Ok, final.Count);
    }

    private async Task<bool> ManagesAllAsync(Actor actor, IReadOnlyList<DocLinkTarget> targets, CancellationToken ct)
    {
        foreach (var t in targets)
        {
            var gate = await ResolveTargetAsync(actor, t, ct).ConfigureAwait(false);
            if (gate is not { Manages: true }) return false;
        }

        return true;
    }

    private async Task<(DocLinkTarget Target, bool Manages)?> ResolveTargetAsync(Actor actor, DocLinkTarget target, CancellationToken ct)
    {
        if (target.Kind == DocLinkTargetKind.Procedure) return null;
        if (!TableIdentifier.TryCreate(target.Domain, target.Schema, target.Name, out var id)) return null;
        var table = await _metadata.GetTableMetadataAsync(id, ct).ConfigureAwait(false);
        if (table is null || !table.Table.IsActive) return null;
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Value.Tenancy);
        var shared = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Value.Catalog.SharedTables);
        if (DiscoveryTenantScope.DropForeignVirtualTables([table], actor.Tenant).Count == 0 ||
            !DiscoveryTenantScope.IsInScope(table, actor.Tenant, catalogOwner, shared, new HashSet<TableIdentifier>())) return null;
        var isAdmin = actor.Scope is CallerScope.Global or CallerScope.CatalogOwner or CallerScope.Tenant;
        var isOwner = !isAdmin && await _owners.IsAuthorizedApproverForTableAsync(table.Identifier, actor.Sid, ct).ConfigureAwait(false);
        var manages = isAdmin || isOwner;
        if (table.Table.DiscoveryHidden && !manages) return null;
        string? column = null;
        if (target.Kind == DocLinkTargetKind.Column)
        {
            var col = table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, target.Column, StringComparison.OrdinalIgnoreCase));
            if (col is null || (col.DiscoveryHidden && !manages)) return null;
            column = col.ColumnName;
        }

        return (new DocLinkTarget(target.Kind, table.Identifier.Domain, table.Identifier.Schema, table.Identifier.TableName, column), manages);
    }

    private async Task<DocRecord?> FindDocumentAsync(Actor actor, string appId, DocCategory category, string docId, CancellationToken ct)
    {
        if (!DocumentRules.IsValidAppId(appId) || !DocumentRules.IsValidDocId(docId)) return null;
        return await _documents.GetAsync(new DocKey(actor.Tenant.Value, appId, category, docId), ct).ConfigureAwait(false)
            ?? await _documents.GetAsync(new DocKey(null, appId, category, docId), ct).ConfigureAwait(false);
    }

    private static bool IsWriter(DocCaller caller, string appId, string? docTenantId)
    {
        var writer = DocAccessPolicy.ResolveWriter(caller, appId, out _);
        return writer is not null && string.Equals(writer.TenantId, docTenantId, StringComparison.OrdinalIgnoreCase);
    }

    private static DocLinkResult? Validate(IReadOnlyList<DocLinkTarget> targets)
    {
        if (targets is null) return Fail(DocLinkOutcome.InvalidRequest, "Targets are required.");
        if (targets.Count > DocLinkLimits.MaxTargetsPerSource) return Fail(DocLinkOutcome.InvalidRequest, "Too many link targets.");
        foreach (var t in targets)
        {
            if (t is null || string.IsNullOrWhiteSpace(t.Domain) || string.IsNullOrWhiteSpace(t.Schema) || string.IsNullOrWhiteSpace(t.Name) ||
                (t.Kind == DocLinkTargetKind.Column) == string.IsNullOrWhiteSpace(t.Column))
                return Fail(DocLinkOutcome.InvalidRequest, "A link target is invalid.");
        }

        return null;
    }

    private Actor? ResolveActor(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true || principal.GetUserSid() is not { } sid || string.IsNullOrWhiteSpace(sid.Value) ||
            string.Equals(sid.Value, DiscoveryCallerGate.AnonymousMcpClientSid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sid.Value, DiscoveryCallerGate.AnonymousSidValue, StringComparison.OrdinalIgnoreCase)) return null;
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Value.Tenancy);
        var resolution = CallerScopeResolver.ResolutionOf(principal, catalogOwner);
        var scope = CallerScopeResolver.Resolve(principal, resolution, catalogOwner);
        return scope == CallerScope.None || resolution.Tenant is not { } tenant ? null : new Actor(sid, tenant, scope);
    }

    private AuditLogEntry NewEntry(Actor actor, string eventType, object details) => new()
    {
        TenantId = actor.Tenant,
        OccurredAt = _time.GetUtcNow(),
        ActorSid = actor.Sid,
        EventType = eventType,
        Decision = "ALLOW",
        DetailsJson = JsonSerializer.Serialize(details)
    };

    private static string Describe(DocLinkTarget t) => $"{t.Kind}:{t.Domain}.{t.Schema}.{t.Name}{(t.Column is null ? string.Empty : "." + t.Column)}";

    private static DocLinkResult Fail(DocLinkOutcome outcome, string? message = null) => new(outcome, 0, message);

    private sealed record Actor(Sid Sid, TenantId Tenant, CallerScope Scope);
}
