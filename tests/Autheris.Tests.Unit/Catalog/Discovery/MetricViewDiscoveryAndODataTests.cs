namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.OData;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class MetricViewDiscoveryAndODataTests
{
    [Fact]
    public void CatalogDiscoveryProjection_MetricView_EmitsWireKindAndColumnRoles()
    {
        var mv = new TableMetadata
        {
            Identifier = new TableIdentifier("dbx", "analytics", "sales_mv"),
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceType = "Databricks",
                SourceName = "dbx",
                SchemaName = "analytics",
                TableName = "sales_mv",
                DisplayName = "Sales Metric View",
                ObjectKind = DiscoveryObjectKind.MetricView,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "region", DataType = "string", Role = MetricViewColumnRole.Dimension },
                new TableColumn { ColumnName = "revenue", DataType = "decimal(18,2)", Role = MetricViewColumnRole.Measure },
                new TableColumn { ColumnName = "hidden_col", DataType = "string", DiscoveryHidden = true, Role = null }
            ]
        };

        var decision = TableAccessDecision.Allowed(
            mv.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["region"] = ColumnAccessLevel.Clear,
                ["revenue"] = ColumnAccessLevel.Clear,
                ["hidden_col"] = ColumnAccessLevel.Deny
            });

        var obj = DiscoveryAccessMapper.Map(mv, decision, [], false, false, null, language: null, isHidden: false);

        var summary = CatalogDiscoveryProjection.ToDatasetSummary(obj, language: null);
        summary.SourceType.ShouldBe("metric_view");

        var detail = CatalogDiscoveryProjection.ToDatasetDetail(obj, language: null);
        detail.Kind.ShouldBe("metric_view");
        detail.Columns.Select(c => c.Name).ShouldBe(["region", "revenue"]);
        detail.Columns.Single(c => c.Name == "region").Role.ShouldBe("dimension");
        detail.Columns.Single(c => c.Name == "revenue").Role.ShouldBe("measure");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ODataHandler_ExecuteEntitySetQueryAsync_MetricView_FailsClosed()
    {
        var mv = DiscoveryTestBuilder.MakeTable("dbx", "sales_mv", schema: "analytics", columns: [("region", false, false)], objectKind: DiscoveryObjectKind.MetricView);
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(mv.Identifier, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(mv));
        var execService = Substitute.For<IGatewayExecutionService>();

        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-USER"), new Claim("tenant_id", "default")], "Test"));

        var result = await handler.ExecuteEntitySetQueryAsync(
            principal,
            "https://gateway/odata/v4",
            mv.Identifier,
            top: 10,
            skip: 0,
            select: null,
            includeCount: false,
            headers: null);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(403);
        result.ErrorCode.ShouldBe("ACCESS_DENIED");
        await execService.DidNotReceiveWithAnyArgs().ExecuteTablePageAsync(default!, default!, default!, default);
    }
}
