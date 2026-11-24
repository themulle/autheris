namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed partial class AccessDecisionPoint
{
    /// <summary>
    /// The decision to run a write procedure exposed as a data product. It needs an <see cref="WriteOperation.ExecuteWrite"/> grant on the
    /// product object itself (never implied by a read <c>Execute</c> grant), an authenticated caller on the procedure transport, and no
    /// deny on the product. It decides no columns and no rows: a write procedure returns no result set.
    /// </summary>
    private async ValueTask<TableAccessDecision> DecideExecuteWriteAsync(AccessRequest request, CancellationToken ct)
    {
        var subject = request.Subject;
        var table = request.Object.Identifier;
        if (!subject.IsAuthenticated)
        {
            return PreconditionDenied(table, "IDENTITY_INCOMPLETE", "Identity", null, "Access denied: write execution requires an authenticated caller.");
        }

        if (request.Transport != AccessTransport.Procedure)
        {
            return PreconditionDenied(table, "WRITE_TRANSPORT_NOT_WIRED", "Transport", request.Transport.ToString(),
                $"Write access on transport '{request.Transport}' is not permitted (dml_transport_not_wired).");
        }

        if (_writeGrantEvaluator == null)
        {
            return TableAccessDecision.Denied(table, "Write execution denied by policy (write grant evaluator unavailable).") with
            {
                Reasons = [new DecisionReason(DecisionStep.DefaultDeny, "POLICY_UNAVAILABLE", "WriteGrant", null)],
                EvaluatedByPdp = true
            };
        }

        var principal = subject.Principal ?? BuildWritePrincipal(subject);
        WriteGrantDecision grant;
        try
        {
            grant = await _writeGrantEvaluator.EvaluateAsync(
                new Autheris.Application.Policy.WriteGrants.WriteGrantQuery(principal, subject.TenantId, request.Object, WriteOperation.ExecuteWrite, new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            grant = WriteGrantDecision.Denied("evaluator_error");
        }

        if (!grant.IsAllowed)
        {
            return TableAccessDecision.Denied(table, "Write execution is not permitted (no_write_grant).") with
            {
                DenialKind = TableAccessDenialKind.ConsentMissing,
                Reasons = [new DecisionReason(DecisionStep.DefaultDeny, grant.DenyCode ?? "no_write_grant", "WriteGrant", null)],
                EvaluatedByPdp = true
            };
        }

        var matching = await LoadMatchingWriteConsentsAsync(principal, subject, table, _timeProvider.GetUtcNow(), request.SupplementalConsents, prefetch: null, ct).ConfigureAwait(false);
        if (matching.HasUnconditionalDeny)
        {
            return TableAccessDecision.Denied(table, "Access denied: explicit DENY consent on the product.") with
            {
                DenialKind = TableAccessDenialKind.ConsentDeny,
                Reasons = [new DecisionReason(DecisionStep.ExplicitDeny, "CONSENT_DENY", "Consent", null)],
                EvaluatedByPdp = true
            };
        }

        return TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: false) with
        {
            WriteGrant = new TableWriteDecision(grant.RowScope, grant.WritableColumns, grant.MaxRowsPerStatement, grant.GrantIds, grant.DenyCode),
            EvaluatedByPdp = true
        };
    }
}
