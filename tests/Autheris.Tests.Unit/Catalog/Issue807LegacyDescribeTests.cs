#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #807 (SEC-807-1) on the legacy (non-discovery) describe_dataset path: the required filters are projected onto the columns the
/// answer lists, so the name of a column the caller cannot see is never disclosed through them.
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue807LegacyDescribeTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Readings = new("sales", "public", "readings");

    internal static TableMetadata Table(ImmutableArray<string> required, ImmutableArray<ImmutableArray<string>> groups) => new()
    {
        Identifier = Readings,
        Table = new Table { TableName = "readings", SchemaName = "public", Sensitivity = "INTERNAL", RequiredFilterColumns = required, RequiredFilterColumnGroups = groups },
        ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("secret_key"),
        PrimaryKeyColumns = ["device_id", "ts"],
        PrimaryKeyDirections = [KeyColumnDirection.Ascending, KeyColumnDirection.Descending],
        Columns =
        [
            new TableColumn { ColumnName = "device_id", DataType = "int" },
            new TableColumn { ColumnName = "ts", DataType = "datetime" },
            new TableColumn { ColumnName = "secret_key", DataType = "varchar" }
        ]
    };

    internal static async Task<McpDatasetDescription> DescribeAsync(TableMetadata table, bool brief, params string[] visibleColumns)
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
        golden.GetGoldenQueriesAsync(Arg.Any<GoldenQueryReadContext>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]));
        var catalog = new McpDatasetCatalog(metadata, consents, Substitute.For<IGatewayExecutionService>(), golden,
            Options.Create(new GatewayOptions()), Substitute.For<IGraphQlCatalogMap>(),
            visibilityService: CatalogVisibilityTestFactory.Create(metadata.ServeSingleReadsFromCatalog(), consents));
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", Tenant)], "MCP"));

        return await catalog.DescribeDatasetAsync(user, "sales.public.readings", language: null, brief);
    }

    [Fact]
    public async Task HiddenRequiredFilterColumn_IsNotNamed_AndItsGroupIsWithheld()
    {
        var table = Table(["device_id", "secret_key"], [["ts"], ["secret_key", "device_id"]]);

        var described = await DescribeAsync(table, brief: false, "device_id", "ts");

        described.Columns.ShouldNotContain(c => c.Name == "secret_key");
        described.RequiredFilterColumns.ShouldBe(["device_id"]);
        described.RequiredFilterColumnGroups.ShouldNotBeNull().Select(g => string.Join(",", g)).ShouldBe(["ts"]);
        System.Text.Json.JsonSerializer.Serialize(described).ShouldNotContain("secret_key");
    }

    [Fact]
    public async Task VisibleRequiredFilterColumns_AreReported_WithTheKeyOrder()
    {
        var described = await DescribeAsync(Table(["device_id"], [["ts"]]), brief: false, "device_id", "ts");

        described.RequiredFilterColumns.ShouldBe(["device_id"]);
        described.RequiredFilterColumnGroups.ShouldNotBeNull().Select(g => string.Join(",", g)).ShouldBe(["ts"]);
        described.PrimaryKeyOrder.ShouldNotBeNull().ShouldBe([new McpKeyColumn("device_id", "ASC"), new McpKeyColumn("ts", "DESC")]);
    }
}
