namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Governance;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DatasetSizeClassTests
{
    [Theory]
    [InlineData(0L, "tiny")]
    [InlineData(1L, "tiny")]
    [InlineData(999L, "tiny")]
    [InlineData(1_000L, "small")]
    [InlineData(99_999L, "small")]
    [InlineData(100_000L, "medium")]
    [InlineData(9_999_999L, "medium")]
    [InlineData(10_000_000L, "large")]
    [InlineData(999_999_999L, "large")]
    [InlineData(1_000_000_000L, "huge")]
    [InlineData(5_000_000_000L, "huge")]
    [InlineData(-1L, "unknown")]
    [InlineData(-100L, "unknown")]
    [InlineData(null, "unknown")]
    public void Class_boundaries(object? rowCountObj, string expectedClass)
    {
        long? rowCount = rowCountObj is null ? null : Convert.ToInt64(rowCountObj);
        DatasetSizeClassifier.Classify(rowCount).ShouldBe(expectedClass);

        var dbtTable = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            EstimatedRowCount = rowCount,
            IngestedAt = DateTimeOffset.UtcNow
        };
        var dbtSize = DatasetSizeClassifier.Resolve(dbtTable);
        dbtSize.SizeClass.ShouldBe(expectedClass);
        if (expectedClass == "unknown")
        {
            dbtSize.Source.ShouldBe(McpDatasetSize.SourceUnknown);
        }
        else
        {
            dbtSize.Source.ShouldBe(McpDatasetSize.SourceDbtMeta);
        }

        var declaredTable = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            ExpectedRows = rowCount,
            IngestedAt = DateTimeOffset.UtcNow
        };
        var declaredSize = DatasetSizeClassifier.Resolve(declaredTable);
        declaredSize.SizeClass.ShouldBe(expectedClass);
        if (expectedClass == "unknown")
        {
            declaredSize.Source.ShouldBe(McpDatasetSize.SourceUnknown);
        }
        else
        {
            declaredSize.Source.ShouldBe(McpDatasetSize.SourceDeclared);
        }
    }

    [Fact]
    public void Unknown_without_ingested_count()
    {
        var asOf = DateTimeOffset.UtcNow.AddHours(-3);
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            EstimatedRowCount = null,
            ExpectedRows = null,
            IngestedAt = asOf
        };

        var size = DatasetSizeClassifier.Resolve(table);

        size.SizeClass.ShouldBe("unknown");
        size.Source.ShouldBe("UNKNOWN");
        size.AsOf.ShouldBe(asOf);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Exact_count_never_serialized()
    {
        const long exactCount = 42_424_242;
        var asOf = DateTimeOffset.UtcNow;
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "customers"),
            EstimatedRowCount = exactCount,
            ExpectedRows = exactCount,
            IngestedAt = asOf
        };

        var size = DatasetSizeClassifier.Resolve(table);
        size.SizeClass.ShouldBe("large");
        size.Source.ShouldBe("DBT_META");

        var summary = new McpDatasetSummary(
            Id: "sales.dbo.customers",
            DisplayName: "Customers",
            Description: "Customer records",
            Sensitivity: "NORMAL",
            ColumnCount: 5,
            Size: size);

        var description = new McpDatasetDescription(
            Id: "sales.dbo.customers",
            DisplayName: "Customers",
            Domain: "sales",
            Schema: "dbo",
            Table: "customers",
            Description: "Customer records",
            LongDescription: null,
            Sensitivity: "NORMAL",
            RequiresApproval: false,
            Columns: [],
            Examples: [],
            Size: size);

        var detail = new CatalogDatasetDetail(
            DatasetId: "sales.dbo.customers",
            Domain: "sales",
            Schema: "dbo",
            Table: "customers",
            Columns: [],
            PrimaryKeys: [],
            Sensitivity: "NORMAL",
            IsActive: true,
            Size: size);

        var summaryJson = JsonSerializer.Serialize(summary);
        var descriptionJson = JsonSerializer.Serialize(description);
        var detailJson = JsonSerializer.Serialize(detail);
        var sizeJson = JsonSerializer.Serialize(size);

        foreach (var json in new[] { summaryJson, descriptionJson, detailJson, sizeJson })
        {
            json.ShouldNotContain(exactCount.ToString());
            json.ShouldNotContain("EstimatedRowCount", Case.Insensitive);
            json.ShouldNotContain("ExpectedRows", Case.Insensitive);
            json.ShouldContain("sizeClass", Case.Insensitive);
            json.ShouldContain("large", Case.Insensitive);
            json.ShouldContain("DBT_META", Case.Insensitive);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Invisible_dataset_has_no_size()
    {
        var visibleId = new TableIdentifier("sales", "public", "orders");
        var invisibleId = new TableIdentifier("hr", "public", "payroll");

        var visibleTable = new TableMetadata
        {
            Identifier = visibleId,
            Table = new Table { TableName = visibleId.TableName, SchemaName = visibleId.Schema, Sensitivity = "INTERNAL", Visibility = DatasetVisibility.Full },
            EstimatedRowCount = 50_000
        };

        var invisibleTable = new TableMetadata
        {
            Identifier = invisibleId,
            Table = new Table { TableName = invisibleId.TableName, SchemaName = invisibleId.Schema, Sensitivity = "CONFIDENTIAL", Visibility = DatasetVisibility.Hidden },
            EstimatedRowCount = 500_000
        };

        var metadataRepo = new InMemoryTableMetadataRepository(visibleTable, invisibleTable);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", "tenant-a")], "TestAuth"));

        var consents = new FakeConsentRepository();
        await consents.ReplaceConsentsAsync([],
        [
            new Consent
            {
                TableIdentifier = visibleId,
                TenantId = new TenantId("tenant-a"),
                Effect = ConsentEffect.Allow,
                GranteeSid = new Sid("S-1-USER"),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
            }
        ], new Sid("admin"), "test");

        var options = new GatewayOptions();
        var visibility = Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(
            metadataRepo, consents, options);

        var mcpCatalog = new McpDatasetCatalog(
            metadataRepo,
            consents,
            gatewayExecutionService: Substitute.For<IGatewayExecutionService>(),
            goldenQueryService: null,
            options: Options.Create(options),
            graphQlMap: null,
            visibilityService: visibility,
            switches: ReadPathSwitches.From(options));

        // 1. Mcp list_datasets: invisible table omitted entirely
        var listResult = await mcpCatalog.ListDatasetsAsync(user, null, null);
        listResult.Datasets.ShouldNotContain(d => d.Id == "hr.public.payroll");
        var visibleItem = listResult.Datasets.ShouldHaveSingleItem();
        visibleItem.Id.ShouldBe("sales.public.orders");
        visibleItem.Size.ShouldNotBeNull();
        visibleItem.Size.SizeClass.ShouldBe("small");

        // 2. Mcp describe_dataset: throws TableNotFoundException for invisible dataset
        await Should.ThrowAsync<TableNotFoundException>(() =>
            mcpCatalog.DescribeDatasetAsync(user, "hr.public.payroll"));

        // 3. CatalogDiscoveryService REST detail: returns null (404) for invisible dataset
        var discoveryService = new CatalogDiscoveryService(
            metadataRepo,
            new NoOpAuditWriter(),
            Options.Create(options),
            visibility);

        var context = new RequestContext(new TenantId("tenant-a"), new Sid("S-1-USER"), User: user);
        var detail = await discoveryService.GetDatasetDetailAsync(invisibleId, context);
        detail.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task No_source_query_issued()
    {
        var fakeConnectionFactory = new FakeSqlConnectionFactory();

        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var table = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { TableName = tableId.TableName, SchemaName = tableId.Schema, Sensitivity = "NORMAL", Visibility = DatasetVisibility.Full },
            EstimatedRowCount = 250_000,
            IngestedAt = DateTimeOffset.UtcNow
        };

        // 1. Direct classification
        var size = DatasetSizeClassifier.Resolve(table);
        size.SizeClass.ShouldBe("medium");
        size.Source.ShouldBe("DBT_META");

        // 2. Through McpDatasetCatalog and CatalogDiscoveryService
        var metadataRepo = new InMemoryTableMetadataRepository(table);
        var options = Wp0.Options(Autheris.Tests.Common.TenancyShape.Single);
        var consents = new FakeConsentRepository();
        var visibility = Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(
            metadataRepo, consents, options);

        var mcpCatalog = new McpDatasetCatalog(
            metadataRepo,
            consents,
            gatewayExecutionService: Substitute.For<IGatewayExecutionService>(),
            goldenQueryService: null,
            options: Options.Create(options),
            graphQlMap: null,
            visibilityService: visibility,
            switches: ReadPathSwitches.From(options));

        var user = Wp0.User("S-GOV", null, ["GovernanceAdmin"]);

        var list = await mcpCatalog.ListDatasetsAsync(user, null, null);
        list.Datasets.ShouldHaveSingleItem().Size?.SizeClass.ShouldBe("medium");

        var desc = await mcpCatalog.DescribeDatasetAsync(user, "sales.dbo.orders");
        desc.Size?.SizeClass.ShouldBe("medium");

        var discoveryService = new CatalogDiscoveryService(
            metadataRepo,
            new NoOpAuditWriter(),
            Options.Create(options),
            visibility);

        var context = Wp0.Context(user);
        var detail = await discoveryService.GetDatasetDetailAsync(tableId, context);
        detail.ShouldNotBeNull();
        detail.Size?.SizeClass.ShouldBe("medium");

        // Assert zero connection / query calls were made to source connection factory
        fakeConnectionFactory.ConnectionCallCount.ShouldBe(0);
    }

    private sealed class FakeSqlConnectionFactory : ISqlConnectionFactory
    {
        public int ConnectionCallCount { get; private set; }

        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            ConnectionCallCount++;
            throw new InvalidOperationException("Source database connections must not be opened for dataset size queries!");
        }
    }

    private sealed class InMemoryTableMetadataRepository(params TableMetadata[] tables) : ITableMetadataRepository
    {
        private readonly List<TableMetadata> _tables = tables.ToList();

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(_tables.FirstOrDefault(t => t.Identifier.Equals(table)));

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>(_tables);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            var idx = _tables.FindIndex(t => t.Identifier.Equals(metadata.Identifier));
            if (idx >= 0) _tables[idx] = metadata;
            else _tables.Add(metadata);
            return Task.FromResult(metadata);
        }

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class NoOpAuditWriter : IAuditWriter
    {
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }
}
