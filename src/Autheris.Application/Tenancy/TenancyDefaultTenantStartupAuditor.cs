namespace Autheris.Application.Tenancy;

using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// DT-A-8 / DT-SC-11: writes the audit event <c>TENANCY_DEFAULT_TENANT_CONFIGURED</c> (platform bucket) when a default tenant is
/// configured and logs the start-up line either way. Takes the value from <see cref="ITenantResolver"/> (DT-SC-01).
/// </summary>
public sealed partial class TenancyDefaultTenantStartupAuditor(
    ITenantResolver resolver,
    IAuditWriter audit,
    ILogger<TenancyDefaultTenantStartupAuditor> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (resolver.ConfiguredDefaultTenant is not { } defaultTenant)
        {
            LogNotConfigured(logger);
            return;
        }

        LogConfigured(logger, defaultTenant.Value);
        await audit.RecordAsync(new AuditLogEntry
        {
            TenantId = TenantId.Platform,
            EventType = AuditEventTypes.TenancyDefaultTenantConfigured,
            ActorSid = new Sid("S-1-0-0"),
            Decision = "ALLOW",
            TargetTable = "tenancy/default-tenant",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new { DefaultTenantId = defaultTenant.Value })
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Gateway:Tenancy:DefaultTenantId is configured ({DefaultTenantId}): principals without a tenant claim are assigned this tenant.")]
    private static partial void LogConfigured(ILogger logger, string defaultTenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gateway:Tenancy:DefaultTenantId is not configured: principals without a tenant claim are rejected.")]
    private static partial void LogNotConfigured(ILogger logger);
}
