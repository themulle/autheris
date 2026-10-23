using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Parity.Seed;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Autheris.Tests.Parity.Infrastructure;

[CollectionDefinition(Name)]
public sealed class ParityHostCollection : ICollectionFixture<ParityHostFixture>
{
    public const string Name = "ParityHost";
}

/// <summary>
/// One in-process API host per run (D-7): test auth, SQLite governance file, SQLite data source <c>default</c> seeded by
/// <see cref="ParitySeed"/> and a second source <c>second</c>. The catalog is registered by a first host so the GraphQL schema
/// (built at start-up) contains the tables. Test auth is enabled only here (SEC-PAR-03).
/// </summary>
public sealed class ParityHostFixture : IAsyncLifetime
{
    private string _workDir = string.Empty;
    private string _dataPath = string.Empty;
    private string _secondPath = string.Empty;
    private string _govPath = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    public string DataPath => _dataPath;

    public async Task InitializeAsync()
    {
        ParityRunGuard.EnsureEnabled();
        _workDir = Path.Combine(Path.GetTempPath(), "autheris-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _dataPath = Path.Combine(_workDir, "data.db");
        _secondPath = Path.Combine(_workDir, "second.db");
        _govPath = Path.Combine(_workDir, "gov.db");
        ParitySeed.Create(_dataPath);
        ParitySeed.Create(_secondPath, ParitySeed.SecondSales);

        using (var seeding = NewHost())
        {
            await RegisterCatalogAsync(seeding);
        }

        _factory = NewHost();
        _ = _factory.Server;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_workDir, true);
        }
        catch (IOException)
        {
            // best effort
        }

        return Task.CompletedTask;
    }

    /// <summary>Client with test-auth headers; a timeout is a cell failure, there is no retry (M-5).</summary>
    public HttpClient CreateClient(string roles = "Reader", string tenant = "tenant_parity")
    {
        var client = (_factory ?? throw new InvalidOperationException("Host not started.")).CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-PARITY");
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        return client;
    }

    private WebApplicationFactory<Program> NewHost() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
        b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "100000");
        b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
        b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source={_govPath}");
        b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
        b.UseSetting("Gateway:Rebac:Enabled", "false");
        b.UseSetting("Gateway:OpenSchema", "true");
        b.UseSetting("Gateway:Tenancy:Mode", "Multi");
        b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
        b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={_dataPath}");
        b.UseSetting("Gateway:DataSources:Connections:second:Provider", "Sqlite");
        b.UseSetting("Gateway:DataSources:Connections:second:ConnectionString", $"Data Source={_secondPath}");
        b.UseSetting("Gateway:WebSql:Enabled", "true");
        b.UseSetting("Gateway:WebSql:AllowDml", "false");
        b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");
    });

    private static async Task RegisterCatalogAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(Metadata(ParitySeed.RegionsTable,
            ("id", "integer"), ("name", "varchar"), ("tz", "varchar")));
        await repo.UpsertTableMetadataAsync(Metadata(ParitySeed.SalesTable,
            ("id", "integer"), ("region_id", "integer"), ("region", "varchar"), ("product", "varchar"), ("qty", "integer"),
            ("amount", "decimal"), ("sale_date", "date"), ("sold_at", "timestamp"), ("is_return", "boolean"), ("note", "varchar")));
    }

    private static TableMetadata Metadata(string table, params (string Name, string Type)[] columns) => new()
    {
        Table = new Table { Id = Guid.NewGuid(), DisplayName = table, TableName = table, SchemaName = "main", SourceType = "Sqlite", SourceName = "default" },
        Identifier = new TableIdentifier("default", "main", table),
        Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type }).ToList(),
    };
}
