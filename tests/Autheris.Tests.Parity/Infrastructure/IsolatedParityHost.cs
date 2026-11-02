using Autheris.Tests.Parity.Seed;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Autheris.Tests.Parity.Infrastructure;

/// <summary>
/// An ephemeral in-process host created for tests that perform state mutations (such as GraphQL mutations).
/// Runs against its own isolated SQLite seed files and governance database so it never disrupts the shared <see cref="ParityHostFixture"/>.
/// </summary>
public sealed class IsolatedParityHost : IAsyncDisposable, IParityHost
{
    private readonly string _workDir;
    private readonly string _dataPath;
    private readonly string _secondPath;
    private readonly string _govPath;
    private WebApplicationFactory<Program>? _factory;

    private IsolatedParityHost(string workDir, string dataPath, string secondPath, string govPath)
    {
        _workDir = workDir;
        _dataPath = dataPath;
        _secondPath = secondPath;
        _govPath = govPath;
    }

    public static async Task<IsolatedParityHost> CreateAsync(bool enableSubscriptions = false)
    {
        ParityRunGuard.EnsureEnabled();
        var workDir = Path.Combine(Path.GetTempPath(), "autheris-parity-iso-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var dataPath = Path.Combine(workDir, "data.db");
        var secondPath = Path.Combine(workDir, "second.db");
        var govPath = Path.Combine(workDir, "gov.db");
        ParitySeed.Create(dataPath);
        ParitySeed.Create(secondPath, ParitySeed.SecondSales);

        var host = new IsolatedParityHost(workDir, dataPath, secondPath, govPath);
        using (var seeding = host.NewHost(enableSubscriptions))
        {
            await ParityHostFixture.RegisterCatalogAsync(seeding);
        }

        host._factory = host.NewHost(enableSubscriptions);
        _ = host._factory.Server;
        return host;
    }

    public HttpClient CreateClient(string roles = "ClusterAdmin", string tenant = "tenant_parity") =>
        ParityHostSettings.CreateClient(_factory ?? throw new InvalidOperationException("Host not started."), roles, tenant);

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Host not started.");

    HttpClient IParityHost.CreateClient() => CreateClient();

    private WebApplicationFactory<Program> NewHost(bool enableSubscriptions) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        ParityHostSettings.Apply(b, _govPath);
        b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
        b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={_dataPath}");
        b.UseSetting("Gateway:DataSources:Connections:second:Provider", "Sqlite");
        b.UseSetting("Gateway:DataSources:Connections:second:ConnectionString", $"Data Source={_secondPath}");
        b.UseSetting("Gateway:WebSql:Enabled", "true");
        b.UseSetting("Gateway:WebSql:AllowDml", "false");
        b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");
        b.UseSetting("Gateway:WebSql:CrossSource:Enabled", "true");
        b.UseSetting("Gateway:WebSql:AllowedDataSources:0", "default");
        b.UseSetting("Gateway:WebSql:AllowedDataSources:1", "second");
        if (enableSubscriptions)
        {
            b.UseSetting("Gateway:GraphQL:Subscriptions:Enabled", "true");
        }
    });

    public async ValueTask DisposeAsync()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, true);
            }
        }
        catch (IOException)
        {
            // best effort cleanup
        }

        await Task.CompletedTask;
    }
}
