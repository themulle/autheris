namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed record ExplainSubjectRef(string? Sid, string? Upn);

public sealed record ExplainRequest(
    ExplainSubjectRef? Subject,
    [property: JsonPropertyName("object")] string? ObjectName,
    [property: JsonConverter(typeof(JsonStringEnumConverter<AccessOperation>))] AccessOperation? Operation,
    [property: JsonConverter(typeof(JsonStringEnumConverter<AccessTransport>))] AccessTransport? Transport = null,
    IReadOnlyList<string>? Columns = null);

public enum ExplainOutcome
{
    Ok,
    BadRequest,
    Forbidden,
    NotFound
}

public sealed record ExplainResult(ExplainOutcome Outcome, AccessExplanation? Explanation = null);

/// <summary>One active grant record as shown to an authorized administrator: ids, names and counts only, never row-filter values.</summary>
public sealed record GrantRecordView(
    Guid GrantId,
    string Effect,
    string GranteeType,
    string? GranteeSid,
    IReadOnlyList<string> Operations,
    IReadOnlyList<ColumnStateExplanation> Columns,
    IReadOnlyList<Guid> RowFilterIds,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    string? ApprovedBy);

public sealed record GrantsListResult(ExplainOutcome Outcome, IReadOnlyList<GrantRecordView>? Grants = null, bool Truncated = false);

public interface IAccessExplanationService
{
    Task<ExplainResult> ExplainAsync(SecurityPrincipalContext caller, ExplainRequest request, CancellationToken ct = default);

    Task<GrantsListResult> ListGrantsAsync(SecurityPrincipalContext caller, string? objectName, CancellationToken ct = default);
}

/// <summary>Builds the context of another subject for an explain call; null = not resolvable (the call is rejected, fail closed).</summary>
public interface IExplainSubjectResolver
{
    ValueTask<SecurityPrincipalContext?> ResolveAsync(string? sid, string? upn, TenantId tenant, CancellationToken ct);
}

/// <summary>
/// Default resolver: a SID is accepted, but without a verified token the group set is unknown, so the context stays
/// <c>Degraded</c> (the decision point then denies and the explanation says so). A UPN cannot be resolved without a directory.
/// </summary>
public sealed class DegradedExplainSubjectResolver : IExplainSubjectResolver
{
    public ValueTask<SecurityPrincipalContext?> ResolveAsync(string? sid, string? upn, TenantId tenant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return ValueTask.FromResult<SecurityPrincipalContext?>(null);
        }

        return ValueTask.FromResult<SecurityPrincipalContext?>(new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "explain"
        });
    }
}

/// <summary>
/// Explain and grants listing. The decision always comes from <see cref="IAccessDecisionPoint"/>
/// (no second decision logic). Roles only gate this control-plane read, they never grant data access.
/// </summary>
public sealed class AccessExplanationService(
    IAccessDecisionPoint decisionPoint,
    ITableMetadataRepository tables,
    ICatalogVisibilityService visibility,
    IDataOwnershipRepository owners,
    IConsentRepository consents,
    IExplainSubjectResolver subjectResolver,
    IAuditWriter audit,
    TimeProvider? timeProvider = null) : IAccessExplanationService
{
    private const int MaxGrantPages = 10;
    private const int MaxSubjectLength = 256;
    private static readonly Regex SubjectIdFormat = new(@"^[A-Za-z0-9._:@\-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ExplainResult> ExplainAsync(SecurityPrincipalContext caller, ExplainRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        var (outcome, explanation, subjectSid) = await ExplainCoreAsync(caller, request, ct).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.AuthzExplain, caller, request.ObjectName, subjectSid, outcome, explanation?.Allowed, ct).ConfigureAwait(false);
        return new ExplainResult(outcome, outcome == ExplainOutcome.Ok ? explanation : null);
    }

    public async Task<GrantsListResult> ListGrantsAsync(SecurityPrincipalContext caller, string? objectName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var (outcome, grants, truncated) = await ListGrantsCoreAsync(caller, objectName, ct).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.AuthzGrantsList, caller, objectName, null, outcome, null, ct).ConfigureAwait(false);
        return new GrantsListResult(outcome, outcome == ExplainOutcome.Ok ? grants : null, truncated);
    }

    private async Task<(ExplainOutcome, AccessExplanation?, string?)> ExplainCoreAsync(SecurityPrincipalContext caller, ExplainRequest request, CancellationToken ct)
    {
        var sidText = request.Subject?.Sid?.Trim();
        var upnText = request.Subject?.Upn?.Trim();
        if ((string.IsNullOrEmpty(sidText) && string.IsNullOrEmpty(upnText))
            || request.Operation is not { } operation || !Enum.IsDefined(operation)
            || (request.Transport is { } transport && !Enum.IsDefined(transport))
            || !IsValidSubjectId(sidText) || (upnText?.Length > MaxSubjectLength))
        {
            return (ExplainOutcome.BadRequest, null, string.IsNullOrEmpty(sidText) || sidText.Length > MaxSubjectLength ? null : sidText);
        }

        var (lookup, metadata) = await FindObjectAsync(caller, request.ObjectName, ct).ConfigureAwait(false);
        if (lookup != ExplainOutcome.Ok)
        {
            return (lookup, null, sidText);
        }

        try
        {
            var tenant = caller.TenantId;
            var isSelf = IsSelf(caller, sidText, upnText);
            if (!isSelf && !await IsPrivilegedAsync(caller, metadata!.Identifier, ct).ConfigureAwait(false))
            {
                return (ExplainOutcome.Forbidden, null, sidText);
            }

            var subject = isSelf ? caller : await subjectResolver.ResolveAsync(sidText, upnText, tenant, ct).ConfigureAwait(false);
            if (subject is null)
            {
                return (ExplainOutcome.BadRequest, null, sidText);
            }

            var explanation = await decisionPoint.ExplainAsync(
                new AccessRequest(subject, metadata!, operation, request.Transport ?? AccessTransport.Rest, request.Columns, Trace: true),
                ct).ConfigureAwait(false);
            var resolved = isSelf || subject.GroupResolutionStatus == Autheris.Domain.Identity.GroupResolutionStatus.Complete;
            return (ExplainOutcome.Ok, explanation with { SubjectResolved = resolved }, subject.UserSid.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any resolution error denies: the caller learns nothing beyond "not found".
            return (ExplainOutcome.NotFound, null, sidText);
        }
    }

    private async Task<(ExplainOutcome, IReadOnlyList<GrantRecordView>?, bool)> ListGrantsCoreAsync(SecurityPrincipalContext caller, string? objectName, CancellationToken ct)
    {
        var (lookup, metadata) = await FindObjectAsync(caller, objectName, ct).ConfigureAwait(false);
        if (lookup != ExplainOutcome.Ok)
        {
            return (lookup, null, false);
        }

        try
        {
            if (!await IsPrivilegedAsync(caller, metadata!.Identifier, ct).ConfigureAwait(false))
            {
                return (ExplainOutcome.Forbidden, null, false);
            }

            var views = new List<GrantRecordView>();
            var now = _time.GetUtcNow();
            var truncated = false;
            Guid? after = null;
            for (var page = 0; page < MaxGrantPages; page++)
            {
                var result = await consents.GetConsentPageAsync(new ConsentPageQuery(caller.TenantId, metadata.Identifier, AfterId: after), ct).ConfigureAwait(false);
                views.AddRange(result.Items.Where(c => c.IsActive(now)).Select(ToView));
                if (result.NextAfterId is not { } next)
                {
                    break;
                }

                after = next;
                truncated = page == MaxGrantPages - 1;
            }

            return (ExplainOutcome.Ok, views, truncated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (ExplainOutcome.NotFound, null, false);
        }
    }

    /// <summary>Uniform NotFound for unknown, foreign-tenant, hidden or unresolvable objects (no existence oracle).</summary>
    private async Task<(ExplainOutcome, TableMetadata?)> FindObjectAsync(SecurityPrincipalContext caller, string? objectName, CancellationToken ct)
    {
        if (!TableIdentifier.TryParse(objectName, out var id))
        {
            return (ExplainOutcome.BadRequest, null);
        }

        try
        {
            if (caller.ResolvedTenant is not { Value: not null } tenant)
            {
                return (ExplainOutcome.NotFound, null);
            }

            var metadata = await tables.GetTableMetadataAsync(id, ct).ConfigureAwait(false);
            if (metadata is null
                || (metadata.OwnerTenantId is { } ownerTenant && !string.Equals(ownerTenant, tenant.Value, StringComparison.Ordinal)))
            {
                return (ExplainOutcome.NotFound, null);
            }

            var visible = await visibility.FindVisibleAsync(new CatalogCaller(caller.Principal, tenant, CatalogChannel.Rest), id, ct).ConfigureAwait(false);
            return visible is null ? (ExplainOutcome.NotFound, null) : (ExplainOutcome.Ok, metadata);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (ExplainOutcome.NotFound, null);
        }
    }

    private static bool IsValidSubjectId(string? sid)
        => string.IsNullOrEmpty(sid) || (sid.Length <= MaxSubjectLength && SubjectIdFormat.IsMatch(sid));

    private static bool IsSelf(SecurityPrincipalContext caller, string? sid, string? upn)
    {
        var sidMatches = string.IsNullOrEmpty(sid) || caller.UserSid.Equals(new Sid(sid));
        var callerUpn = caller.Principal?.FindFirst(ClaimTypes.Upn)?.Value ?? caller.Principal?.FindFirst("upn")?.Value;
        var upnMatches = string.IsNullOrEmpty(upn) || (callerUpn is not null && string.Equals(upn, callerUpn, StringComparison.OrdinalIgnoreCase));
        return sidMatches && upnMatches;
    }

    private async Task<bool> IsPrivilegedAsync(SecurityPrincipalContext caller, TableIdentifier table, CancellationToken ct)
        => caller.HasRole(GatewayRole.GovernanceAdmin)
           || caller.HasRole(GatewayRole.SecurityAuditor)
           || await owners.IsAuthorizedApproverForTableAsync(table, caller.UserSid, ct).ConfigureAwait(false);

    private static GrantRecordView ToView(Consent c)
    {
        var operations = new List<string>();
        if (c.ColumnRules.Count > 0 || c.WriteRules.Count == 0)
        {
            operations.Add("Read");
        }

        operations.AddRange(c.WriteRules.Select(w => w.Operation.ToString()));
        return new GrantRecordView(
            c.Id,
            c.Effect.ToString(),
            c.GranteeType.ToString(),
            c.GranteeSid?.Value,
            operations,
            c.ColumnRules.Select(r => new ColumnStateExplanation(r.ColumnName, r.AccessLevel)).ToList(),
            c.RowFilters.Select(f => f.Id).ToList(),
            c.ValidFrom,
            c.EffectiveValidTo,
            c.CreatedBySid?.Value);
    }

    private Task AuditAsync(string eventType, SecurityPrincipalContext caller, string? objectName, string? subjectSid, ExplainOutcome outcome, bool? allowed, CancellationToken ct)
    {
        // Never contains predicate values or requested column data: only ids, the qualified object name and the outcome.
        var details = JsonSerializer.Serialize(new { subject = subjectSid, outcome = outcome.ToString(), allowed });
        return audit.RecordAsync(new AuditLogEntry
        {
            TenantId = caller.ResolvedTenant ?? default,
            OccurredAt = _time.GetUtcNow(),
            EventType = eventType,
            ActorSid = caller.UserSid,
            TargetTable = TableIdentifier.TryParse(objectName, out var id) ? id.ToString() : string.Empty,
            Decision = outcome == ExplainOutcome.Ok && allowed != false ? "ALLOW" : "DENY",
            DetailsJson = details
        }, ct);
    }
}
