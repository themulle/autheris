using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Seed;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Cryptography;
using Testcontainers.MsSql;

namespace Autheris.Tests.Parity.Dialects;

[CollectionDefinition(Name)]
public sealed class ParitySqlServerCollection : ICollectionFixture<ParitySqlServerHostFixture>
{
    public const string Name = "ParitySqlServerHost";
}

/// <summary>
/// Container dialect host (REQ-LB-10): a SQL Server 2022 Testcontainers instance seeded with the same rows as the SQLite file (SQL Server
/// DDL; <c>sold_at</c> is plain UTC wall time in <c>DATETIME2</c>), registered as source <c>default</c> in schema <c>dbo</c>. Used only by
/// the <c>parity-dialects</c> job (trait <c>Dialect=Container</c>); it is never run locally. SEC-LB-12: generated per-run password, default
/// loopback mapping, disposed here, connection string never logged. SEC-LB-13: seed values go through <see cref="SqlParameter"/> only.
/// </summary>
public sealed class ParitySqlServerHostFixture : IAsyncLifetime, IParityHost
{
    public const string Schema = "dbo";

    private MsSqlContainer? _container;
    private string _workDir = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        ParityRunGuard.EnsureEnabled();
        _workDir = Path.Combine(Path.GetTempPath(), "autheris-parity-mssql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").WithPassword(NewPassword()).Build();
        try
        {
            await _container.StartAsync();
            await SeedAsync(_container.GetConnectionString());

            using (var seeding = NewHost(_container.GetConnectionString()))
            {
                await RegisterCatalogAsync(seeding);
            }

            _factory = NewHost(_container.GetConnectionString());
            _ = _factory.Server;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        SqlConnection.ClearAllPools();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        try
        {
            Directory.Delete(_workDir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    public HttpClient CreateClient() =>
        ParityHostSettings.CreateClient(_factory ?? throw new InvalidOperationException("Host not started."));

    private WebApplicationFactory<Program> NewHost(string connectionString) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        ParityHostSettings.Apply(b, Path.Combine(_workDir, "gov.db"));
        b.UseSetting("Gateway:DataSources:Connections:default:Provider", "SqlServer");
        b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", connectionString);
        b.UseSetting("Gateway:DataSources:Connections:default:DefaultSchema", Schema);
        b.UseSetting("Gateway:WebSql:Enabled", "true");
        b.UseSetting("Gateway:WebSql:AllowDml", "false");
        b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");
    });

    /// <summary>Random per-run SA password that satisfies the SQL Server complexity rule (SEC-LB-12); never a reused literal.</summary>
    private static string NewPassword() => "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static async Task SeedAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var create = new SqlCommand(
            "CREATE TABLE dbo.parity_sales (id INT PRIMARY KEY, region_id INT NULL, region NVARCHAR(100) NULL, product NVARCHAR(100) NOT NULL, qty INT NULL, " +
            "amount DECIMAL(12,2) NULL, sale_date DATE NOT NULL, sold_at DATETIME2 NOT NULL, is_return BIT NULL, note NVARCHAR(200) NULL);", conn))
        {
            await create.ExecuteNonQueryAsync();
        }

        foreach (var s in ParitySeed.Sales)
        {
            await using var cmd = new SqlCommand(
                "INSERT INTO dbo.parity_sales (id, region_id, region, product, qty, amount, sale_date, sold_at, is_return, note) " +
                "VALUES (@id, @region_id, @region, @product, @qty, @amount, @sale_date, @sold_at, @is_return, @note)", conn);
            cmd.Parameters.Add("@id", SqlDbType.Int).Value = s.Id;
            cmd.Parameters.Add("@region_id", SqlDbType.Int).Value = (object?)s.RegionId ?? DBNull.Value;
            cmd.Parameters.Add("@region", SqlDbType.NVarChar, 100).Value = (object?)s.Region ?? DBNull.Value;
            cmd.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = s.Product;
            cmd.Parameters.Add("@qty", SqlDbType.Int).Value = (object?)s.Qty ?? DBNull.Value;
            var amount = cmd.Parameters.Add("@amount", SqlDbType.Decimal);
            amount.Precision = 12;
            amount.Scale = 2;
            amount.Value = (object?)s.Amount ?? DBNull.Value;
            cmd.Parameters.Add("@sale_date", SqlDbType.Date).Value = DateTime.ParseExact(s.SaleDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            cmd.Parameters.Add("@sold_at", SqlDbType.DateTime2).Value = DateTime.SpecifyKind(DateTime.Parse(s.SoldAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal), DateTimeKind.Unspecified);
            cmd.Parameters.Add("@is_return", SqlDbType.Bit).Value = (object?)s.IsReturn ?? DBNull.Value;
            cmd.Parameters.Add("@note", SqlDbType.NVarChar, 200).Value = (object?)s.Note ?? DBNull.Value;
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
            ("amount", "decimal"), ("sale_date", "date"), ("sold_at", "datetime2"), ("is_return", "boolean"), ("note", "varchar"),
        ];
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table
            {
                Id = id,
                DisplayName = ParitySeed.SalesTable,
                TableName = ParitySeed.SalesTable,
                SchemaName = Schema,
                SourceType = "SqlServer",
                SourceName = "default",
                IsActive = true,
            },
            Identifier = new TableIdentifier("default", Schema, ParitySeed.SalesTable),
            Columns = columns.Select(c => new TableColumn { TableId = id, ColumnName = c.Name, DataType = c.Type }).ToList(),
        });
    }
}
