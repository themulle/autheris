namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

[Collection("HostStartupSequential")]
[Trait("Category", "Security")]
public sealed class HostCompositionScopeValidationTests
{
    private const string NonConnectingPostgreSql =
        "Host=127.0.0.1;Port=1;Database=autheris_test;Username=autheris_test;Password=not-a-secret;SSL Mode=VerifyFull;Timeout=1";

    [Fact]
    public async Task Production_host_has_no_captive_dependencies()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "autheris-di-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var apiContentRoot = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Api");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production,
                ContentRootPath = Directory.Exists(apiContentRoot) ? apiContentRoot : tempRoot
            });

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = NonConnectingPostgreSql,
                ["Gateway:DataMasking:HmacSecretKeyVaultRef"] = "kv://masking-hmac",
                ["kv://masking-hmac"] = "0123456789012345678901234567890123456789",
                ["Gateway:Audit:ChainAnchorPath"] = Path.Combine(tempRoot, "audit-anchor"),
                ["Gateway:RestApi:BulkStream:SigningKey"] = "test-bulk-stream-signing-key-minimum-32-bytes",
                ["Gateway:Hosting:AllowedHosts:0"] = "gateway.corp.example",
                ["Gateway:Authentication:RequireKerberosOnly"] = "false",
                ["Gateway:Authentication:EntraId:Enabled"] = "true",
                ["Gateway:Authentication:EntraId:TenantId"] = "00000000-0000-0000-0000-000000000001",
                ["Gateway:Authentication:EntraId:ClientId"] = "00000000-0000-0000-0000-000000000002",
                ["Gateway:Authentication:EntraId:AllowedClientIds:0"] = "00000000-0000-0000-0000-000000000003",
                ["Gateway:Authentication:EntraId:Audience"] = "api://autheris-test",
                ["Authentication:Realms:Realms:workforce-entra:Kind"] = "EntraWorkforce",
                ["Authentication:Realms:Realms:workforce-entra:Issuers:0"] = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0",
                ["Authentication:Realms:Realms:workforce-entra:Audiences:0"] = "api://autheris-test",
                ["Authentication:Realms:Realms:workforce-entra:FixedTenantId"] = "tenant-test",
                ["Authentication:Realms:Realms:workforce-entra:GroupSource"] = "Token",
                ["Authentication:Realms:Realms:workforce-entra:Provisioning"] = "None",
                ["Gateway:State:StateDirectory"] = tempRoot
            });

            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });

            LegacySwitchGuard.ThrowIfLegacyKeysSet(builder.Configuration);
            SerilogSectionGuard.ThrowIfConfigured(builder.Configuration);
            var devReport = DevConfiguration.Apply(builder.Configuration, builder.Environment);
            GovernanceProviderGuard.ThrowIfUnsupported(builder.Configuration, builder.Environment);
            UnknownGatewayKeyGuard.ThrowIfUnknownKeys(builder.Configuration);
            builder.Services.AddSingleton(devReport);

            builder.Services.AddGatewayTelemetry(builder.Configuration);
            builder.Services.AddDataSourceHierarchicalConfiguration(builder.Configuration);
            var gatewayOptions = builder.Services.AddGatewayOptions(builder.Configuration, builder.Environment);
            builder.Services.AddGatewayInfrastructure(gatewayOptions, builder.Environment);
            builder.Services.AddGatewayAuth(gatewayOptions, builder.Environment);
            var demoDataEnabled = DemoDataSwitch.Resolve(gatewayOptions, builder.Environment.EnvironmentName);
            builder.Services.AddSingleton<IDemoDataSwitch>(new DemoDataSwitch(demoDataEnabled));
            builder.Services.AddGatewayGraphQL(gatewayOptions);
            builder.Services.AddAsyncJobsAndDatasourceTesting(builder.Configuration);
            using var bootstrapLoggerFactory = LoggerFactory.Create(static _ => { });
            builder.Services.AddGatewayModules(builder.Configuration, builder.Environment, gatewayOptions, bootstrapLoggerFactory);

            await using var app = builder.Build();
            app.Environment.IsProduction().ShouldBeTrue();
            GovernanceStartupAssertions.AssertAnchorStoreResolvable(app.Services);
            app.UseGatewayPipeline(gatewayOptions);
            app.MapGatewayEndpoints(gatewayOptions);
            _ = app.Services.GetServices<IHostedService>().ToList();
            await using var scope = app.Services.CreateAsyncScope();
            scope.ServiceProvider.ShouldNotBeNull();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
