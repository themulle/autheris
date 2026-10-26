namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DatasourceSecretNamespaceTests
{
    private const string OperatorSecretValue = "SuperSecretOperatorHmacKey1234567890!";

    [Fact]
    public void DatasourceRef_MatchingOperatorConfigKey_IsNotResolved()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:DataMasking:HmacSecret"] = OperatorSecretValue
            })
            .Build();

        var provider = new DefaultEnvironmentSecretProvider(configuration: config);

        Should.Throw<KeyNotFoundException>(() =>
        {
            provider.GetSecretBytes("datasource:Gateway:DataMasking:HmacSecret");
        }).Message.ShouldContain("datasource secret not found");
    }

    [Fact]
    public void DatasourceRef_MatchingEnvVarAlias_IsNotResolved()
    {
        var envVarKey = "DATASOURCE_GATEWAY_DATAMASKING_HMACSECRET";
        using var env = new EnvVarScope(envVarKey, OperatorSecretValue);
        var provider = new DefaultEnvironmentSecretProvider();

        Should.Throw<KeyNotFoundException>(() =>
        {
            provider.GetSecretBytes("datasource:Gateway:DataMasking:HmacSecret");
        }).Message.ShouldContain("datasource secret not found");
    }

    [Fact]
    public void DatasourceRef_CaseVariantOfOperatorKey_IsNotResolved()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["gateway:datamasking:hmacsecret"] = OperatorSecretValue
            })
            .Build();

        var provider = new DefaultEnvironmentSecretProvider(configuration: config);

        Should.Throw<KeyNotFoundException>(() =>
        {
            provider.GetSecretBytes("datasource:GATEWAY:DATAMASKING:HMACSECRET");
        }).Message.ShouldContain("datasource secret not found");
    }

    [Fact]
    public void DatasourceRef_SetViaSetSecret_IsResolved()
    {
        var provider = new DefaultEnvironmentSecretProvider();
        var secretRef = "datasource:sales:api-key";
        var secretBytes = Encoding.UTF8.GetBytes("sales-secret-api-token-1234567890");

        provider.SetSecret(secretRef, secretBytes);
        var resolved = provider.GetSecretBytes(secretRef);

        resolved.ShouldBe(secretBytes);
    }

    [Fact]
    public void NonHttpsUriRef_DoesNotUseSegmentAlias()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["my-secret"] = OperatorSecretValue
            })
            .Build();

        var provider = new DefaultEnvironmentSecretProvider(configuration: config);

        Should.Throw<InvalidOperationException>(() =>
        {
            provider.GetSecretBytes("http://my-vault.vault.azure.net/secrets/my-secret");
        });

        Should.Throw<InvalidOperationException>(() =>
        {
            provider.GetSecretBytes("ldap://my-vault.vault.azure.net/secrets/my-secret");
        });
    }

    [Fact]
    public void HttpsVaultUri_StillResolvesSegmentAlias()
    {
        var secretValue = "ValidSecretTokenValueForVault123456";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["my-secret"] = secretValue
            })
            .Build();

        var provider = new DefaultEnvironmentSecretProvider(configuration: config);
        var bytes = provider.GetSecretBytes("https://my-vault.vault.azure.net/secrets/my-secret");

        Encoding.UTF8.GetString(bytes).ShouldBe(secretValue);
    }

    [Fact]
    public void ValidateClientRef_RejectsOperatorRootUnderDomainPrefix()
    {
        var domainPrefix = DatasourceSecretRefPolicy.DomainPrefix("sales"); // "datasource:sales:"

        Should.Throw<ArgumentException>(() =>
        {
            DatasourceSecretRefPolicy.ValidateClientRef("datasource:sales:Gateway:DataMasking:HmacSecret", domainPrefix);
        }).Message.ShouldContain("operator configuration root");

        Should.Throw<ArgumentException>(() =>
        {
            DatasourceSecretRefPolicy.ValidateClientRef("datasource:sales:ConnectionStrings:DefaultConnection", domainPrefix);
        }).Message.ShouldContain("operator configuration root");

        Should.Throw<ArgumentException>(() =>
        {
            DatasourceSecretRefPolicy.ValidateClientRef("datasource:sales:Authentication:Jwt:Key", domainPrefix);
        }).Message.ShouldContain("operator configuration root");

        // Valid domain ref does not throw
        Should.NotThrow(() =>
        {
            DatasourceSecretRefPolicy.ValidateClientRef("datasource:sales:custom-api-key", domainPrefix);
        });
    }

    [Fact]
    public async Task DeclarativeHttpExecutor_DatasourceRefToOperatorKey_FailsWithoutSendingSecret()
    {
        var sentRequests = new List<HttpRequestMessage>();
        var handler = new DelegatingHandlerStub(req =>
        {
            sentRequests.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"id\": 1, \"name\": \"test\"}]", Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:DataMasking:HmacSecret"] = OperatorSecretValue
            })
            .Build();

        var secretProvider = new DefaultEnvironmentSecretProvider(configuration: config);

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var executor = new DeclarativeHttpDataSourceExecutor(
            factory,
            NullLogger<DeclarativeHttpDataSourceExecutor>.Instance,
            secretProvider: secretProvider,
            environment: env);

        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.example.com",
            PathTemplate = "/customers",
            Method = "GET",
            AuthMode = HttpAuthMode.StaticApiKey,
            ApiKeyHeaderName = "X-Api-Key",
            ApiKeySecretName = "datasource:crm:Gateway:DataMasking:HmacSecret"
        };

        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "customers"),
            Table = new Table
            {
                SourceName = "crm",
                SchemaName = "public",
                TableName = "customers",
                DataSourceType = DataSourceType.HttpDeclarative,
                HttpEndpoint = descriptor
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        };

        var context = new DataSourceExecutionContext(
            "crm",
            metadata,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "Test")),
            TableAccessDecision.Allowed(
                metadata.Identifier,
                new Dictionary<string, ColumnAccessLevel>()),
            new Dictionary<string, object?>(),
            ["id", "name"]);

        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await executor.ExecuteAsync(context, CancellationToken.None);
        });

        sentRequests.ShouldBeEmpty();
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
