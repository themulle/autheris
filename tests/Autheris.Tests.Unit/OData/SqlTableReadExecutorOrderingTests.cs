namespace Autheris.Tests.Unit.OData;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #94: the SQL executor reports the ordering rule internally and maps a rejected read to a client error.</summary>
public sealed class SqlTableReadExecutorOrderingTests
{
    private static readonly TableIdentifier Id = new("d", "s", "t");

    private static TableMetadata Meta(string type, IReadOnlyList<string>? pk = null) => new()
    {
        Identifier = Id,
        Table = new Table { SchemaName = "s", TableName = "t", SourceName = "d", DataSourceType = DataSourceType.Sql },
        Columns = [new TableColumn { ColumnName = "c", DataType = type }],
        PrimaryKeyColumns = pk ?? []
    };

    private static DataSourceExecutionContext Context(TableMetadata meta, int offset = 0) => new(
        SourceName: "d",
        Metadata: meta,
        Principal: new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-X")], "Test")),
        AccessDecision: TableAccessDecision.Allowed(Id, new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["c"] = ColumnAccessLevel.Clear }, hasUnconstrainedColumnAllow: true),
        Arguments: new Dictionary<string, object?>(),
        RequestedFields: ["c"],
        Limit: 5,
        Offset: offset,
        Items: new Dictionary<string, object?>());

    private static SqlTableReadExecutor Executor()
    {
        var service = Substitute.For<IGovernedSqlExecutionService>();
        service.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("select", ["c"], new List<IReadOnlyDictionary<string, object?>>(), 0, 1));
        return new SqlTableReadExecutor(new GovernedTableReadPlanner(), () => service);
    }

    [Theory]
    [InlineData("int", "PrimaryKey")]
    [InlineData("xml", "None")]
    public async Task ExecuteAsync_ReportsTheOrderingRuleInternally(string type, string expected)
    {
        var ctx = Context(Meta(type, type == "int" ? ["c"] : null));

        await Executor().ExecuteAsync(ctx);

        ctx.Items[TableQueryItems.Ordering].ShouldBe(expected);
    }

    [Fact]
    public async Task ExecuteAsync_UnorderedPagingWithOffset_IsAClientError()
    {
        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() => Executor().ExecuteAsync(Context(Meta("xml"), offset: 5)));

        ex.Message.ShouldContain("no orderable column readable in clear text");
    }
}
