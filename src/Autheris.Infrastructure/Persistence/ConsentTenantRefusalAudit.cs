using System.Text.Json;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// The refusal audit row of a consent tenant-integrity refusal (DEF-02, DEF-SC-02/04). Kept apart from <see cref="ConsentGrantSqlCore"/> so the
/// dialect SQL goldens only see the statement-level code.
/// </summary>
internal static class ConsentTenantRefusalAudit
{
    /// <summary>
    /// The explicit platform audit bucket of the refusal (DEF-SC-04). It is not a fallback for the consent's tenant, is referenced only by
    /// <see cref="Build"/> and is the platform bucket (DT-A-6); never use it as a tenant for a non-audit decision.
    /// </summary>
    public static TenantId IntegrityAuditTenant => TenantId.Platform;

    /// <summary>Ids only, never the tenant value (DEF-SC-01/02).</summary>
    public static AuditLogEntry Build(Sid actor, ConsentTenantIntegrityException refusal) => new()
    {
        TenantId = IntegrityAuditTenant,
        EventType = AuditEventTypes.ConsentTenantIntegrityRefused,
        ActorSid = actor,
        TargetTable = refusal.TargetTable ?? "unknown",
        Decision = "DENY",
        TraceId = Guid.NewGuid().ToString("N"),
        DetailsJson = JsonSerializer.Serialize(new { ConsentId = refusal.ConsentId, Code = ConsentTenantIntegrityException.Code })
    };
}
