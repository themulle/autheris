namespace Autheris.Infrastructure.Persistence;

using System;
using System.Linq;
using System.Text.Json;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Builds the audit entry for per-classification Clear granted with a consent (codes, approver, expiry; no data values).</summary>
internal static class ClearGrantAudit
{
    public static AuditLogEntry? TryBuild(Consent created)
    {
        var clear = created.ClassificationAccess.Where(static a => a.AccessLevel == ColumnAccessLevel.Clear).ToList();
        if (clear.Count == 0)
        {
            return null;
        }

        return new AuditLogEntry
        {
            TenantId = created.TenantId,
            EventType = AuditEventTypes.ClassificationClearGranted,
            ActorSid = new Sid(clear[0].ApprovedBySid ?? created.CreatedBySid?.Value ?? "UNKNOWN"),
            TargetTable = created.TableIdentifier.ToString(),
            Decision = "ALLOW",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                ConsentId = created.Id,
                created.ConsentRequestId,
                Grants = clear.Select(static a => new { a.Code, a.ApprovedBySid, a.ValidUntil })
            })
        };
    }
}
