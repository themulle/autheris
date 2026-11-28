namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
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

public sealed class WebSqlViewEndpointTests
{
    private const string Stored = "SELECT id, werk, betrag FROM erp.sales.auftraege WHERE betrag >= @min";

    private static (DefaultHttpContext Context, MemoryStream Response) CreateContext(string body)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Path = "/api/v1/sql";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user_1"), new Claim("tenant_id", "tenant_a")], "Test"));
        var response = new MemoryStream();
        context.Response.Body = response;
        return (context, response);
    }

    private static string Body(string view, string? extra = null) =>
        $$"""{ "sql": "{{Stored}}", "parameters": { "min": 5 }, "view": {{view}} {{extra}} }""";

    private static async Task<(RecordingService Service, JsonElement Root, int Status)> PostAsync(string body)
    {
        var service = new RecordingService();
        var (context, response) = CreateContext(body);

        await WebSqlEndpoints.HandleWebSqlRequest(context, service, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        response.Position = 0;
        var root = response.Length == 0 ? default : JsonDocument.Parse(response).RootElement.Clone();
        return (service, root, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_view_is_composed_into_the_governed_request_with_bound_values()
    {
        var (service, root, status) = await PostAsync(Body("""{ "filter": { "column": "werk", "op": "eq", "value": "Ehingen" }, "orderBy": [ { "column": "betrag", "direction": "desc" } ] }"""));

        status.ShouldBe(StatusCodes.Status200OK);
        root.GetProperty("rowCount").GetInt32().ShouldBe(1);
        root.TryGetProperty("totalCount", out _).ShouldBeFalse();
        var request = service.Requests.Single();
        request.Sql.ShouldBe("SELECT * FROM (\n" + Stored + "\n) AS v WHERE v.werk = @__gql_tf_0 ORDER BY v.betrag DESC");
        request.Sql.ShouldNotContain("Ehingen");
        request.Parameters!["min"].ShouldBeOfType<JsonElement>();
        request.GatewayParameters!["@__gql_tf_0"].ShouldBe("Ehingen");
        request.StructuredViewRead.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_a_view_the_request_is_unchanged()
    {
        var (service, _, status) = await PostAsync($$"""{ "sql": "{{Stored}}", "parameters": { "min": 5 }, "view": null }""");

        status.ShouldBe(StatusCodes.Status200OK);
        var request = service.Requests.Single();
        request.Sql.ShouldBe(Stored);
        request.GatewayParameters.ShouldBeNull();
        request.StructuredViewRead.ShouldBeFalse();
    }

    [Fact]
    public async Task Total_count_runs_a_second_governed_statement_and_is_reported()
    {
        var (service, root, status) = await PostAsync(Body("""{ "filter": { "column": "werk", "op": "eq", "value": "Ehingen" }, "totalCount": true }"""));

        status.ShouldBe(StatusCodes.Status200OK);
        root.GetProperty("totalCount").GetInt64().ShouldBe(42);
        service.BufferedRequests.Single().Sql.ShouldStartWith("SELECT COUNT(*) AS total_count FROM (");
        service.Requests.Single().Sql.ShouldStartWith("SELECT * FROM (");
    }

    [Theory]
    [InlineData("""{ "filter": { "column": "geheim", "op": "eq", "value": 1 } }""")]
    [InlineData("""{ "groupBy": ["werk"] }""")]
    [InlineData("""{ "filter": { "column": "werk", "op": "regex", "value": "x" } }""")]
    [InlineData("""[1]""")]
    [Trait("Category", "Security")]
    public async Task A_refused_view_is_a_400_and_never_reaches_the_pipeline(string view)
    {
        var (service, root, status) = await PostAsync(Body(view));

        status.ShouldBe(StatusCodes.Status400BadRequest);
        root.GetProperty("code").GetString().ShouldBe("SQL_VIEW_INVALID");
        service.Requests.ShouldBeEmpty();
        service.BufferedRequests.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Total_count_is_refused_with_wait_timeout()
    {
        var service = new RecordingService();
        var (context, response) = CreateContext(Body("""{ "totalCount": true }"""));
        context.Request.QueryString = new QueryString("?wait_timeout=5s");

        await WebSqlEndpoints.HandleWebSqlRequest(context, service, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        service.Requests.ShouldBeEmpty();
        service.BufferedRequests.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task A_view_with_a_dml_statement_is_refused()
    {
        var service = new RecordingService();
        var (context, _) = CreateContext("""{ "sql": "DELETE FROM erp.sales.auftraege", "view": { "orderBy": [ { "column": "id" } ] } }""");

        await WebSqlEndpoints.HandleWebSqlRequest(context, service, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        service.Requests.ShouldBeEmpty();
    }

    private sealed class RecordingService : IGovernedSqlExecutionService
    {
        public List<GovernedSqlQueryRequest> Requests { get; } = [];

        public List<GovernedSqlQueryRequest> BufferedRequests { get; } = [];

        public async Task ExecuteGovernedQueryAsync(
            GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId,
            Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter, CancellationToken ct = default)
        {
            Requests.Add(request);
            var table = new DataTable();
            table.Columns.Add("id", typeof(int));
            table.Rows.Add(1);
            using var reader = table.CreateDataReader();
            await rowWriter(reader, new GovernedResultInfo(SqlResultColumns.Describe(reader), System.Collections.Immutable.ImmutableArray.Create(false)), ct);
        }

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
        {
            BufferedRequests.Add(request);
            IReadOnlyDictionary<string, object?> row = new Dictionary<string, object?> { ["total_count"] = 42L };
            return Task.FromResult(new GovernedSqlResult(request.Sql, ["total_count"], [row], 1, 0));
        }

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
