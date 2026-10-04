namespace Autheris.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IItsmWebhookHandler
{
    /// <summary>
    /// Verarbeitet ITSM-Statusänderungen unter strikter Prüfung von Tenant- und Ticket-Bindung.
    /// </summary>
    Task<bool> HandleStatusChangeAsync(
        string rawPayload,
        string hmacSignature,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
        => HandleStatusChangeAsync(rawPayload, hmacSignature, timestamp, null, ct);

    /// <summary>
    /// Verarbeitet ITSM-Statusänderungen mit optionalem Instanz-Header unter strikter Prüfung von Tenant- und Ticket-Bindung.
    /// Unterstützt kanonische DTOs sowie native ServiceNow- und Jira-Webhook-Payloads.
    /// </summary>
    Task<bool> HandleStatusChangeAsync(
        string rawPayload,
        string hmacSignature,
        DateTimeOffset timestamp,
        string? headerInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// Verarbeitet ITSM-Statusänderungen mit optionalem Instanz-Header und Roh-Timestamp-Header unter strikter Prüfung von Tenant- und Ticket-Bindung.
    /// Unterstützt kanonische DTOs sowie native ServiceNow- und Jira-Webhook-Payloads.
    /// </summary>
    Task<bool> HandleStatusChangeAsync(
        string rawPayload,
        string hmacSignature,
        DateTimeOffset timestamp,
        string? headerInstanceId,
        string? rawTimestampHeader,
        CancellationToken ct = default)
        => HandleStatusChangeAsync(rawPayload, hmacSignature, timestamp, headerInstanceId, ct);
}
