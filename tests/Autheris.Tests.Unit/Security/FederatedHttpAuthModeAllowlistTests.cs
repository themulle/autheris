namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Analysis;
using Xunit;

/// <summary>
/// <c>WebSql:CrossSource:AllowedHttpAuthModes</c> is enforced fail-closed in staging, before any source is read:
/// a federated read of an HTTP source whose auth mode is not listed is denied with a stable code (mapped to 403).
/// </summary>
public sealed class FederatedHttpAuthModeAllowlistTests
{
    private const string Tenant = "tenant_test";

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1")], "Test"));

    private static FederationBudget Budget() => new(5, 1000, 5000, 32 * 1024 * 1024, 128 * 1024 * 1024, 30, 4);

    private static StagingTableRequest HttpRequest(HttpAuthMode mode)
    {
        var meta = new TableMetadata
        {
            Identifier = new TableIdentifier("logistics", "v1", "shipments"),
            Table = new Table
            {
                TableName = "shipments",
                SchemaName = "v1",
                SourceType = "Http",
                DataSourceType = DataSourceType.HttpDeclarative,
                SourceName = "logistics",
                IsActive = true,
                HttpEndpoint = new HttpEndpointDescriptor
                {
                    BaseUrl = "https://api.logistics.internal",
                    PathTemplate = "/shipments",
                    CompleteResponse = true,
                    AuthMode = mode
                }
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
        };

        return new StagingTableRequest(
            new TableAccessTarget(null, "v1", "shipments", "shipments", "v1.shipments"),
            meta,
            TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            "s0",
            ["id"]);
    }

    private static (FederatedStagingService Service, IAutherisConnectorRegistry Registry) Create(List<string>? allowedModes)
    {
        var registry = Substitute.For<IAutherisConnectorRegistry>();
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { CrossSource = new CrossSourceOptions { Enabled = true, AllowedHttpAuthModes = allowedModes } }
        };
        var service = new FederatedStagingService(
            registry,
            Substitute.For<IColumnMaskingProvider>(),
            Substitute.For<IAuditLogRepository>(),
            Options.Create(options),
            NullLogger<FederatedStagingService>.Instance);
        return (service, registry);
    }

    // The stub registry has no connector: a request that passes the auth-mode gate reaches the read phase and stops there.
    private static async Task ShouldPassAuthGateAsync(FederatedStagingService service, HttpAuthMode mode)
    {
        var ex = await Should.ThrowAsync<GatewaySecurityException>(async () =>
            await service.StageAsync([HttpRequest(mode)], User(), new TenantId(Tenant), Budget()));
        ex.ErrorCode.ShouldBe("CONNECTOR_UNAVAILABLE");
    }

    [Fact]
    public async Task ModeNotListed_IsDenied_WithStableCode_BeforeAnyRead()
    {
        var (service, registry) = Create(["None"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(async () =>
            await service.StageAsync([HttpRequest(HttpAuthMode.StaticApiKey)], User(), new TenantId(Tenant), Budget()));

        ex.ErrorCode.ShouldBe("HTTP_AUTH_MODE_DENIED");
        registry.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ModeNoneNotListed_IsDenied()
    {
        var (service, _) = Create(["ClientCredentials"]);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(async () =>
            await service.StageAsync([HttpRequest(HttpAuthMode.None)], User(), new TenantId(Tenant), Budget()));

        ex.ErrorCode.ShouldBe("HTTP_AUTH_MODE_DENIED");
    }

    [Fact]
    public async Task ModeListed_CaseInsensitive_IsAllowed()
    {
        var (service, _) = Create(["staticapikey"]);

        await ShouldPassAuthGateAsync(service, HttpAuthMode.StaticApiKey);
    }

    [Fact]
    public async Task NotConfigured_DefaultAllowsNoneStaticApiKeyAndClientCredentials()
    {
        var (service, _) = Create(null);

        foreach (var mode in new[] { HttpAuthMode.None, HttpAuthMode.StaticApiKey, HttpAuthMode.ClientCredentials })
        {
            await ShouldPassAuthGateAsync(service, mode);
        }
    }

    [Fact]
    public async Task ForwardBearerToken_RemainsDenied_EvenIfListed()
    {
        var (service, _) = Create(["ForwardBearerToken"]);

        await Should.ThrowAsync<GatewaySecurityException>(async () =>
            await service.StageAsync([HttpRequest(HttpAuthMode.ForwardBearerToken)], User(), new TenantId(Tenant), Budget()));
    }
}
