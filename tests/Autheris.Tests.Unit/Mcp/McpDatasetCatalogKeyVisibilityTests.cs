#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Mcp;

using Autheris.Tests.Unit.TestSupport;
using System;
using System.Collections.Generic;
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
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (SD-4): MCP reports keys and indexes only over the columns the caller can see, as a whole.</summary>
[Trait("Category", "Security")]
public sealed class McpDatasetCatalogKeyVisibilityTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private readonly ITableMetadataRepository _metadata = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consents = Substitute.For<IConsentRepository>();

    public McpDatasetCatalogKeyVisibilityTests()
    {
        var table = new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { TableName = "orders", SchemaName = "public" },
            PrimaryKeyColumns = ["id", "customer_email"],
            Indexes =
            [
                new TableIndexDefinition("ux_hidden", ["customer_email"], true),
                new TableIndexDefinition("ux_visible", ["id", "amount"], true)
            ],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "customer_email", DataType = "varchar" }
            ]
        }.WithPii("customer_email");
        _metadata.StubAllTables(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
        // Same grants as the sibling MCP tests: id and amount are visible, customer_email is not.
        _consents.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(
            [
                new Consent
                {
                    TableIdentifier = Orders,
                    TenantId = new TenantId(Tenant),
                    Effect = ConsentEffect.Allow,
                    GranteeSid = new Sid("S-1-USER"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                    ColumnRules =
                    [
                        new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                        new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
                    ]
                }
            ]));
    }

    [Fact]
    public async Task IndexWithInvisibleColumn_NotReported_AndPartialPrimaryKeyIsDropped()
    {
        var catalog = Catalog();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", Tenant)], "MCP"));

        var result = await catalog.DescribeDatasetAsync(user, "sales.public.orders");

        result.Columns.Select(c => c.Name).ShouldNotContain("customer_email");
        result.Columns.ShouldAllBe(c => !c.PrimaryKey);
        result.Indexes.ShouldNotBeNull().Select(i => string.Join(",", i.Columns)).ShouldBe(new[] { "id,amount" });
    }

    private McpDatasetCatalog Catalog(params string[] keysetColumns)
    {
        var golden = Substitute.For<IGoldenQueryService>();
        golden.GetGoldenQueriesAsync(Arg.Any<GoldenQueryReadContext>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]));
        var map = Substitute.For<IGraphQlCatalogMap>();
        map.GetTableAsync(Orders, Arg.Any<CancellationToken>()).Returns(new GraphQlTableMapping(
            Orders, "orders", "orders", "orders_filter", "orders_order_by",
            [
                new GraphQlColumnMapping("id", "id", "Int"),
                new GraphQlColumnMapping("amount", "amount", "Decimal"),
                new GraphQlColumnMapping("customer_email", "customer_email", "String")
            ],
            [],
            [new("where", "orders_filter")],
            new GraphQlRootFieldSignature("orders_aggregate", "orders_aggregate_result", []),
            new GraphQlRootFieldSignature("ordersConnection", "ordersConnection", [new("first", "Int")]),
            keysetColumns));
        return new McpDatasetCatalog(_metadata, _consents, Substitute.For<IGatewayExecutionService>(), golden,
            Options.Create(new GatewayOptions()), map,
            visibilityService: CatalogVisibilityTestFactory.Create(_metadata.ServeSingleReadsFromCatalog(), _consents));
    }

    private static ClaimsPrincipal Caller() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", Tenant)], "MCP"));

    [Fact]
    public async Task Describe_HiddenKeyColumn_ConnectionNoUsableKey_LikeKeysSection()
    {
        var hidden = (await Catalog("id", "customer_email").DescribeDatasetAsync(Caller(), "sales.public.orders")).GraphQl!.Connection!;
        var keyless = (await Catalog().DescribeDatasetAsync(Caller(), "sales.public.orders")).GraphQl!.Connection!;

        hidden.Available.ShouldBeFalse();
        hidden.Reason.ShouldBe("no_usable_key");
        System.Text.Json.JsonSerializer.Serialize(hidden, McpResultJson.Options).ShouldBe(System.Text.Json.JsonSerializer.Serialize(keyless, McpResultJson.Options));
    }

    [Fact]
    public async Task Describe_MaskedKey_ConnectionMaskedKey()
    {
        _consents.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(
            [
                new Consent
                {
                    TableIdentifier = Orders,
                    TenantId = new TenantId(Tenant),
                    Effect = ConsentEffect.Allow,
                    GranteeSid = new Sid("S-1-USER"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                    ColumnRules =
                    [
                        new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                        new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Mask }
                    ]
                }
            ]));

        var connection = (await Catalog("id", "amount").DescribeDatasetAsync(Caller(), "sales.public.orders")).GraphQl!.Connection!;

        connection.Available.ShouldBeFalse();
        connection.Reason.ShouldBe("masked_key");
        connection.KeyFields.ShouldBeNull();
    }
}
