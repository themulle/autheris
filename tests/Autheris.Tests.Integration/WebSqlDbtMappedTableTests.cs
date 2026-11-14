namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Domain.Options.DataSources;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// #668: a dbt table of database <c>salesdb</c>, mapped by <c>Dbt:DatabaseMappings</c> to the connection <c>pgdata</c>, is catalogued as
/// <c>salesdb.public.orders</c> with <c>SourceName = pgdata</c>. WebSQL reaches it for the logical name <c>salesdb</c> through
/// <c>WebSql:DataSourceMappings:salesdb = pgdata</c> (real container, real DI container, real governance database).
/// </summary>
public sealed class WebSqlDbtMappedTableTests : IAsyncLifetime
{
    private const string Logical = "salesdb";
    private const string Connection = "pgdata";

    private PostgreSqlTestDatabase? _db;
    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    public async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            if (!_db.IsAvailable)
            {
                return;
            }

            await using var conn = new NpgsqlConnection(_db.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE public.orders (id integer, tenant_id text, region text, email text);
                INSERT INTO public.orders VALUES (1,'acme','EU','alice@x.example'),(2,'acme','US','bob@x.example'),(5,'other','EU','dan@x.example');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private async Task<WebApplicationFactory<Program>?> HostAsync(string? webSqlMappingTarget, bool withSqliteConnection = false)
    {
        if (_db is null || !_db.IsAvailable)
        {
            return null;
        }

        var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("WebSqlDbtMappedTableTests"));
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:DefaultDataSourceName", Connection);
            builder.UseSetting("Gateway:WebSql:AllowedDataSources:0", Logical);
            builder.UseSetting("Gateway:DataSources:RequireTenantColumn", "true");
            builder.UseSetting($"Gateway:Dbt:DatabaseMappings:{Logical}", Connection);
            if (webSqlMappingTarget is not null)
            {
                builder.UseSetting($"Gateway:WebSql:DataSourceMappings:{Logical}", webSqlMappingTarget);
            }

            if (withSqliteConnection)
            {
                builder.UseSetting("Gateway:DataSources:Connections:litedata:Provider", "Sqlite");
                builder.UseSetting("Gateway:DataSources:Connections:litedata:ConnectionString", "Data Source=:memory:");
            }

            builder.UseSetting($"Gateway:DataSources:Connections:{Connection}:Provider", "PostgreSql");
            builder.UseSetting($"Gateway:DataSources:Connections:{Connection}:ConnectionString", _db.ConnectionString);
            builder.UseSetting($"Gateway:DataSources:Connections:{Connection}:DefaultSchema", "public");
            builder.ConfigureTestServices(s => s.AddSingleton<IIdempotencyStore, ClusterWideTestIdempotencyStore>());
        });
        _hosts.Add(host);

        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await Register(repo, "orders");
        return host;
    }

    private static Task Register(ITableMetadataRepository repo, string table) =>
        repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = table, TableName = table, SchemaName = "public", SourceName = Logical, SourceType = "PostgreSql" },
            Identifier = new TableIdentifier(Logical, "public", table),
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "email", DataType = "varchar", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>()
        });

    private static async Task<GovernedSqlResult> QueryAsync(WebApplicationFactory<Program> host, string sql)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IIngressTransportContext>().Set(DataSourceTransportFlags.WebSql);
        var service = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-668"), new Claim("tenant_id", "acme")], "Test"));
        return await service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql, null, Logical), user, new TenantId("acme"));
    }

    [Fact]
    public async Task Query_TableCreatedThroughDbtDatabaseMapping_WithWebSqlMapping_ReturnsRows()
    {
        if (await HostAsync(Connection) is not { } host) return;

        var result = await QueryAsync(host, "SELECT id FROM salesdb.public.orders ORDER BY id");

        result.Rows.Select(r => Convert.ToInt64(r.First(kv => kv.Key.Equals("id", StringComparison.OrdinalIgnoreCase)).Value)).ShouldBe([1L, 2L]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Query_MappingToConnectionOfOtherDialect_Denied()
    {
        if (await HostAsync("litedata", withSqliteConnection: true) is not { } host) return;

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => QueryAsync(host, "SELECT id FROM salesdb.public.orders"));

        ex.Message.ShouldNotContain("litedata");
        ex.Message.ShouldNotContain(Connection);
    }

    [Fact]
    public async Task Query_WithoutWebSqlMapping_DeniedWithReason()
    {
        if (await HostAsync(null) is not { } host) return;

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => QueryAsync(host, "SELECT id FROM salesdb.public.orders"));

        ex.Message.ShouldContain("denied");
        ex.Message.ShouldNotContain(Connection);
    }
}
