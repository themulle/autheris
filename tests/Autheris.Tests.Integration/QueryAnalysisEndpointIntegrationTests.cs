namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Query analysis of an ad-hoc SQL statement answers the code the SQL endpoint answers for the same statement, and opens no source
/// connection. Runs on the host of <see cref="GovernedWebSqlIntegrationTests"/> (real PostgreSQL, executed on CI).
/// </summary>
public sealed class QueryAnalysisEndpointIntegrationTests : IClassFixture<GovernedWebSqlIntegrationTests.HostFixture>
{
    private const string Table = "ledger_masked";
    private readonly GovernedWebSqlIntegrationTests.HostFixture _factory;

    public QueryAnalysisEndpointIntegrationTests(GovernedWebSqlIntegrationTests.HostFixture factory) => _factory = factory;

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-WEBSQL");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_alpha");
        return client;
    }

    private async Task RegisterAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var id = new TableIdentifier("default", "public", Table);
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = Table, TableName = Table, SchemaName = "public", SourceName = "default", SourceType = "PostgreSql" },
            Identifier = id,
            Columns = new[] { "id", "note", "ssn", "tenant_id" }.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList()
        });
        var meta = await repo.GetTableMetadataAsync(id);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = meta!.Table.Id,
            TableIdentifier = id,
            TenantId = new TenantId("tenant_alpha"),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-WEBSQL"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "note", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "tenant_id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Mask }
            ]
        });
    }

    private static Task<HttpResponseMessage> AnalyzeAsync(HttpClient client, string sql) =>
        client.PostAsJsonAsync("/api/v1/governance/queries/analyze", new { queries = new[] { new { sqlQuery = sql, dataSource = "default" } } });

    private static bool ContainsString(JsonElement e, string value) => e.ValueKind switch
    {
        JsonValueKind.String => string.Equals(e.GetString(), value, StringComparison.Ordinal),
        JsonValueKind.Object => e.EnumerateObject().Any(p => ContainsString(p.Value, value)),
        JsonValueKind.Array => e.EnumerateArray().Any(i => ContainsString(i, value)),
        _ => false
    };

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("SELECT pg_sleep(1) FROM ledger_masked")]
    [InlineData("SELECT id FROM ledger_masked TABLESAMPLE BERNOULLI (10)")]
    [InlineData("SELECT id FROM ledger_masked /* a /* b */ c */")]
    [InlineData("SELECT id FROM ledger_masked WHERE ssn = 'x'")]
    public async Task Analyze_Sql_SameCodeAsExecute_RealPostgres(string sql)
    {
        await RegisterAsync();
        var client = Client(_factory);

        var executed = await client.PostAsJsonAsync("/api/v1/sql", new { sql });
        var executedBody = await executed.Content.ReadAsStringAsync();
        executed.IsSuccessStatusCode.ShouldBeFalse(executedBody);
        using var executedDoc = JsonDocument.Parse(executedBody);
        var code = executedDoc.RootElement.GetProperty("code").GetString()!;

        var analyzed = await AnalyzeAsync(client, sql);
        var analyzedBody = await analyzed.Content.ReadAsStringAsync();
        analyzed.StatusCode.ShouldBe(HttpStatusCode.OK, analyzedBody);
        using var analyzedDoc = JsonDocument.Parse(analyzedBody);
        ContainsString(analyzedDoc.RootElement, code).ShouldBeTrue($"the analysis does not report {code}: {analyzedBody}");
        analyzedBody.ShouldContain("\"isValid\":false");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Analyze_Sql_NoSourceConnectionOpened()
    {
        await RegisterAsync();
        var counter = new CountingConnectionFactory();
        using var counted = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(ISqlConnectionFactory)).ToList())
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<ISqlConnectionFactory>(counter);
            }));
        var client = Client(counted);

        foreach (var sql in new[] { "SELECT id FROM ledger_masked", "SELECT pg_sleep(1) FROM ledger_masked", "SELECT id FROM ledger_masked WHERE ssn = 'x'" })
        {
            (await AnalyzeAsync(client, sql)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        counter.Opened.ShouldBe(0);
    }

    private sealed class CountingConnectionFactory : ISqlConnectionFactory
    {
        private int _opened;

        public int Opened => Volatile.Read(ref _opened);

        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _opened);
            throw new InvalidOperationException("Query analysis must not open a source connection.");
        }
    }
}
