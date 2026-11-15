namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>Candidate consent or profile item passed into non-blocking approval-time effect preview.</summary>
public sealed record GrantPreviewRequestedItem(
    string Target,
    IReadOnlyList<ConsentColumnRule>? ColumnRules = null,
    IReadOnlyList<GrantRowScopeEntry>? RowScope = null,
    IReadOnlyList<WriteRule>? WriteRules = null,
    ColumnSetMode ColumnSetMode = ColumnSetMode.Explicit,
    ConsentEffect Effect = ConsentEffect.Allow,
    Guid? ItemId = null);

/// <summary>Non-blocking preview flags and counts returned for pending requests and profile assignments.</summary>
public sealed record GrantEffectPreviewResult(
    int MatchedObjectCount,
    bool IncludesFutureObjects,
    IReadOnlyList<RedundantGrantFlag> RedundantGrants,
    bool EmptyHardIntersection,
    IReadOnlyList<string> AffectedObjects);

/// <summary>Computes non-blocking structural effect previews for pending access requests and profile items.</summary>
public interface IGrantEffectPreview
{
    Task<GrantEffectPreviewResult> PreviewAsync(
        SecurityPrincipalContext subject,
        IReadOnlyList<GrantPreviewRequestedItem> requestedItems,
        CancellationToken ct = default);

    Task<GrantEffectPreviewResult> PreviewAsync(
        SecurityPrincipalContext actor,
        SecurityPrincipalContext subject,
        IReadOnlyList<GrantPreviewRequestedItem> requestedItems,
        CancellationToken ct = default) =>
        PreviewAsync(subject, requestedItems, ct);
}
