namespace Autheris.Application.ConsentRequests;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Input of a table access (consent) request submission.</summary>
/// <param name="Domain">Domain of the requested table.</param>
/// <param name="Schema">Schema of the requested table.</param>
/// <param name="TableName">Name of the requested table.</param>
/// <param name="Justification">Business justification (5 to 2000 characters).</param>
/// <param name="DurationDays">Requested validity in days (1 to 365).</param>
/// <param name="IdempotencyKey">Optional client idempotency key.</param>
/// <param name="UserSid">Authenticated requester.</param>
/// <param name="Principal">Authenticated principal (claims used for tenant and identity resolution).</param>
/// <param name="ContextTenant">Tenant resolved for the current request/connection context, or the legacy single tenant.</param>
public sealed record ConsentRequestSubmission(
    string Domain,
    string Schema,
    string TableName,
    string Justification,
    int DurationDays,
    string? IdempotencyKey,
    Sid UserSid,
    ClaimsPrincipal? Principal,
    TenantId ContextTenant);

/// <summary>Outcome of a consent request submission.</summary>
public sealed class SubmitConsentRequestResult
{
    public Guid RequestId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ItsmTicketReference? ItsmTicketReference { get; init; }
}

/// <summary>Business failure of a consent use case; <see cref="Code"/> is the stable error code exposed to clients.</summary>
public sealed class ConsentRequestException : Exception
{
    public ConsentRequestException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Use case: a user requests access to a table.</summary>
public interface IConsentRequestService
{
    Task<SubmitConsentRequestResult> SubmitAsync(ConsentRequestSubmission command, CancellationToken ct = default);
}

/// <summary>
/// Seam for the external approval hand-off (ITSM ticket creation) of a consent request. The transactional outbox
/// replaces the implementation later without touching <see cref="IConsentRequestService"/>.
/// </summary>
public interface IConsentRequestDispatcher
{
    Task<ItsmTicketResult> DispatchAsync(ItsmTicketRequest request, ItsmSystemType preferredSystem, CancellationToken ct = default);
}
