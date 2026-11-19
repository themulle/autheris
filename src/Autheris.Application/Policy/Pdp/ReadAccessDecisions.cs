namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;

/// <summary>
/// Read decisions for table-serving consumers: builds the <see cref="AccessRequest"/> (always <see cref="AccessOperation.Read"/>,
/// columns and supplemental consents forwarded unchanged) and records a value-free warning when the decision is a denial.
/// Exceptions and cancellation from the decision point propagate to the caller; they are never treated as an allow.
/// </summary>
public static partial class ReadAccessDecisions
{
    public static async Task<TableAccessDecision> DecideReadAsync(
        this IAccessDecisionPoint pdp,
        TableMetadata metadata,
        SecurityPrincipalContext subject,
        AccessTransport transport,
        IReadOnlyList<string>? requestedColumns,
        ILogger? logger,
        CancellationToken ct,
        IReadOnlyList<Consent>? supplementalConsents = null)
    {
        ArgumentNullException.ThrowIfNull(pdp);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(subject);

        var request = new AccessRequest(
            subject,
            metadata,
            AccessOperation.Read,
            transport,
            Columns: requestedColumns,
            SupplementalConsents: supplementalConsents);

        var decision = await pdp.DecideAsync(request, ct).ConfigureAwait(false);
        if (!decision.IsAllowed && logger is not null)
        {
            LogDenied(logger, metadata.Identifier, subject.UserSid.Value, string.Join("; ", decision.DeniedReasons));
        }

        return decision;
    }

    [LoggerMessage(EventId = 4601, Level = LogLevel.Warning, Message = "Access to table {Table} denied for subject {Subject}: {Reasons}")]
    private static partial void LogDenied(ILogger logger, Autheris.Domain.Common.TableIdentifier table, string subject, string reasons);
}
