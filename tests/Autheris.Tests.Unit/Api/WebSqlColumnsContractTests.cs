namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class WebSqlColumnsContractTests
{
    private static (DefaultHttpContext, MemoryStream) CreateContext(string sql, string queryString = "")
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = sp };
        context.Request.Path = "/api/v1/websql/query";
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(queryString);
        context.Request.ContentType = "application/json";

        var body = JsonSerializer.Serialize(new { sql });
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;

        var claims = new[]
        {
            new Claim("sub", "user_1"),
            new Claim("tenant_id", "tenant_a")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        return (context, responseBody);
    }

    [Fact]
    public async Task Columns_HaveNameTypeNullableMasked()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("email", typeof(string));
        table.Rows.Add(1, "alice@example.com");

        var masks = ImmutableArray.Create(false, true);
        var service = new TestGovernedSqlService(table, masks);
        var (context, responseStream) = CreateContext("SELECT id, email FROM users");
        var options = Options.Create(new GatewayOptions());

        await WebSqlEndpoints.HandleWebSqlRequest(context, service, options, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        responseStream.Position = 0;
        using var doc = JsonDocument.Parse(responseStream);
        var root = doc.RootElement;
        root.TryGetProperty("columns", out var columns).ShouldBeTrue();
        columns.ValueKind.ShouldBe(JsonValueKind.Array);
        columns.GetArrayLength().ShouldBe(2);

        var col0 = columns[0];
        col0.GetProperty("name").GetString().ShouldBe("id");
        col0.GetProperty("type").GetString().ShouldBe("integer");
        col0.GetProperty("nullable").GetBoolean().ShouldBeTrue();
        col0.GetProperty("masked").GetBoolean().ShouldBeFalse();

        var col1 = columns[1];
        col1.GetProperty("name").GetString().ShouldBe("email");
        col1.GetProperty("type").GetString().ShouldBe("varchar");
        col1.GetProperty("nullable").GetBoolean().ShouldBeTrue();
        col1.GetProperty("masked").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ArraysFormat_SameColumnObjects()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("email", typeof(string));
        table.Rows.Add(1, "alice@example.com");

        var masks = ImmutableArray.Create(false, true);
        var service = new TestGovernedSqlService(table, masks);
        var (context, responseStream) = CreateContext("SELECT id, email FROM users", "?format=arrays");
        var options = Options.Create(new GatewayOptions());

        await WebSqlEndpoints.HandleWebSqlRequest(context, service, options, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        responseStream.Position = 0;
        using var doc = JsonDocument.Parse(responseStream);
        var root = doc.RootElement;
        root.TryGetProperty("columns", out var columns).ShouldBeTrue();
        columns.ValueKind.ShouldBe(JsonValueKind.Array);
        columns.GetArrayLength().ShouldBe(2);

        var col0 = columns[0];
        col0.GetProperty("name").GetString().ShouldBe("id");
        col0.GetProperty("type").GetString().ShouldBe("integer");
        col0.GetProperty("nullable").GetBoolean().ShouldBeTrue();
        col0.GetProperty("masked").GetBoolean().ShouldBeFalse();

        var col1 = columns[1];
        col1.GetProperty("name").GetString().ShouldBe("email");
        col1.GetProperty("type").GetString().ShouldBe("varchar");
        col1.GetProperty("nullable").GetBoolean().ShouldBeTrue();
        col1.GetProperty("masked").GetBoolean().ShouldBeTrue();

        root.TryGetProperty("rows", out var rows).ShouldBeTrue();
        rows.ValueKind.ShouldBe(JsonValueKind.Array);
        rows[0].ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task EmptyResult_EmptyColumns()
    {
        var table = new DataTable();
        // 0 columns
        var service = new TestGovernedSqlService(table, ImmutableArray<bool>.Empty);
        var (context, responseStream) = CreateContext("SELECT 1 WHERE 1=0");
        var options = Options.Create(new GatewayOptions());

        await WebSqlEndpoints.HandleWebSqlRequest(context, service, options, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        responseStream.Position = 0;
        using var doc = JsonDocument.Parse(responseStream);
        var root = doc.RootElement;
        root.TryGetProperty("columns", out var columns).ShouldBeTrue();
        columns.ValueKind.ShouldBe(JsonValueKind.Array);
        columns.GetArrayLength().ShouldBe(0);
    }

    private sealed class TestGovernedSqlService : IGovernedSqlExecutionService
    {
        private readonly DataTable _table;
        private readonly ImmutableArray<bool> _masks;

        public TestGovernedSqlService(DataTable table, ImmutableArray<bool> masks)
        {
            _table = table;
            _masks = masks;
        }

        public async Task ExecuteGovernedQueryAsync(
            GovernedSqlQueryRequest request,
            ClaimsPrincipal user,
            TenantId tenantId,
            Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter,
            CancellationToken ct = default)
        {
            using var reader = _table.CreateDataReader();
            var cols = SqlResultColumns.Describe(reader);
            var info = new GovernedResultInfo(cols, _masks.IsDefault ? ImmutableArray.CreateRange(Enumerable.Repeat(false, reader.FieldCount)) : _masks);
            await rowWriter(reader, info, ct);
        }

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
