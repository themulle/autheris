namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// WebSQL findings v1.1.0, 2.2 and 2.3: the Trino endpoint announces real column types (with the Trino value encoding)
/// and names unnamed or duplicate result columns <c>_colN</c>.
/// </summary>
public sealed class TrinoColumnTypeTests
{
    [Theory]
    [InlineData(typeof(long), "bigint")]
    [InlineData(typeof(int), "integer")]
    [InlineData(typeof(short), "smallint")]
    [InlineData(typeof(byte), "smallint")]
    [InlineData(typeof(double), "double")]
    [InlineData(typeof(float), "real")]
    [InlineData(typeof(bool), "boolean")]
    [InlineData(typeof(string), "varchar")]
    [InlineData(typeof(DateTime), "timestamp(3)")]
    [InlineData(typeof(DateTimeOffset), "timestamp(3) with time zone")]
    [InlineData(typeof(DateOnly), "date")]
    [InlineData(typeof(TimeSpan), "time(3)")]
    [InlineData(typeof(Guid), "uuid")]
    [InlineData(typeof(byte[]), "varbinary")]
    [InlineData(null, "varchar")]
    public void Map_ClrType_ToTrinoType(Type? clrType, string expected)
    {
        TrinoColumnTypes.Map(new SqlResultColumn("c", clrType), []).Name.ShouldBe(expected);
    }

    [Fact]
    public void Map_Decimal_UsesSchemaPrecisionAndScale()
    {
        TrinoColumnTypes.Map(new SqlResultColumn("c", typeof(decimal), 18, 2), []).Name.ShouldBe("decimal(18,2)");
    }

    [Fact]
    public void Map_Decimal_WithoutSchema_UsesLargestObservedScale()
    {
        TrinoColumnTypes.Map(new SqlResultColumn("c", typeof(decimal)), [1.5m, null, 2.125m]).Name.ShouldBe("decimal(38,3)");
    }

    [Fact]
    public void Encode_FollowsTrinoWireFormat()
    {
        var ts = TrinoColumnTypes.Map(new SqlResultColumn("c", typeof(DateTime)), []);
        TrinoColumnTypes.Encode(new DateTime(2026, 10, 8, 13, 4, 5, 123), ts).ShouldBe("2026-10-08 13:04:05.123");

        var tz = TrinoColumnTypes.Map(new SqlResultColumn("c", typeof(DateTimeOffset)), []);
        TrinoColumnTypes.Encode(new DateTimeOffset(2026, 10, 8, 13, 4, 5, 123, TimeSpan.FromHours(2)), tz).ShouldBe("2026-10-08 13:04:05.123 +02:00");

        var dec = TrinoColumnTypes.Map(new SqlResultColumn("c", typeof(decimal), 10, 2), []);
        TrinoColumnTypes.Encode(12.5m, dec).ShouldBe("12.5");

        var date = TrinoColumnTypes.Map(new SqlResultColumn("c", typeof(DateTime), DataTypeName: "date"), []);
        date.Name.ShouldBe("date");
        TrinoColumnTypes.Encode(new DateTime(2026, 10, 8), date).ShouldBe("2026-10-08");

        var bin = TrinoColumnTypes.Map(new SqlResultColumn("c", typeof(byte[])), []);
        TrinoColumnTypes.Encode(new byte[] { 1, 2, 3 }, bin).ShouldBe("AQID");

        var dbl = TrinoColumnTypes.Map(new SqlResultColumn("c", typeof(double)), []);
        TrinoColumnTypes.Encode(double.NaN, dbl).ShouldBe("NaN");
        TrinoColumnTypes.Encode(1.5d, dbl).ShouldBe(1.5d);

        var big = TrinoColumnTypes.Map(new SqlResultColumn("c", typeof(long)), []);
        TrinoColumnTypes.Encode(7235L, big).ShouldBe(7235L);
        TrinoColumnTypes.Encode(null, big).ShouldBeNull();
    }

    [Fact]
    public void ColumnNames_EmptyOrDuplicate_BecomePositionalColN()
    {
        SqlResultColumns.MakeUnique(["", "id", "ID", "_col4", ""]).ShouldBe(["_col0", "id", "_col2", "_col4", "_col4_1"]);
    }

    [Fact]
    public async Task BufferedResult_FromSqlite_DescribesColumnsAndNamesUnnamedColumns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), 1.5 AS x, 'a' AS x";
        await using var reader = await command.ExecuteReaderAsync();

        var columns = SqlResultColumns.Describe(reader);

        columns.Select(c => c.Name).ShouldBe(["COUNT(*)", "x", "_col2"]);
        columns[0].ClrType.ShouldBe(typeof(long));
    }

    [Fact]
    public async Task StatementManager_EncodesDataAndReportsColumnTypes()
    {
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IGovernedSqlExecutionService)).Returns(sql);

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "user-abc")], "Test"));
        var tenant = new TenantId("tenant-123");
        var request = new GovernedSqlQueryRequest("SELECT COUNT(*), created FROM t");
        var created = new DateTimeOffset(2026, 10, 8, 10, 35, 0, TimeSpan.Zero);
        sql.ExecuteQueryBufferedAsync(request, user, tenant, Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult(
                request.Sql, request.Sql, ["_col0", "created"],
                [new Dictionary<string, object?> { ["_col0"] = 7235L, ["created"] = created }],
                1, 1,
                ColumnDescriptions: [new SqlResultColumn("_col0", typeof(long)), new SqlResultColumn("created", typeof(DateTimeOffset))]));

        using var manager = new WebSqlStatementManager(scopeFactory, NullLogger<WebSqlStatementManager>.Instance);
        var status = await manager.SubmitOrWaitAsync(request, user, tenant, TimeSpan.FromSeconds(5));

        status.State.ShouldBe("FINISHED");
        status.ColumnTypes!.Select(t => t.Name).ShouldBe(["bigint", "timestamp(3) with time zone"]);
        status.Data![0].ShouldBe([7235L, "2026-10-08 10:35:00.000 +00:00"]);
    }

    [Fact]
    public async Task TrinoResponse_WritesTypeAndTypeSignature()
    {
        var status = new StatementExecutionStatus(
            "q1", "FINISHED", ["_col0", "name", "amount"], [], null, null, 1,
            ColumnTypes:
            [
                TrinoColumnTypes.Map(new SqlResultColumn("_col0", typeof(long)), []),
                TrinoColumnTypes.Map(new SqlResultColumn("name", typeof(string)), []),
                TrinoColumnTypes.Map(new SqlResultColumn("amount", typeof(decimal), 18, 2), [])
            ]);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await WebSqlEndpoints.WriteTrinoStatementResponseAsync(context, status, CancellationToken.None);

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var columns = doc.RootElement.GetProperty("columns").EnumerateArray().ToList();
        columns.Select(c => c.GetProperty("type").GetString()).ShouldBe(["bigint", "varchar", "decimal(18,2)"]);
        columns[0].GetProperty("typeSignature").GetProperty("rawType").GetString().ShouldBe("bigint");
        columns[1].GetProperty("typeSignature").GetProperty("arguments")[0].GetProperty("value").GetInt64().ShouldBe(2147483647);
        var decimalArgs = columns[2].GetProperty("typeSignature").GetProperty("arguments").EnumerateArray()
            .Select(a => a.GetProperty("value").GetInt64()).ToList();
        decimalArgs.ShouldBe([18L, 2L]);
    }
}
