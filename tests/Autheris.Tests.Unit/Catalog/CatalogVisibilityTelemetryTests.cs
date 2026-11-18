namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Audit;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Startup and configuration audit of the default-tenant mode (the tier gauge and tier tally were retired with the tier model).</summary>
public sealed class CatalogVisibilityTelemetryTests
{
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    private CatalogGovernanceHostedService Create(string? mode = null)
        => new(_audit, new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = string.Equals(mode, "Single", StringComparison.Ordinal) ? "default-tenant" : null }
        }), NullLogger<CatalogGovernanceHostedService>.Instance);

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_Startup_AuditsTheEffectiveDefaultVisibilityAndDefaultTenant()
    {
        await Create("Single").StartAsync(default);

        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.CatalogVisibilityChanged
            && e.DetailsJson.Contains("\"source\":\"startup\"")
            && e.DetailsJson.Contains("\"defaultTenant\":\"Configured\"")), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_Startup_NoDefaultTenant_IsAuditedAsNone()
    {
        await Create().StartAsync(default);

        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e => e.DetailsJson.Contains("\"defaultTenant\":\"None\"")), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_ConfigurationChange_IsAudited_OnlyWhenTheAuditedValuesChanged()
    {
        var service = Create();
        await service.StartAsync(default);
        _audit.ClearReceivedCalls();

        await service.OnOptionsChangedAsync(new GatewayOptions());
        await _audit.DidNotReceiveWithAnyArgs().RecordBestEffortAsync(default!, default);

        await service.OnOptionsChangedAsync(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = "default-tenant" } });
        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e =>
            e.DetailsJson.Contains("\"source\":\"configuration\"") && e.DetailsJson.Contains("\"defaultTenant\":\"Configured\"")), Arg.Any<CancellationToken>());
    }
}
