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
public sealed class ParityHostFixture : IAsyncLifetime, IParityHost
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
    public HttpClient CreateClient(string roles = "Reader", string tenant = "tenant_parity") =>
        ParityHostSettings.CreateClient(_factory ?? throw new InvalidOperationException("Host not started."), roles, tenant);

    HttpClient IParityHost.CreateClient() => CreateClient();

    private WebApplicationFactory<Program> NewHost() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        ParityHostSettings.Apply(b, _govPath);
        b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
        b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={_dataPath}");
        b.UseSetting("Gateway:DataSources:Connections:second:Provider", "Sqlite");
        b.UseSetting("Gateway:DataSources:Connections:second:ConnectionString", $"Data Source={_secondPath}");
        b.UseSetting("Gateway:WebSql:Enabled", "true");
        b.UseSetting("Gateway:WebSql:AllowDml", "false");
        b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");
        // Federated scenarios (Robin #131) join the sources "default" and "second".
        b.UseSetting("Gateway:WebSql:CrossSource:Enabled", "true");
        b.UseSetting("Gateway:WebSql:AllowedDataSources:0", "default");
        b.UseSetting("Gateway:WebSql:AllowedDataSources:1", "second");
    });

    private static readonly (string Name, string Type)[] SalesColumns =
    [
        ("id", "integer"), ("region_id", "integer"), ("region", "varchar"), ("product", "varchar"), ("qty", "integer"),
        ("amount", "decimal"), ("sale_date", "date"), ("sold_at", "timestamp"), ("is_return", "boolean"), ("note", "varchar"),
    ];

    private static async Task RegisterCatalogAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(Metadata(ParitySeed.RegionsTable, "default",
            ("id", "integer"), ("name", "varchar"), ("tz", "varchar")));
        await repo.UpsertTableMetadataAsync(Metadata(ParitySeed.SalesTable, "default", SalesColumns));
        await repo.UpsertTableMetadataAsync(Metadata(ParitySeed.SalesTable, "second", SalesColumns));
        await repo.UpsertTableMetadataAsync(Metadata(ParitySeed.BigTable, "default", ("id", "integer"), ("grp", "varchar")));

        // WP-R2: the bulk keyset route and the analyze endpoint need a declared key and a visible tier (Hidden tables answer 404 / UNKNOWN_TABLE for a reader).
        var governance = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        foreach (var table in new[] { ParitySeed.SalesTable, ParitySeed.BigTable })
        {
            var id = new TableIdentifier("default", "main", table);
            await governance.ReplaceTableKeyDefinitionAsync(id, ["id"], []);
            await governance.SetDatasetVisibilityAsync(id, DatasetVisibility.Full, new Sid("parity-seed"));
        }

        var scoped = Metadata(ParitySeed.ScopedTable, "default", SalesColumns);
        await repo.UpsertTableMetadataAsync(scoped with { Table = scoped.Table with { RequiredFilterColumns = ["region"] } });

        var masked = Metadata(ParitySeed.MaskedTable, "default", SalesColumns);
        await repo.UpsertTableMetadataAsync(masked with
        {
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["amount"] = new MaskingRule { RuleType = "NULLIFY" } },
        });
    }

    private static TableMetadata Metadata(string table, string source, params (string Name, string Type)[] columns)
    {
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Table = new Table
            {
                Id = id,
                DisplayName = table,
                TableName = table,
                SchemaName = "main",
                SourceType = "Sqlite",
                SourceName = source,
                IsActive = true,
            },
            Identifier = new TableIdentifier(source, "main", table),
            Columns = columns.Select(c => new TableColumn { TableId = id, ColumnName = c.Name, DataType = c.Type }).ToList(),
        };
    }
}
