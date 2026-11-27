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
using Autheris.Domain.Interfaces;
using Autheris.Domain.Common;
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
    private const string Table = "payroll";
    private readonly GovernedWebSqlIntegrationTests.HostFixture _factory;

    public QueryAnalysisEndpointIntegrationTests(GovernedWebSqlIntegrationTests.HostFixture factory) => _factory = factory;

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-WEBSQL");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_123");
        return client;
    }

    private static readonly SemaphoreSlim RegistrationGate = new(1, 1);
    private static bool _registered;

    // The physical table "payroll" is seeded by the host; its ssn column is masked by a catalog masking rule (the consent bypass of the host
    // does not matter for a catalog rule). Registered once per class.
    private async Task RegisterAsync()
    {
        await RegistrationGate.WaitAsync();
        try
        {
            if (_registered)
            {
                return;
            }

            using var scope = _factory.Services.CreateScope();
            var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            await tableRepo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = Table, TableName = Table, SchemaName = "public", SourceName = "default", SourceType = "PostgreSql" },
                Identifier = new TableIdentifier("default", "public", Table),
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "varchar" },
                    new TableColumn { ColumnName = "ssn", DataType = "varchar" },
                    new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
                ],
                ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("ssn"),
                ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["ssn"] = new MaskingRule { RuleType = "REDACT", Replacement = "XXX" } }
            });
            _registered = true;
        }
        finally
        {
            RegistrationGate.Release();
        }
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
    [InlineData("SELECT pg_sleep(1) FROM payroll")]
    [InlineData("SELECT id FROM payroll TABLESAMPLE BERNOULLI (10)")]
    [InlineData("SELECT id FROM payroll /* a /* b */ c */")]
    [InlineData("SELECT id FROM payroll WHERE ssn = 'x'")]
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

        foreach (var sql in new[] { "SELECT id FROM payroll", "SELECT pg_sleep(1) FROM payroll", "SELECT id FROM payroll WHERE ssn = 'x'" })
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
