using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Seed;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Autheris.Tests.Parity.Dialects;

[CollectionDefinition(Name)]
public sealed class ParityPostgresCollection : ICollectionFixture<ParityPostgresHostFixture>
{
    public const string Name = "ParityPostgresHost";
}

/// <summary>
/// Container dialect host (D-15, block B): a PostgreSQL 16 Testcontainers instance seeded with the same rows as the SQLite file (PostgreSQL
/// DDL), registered as source <c>default</c> in schema <c>public</c>. Used only by the <c>parity-dialects</c> job (trait
/// <c>Dialect=Container</c>); it is never run locally. SQL Server is registered Pending (plan section 6).
/// </summary>
public sealed class ParityPostgresHostFixture : IAsyncLifetime, IParityHost
{
    public const string Schema = "public";

    private PostgreSqlContainer? _container;
    private string _workDir = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        ParityRunGuard.EnsureEnabled();
        _workDir = Path.Combine(Path.GetTempPath(), "autheris-parity-pg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        await SeedAsync(_container.GetConnectionString());

        using (var seeding = NewHost(_container.GetConnectionString()))
        {
            await RegisterCatalogAsync(seeding);
        }

        _factory = NewHost(_container.GetConnectionString());
        _ = _factory.Server;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        NpgsqlConnection.ClearAllPools();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        try
        {
            Directory.Delete(_workDir, true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    public HttpClient CreateClient() =>
        ParityHostSettings.CreateClient(_factory ?? throw new InvalidOperationException("Host not started."));

    private WebApplicationFactory<Program> NewHost(string connectionString) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        ParityHostSettings.Apply(b, Path.Combine(_workDir, "gov.db"));
        b.UseSetting("Gateway:DataSources:Connections:default:Provider", "PostgreSql");
        b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", connectionString);
        b.UseSetting("Gateway:DataSources:Connections:default:DefaultSchema", Schema);
        b.UseSetting("Gateway:WebSql:Enabled", "true");
        b.UseSetting("Gateway:WebSql:AllowDml", "false");
        b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");
    });

    private static async Task SeedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var create = new NpgsqlCommand(
            "CREATE TABLE parity_sales (id INTEGER PRIMARY KEY, region_id INTEGER NULL, region TEXT NULL, product TEXT NOT NULL, qty INTEGER NULL, " +
            "amount NUMERIC(12,2) NULL, sale_date DATE NOT NULL, sold_at TIMESTAMP NOT NULL, is_return BOOLEAN NULL, note TEXT NULL);", conn))
        {
            await create.ExecuteNonQueryAsync();
        }

        foreach (var s in ParitySeed.Sales)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO parity_sales (id, region_id, region, product, qty, amount, sale_date, sold_at, is_return, note) " +
                "VALUES (@id, @region_id, @region, @product, @qty, @amount, @sale_date, @sold_at, @is_return, @note)", conn);
            cmd.Parameters.AddWithValue("id", s.Id);
            cmd.Parameters.AddWithValue("region_id", (object?)s.RegionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("region", (object?)s.Region ?? DBNull.Value);
            cmd.Parameters.AddWithValue("product", s.Product);
            cmd.Parameters.AddWithValue("qty", (object?)s.Qty ?? DBNull.Value);
            cmd.Parameters.AddWithValue("amount", (object?)s.Amount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sale_date", DateOnly.ParseExact(s.SaleDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("sold_at", DateTime.SpecifyKind(DateTime.Parse(s.SoldAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal), DateTimeKind.Unspecified));
            cmd.Parameters.AddWithValue("is_return", (object?)s.IsReturn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("note", (object?)s.Note ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task RegisterCatalogAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var id = Guid.NewGuid();
        (string Name, string Type)[] columns =
        [
            ("id", "integer"), ("region_id", "integer"), ("region", "varchar"), ("product", "varchar"), ("qty", "integer"),
            ("amount", "decimal"), ("sale_date", "date"), ("sold_at", "timestamp"), ("is_return", "boolean"), ("note", "varchar"),
        ];
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table
            {
                Id = id,
                DisplayName = ParitySeed.SalesTable,
                TableName = ParitySeed.SalesTable,
                SchemaName = Schema,
                SourceType = "PostgreSql",
                SourceName = "default",
                IsActive = true,
            },
            Identifier = new TableIdentifier("default", Schema, ParitySeed.SalesTable),
            Columns = columns.Select(c => new TableColumn { TableId = id, ColumnName = c.Name, DataType = c.Type }).ToList(),
        });
    }
}
