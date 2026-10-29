using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>Audit event of a governance migration that ran DDL with the runtime login (I388-SC-13, plan Q-1). Carries no connection detail.</summary>
internal static class RuntimeDdlAudit
{
    public static AuditLogEntry Build(int appliedSteps) => new()
    {
        EventType = GovernanceMigrationCodes.RuntimeDdl,
        ActorSid = new Sid("SYSTEM"),
        TargetTable = "autheris_schema_migrations",
        Decision = "ALLOW",
        TraceId = Guid.NewGuid().ToString("N"),
        DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { AppliedSteps = appliedSteps })
    };
}
