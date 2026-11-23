using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Governance.Requests;

/// <summary>Kind of access an IT administrator may self-grant.</summary>
public enum PermissionKind
{
    Data = 0,
    Unmask = 1
}

/// <summary>
/// IT-administrator self-grant command. Deliberately carries no self-grant flag: the service is the only completer of a self-grant.
/// </summary>
public sealed record SelfGrantCommand(
    SecurityPrincipalContext Actor,
    PermissionKind Kind,
    PermissionTarget Target,
    GranteeType GranteeType,
    string GranteeRef,
    DateTimeOffset ValidTo,
    string Justification);

/// <summary>
/// Only entry point that may complete a self-grant. Refusals are audited fail-closed as <c>SELF_GRANT_DENIED</c>;
/// if that audit cannot be written the call still fails and never allows.
/// </summary>
public sealed class SelfGrantService(
    PermissionRequestEngine engine,
    IAuditWriter auditWriter,
    TimeProvider? timeProvider = null,
    ILogger<SelfGrantService>? logger = null)
{
    private readonly PermissionRequestEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly IAuditWriter _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<PermissionDecisionResult> SelfGrantAsync(SelfGrantCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        PermissionDecisionResult result;
        try
        {
            result = await _engine.ExecuteSelfGrantAsync(command, ct).ConfigureAwait(false);
        }
        catch (ConsentDecisionException ex) when (ex.StatusCode is 403 or 422)
        {
            await RecordDeniedAsync(command, ex.ErrorCode, ct).ConfigureAwait(false);
            throw;
        }

        // The grant is committed: a notification failure must neither fail the call nor undo or hide the grant.
        try
        {
            await _engine.NotifySelfGrantAsync(result.Request, command.Actor, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Self-grant notification for request {RequestId} failed after the grant was committed.", result.Request.Id);
        }

        return result;
    }

    private Task RecordDeniedAsync(SelfGrantCommand command, string errorCode, CancellationToken ct)
    {
        var actor = command.Actor;
        var tenant = actor is null || TenantId.IsReserved(actor.TenantId) || string.IsNullOrWhiteSpace(actor.TenantId.Value)
            ? TenantId.Platform
            : actor.TenantId;
        var target = command.Target?.HasTable == true ? command.Target.Table!.Value.ToString() : string.Empty;

        return _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = AuditEventTypes.SelfGrantDenied,
            ActorSid = actor?.UserSid ?? default,
            TargetTable = target,
            Decision = "DENY",
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            TenantId = tenant,
            DetailsJson = new PermissionAuditDetails(
                Actor: actor?.UserSid.Value ?? string.Empty,
                Subject: command.GranteeRef ?? string.Empty,
                TargetObject: target,
                ValidFrom: null,
                ValidTo: command.ValidTo,
                Justification: command.Justification,
                Capacity: ApproverCapacity.SelfGrant,
                SelfGrant: true,
                RequestId: null,
                ActorKind: PermissionAuditDetails.ResolveActorKind(actor),
                GrantKind: command.Kind == PermissionKind.Unmask ? PermissionGrantKind.Unmask.ToSqlString() : PermissionGrantKind.Data.ToSqlString(),
                ErrorCode: errorCode).ToDetailsJson()
        }, ct);
    }
}
