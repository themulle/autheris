namespace Autheris.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed record ItsmTicketRequest(
    TenantId Tenant,
    Sid RequesterSid,
    TableIdentifier TargetTable,
    string Justification,
    int DurationDays,
    JustificationCategory? TriageCategory,
    double? TriageConfidence
);

public sealed record ItsmTicketResult(
    bool Success,
    ItsmTicketReference? TicketReference,
    string? ErrorCode,
    string? ErrorMessage
);

public interface IItsmWorkflowClient
{
    ItsmSystemType SystemType { get; }
    Task<ItsmTicketResult> CreateAccessTicketAsync(ItsmTicketRequest request, CancellationToken ct = default);
}
