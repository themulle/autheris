namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SR-H-03: client-supplied Auth.SecretRef must never be used verbatim as vault key.</summary>
public sealed class SrH03DatasourceSecretRefTests
{
    private const string Spec = """{"openapi":"3.0.0","info":{"title":"T","version":"1"},"paths":{"/a":{"get":{"summary":"x"}}},"components":{"schemas":{"A":{"type":"object","properties":{"id":{"type":"string"}}}}}}""";

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static DefaultEnvironmentSecretProvider RealProvider(string configuredKey, string configuredValue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [configuredKey] = configuredValue })
            .Build();
        return new DefaultEnvironmentSecretProvider(configuration, Env(Environments.Production));
    }

    private static CatalogDiscoveryService Discovery(IKeyVaultSecretProvider provider, IOpenApiIngestionService ingestion) =>
        new(Substitute.For<ITableMetadataRepository>(), Substitute.For<IAuditLogRepository>(),
            Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions()), null,
            provider, ingestion, Substitute.For<ICatalogSearchEngine>(), NullLogger<CatalogDiscoveryService>.Instance);

    private static RequestContext Ctx() => new(new TenantId("tenant-a"), new Sid("admin"), Roles: ["GovernanceAdmin"]);

    [Fact]
    public async Task SR_H_03_Register_WellKnownSecretRefWithValue_IsRejected_AndSecretUnchanged()
    {
        var configured = new string('g', 40);
        var provider = RealProvider("FORWARDAUTH_SHARED_SECRET", configured);
        var ingestion = Substitute.For<IOpenApiIngestionService>();
        var service = Discovery(provider, ingestion);

        var request = new DatasourceRegistrationRequest("Crane", "telemetry", SpecContent: Spec,
            Auth: new DatasourceAuthDto("apiKey", Name: "X-K", Value: "attacker-chosen-secret", SecretRef: "forwardauth-secret"));

        await Should.ThrowAsync<ArgumentException>(() => service.RegisterDatasourceAsync(request, Ctx()));

        Encoding.UTF8.GetString(provider.GetSecretBytes("forwardauth-secret")).ShouldBe(configured);
    }

    [Theory]
    [InlineData("ConnectionStrings:X")]
    [InlineData("file:/run/secrets/db")]
    [InlineData("datasource:other:crane:secret")]
    [InlineData("datasource:telemetry:crane:../../x")]
    public async Task SR_H_03_Register_ForeignSecretRefWithoutValue_IsRejected(string secretRef)
    {
        var ingestion = Substitute.For<IOpenApiIngestionService>();
        var service = Discovery(Substitute.For<IKeyVaultSecretProvider>(), ingestion);

        var request = new DatasourceRegistrationRequest("Crane", "telemetry", SpecContent: Spec,
            Auth: new DatasourceAuthDto("apiKey", Name: "X-K", SecretRef: secretRef));

        await Should.ThrowAsync<ArgumentException>(() => service.RegisterDatasourceAsync(request, Ctx()));
        await ingestion.DidNotReceiveWithAnyArgs().IngestOpenApiJsonAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task SR_H_03_Register_WithoutSecretRef_UsesServerGeneratedDatasourceRef()
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        var ingestion = Substitute.For<IOpenApiIngestionService>();
        ingestion.IngestOpenApiJsonAsync(default!, default!, default, default, default, default).ReturnsForAnyArgs(
            new OpenApiIngestionResult(true, "T", 1, 1, ["a"], []));
        var service = Discovery(secrets, ingestion);

        var request = new DatasourceRegistrationRequest("Crane", "telemetry", SpecContent: Spec,
            Auth: new DatasourceAuthDto("apiKey", Name: "X-K", Value: "legit-secret"));
        var result = await service.RegisterDatasourceAsync(request, Ctx());

        result.Success.ShouldBeTrue();
        secrets.Received().SetSecret("datasource:telemetry:crane:secret", Arg.Any<byte[]>());
    }

    [Theory]
    [InlineData("apiKey", null)]
    [InlineData("http", "bearer")]
    [InlineData("oauth2", null)]
    public async Task SR_H_03_OpenApiIngestion_ForeignSecretRef_IsRejected(string type, string? scheme)
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        var service = new OpenApiIngestionService(Substitute.For<ITableMetadataRepository>(),
            NullLogger<OpenApiIngestionService>.Instance, secretProvider: secrets);
        var auth = new DatasourceAuthDto(type, Scheme: scheme, Value: "v", ClientSecret: "v", SecretRef: "audit-hmac-key");

        await Should.ThrowAsync<ArgumentException>(() =>
            service.IngestOpenApiJsonAsync(Spec, "telemetry", defaultBaseUrl: null, auth: auth, dryRun: false));
        secrets.DidNotReceiveWithAnyArgs().SetSecret(default!, default!);
    }

    [Fact]
    public async Task SR_H_03_AccessPlanning_ForeignSecretRef_IsRejected()
    {
        var service = new AccessPlanningService(Substitute.For<IAuditLogRepository>(), Substitute.For<ITableMetadataRepository>(),
            Substitute.For<IRebacStore>(), new TotpVerificationService(), new InMemoryTotpSecretStore());
        var request = new AdminRegisterDatasourceRequest("crane", "telemetry",
            Auth: new DatasourceAuthDto("apiKey", SecretRef: "ConnectionStrings:Default"));

        await Should.ThrowAsync<ArgumentException>(() => service.RegisterDatasourceAsync(request, "S-1-5-21-1-500"));
    }

    [Fact]
    public async Task SR_H_03_AccessPlanning_OwnNamespaceRef_IsAccepted()
    {
        var service = new AccessPlanningService(Substitute.For<IAuditLogRepository>(), Substitute.For<ITableMetadataRepository>(),
            Substitute.For<IRebacStore>(), new TotpVerificationService(), new InMemoryTotpSecretStore());
        var request = new AdminRegisterDatasourceRequest("crane", "telemetry",
            Auth: new DatasourceAuthDto("apiKey", SecretRef: "vault://datasources/telemetry/crane/auth_secret"));

        var result = await service.RegisterDatasourceAsync(request, "S-1-5-21-1-500");
        result.SecretRef.ShouldBe("vault://datasources/telemetry/crane/auth_secret");
    }

    [Theory]
    [InlineData("forwardauth-secret")]
    [InlineData("audit-hmac-key")]
    [InlineData("ConnectionStrings:X")]
    [InlineData("file:/run/secrets/x")]
    public void SR_H_03_Provider_SetSecret_OutsideDatasourceNamespace_IsRefused(string secretRef)
    {
        var provider = RealProvider("FORWARDAUTH_SHARED_SECRET", new string('g', 40));
        Should.Throw<ArgumentException>(() => provider.SetSecret(secretRef, [1, 2, 3]));
        Encoding.UTF8.GetString(provider.GetSecretBytes("forwardauth-secret")).ShouldBe(new string('g', 40));
    }

    [Fact]
    public void SR_H_03_Provider_SetSecret_InsideDatasourceNamespace_Works()
    {
        var provider = RealProvider("X", "y");
        provider.SetSecret("datasource:telemetry:crane:secret", [1, 2, 3]);
        provider.GetSecretBytes("datasource:telemetry:crane:secret").ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task SR_H_03_TestingService_PersistedForeignRef_IsNeverResolved()
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("leak"));
        var table = new Table
        {
            SourceName = "crm", SchemaName = "dbo", TableName = "customers", DataSourceType = DataSourceType.HttpDeclarative,
            IsActive = true,
            HttpEndpoint = new HttpEndpointDescriptor
            {
                BaseUrl = "https://api.crm-sample.com", PathTemplate = "/x", AuthMode = HttpAuthMode.StaticApiKey,
                ApiKeyHeaderName = "X-Api-Key", ApiKeySecretName = "ConnectionStrings:Default"
            }
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(
            [new TableMetadata { Identifier = new TableIdentifier("crm", "dbo", "customers"), Table = table, Columns = [] }]);
        string? sentKey = null;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("DeclarativeHttp").Returns(new HttpClient(new CapturingHandler(r => sentKey = r.Headers.TryGetValues("X-Api-Key", out var v) ? v.First() : null)));
        var claims = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "a"), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"), new System.Security.Claims.Claim("tenant_id", "t")], "T"));
        var service = new DatasourceTestingService(repo, factory, Substitute.For<IAuditLogRepository>(), secrets, Env("Development"),
            NullLogger<DatasourceTestingService>.Instance);

        await service.TestDatasourceAsync("crm.dbo.customers", new DatasourceTestRequest(), claims);

        sentKey.ShouldBeNull();
        secrets.DidNotReceive().GetSecretBytes("ConnectionStrings:Default");
    }

    private sealed class CapturingHandler(Action<System.Net.Http.HttpRequestMessage> onSend) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            onSend(request);
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
