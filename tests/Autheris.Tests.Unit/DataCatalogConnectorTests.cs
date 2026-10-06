namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DataCatalogConnectorTests
{
    [Fact]
    public async Task OpenMetadataCatalogAdapter_QueriesTables_AndMapsColumnsAndTags()
    {
        // EXT-MOVE: OpenMetadata as data catalog is served by OpenMetadataCatalogAdapter on top of the hardened
        // IOpenMetadataClient (the former core OpenMetadataDataCatalogClient was removed).
        var jsonResponse = """
        {
          "data": [
            {
              "id": "6f1c3a52-3d43-4a3b-9b1e-6a1f0d3f5c11",
              "name": "customer_records",
              "fullyQualifiedName": "sales.crm.public.customer_records",
              "service": { "name": "sales" },
              "databaseSchema": { "name": "public" },
              "columns": [
                {
                  "name": "email",
                  "dataType": "varchar",
                  "tags": [
                    { "tagFQN": "PII.Sensitive" }
                  ]
                },
                {
                  "name": "iban",
                  "dataType": "varchar",
                  "tags": [
                    { "tagFQN": "Classification.PII" }
                  ]
                }
              ]
            }
          ],
          "paging": { "total": 1 }
        }
        """;

        var handler = new MockHttpMessageHandler(jsonResponse);
        var httpClient = new HttpClient(handler);

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                ServerUrl = "https://openmetadata.corp.internal/api/v1",
                AuthToken = "test-token"
            }
        });

        var omClient = new OpenMetadataClient(httpClient, options, NullLogger<OpenMetadataClient>.Instance, null, DevEnv());
        var client = new OpenMetadataCatalogAdapter(omClient, NullLogger<OpenMetadataCatalogAdapter>.Instance);

        client.ProviderType.ShouldBe(DataCatalogProviderType.OpenMetadata);
        var tables = await client.GetTablesAsync();

        tables.Count.ShouldBe(1);
        var table = tables[0];
        table.Identifier.ShouldBe(new TableIdentifier("sales", "public", "customer_records"));
        table.DisplayName.ShouldBe("customer_records");
        table.Columns.Count.ShouldBe(2);
        table.Columns[0].ColumnName.ShouldBe("email");
        table.Columns[0].Tags.ShouldContain("PII.Sensitive");
        table.Columns[1].ColumnName.ShouldBe("iban");
    }

    [Fact]
    public async Task DataCatalogSyncService_IngestsTables_ConfiguresMasking_AndInvalidatesEpoch()
    {
        var clientFactory = Substitute.For<IDataCatalogClientFactory>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var epochService = Substitute.For<IEpochValidationService>();

        var testClient = Substitute.For<IDataCatalogClient>();
        testClient.ProviderType.Returns(DataCatalogProviderType.OpenMetadata);

        var tableAsset = new CatalogTableAsset
        {
            Identifier = new TableIdentifier("sales", "dbo", "customers"),
            DisplayName = "customers",
            Tags = ["GDPR.Article9"],
            Columns =
            [
                new CatalogColumnAsset
                {
                    ColumnName = "email",
                    Tags = ["PII.Email"]
                },
                new CatalogColumnAsset
                {
                    ColumnName = "user_name",
                    Tags = []
                }
            ]
        };

        testClient.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CatalogTableAsset>>([tableAsset]));

        clientFactory.GetActiveClient().Returns(testClient);

        var options = Options.Create(new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                Enabled = true,
                Provider = DataCatalogProviderType.OpenMetadata,
                TagToMaskingRuleMap = new Dictionary<string, string>
                {
                    ["PII.Email"] = "MASK_EMAIL"
                },
                GdprArticle9Tags = ["GDPR.Article9"]
            }
        });

        var syncService = new DataCatalogSyncService(
            clientFactory,
            metadataRepo,
            epochService,
            options,
            NullLogger<DataCatalogSyncService>.Instance);

        var result = await syncService.SyncCatalogAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        result.SyncedTablesCount.ShouldBe(1);
        result.SyncedColumnsCount.ShouldBe(2);
        result.MaskedColumnsCount.ShouldBe(1);
        result.Art9ProtectedTablesCount.ShouldBe(1);

        await metadataRepo.Received(1).UpsertTableMetadataAsync(
            Arg.Is<TableMetadata>(m =>
                m.Identifier.Equals(tableAsset.Identifier) &&
                m.Table.IsHighlySensitive &&
                m.ColumnMaskingRules.ContainsKey("email") &&
                m.ColumnMaskingRules["email"].RuleType == "MASK_EMAIL"),
            Arg.Any<CancellationToken>());

        await epochService.Received(1).InvalidateEpochAsync(Arg.Is<TableIdentifier>(t => t.Equals(tableAsset.Identifier)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DataCatalogClientFactory_ReturnsConfiguredClient()
    {
        var services = new ServiceCollection();
        var options = Options.Create(new GatewayOptions
        {
            Catalog = new DataCatalogOptions { Provider = DataCatalogProviderType.OpenMetadata }
        });

        services.AddSingleton(options);
        services.AddHttpClient();
        services.AddTransient(_ => new OpenMetadataCatalogAdapter(Substitute.For<IOpenMetadataClient>(), NullLogger<OpenMetadataCatalogAdapter>.Instance));
        services.AddTransient(sp => new PurviewDataCatalogClient(sp.GetRequiredService<HttpClient>(), options, NullLogger<PurviewDataCatalogClient>.Instance));
        services.AddTransient(sp => new CollibraDataCatalogClient(sp.GetRequiredService<HttpClient>(), options, NullLogger<CollibraDataCatalogClient>.Instance));
        services.AddTransient(sp => new AlationCatalogClient(sp.GetRequiredService<HttpClient>(), options, NullLogger<AlationCatalogClient>.Instance));

        var sp = services.BuildServiceProvider();
        var factory = new DataCatalogClientFactory(sp, options);

        var activeClient = factory.GetActiveClient();
        activeClient.ShouldNotBeNull();
        activeClient.ProviderType.ShouldBe(DataCatalogProviderType.OpenMetadata);
        activeClient.ShouldBeOfType<OpenMetadataCatalogAdapter>();

        var purview = factory.CreateClient(DataCatalogProviderType.MicrosoftPurview);
        purview.ProviderType.ShouldBe(DataCatalogProviderType.MicrosoftPurview);

        var alation = factory.CreateClient(DataCatalogProviderType.Alation);
        alation.ProviderType.ShouldBe(DataCatalogProviderType.Alation);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseContent;

        public MockHttpMessageHandler(string responseContent)
        {
            _responseContent = responseContent;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseContent, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static Microsoft.Extensions.Hosting.IHostEnvironment DevEnv()
    {
        var env = NSubstitute.Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return env;
    }
}
