namespace Autheris.Infrastructure.Telemetry;

using System.Diagnostics;
using Autheris.Domain.Diagnostics;
using OpenTelemetry;

/// <summary>
/// Enriches started spans with ambient authenticated tenant identity from <see cref="TelemetryTenantContext"/>.
/// Enforces SEC-OT06-12.
/// </summary>
public sealed class TenantEnrichmentProcessor : BaseProcessor<Activity>
{
    private readonly bool _includeTenant;

    public TenantEnrichmentProcessor(bool includeTenant = true)
    {
        _includeTenant = includeTenant;
    }

    public override void OnStart(Activity activity)
    {
        if (!_includeTenant || activity == null)
        {
            return;
        }

        var tenantId = TelemetryTenantContext.CurrentTenantId;
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            activity.SetTag(TelemetryAttributes.AutherisTenantId, tenantId);
        }
    }
}
