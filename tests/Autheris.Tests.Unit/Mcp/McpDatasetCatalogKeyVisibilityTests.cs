namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
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
            Table = new Table { TableName = "orders", SchemaName = "public", Sensitivity = "INTERNAL" },
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
                new TableColumn { ColumnName = "customer_email", DataType = "varchar", IsSensitive = true }
            ]
        };
        _metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
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
        var golden = Substitute.For<IGoldenQueryService>();
        golden.GetGoldenQueriesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]));
        var catalog = new McpDatasetCatalog(_metadata, _consents, Substitute.For<IGatewayExecutionService>(), golden,
            Options.Create(new GatewayOptions()), Substitute.For<IGraphQlCatalogMap>());
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", Tenant)], "MCP"));

        var result = await catalog.DescribeDatasetAsync(user, "sales.public.orders");

        result.Columns.Select(c => c.Name).ShouldNotContain("customer_email");
        result.Columns.ShouldAllBe(c => !c.PrimaryKey);
        result.Indexes.ShouldNotBeNull().Select(i => i.Name).ShouldBe(new[] { "ux_visible" });
    }
}
