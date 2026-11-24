namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Engine-backed implementation of <see cref="IConsentRequestService"/> used by the GraphQL, MCP and discovery entry points.
/// It only translates the legacy table-access submission shape into a <see cref="PermissionRequestSubmission"/> and
/// delegates to <see cref="IPermissionRequestEngine"/>; it never writes requests or grants itself.
/// </summary>
public sealed class ConsentRequestEndpointAdapter(
    IPermissionRequestEngine engine,
    IIdentitySubjectResolver? subjectResolver = null) : IConsentRequestService
{
    private readonly IPermissionRequestEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public async Task<SubmitConsentRequestResult> SubmitAsync(ConsentRequestSubmission command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var principal = command.Principal;
        ConsentSubmissionGuards.EnsurePersonSubject(principal, subjectResolver);

        if (!TableIdentifier.TryCreate(command.Domain, command.Schema, command.TableName, out var table))
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "Domain, Schema and TableName must be valid, non-empty values.");
        }

        if (command.RequestWriteOnly == true && command.RequestedWriteRules is not { Count: > 0 })
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "A write-only access request requires at least one write rule.");
        }

        var tenant = ConsentTenantResolver.Resolve(
            principal, command.ContextTenant, ClusterAdminPolicy.IsCanonicalClusterAdmin(principal));

        var hasClear = command.RequestedUnmaskColumns is { Count: > 0 };
        if (hasClear && command.RequestedWriteRules is { Count: > 0 })
        {
            throw new ConsentRequestException("INVALID_COLUMNS", "Clear columns cannot be requested together with write rules.");
        }

        PermissionSubmitResult primary;
        if (hasClear)
        {
            // Clear access is one Unmask request over the union of the requested columns.
            var columns = command.RequestedUnmaskColumns!
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            primary = await _engine.SubmitAsync(
                Build(command, tenant, ConsentRequestKind.Unmask, PermissionTarget.ForTable(table, columns), includeRead: false),
                ct).ConfigureAwait(false);
        }
        else
        {
            primary = await _engine.SubmitAsync(
                Build(command, tenant, ConsentRequestKind.Data, PermissionTarget.ForTable(table), includeRead: command.RequestWriteOnly != true),
                ct).ConfigureAwait(false);
        }

        return new SubmitConsentRequestResult
        {
            RequestId = primary.Request.Id,
            Status = primary.Request.Status,
            Message = primary.AutoApproved
                ? "[INSECURE GETTING STARTED] Consent request auto-approved."
                : "Access request submitted."
        };
    }

    public Task<bool> WithdrawAsync(Guid requestId, ClaimsPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ConsentSubmissionGuards.EnsurePersonSubject(principal, subjectResolver);
        return _engine.WithdrawAsync(requestId, principal, ct);
    }

    private static PermissionRequestSubmission Build(
        ConsentRequestSubmission command,
        TenantId tenant,
        ConsentRequestKind kind,
        PermissionTarget target,
        bool includeRead) =>
        new(
            command.Principal!,
            tenant,
            kind,
            target,
            GranteeType.User,
            command.UserSid.Value,
            command.Justification,
            command.RequestedValidFrom,
            command.RequestedValidTo,
            command.RequestedValidTo is null && string.IsNullOrWhiteSpace(command.RequestedDuration) ? command.DurationDays : null,
            command.RequestedDuration,
            command.RequestedWriteRules,
            includeRead);
}
