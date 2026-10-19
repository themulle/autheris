namespace Autheris.Tests.Unit.Telemetry;

using Autheris.Domain.Diagnostics;
using Shouldly;
using Xunit;

public sealed class TelemetrySpanCatalogTests
{
    [Fact]
    public void IsAllowed_PermitsAllowedKeysForSpan()
    {
        TelemetrySpanCatalog.IsAllowed("autheris.sql.compile", TelemetryAttributes.DbSystemName).ShouldBeTrue();
        TelemetrySpanCatalog.IsAllowed("autheris.sql.compile", TelemetryAttributes.AutherisSqlStatementClass).ShouldBeTrue();
        TelemetrySpanCatalog.IsAllowed("autheris.governance.decide", TelemetryAttributes.AutherisDecisionStage).ShouldBeTrue();
        TelemetrySpanCatalog.IsAllowed("autheris.audit.append", TelemetryAttributes.AutherisAuditTier).ShouldBeTrue();
    }

    [Fact]
    public void IsAllowed_RejectsUnpermittedKeys()
    {
        // Unapproved/sensitive keys should be rejected
        TelemetrySpanCatalog.IsAllowed("autheris.sql.compile", "user.sid").ShouldBeFalse();
        TelemetrySpanCatalog.IsAllowed("autheris.sql.compile", "password").ShouldBeFalse();
        TelemetrySpanCatalog.IsAllowed("autheris.governance.decide", "raw_sql").ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_RejectsUnknownSpan_UnlessBaseAttribute()
    {
        TelemetrySpanCatalog.IsAllowed("unknown.custom.span", "custom.tag").ShouldBeFalse();
        TelemetrySpanCatalog.IsAllowed("unknown.custom.span", TelemetryAttributes.AutherisTenantId).ShouldBeTrue();
    }
}
