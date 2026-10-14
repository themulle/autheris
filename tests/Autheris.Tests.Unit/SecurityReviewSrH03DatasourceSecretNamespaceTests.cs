namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
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

/// <summary>
/// SR-H-03: a client-supplied Auth.SecretRef must never be used verbatim as a vault key. Datasource secrets live in
/// the server-controlled "datasource:" namespace on write and read.
/// </summary>
public sealed class SecurityReviewSrH03DatasourceSecretNamespaceTests
{
    private const string MinimalSpec = """
        { "openapi": "3.0.0", "info": { "title": "T", "version": "1" },
          "paths": { "/items": { "get": { "summary": "x" } } },
          "components": { "schemas": { "Item": { "type": "object", "properties": { "id": { "type": "string" } } } } } }
        """;

    private static DefaultEnvironmentSecretProvider CreateProvider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Authentication:ForwardAuth:SharedSecret"] = "configured-forwardauth-secret-0123456789"
        }).Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        return new DefaultEnvironmentSecretProvider(config, env);
    }

    [Theory]
    [InlineData("forwardauth-secret")]
    [InlineData("Gateway:Authentication:ForwardAuth:SharedSecret")]
    [InlineData("hmac-masking-secret")]
    [InlineData("file:/run/secrets/x")]
    public void SR_H_03_SetSecret_OutsideDatasourceNamespace_IsRejected(string key)
    {
        var provider = CreateProvider();
        Should.Throw<ArgumentException>(() => provider.SetSecret(key, Encoding.UTF8.GetBytes("attacker")));
    }

    [Fact]
    public void SR_H_03_SetSecret_DatasourceNamespace_IsAccepted()
    {
        var provider = CreateProvider();
        provider.SetSecret("datasource:t:d:n:secret", Encoding.UTF8.GetBytes("v"));
        Encoding.UTF8.GetString(provider.GetSecretBytes("datasource:t:d:n:secret")).ShouldBe("v");
    }

    [Fact]
    public void SR_H_03_ConfiguredForwardAuthSecret_CannotBeOverridden()
    {
        var provider = CreateProvider();
        try { provider.SetSecret("forwardauth-secret", Encoding.UTF8.GetBytes("attacker")); } catch (ArgumentException) { }
        Encoding.UTF8.GetString(provider.GetSecretBytes("forwardauth-secret")).ShouldBe("configured-forwardauth-secret-0123456789");
    }

    [Fact]
    public async Task SR_H_03_CatalogRegister_ClientSecretRefWithValue_IsReplacedByServerGeneratedNamespacedRef()
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        var ingestion = Substitute.For<IOpenApiIngestionService>();
        ingestion.IngestOpenApiJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DatasourceAuthDto?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new OpenApiIngestionResult(true, "T", 1, 1, ["items"], []));
        var service = new CatalogDiscoveryService(
            Substitute.For<ITableMetadataRepository>(), Substitute.For<IAuditLogRepository>(), Substitute.For<IRebacEvaluator>(),
            secrets, ingestion, Substitute.For<ICatalogSearchEngine>(), NullLogger<CatalogDiscoveryService>.Instance);
        var ctx = new RequestContext(new TenantId("tenant-a"), new Sid("user:admin"), Roles: ["GovernanceAdmin"]);

        var request = new DatasourceRegistrationRequest("Evil", "dom", SpecContent: MinimalSpec, BaseUrl: "https://evil.example",
            Auth: new DatasourceAuthDto("apiKey", Value: "x-secret-value", SecretRef: "forwardauth-secret"));
        await service.RegisterDatasourceAsync(request, ctx);

        secrets.DidNotReceive().SetSecret("forwardauth-secret", Arg.Any<byte[]>());
        secrets.Received(1).SetSecret(Arg.Is<string>(s => s.StartsWith("datasource:", StringComparison.Ordinal)), Arg.Any<byte[]>());
    }

    [Fact]
    public async Task SR_H_03_CatalogRegister_NonNamespacedSecretRefWithoutValue_IsRejected()
    {
        var ingestion = Substitute.For<IOpenApiIngestionService>();
        var service = new CatalogDiscoveryService(
            Substitute.For<ITableMetadataRepository>(), Substitute.For<IAuditLogRepository>(), Substitute.For<IRebacEvaluator>(),
            Substitute.For<IKeyVaultSecretProvider>(), ingestion, Substitute.For<ICatalogSearchEngine>(), NullLogger<CatalogDiscoveryService>.Instance);
        var ctx = new RequestContext(new TenantId("tenant-a"), new Sid("user:admin"), Roles: ["GovernanceAdmin"]);

        var request = new DatasourceRegistrationRequest("Evil", "dom", SpecContent: MinimalSpec, BaseUrl: "https://evil.example",
            Auth: new DatasourceAuthDto("apiKey", SecretRef: "Gateway:Authentication:ForwardAuth:SharedSecret"));

        await Should.ThrowAsync<ArgumentException>(() => service.RegisterDatasourceAsync(request, ctx));
        await ingestion.DidNotReceiveWithAnyArgs().IngestOpenApiJsonAsync(default!, default!, default, default, default, default);
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("oauth2")]
    public async Task SR_H_03_OpenApiIngest_ClientSecretRefWithValue_NeverWritesClientChosenKey(string type)
    {
        const string clientRef = "forwardauth-secret";
        var repo = Substitute.For<ITableMetadataRepository>();
        TableMetadata? saved = null;
        repo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(t => saved = t), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(c.Arg<TableMetadata>()));
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance, secretProvider: secrets);

        var auth = new DatasourceAuthDto(type, Value: "v", ClientSecret: "v", SecretRef: clientRef);
        await service.IngestOpenApiJsonAsync(MinimalSpec, "dom", "https://evil.example", auth, dryRun: false);

        secrets.DidNotReceive().SetSecret(clientRef, Arg.Any<byte[]>());
        saved.ShouldNotBeNull();
        saved!.Table.HttpEndpoint!.ApiKeySecretName.ShouldNotBeNull();
        saved.Table.HttpEndpoint.ApiKeySecretName!.ShouldStartWith("datasource:");
    }

    [Fact]
    public async Task SR_H_03_OpenApiIngest_NonNamespacedSecretRefWithoutValue_IsRejected()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance, secretProvider: Substitute.For<IKeyVaultSecretProvider>());
        var auth = new DatasourceAuthDto("apiKey", SecretRef: "FORWARDAUTH_SHARED_SECRET");

        await Should.ThrowAsync<ArgumentException>(() => service.IngestOpenApiJsonAsync(MinimalSpec, "dom", "https://evil.example", auth, dryRun: false));
        await repo.DidNotReceiveWithAnyArgs().UpsertTableMetadataAsync(default!, default);
    }

    [Fact]
    public async Task SR_H_03_DatasourceTest_NonNamespacedApiKeySecretName_IsNeverResolved()
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("evil", "dbo", "t"),
            Table = new Table
            {
                SourceName = "evil", SchemaName = "dbo", TableName = "t", DataSourceType = DataSourceType.HttpDeclarative, IsActive = true,
                HttpEndpoint = new HttpEndpointDescriptor
                {
                    Name = "e", BaseUrl = "https://api.crm-sample.com", PathTemplate = "/x",
                    AuthMode = HttpAuthMode.StaticApiKey, ApiKeyHeaderName = "X-Api-Key", ApiKeySecretName = "forwardauth-secret"
                }
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([table]);
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("stolen"));
        var seenHeaders = new List<string>();
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient("DeclarativeHttp").Returns(new HttpClient(new CapturingHandler(seenHeaders)));
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var service = new DatasourceTestingService(repo, http, Substitute.For<IAuditLogRepository>(), secrets, env, NullLogger<DatasourceTestingService>.Instance);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "a"), new Claim(ClaimTypes.Role, "Admin"), new Claim("tenant_id", "t")], "x"));

        await service.TestDatasourceAsync("evil.dbo.t", new DatasourceTestRequest(), user);

        secrets.DidNotReceive().GetSecretBytes(Arg.Any<string>());
        seenHeaders.ShouldNotContain("stolen");
    }

    private sealed class CapturingHandler(List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var h in request.Headers) { seen.AddRange(h.Value); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
