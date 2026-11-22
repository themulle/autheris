namespace Autheris.Tests.Unit.Common;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.OData;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class SqlIdentifierConsolidationTests
{
    private static readonly TableIdentifier OrdersId = new("sales", "dbo", "orders");

    [Theory]
    [InlineData("id\nx")]
    [InlineData("id$1")]
    [InlineData("1id")]
    [InlineData("a.b")]
    [InlineData("id_\u00E9")]
    public async Task ODataHandler_SelectAndOrderBy_UseSqlIdentifierGuard(string invalidIdentifier)
    {
        var handler = new ODataHandler(
            new InMemoryTableMetadataRepository(),
            new StubGatewayExecutionService(),
            NullLogger<ODataHandler>.Instance);

        var selectResult = await handler.ExecuteEntitySetQueryAsync(
            principal: null,
            serviceRootUrl: "https://gateway/odata/v4",
            table: OrdersId,
            top: 10,
            skip: 0,
            select: invalidIdentifier,
            includeCount: false,
            headers: null);

        selectResult.Success.ShouldBeFalse();
        selectResult.StatusCode.ShouldBe(400);
        selectResult.ErrorCode.ShouldBe("InvalidQueryOption");

        ODataHandler.TryParseOrderBy(invalidIdentifier, out _, out var orderError).ShouldBeFalse();
        orderError.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TreeSqlCompiler_UsesSqlIdentifierGuard_ForRelationNamesAndLength()
    {
        var meta = new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table { SourceName = "sales", SchemaName = "dbo", TableName = "orders", SourceType = "PostgreSql", IsActive = true },
            PrimaryKeyColumns = ["id"],
            Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
        };
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [OrdersId] = new(meta, TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true))
        };

        var tooLongRelation = new string('a', SqlIdentifierGuard.DefaultMaxLength + 1);
        var relNode = new TreeRelationNode(tooLongRelation, ["id"], ["id"], IsList: true, new TreeQueryNode(OrdersId, ["id"]));
        Should.Throw<GatewayInvalidQueryException>(() =>
            TreeSqlCompiler.Compile(new TreeQueryNode(OrdersId, ["id"]) { Relations = [relNode] }, access, DatabaseDialect.PostgreSql));

        var digitRel = new TreeRelationNode("1orders", ["id"], ["id"], IsList: true, new TreeQueryNode(OrdersId, ["id"]));
        Should.Throw<GatewayInvalidQueryException>(() =>
            TreeSqlCompiler.Compile(new TreeQueryNode(OrdersId, ["id"]) { Relations = [digitRel] }, access, DatabaseDialect.PostgreSql));
    }

    [Theory]
    [InlineData("id$1", "strictly prohibited")]
    [InlineData("1id", "strictly prohibited")]
    [InlineData("id\n", "strictly prohibited")]
    [InlineData("orders.id", "strictly prohibited")]
    [InlineData("select", "reserved SQL keyword")]
    public void KeysetPlanner_ValidateIdentifier_UsesSqlIdentifierGuard_AndPreservesMessages(string badColumn, string expectedSubstring)
    {
        var planner = new GovernedTableReadPlanner();
        var meta = new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table { SourceName = "sales", SchemaName = "dbo", TableName = "orders", SourceType = "PostgreSql", IsActive = true },
            PrimaryKeyColumns = ["id"],
            Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
        };
        var decision = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var ex = Should.Throw<ArgumentException>(() =>
            planner.PlanKeysetStream(new BulkTableStreamRequest(Select: [badColumn], Limit: 10), meta, decision));

        ex.Message.ShouldContain(expectedSubstring);
    }
}
