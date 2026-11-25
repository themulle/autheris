namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Input of a table access (consent) request submission.</summary>
/// <param name="Domain">Domain of the requested table.</param>
/// <param name="Schema">Schema of the requested table.</param>
/// <param name="TableName">Name of the requested table.</param>
/// <param name="Justification">Business justification (5 to 2000 characters).</param>
/// <param name="DurationDays">Requested validity in days (used when <paramref name="RequestedValidTo"/> and <paramref name="RequestedDuration"/> are omitted).</param>
/// <param name="IdempotencyKey">Optional client idempotency key.</param>
/// <param name="UserSid">Authenticated requester.</param>
/// <param name="Principal">Authenticated principal (claims used for tenant and identity resolution).</param>
/// <param name="ContextTenant">Tenant resolved for the current request/connection context, or the legacy single tenant.</param>
/// <param name="Context">Optional operational context provided by the requester.</param>
/// <param name="RequestedWriteRules">Optional requested write rules for insert/update grants.</param>
/// <param name="RequestWriteOnly">Optional flag controlling whether write requests omit read column grants.</param>
/// <param name="RequestedDuration">Optional ISO-8601 duration string (for example, <c>P2W</c>).</param>
/// <param name="RequestedValidFrom">Optional start of the requested validity window.</param>
/// <param name="RequestedValidTo">Optional explicit end of the requested validity window.</param>
/// <param name="RequestedUnmaskColumns">Optional columns the requester wants in clear form: submitted as one per-object unmask request.</param>
public sealed record ConsentRequestSubmission(
    string Domain,
    string Schema,
    string TableName,
    string Justification,
    int DurationDays,
    string? IdempotencyKey,
    Sid UserSid,
    ClaimsPrincipal? Principal,
    TenantId ContextTenant,
    AccessRequestContextInput? Context = null,
    IReadOnlyList<WriteRule>? RequestedWriteRules = null,
    bool? RequestWriteOnly = null,
    string? RequestedDuration = null,
    DateTimeOffset? RequestedValidFrom = null,
    DateTimeOffset? RequestedValidTo = null,
    IReadOnlyList<string>? RequestedUnmaskColumns = null);

/// <summary>Outcome of a consent request submission.</summary>
public sealed class SubmitConsentRequestResult
{
    public Guid RequestId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>Business failure of a consent use case; <see cref="Code"/> is the stable error code exposed to clients.</summary>
public sealed class ConsentRequestException : Exception
{
    public ConsentRequestException(string code, string message, int? retryAfterSeconds = null)
        : base(message)
    {
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public string Code { get; }

    /// <summary>Seconds after which a rate-limited caller may retry (only set for <c>RATE_LIMIT_EXCEEDED</c>).</summary>
    public int? RetryAfterSeconds { get; }
}

/// <summary>Use case: a user requests access to a table.</summary>
public interface IConsentRequestService
{
    Task<SubmitConsentRequestResult> SubmitAsync(ConsentRequestSubmission command, CancellationToken ct = default);
    Task<bool> WithdrawAsync(Guid requestId, ClaimsPrincipal principal, CancellationToken ct = default);
}
