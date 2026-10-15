namespace Autheris.Application.Events.Services;

using System;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Model;

/// <summary>
/// The CloudEvent <c>id</c> of a CDC webhook delivery: an opaque, deterministic hash of the tenant and the CDC event id (identical ids of two tenants never collide). Native ids can embed primary
/// key values (SQL Server change tracking) and are not bounded in length, so they never reach the outbox or the receiver.
/// </summary>
public static class CdcWebhookEventId
{
    public static string From(CdcEvent cdcEvent)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes((cdcEvent.TenantId ?? string.Empty) + "\n" + (cdcEvent.EventId ?? string.Empty)));
        return "cdc-" + Convert.ToHexStringLower(hash)[..40];
    }
}
