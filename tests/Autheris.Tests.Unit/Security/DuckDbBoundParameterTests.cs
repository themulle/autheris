using System;
using System.Collections.Generic;
using System.Security;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>WP-X3: the federated DuckDB plan binds its values as <c>$n</c> parameters; user-facing OLAP SQL still rejects <c>$</c>.</summary>
public sealed class DuckDbBoundParameterTests
{
    private static DuckDbOlapEngine CreateEngine() => new(
        new DuckDbOlapOptions { Enabled = true, MaxMemory = "256MB", QueryTimeoutSeconds = 10, MaxThreads = 1 },
        NullLogger<DuckDbOlapEngine>.Instance);

    private static BoundParameter Param(int ordinal, object? value, SqlParameterType type) =>
        new($"${ordinal + 1}", (ordinal + 1).ToString(), ordinal, value, type, ParameterOrigin.QueryLiteral);

    private static OlapTableSource Source()
    {
        var table = new TableIdentifier("crm", "public", "orders");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = [new TableColumn { ColumnName = "id", DataType = "bigint" }, new TableColumn { ColumnName = "note", DataType = "varchar" }]
        };
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1L, ["note"] = "a' OR 1=1 --" },
            new Dictionary<string, object?> { ["id"] = 2L, ["note"] = "plain" },
            new Dictionary<string, object?> { ["id"] = 3L, ["note"] = "plain" },
        };
        return new OlapTableSource(table, rows, meta, StagingTableName: "s0");
    }

    [Fact]
    public async Task ExecuteGenerated_BoundMarkers_FilterWithBoundValues()
    {
        var query = new GeneratedOlapQuery(
            "SELECT \"id\" FROM \"s0\" WHERE \"note\" = $1 AND \"id\" > $2",
            [Param(0, "plain", SqlParameterType.String), Param(1, 2L, SqlParameterType.Int64)]);

        var result = await CreateEngine().ExecuteGeneratedAsync(query, [Source()]);

        result.Rows.Count.ShouldBe(1);
        Convert.ToInt64(result.Rows[0][0]).ShouldBe(3L);
    }

    [Fact]
    public async Task ExecuteGenerated_InjectionStringIsDataNotSql()
    {
        var query = new GeneratedOlapQuery(
            "SELECT \"id\" FROM \"s0\" WHERE \"note\" = $1",
            [Param(0, "a' OR 1=1 --", SqlParameterType.String)]);

        var result = await CreateEngine().ExecuteGeneratedAsync(query, [Source()]);

        result.Rows.Count.ShouldBe(1);
        Convert.ToInt64(result.Rows[0][0]).ShouldBe(1L);
    }

    [Theory]
    [InlineData("SELECT $$x$$ FROM \"s0\" WHERE \"id\" = $1")]
    [InlineData("SELECT \"id\" FROM \"s0\" WHERE \"note\" = $name")]
    [InlineData("SELECT \"id\" FROM \"s0\" WHERE \"id\" = $2")]
    [InlineData("SELECT \"id\" FROM \"s0\" WHERE \"note\" = '$1'")]
    [InlineData("SELECT \"id\" FROM \"s0\" WHERE \"id\" = $0")]
    public async Task ExecuteGenerated_NonMarkerDollar_IsStillRejected(string sql)
    {
        var query = new GeneratedOlapQuery(sql, [Param(0, 1L, SqlParameterType.Int64)]);

        await Should.ThrowAsync<SecurityException>(() => CreateEngine().ExecuteGeneratedAsync(query, [Source()]));
    }

    [Fact]
    public async Task ExecuteGenerated_MarkerWithoutParameters_IsRejected()
    {
        var query = new GeneratedOlapQuery("SELECT \"id\" FROM \"s0\" WHERE \"id\" = $1");

        await Should.ThrowAsync<SecurityException>(() => CreateEngine().ExecuteGeneratedAsync(query, [Source()]));
    }

    [Fact]
    public async Task ExecuteOlapQuery_UserSqlWithMarker_IsStillRejected()
    {
        await Should.ThrowAsync<SecurityException>(() =>
            CreateEngine().ExecuteOlapQueryAsync(new OlapQueryRequest("SELECT \"id\" FROM \"s0\" WHERE \"id\" = $1", [Source()])));
    }
}
