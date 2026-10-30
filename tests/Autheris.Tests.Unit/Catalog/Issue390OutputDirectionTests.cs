namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Tests.Unit.TestSupport;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #390 (WP-390-4, I390-SC-11): describe_dataset and the catalog detail report key directions only for a key wholly visible to the caller.</summary>
[Trait("Category", "Security")]
public sealed class Issue390OutputDirectionTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Readings = new("sales", "public", "readings");

    private static TableMetadata Table(IReadOnlyList<string> pk, IReadOnlyList<KeyColumnDirection> directions, IReadOnlyList<TableIndexDefinition>? indexes = null, bool forCatalog = false) => new()
    {
        Identifier = Readings,
        Table = forCatalog
            ? new Table { TableName = "readings", SchemaName = "public", Sensitivity = "INTERNAL", DataSourceType = DataSourceType.HttpDeclarative, IsActive = true }
            : new Table { TableName = "readings", SchemaName = "public", Sensitivity = "INTERNAL" },
        PrimaryKeyColumns = pk,
        PrimaryKeyDirections = directions,
        Indexes = indexes ?? [],
        Columns =
        [
            new TableColumn { ColumnName = "device_id", DataType = "int" },
            new TableColumn { ColumnName = "ts", DataType = "datetime" },
            new TableColumn { ColumnName = "secret_key", DataType = "varchar", IsSensitive = true }
        ]
    };

    private static async Task<McpDatasetDescription> DescribeAsync(TableMetadata table, params string[] visibleColumns)
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        var consents = Substitute.For<IConsentRepository>();
        metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
        consents.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(
            [
                new Consent
                {
                    TableIdentifier = Readings,
                    TenantId = new TenantId(Tenant),
                    Effect = ConsentEffect.Allow,
                    GranteeSid = new Sid("S-1-USER"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                    ColumnRules = visibleColumns.Select(c => new ConsentColumnRule { ColumnName = c, AccessLevel = ColumnAccessLevel.Clear }).ToList()
                }
            ]));
        var golden = Substitute.For<IGoldenQueryService>();
        golden.GetGoldenQueriesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]));
        var catalog = new McpDatasetCatalog(metadata, consents, Substitute.For<IGatewayExecutionService>(), golden,
            Options.Create(new GatewayOptions()), Substitute.For<IGraphQlCatalogMap>(),
            visibilityService: Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(metadata.ServeSingleReadsFromCatalog(), consents));
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", Tenant)], "MCP"));

        return await catalog.DescribeDatasetAsync(user, "sales.public.readings");
    }

    [Fact]
    public async Task DescribeDataset_DescKey_ShowsOrderAndDirections_WhenKeyVisible()
    {
        var table = Table(["device_id", "ts"], [KeyColumnDirection.Ascending, KeyColumnDirection.Descending],
            [new TableIndexDefinition("ux_ts", ["ts", "device_id"], true, [KeyColumnDirection.Descending, KeyColumnDirection.Ascending])]);

        var result = await DescribeAsync(table, "device_id", "ts");

        result.PrimaryKeyOrder.ShouldNotBeNull().ShouldBe([new McpKeyColumn("device_id", "ASC"), new McpKeyColumn("ts", "DESC")]);
        result.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ShouldBe(["device_id", "ts"]);
        result.Indexes.ShouldNotBeNull().Single().Directions.ShouldBe([KeyColumnDirection.Descending, KeyColumnDirection.Ascending]);
        System.Text.Json.JsonSerializer.Serialize(result.Indexes).ShouldContain("\"Directions\":[\"DESC\",\"ASC\"]");
    }

    [Fact]
    public async Task DescribeDataset_AscendingKey_ListsKeyOrderWithAscendingDirections_AndIndexHasNoDirections()
    {
        var table = Table(["device_id", "ts"], [], [new TableIndexDefinition("ux_ts", ["ts"], true)]);

        var result = await DescribeAsync(table, "device_id", "ts");

        result.PrimaryKeyOrder.ShouldNotBeNull().ShouldBe([new McpKeyColumn("device_id", "ASC"), new McpKeyColumn("ts", "ASC")]);
        result.Indexes.ShouldNotBeNull().Single().Directions.ShouldBeNull();
    }

    [Fact]
    public async Task DescribeDataset_HiddenKeyColumn_OmitsKeyOrderAndDirections()
    {
        var table = Table(["device_id", "secret_key"], [KeyColumnDirection.Ascending, KeyColumnDirection.Descending]);

        var result = await DescribeAsync(table, "device_id", "ts");

        result.PrimaryKeyOrder.ShouldBeNull("a key touching a hidden column is omitted as a whole, with its directions");
        result.Columns.ShouldAllBe(c => !c.PrimaryKey);
        System.Text.Json.JsonSerializer.Serialize(result).ShouldNotContain("secret_key");
    }

    [Fact]
    public async Task DescribeDataset_IndexTouchingHiddenColumn_Omitted()
    {
        var table = Table(["device_id"], [],
        [
            new TableIndexDefinition("ux_hidden", ["secret_key", "ts"], true, [KeyColumnDirection.Descending, KeyColumnDirection.Descending]),
            new TableIndexDefinition("ux_visible", ["device_id", "ts"], true, [KeyColumnDirection.Ascending, KeyColumnDirection.Descending])
        ]);

        var result = await DescribeAsync(table, "device_id", "ts");

        result.Indexes.ShouldNotBeNull().Select(i => string.Join(",", i.Columns)).ShouldBe(["device_id,ts"]);
        System.Text.Json.JsonSerializer.Serialize(result.Indexes).ShouldNotContain("secret_key");
    }

    private static async Task<CatalogDatasetDetail> DetailAsync(TableMetadata table)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Readings, Arg.Any<CancellationToken>()).Returns(table);
        var consents = Substitute.For<IConsentRepository>();
        var options = Wp0.Options(Autheris.Domain.Options.TenancyMode.Single);
        var service = new CatalogDiscoveryService(
            repo,
            Substitute.For<IAuditLogRepository>(),
            Microsoft.Extensions.Options.Options.Create(options),
            Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(repo, consents, options, Substitute.For<IRebacEvaluator>()),
            Substitute.For<IKeyVaultSecretProvider>(),
            Substitute.For<IOpenApiIngestionService>(),
            Substitute.For<ICatalogSearchEngine>(),
            NullLogger<CatalogDiscoveryService>.Instance);

        return (await service.GetDatasetDetailAsync(Readings, new RequestContext(new TenantId("t"), new Sid("user:x"), User: Wp0.User("user:x", null, ["GovernanceAdmin"]))))!;
    }

    [Fact]
    public async Task CatalogDetail_AscendingKey_PrimaryKeyDirectionsNull()
    {
        var detail = await DetailAsync(Table(["device_id", "ts"], [], forCatalog: true));

        detail.PrimaryKeys.ShouldBe(["device_id", "ts"]);
        detail.PrimaryKeyDirections.ShouldBeNull();
    }

    [Fact]
    public async Task CatalogDetail_DescKey_ReportsDirectionsParallelToPrimaryKeys()
    {
        var detail = await DetailAsync(Table(["device_id", "ts"], [KeyColumnDirection.Ascending, KeyColumnDirection.Descending], forCatalog: true));

        detail.PrimaryKeys.ShouldBe(["device_id", "ts"]);
        detail.PrimaryKeyDirections.ShouldBe(["ASC", "DESC"]);
    }

    [Fact]
    public async Task I390_T_12_CatalogDetail_PartialKey_NoDirectionLeak()
    {
        var detail = await DetailAsync(Table(["device_id", "ghost"], [KeyColumnDirection.Ascending, KeyColumnDirection.Descending], forCatalog: true));

        detail.PrimaryKeys.ShouldBeEmpty();
        detail.PrimaryKeyDirections.ShouldBeNull();
    }
}
