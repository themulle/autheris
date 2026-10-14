namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Backstage;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Resolvers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class Wave3CatalogOwnershipTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TableIdentifier Customers = new("crm", "public", "customers");
    private static readonly TableIdentifier AuditLogs = new("system", "public", "audit_logs");

    private static ClaimsPrincipal CreateUser(string sid = "S-1-USER", string tenant = Tenant, string[]? roles = null)
    {
        var claims = new List<Claim>
        {
            new("sub", sid),
            new("http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid", sid),
            new("tenant_id", tenant)
        };
        if (roles != null)
        {
            foreach (var r in roles)
            {
                claims.Add(new(ClaimTypes.Role, r));
            }
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static TableMetadata CreateTableMetadata(TableIdentifier id, string description, IReadOnlyList<string> tags, DatasetVisibility visibility = DatasetVisibility.Hidden)
    {
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = id.TableName,
                DisplayName = $"{id.Schema}.{id.TableName}",
                Description = description,
                Sensitivity = "NORMAL",
                Tags = tags,
                Visibility = visibility
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ],
            PrimaryKeyColumns = ["id"]
        };
    }

    [Fact]
    public async Task ListDatasets_FilterByTag_ReturnsOnlyMatchingDatasets()
    {
        // Arrange
        var table1 = CreateTableMetadata(Orders, "Orders table", ["sales", "financial", "pii"], DatasetVisibility.Full);
        var table2 = CreateTableMetadata(Customers, "Customers table", ["crm", "pii"], DatasetVisibility.Full);
        var table3 = CreateTableMetadata(AuditLogs, "Audit logs", ["audit", "system"], DatasetVisibility.Full);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table1, table2, table3]));

        var consentRepo = Substitute.For<IConsentRepository>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();
        var user = CreateUser();

        var catalog = new McpDatasetCatalog(metadataRepo, consentRepo, gatewayExec);

        // Act: Filter by tag "financial"
        var financialList = await catalog.ListDatasetsAsync(user, tag: "financial");

        // Assert
        financialList.Datasets.Count.ShouldBe(1);
        financialList.Datasets[0].Id.ShouldBe("sales.public.orders");
        financialList.Datasets[0].Tags.ShouldNotBeNull();
        financialList.Datasets[0].Tags!.ShouldContain("financial");

        // Act: Filter by tag "pii" (case-insensitive)
        var piiList = await catalog.ListDatasetsAsync(user, tag: "PII");

        // Assert
        piiList.Datasets.Count.ShouldBe(2);
        piiList.Datasets.Select(d => d.Id).ShouldBe(["crm.public.customers", "sales.public.orders"]);

        // Act: Filter by non-existent tag
        var emptyList = await catalog.ListDatasetsAsync(user, tag: "non-existent");
        emptyList.Datasets.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescribeDataset_ReturnsDataOwnerAndDelegates()
    {
        // Arrange
        var table = CreateTableMetadata(Orders, "Orders table", ["sales"], DatasetVisibility.Full);
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));

        var consentRepo = Substitute.For<IConsentRepository>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();

        var dataOwnerRepo = Substitute.For<IDataOwnershipRepository>();
        dataOwnerRepo.GetDataOwnersForTableAsync(Orders, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DataOwner>>(
            [
                new DataOwner
                {
                    DisplayName = "Alice Johnson",
                    Email = "alice@example.com",
                    AdAccount = "CORP\\alice",
                    IsActive = true
                },
                new DataOwner
                {
                    DisplayName = "Bob Smith",
                    Email = "bob@example.com",
                    AdAccount = "CORP\\bob",
                    IsActive = true
                }
            ]));

        var catalog = new McpDatasetCatalog(
            metadataRepo,
            consentRepo,
            gatewayExec,
            dataOwnershipRepository: dataOwnerRepo);

        var user = CreateUser();

        // Act
        var desc = await catalog.DescribeDatasetAsync(user, "sales.public.orders");

        // Assert
        desc.DataOwner.ShouldBe("Alice Johnson <alice@example.com>");
        desc.DataOwnerDelegates.ShouldNotBeNull();
        desc.DataOwnerDelegates!.Count.ShouldBe(1);
        desc.DataOwnerDelegates[0].ShouldBe("Bob Smith <bob@example.com>");
        desc.Tags.ShouldNotBeNull();
        desc.Tags!.ShouldContain("sales");
    }

    [Fact]
    public async Task BackstageCatalogExport_PopulatesDataOwnerAndTags()
    {
        // Arrange
        var table = CreateTableMetadata(Orders, "Customer orders", ["sales", "orders"]);
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));

        var dataOwnerRepo = Substitute.For<IDataOwnershipRepository>();
        dataOwnerRepo.GetDataOwnersForTableAsync(Orders, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DataOwner>>(
            [
                new DataOwner
                {
                    DisplayName = "Data Steward",
                    Email = "steward@corp.internal",
                    IsActive = true
                }
            ]));

        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            Backstage = new BackstageIntegrationOptions
            {
                Enabled = true,
                DefaultOwner = "group:default-team"
            }
        });

        var exportService = new BackstageCatalogExportService(
            options: options,
            tableRepository: tableRepo,
            dataOwnershipRepository: dataOwnerRepo);

        // Act
        var entities = await exportService.ExportCatalogEntitiesAsync();

        // Assert
        var tableEntity = entities.FirstOrDefault(e => e.Metadata.Name.Contains("orders"));
        tableEntity.ShouldNotBeNull();
        tableEntity.Spec.Owner.ShouldBe("steward@corp.internal");
        tableEntity.Metadata.Tags.ShouldContain("sales");
        tableEntity.Metadata.Tags.ShouldContain("orders");
        tableEntity.Metadata.Annotations.ShouldContainKey("autheris.io/data-owner");
        tableEntity.Metadata.Annotations["autheris.io/data-owner"].ShouldBe("steward@corp.internal");
    }

    [Fact]
    public async Task BackstageCatalogExport_WithoutDataOwner_FallsBackToDefaultOwner()
    {
        // Arrange
        var table = CreateTableMetadata(Orders, "Unowned orders", ["unowned"]);
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));

        var dataOwnerRepo = Substitute.For<IDataOwnershipRepository>();
        dataOwnerRepo.GetDataOwnersForTableAsync(Orders, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DataOwner>>([]));

        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            Backstage = new BackstageIntegrationOptions
            {
                Enabled = true,
                DefaultOwner = "group:platform-governance"
            }
        });

        var exportService = new BackstageCatalogExportService(
            options: options,
            tableRepository: tableRepo,
            dataOwnershipRepository: dataOwnerRepo);

        // Act
        var entities = await exportService.ExportCatalogEntitiesAsync();

        // Assert
        var tableEntity = entities.FirstOrDefault(e => e.Metadata.Name.Contains("orders"));
        tableEntity.ShouldNotBeNull();
        tableEntity.Spec.Owner.ShouldBe("group:platform-governance");
    }

    [Fact]
    public void CatalogVisibility_ComputeAccessStatus_CorrectlyClassifiesStatus()
    {
        var activeConsent = new Consent
        {
            TableIdentifier = Orders,
            TenantId = new TenantId(Tenant),
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };

        var pendingReq = new ConsentRequest
        {
            TableIdentifier = Customers,
            Status = "PENDING",
            RequesterSid = new Sid("S-1-USER")
        };

        // 1. With Allow consent -> GRANTED
        CatalogVisibility.ComputeAccessStatus(Orders, [activeConsent], null, isAdmin: false)
            .ShouldBe("GRANTED");

        // 2. With Pending request -> PENDING
        CatalogVisibility.ComputeAccessStatus(Customers, [], [pendingReq], isAdmin: false)
            .ShouldBe("PENDING");

        // 3. With neither -> NONE
        CatalogVisibility.ComputeAccessStatus(AuditLogs, [], [pendingReq], isAdmin: false)
            .ShouldBe("NONE");

        // 4. Admin always GRANTED
        CatalogVisibility.ComputeAccessStatus(AuditLogs, [], null, isAdmin: true)
            .ShouldBe("GRANTED");
    }

    [Fact]
    public void CatalogVisibility_FilterForSubject_EnforcesDatasetVisibility()
    {
        var hiddenTable = CreateTableMetadata(Orders, "Hidden without consent", [], DatasetVisibility.Hidden);
        var summaryTable = CreateTableMetadata(Customers, "Summary visible", [], DatasetVisibility.Summary);
        var fullTable = CreateTableMetadata(AuditLogs, "Full visible", [], DatasetVisibility.Full);

        var tenantId = new TenantId(Tenant);
        var emptyConsents = Array.Empty<Consent>();

        // Act
        var visible = CatalogVisibility.FilterForSubject([hiddenTable, summaryTable, fullTable], emptyConsents, tenantId);

        // Assert:
        // Hidden should be omitted
        visible.Any(t => t.Identifier == Orders).ShouldBeFalse();

        // Summary should be included, but with Columns empty
        var crm = visible.FirstOrDefault(t => t.Identifier == Customers);
        crm.ShouldNotBeNull();
        crm.Columns.ShouldBeEmpty();

        // Full should be included with columns
        var sys = visible.FirstOrDefault(t => t.Identifier == AuditLogs);
        sys.ShouldNotBeNull();
        sys.Columns.Count.ShouldBe(2);
    }

    public sealed class TestMaskingQuery
    {
        public string MaskedField(IResolverContext ctx)
        {
            if (ctx.ContextData.TryGetValue("MaskedFields", out var mf) && mf is ConcurrentBag<string> bag)
            {
                bag.Add(ctx.Path.Print());
            }
            return "[REDACTED]";
        }

        public string ClearField() => "cleartext";
    }

    [Fact]
    public async Task MaskedFieldsMiddleware_AddsMaskedFieldsExtensionWhenMasked()
    {
        // Arrange
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<TestMaskingQuery>()
            .UseRequest<MaskedFieldsMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ maskedField }");

        // Assert
        var opResult = result.ExpectOperationResult();
        opResult.Extensions.ShouldNotBeNull();
        opResult.Extensions.ShouldContainKey("maskedFields");

        var maskedList = opResult.Extensions["maskedFields"] as IReadOnlyList<string>;
        maskedList.ShouldNotBeNull();
        maskedList.ShouldContain("maskedField");
    }

    [Fact]
    public async Task MaskedFieldsMiddleware_OmitsExtensionWhenNoFieldsMasked()
    {
        // Arrange
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<TestMaskingQuery>()
            .UseRequest<MaskedFieldsMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ clearField }");

        // Assert
        var opResult = result.ExpectOperationResult();
        if (opResult.Extensions != null)
        {
            opResult.Extensions.ShouldNotContainKey("maskedFields");
        }
    }

    [Fact]
    public async Task GraphQLCatalog_SupportsTagFilteringAndAccessStatus()
    {
        // Arrange
        var table1 = CreateTableMetadata(Orders, "Orders table", ["ecommerce", "sales"], DatasetVisibility.Full);
        var table2 = CreateTableMetadata(Customers, "Customers table", ["crm"], DatasetVisibility.Full);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table1, table2]));

        var consentRepo = Substitute.For<IConsentRepository>();
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        httpContext.User = CreateUser();
        httpContextAccessor.HttpContext.Returns(httpContext);

        var query = new Query();

        // Act
        var result = await query.GetCatalogAsync(
            domain: null,
            first: null,
            after: null,
            search: null,
            tag: "ecommerce",
            metadataRepository: metadataRepo,
            consentRepository: consentRepo,
            approvalRepository: null,
            dataOwnershipRepository: null,
            httpContextAccessor: httpContextAccessor);

        // Assert
        result.Count.ShouldBe(1);
        result[0].TableName.ShouldBe("orders");
        result[0].Tags.ShouldContain("ecommerce");
    }
}
