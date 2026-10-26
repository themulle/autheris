using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Autheris.Sim.ApiHost;

/// <summary>
/// Standalone loopback host for the Schemathesis job (REQ-SIMTEST P2). Entry class is not named <c>Program</c> to avoid a clash with
/// <c>Autheris.Api</c>'s <c>Program</c>. Arguments: <c>--port N --data-dir D --ready-file F --profile read|write</c>.
/// Binds <c>127.0.0.1</c> only, refuses non-loopback configuration, writes the ready file after a self-probe and wipes the data dir on SIGTERM.
/// </summary>
public static class SimApiHostMain
{
    public static async Task<int> Main(string[] args)
    {
        // REQ-LB-04: diagnostics go to stderr only (the script prints the host log through its canary scrub, SEC-LB-03).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"sim-api-host: unhandled exception (terminating={e.IsTerminating}): {(e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.GetType().Name}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Console.Error.WriteLine($"sim-api-host: unobserved task exception: {SimHostShutdown.Describe(e.Exception)}");

        SimApiHostOptions options;
        try
        {
            options = SimApiHostOptions.Parse(args, Environment.GetEnvironmentVariable);
        }
        catch (SimApiHostConfigurationException ex)
        {
            Console.Error.WriteLine($"sim-api-host: refusing to start: {ex.Message}");
            return 2;
        }

#if !AUTHERIS_TEST_AUTH
        Console.Error.WriteLine("sim-api-host: built without -p:AutherisIncludeTestAuth=true; there is no test-auth handler, refusing to start.");
        return 2;
#else
        try
        {
            return await RunAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // REQ-LB-03 / SEC-LB-05: the catch-all returns 1, never 0.
            Console.Error.WriteLine($"sim-api-host: startup failed: {SimHostShutdown.Describe(ex)}");
            return 1;
        }
#endif
    }

#if AUTHERIS_TEST_AUTH
    private static async Task<int> RunAsync(SimApiHostOptions options)
    {
        if (Directory.Exists(options.DataDir) && Directory.EnumerateFileSystemEntries(options.DataDir).Any())
        {
            Console.Error.WriteLine("sim-api-host: --data-dir must be new or empty (it is wiped on shutdown).");
            return 2;
        }

        var dataDir = Path.GetFullPath(options.DataDir);
        if (dataDir.Length < 8 || Path.GetPathRoot(dataDir) == dataDir)
        {
            Console.Error.WriteLine("sim-api-host: --data-dir is too close to the file system root.");
            return 2;
        }

        Directory.CreateDirectory(dataDir);
        var dataPath = Path.Combine(dataDir, "data.db");
        var govPath = Path.Combine(dataDir, "gov.db");
        var shutdown = new SimHostShutdown();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Request(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; shutdown.Request(); });

        // SEC-LB-02: from here on the data dir is wiped on every exit path (codes 0, 1, 3, 4, 5).
        var cleaned = false;
        void Cleanup()
        {
            cleaned = true;
            try
            {
                SqliteConnection.ClearAllPools();
            }
            finally
            {
                WipeDataDir(dataDir);
            }
        }

        WebApplicationFactory<Program>? factory = null;
        try
        {
            var startCode = 0;
            try
            {
                SimApiSeed.Create(dataPath);

                using (var seeding = NewHost(options, dataPath, govPath))
                {
                    await RegisterCatalogAsync(seeding).ConfigureAwait(false);
                }

                factory = NewHost(options, dataPath, govPath);
                var port = options.Port;
                factory.UseKestrel(k => k.Listen(IPAddress.Loopback, port));
                factory.StartServer();

                // SEC-SIM-03 / SEC-LB-01: assert that the loopback bind holds before the ready file is written.
                var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
                if (addresses.Count == 0 || addresses.Any(a => !SimApiHostOptions.IsLoopbackUrl(a)))
                {
                    Console.Error.WriteLine("sim-api-host: bound addresses are not provably loopback; refusing to continue.");
                    startCode = 3;
                }
                else
                {
                    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
                    AddTestAuth(http.DefaultRequestHeaders, "Reader");
                    using var probe = await http.GetAsync(new Uri("/odata/v4/$metadata", UriKind.Relative)).ConfigureAwait(false);
                    if (probe.StatusCode != HttpStatusCode.OK)
                    {
                        Console.Error.WriteLine($"sim-api-host: self-probe failed with status {(int)probe.StatusCode}.");
                        startCode = 3;
                    }
                    else
                    {
                        SimApiSeed.WriteCanaries(Path.Combine(dataDir, "canaries.txt"));
                        if (!string.IsNullOrEmpty(options.ReadyFile))
                        {
                            await File.WriteAllTextAsync(options.ReadyFile, $"127.0.0.1:{port}\n").ConfigureAwait(false);
                        }

                        Console.WriteLine($"sim-api-host: ready on http://127.0.0.1:{port} (profile {options.Profile.ToString().ToLowerInvariant()})");
                        // A SIGTERM during startup is only recorded; it is honoured here once the host is ready. The whole startup is bounded by stop_host's 30 s SIGKILL.
                        await shutdown.Requested.ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                // REQ-LB-03: nothing escapes Main; a real startup crash fails the job with code 1.
                Console.Error.WriteLine($"sim-api-host: startup failed: {SimHostShutdown.Describe(ex)}");
                startCode = 1;
            }

            if (factory is null)
            {
                return startCode == 0 ? 1 : startCode;
            }

            if (startCode != 0)
            {
                try
                {
                    // SEC-LB-06: bounded. A dispose timeout maps to the documented shutdown timeout code, any other failure to code 4.
                    await DisposeFallbackAsync(factory).WaitAsync(DisposeTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    SimHostShutdown.SafeWrite(Console.Error, "sim-api-host: shutdown timed out while disposing after a startup failure");
                    return SimHostShutdown.ExitShutdownTimeout;
                }
                catch (Exception ex)
                {
                    SimHostShutdown.SafeWrite(Console.Error, $"sim-api-host: shutdown failed: {SimHostShutdown.Describe(ex)}");
                    return SimHostShutdown.ExitShutdownFailed;
                }

                return startCode;
            }

            // Normal path: this is the only component that starts the stop (REQ-LB-01); the entry-point thread disposes the host.
            var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStopped.Register(() => stopped.TrySetResult());
            lifetime.StopApplication();
            var host = factory;
            return await shutdown.RunAsync(
                ct => stopped.Task.WaitAsync(ct),
                () => IsDisposed(host),
                () => DisposeFallbackAsync(host),
                Cleanup,
                Console.Error,
                StopTimeout,
                DisposeTimeout).ConfigureAwait(false);
        }
        finally
        {
            if (!cleaned)
            {
                try
                {
                    Cleanup();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"sim-api-host: cleanup failed: {SimHostShutdown.Describe(ex)}");
                }
            }
        }
    }

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private static bool IsDisposed(WebApplicationFactory<Program> factory)
    {
        try
        {
            _ = factory.Services.GetService<IHostApplicationLifetime>();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static async Task DisposeFallbackAsync(WebApplicationFactory<Program> factory) => await factory.DisposeAsync().ConfigureAwait(false);

    /// <summary>Synthetic credentials of the sim host. They are valid only for this test-auth build and carry no secret (SEC-SIM-04).</summary>
    internal static void AddTestAuth(HttpRequestHeaders headers, string roles)
    {
        headers.Add("X-Test-User-Sid", "S-1-5-SIM");
        headers.Add("X-Test-Roles", roles);
        headers.Add("X-Test-Tenant", SimApiSeed.OwnTenant);
        headers.Add("GraphQL-Preflight", "1");
    }

    private static void WipeDataDir(string dataDir)
    {
        try
        {
            Directory.Delete(dataDir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; the data dir is under the runner temp dir and is removed with the job
        }
    }

    private static WebApplicationFactory<Program> NewHost(SimApiHostOptions options, string dataPath, string govPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // The solution-relative content root discovery of WebApplicationFactory finds no solution (this project is in none, SEC-SIM-21).
            b.UseContentRoot(AppContext.BaseDirectory);
            // REQ-LB-01/02: no ConsoleLifetime (SimApiHostMain owns the signals) and a bounded host stop. WithWebHostBuilder services win over Program's.
            b.ConfigureServices(s => s.AddSingleton<IHostLifetime, SimHostLifetime>());
            b.UseShutdownTimeout(TimeSpan.FromSeconds(10));
            ApplySettings(b, govPath);
            b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={dataPath}");
            b.UseSetting("Gateway:WebSql:Enabled", "true");
            b.UseSetting("Gateway:WebSql:AllowDml", options.Profile == SimApiProfile.Write ? "true" : "false");
            if (options.Profile == SimApiProfile.Write)
            {
                // SEC M-20: DML needs explicitly authorized roles; only the throwaway write-phase host allows it.
                b.UseSetting("Gateway:WebSql:DmlWriterRoles:0", "GovernanceAdmin");
            }

            b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");
            b.UseSetting("Gateway:WebSql:AllowedDataSources:0", "default");
        });

    private static void ApplySettings(IWebHostBuilder b, string governancePath)
    {
        b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
        b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "100000");
        b.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokenBucketCapacity", "100000");
        b.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokensPerSecond", "10000");
        b.UseSetting("Gateway:GraphQL:MaxAllowedComplexity", "5000");
        b.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxCostPerQuery", "5000");
        b.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxTokensCapacity", "100000000");
        b.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:TokenRefillRatePerSecond", "1000000");
        b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
        b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source={governancePath}");
        b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
        b.UseSetting("Gateway:Rebac:Enabled", "false");
        b.UseSetting("Gateway:OpenSchema", "true");
        b.UseSetting("Gateway:Tenancy:Mode", "Multi");
    }

    private static async Task RegisterCatalogAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var id = Guid.NewGuid();
        var metadata = new TableMetadata
        {
            Table = new Table
            {
                Id = id,
                DisplayName = SimApiSeed.TableName,
                TableName = SimApiSeed.TableName,
                SchemaName = "main",
                SourceType = "Sqlite",
                SourceName = "default",
                IsActive = true,
            },
            Identifier = new TableIdentifier("default", "main", SimApiSeed.TableName),
            Columns =
            [
                new TableColumn { TableId = id, ColumnName = "id", DataType = "integer" },
                new TableColumn { TableId = id, ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { TableId = id, ColumnName = "name", DataType = "varchar" },
                new TableColumn { TableId = id, ColumnName = "balance", DataType = "decimal" },
                new TableColumn { TableId = id, ColumnName = "opened_on", DataType = "date" },
                new TableColumn { TableId = id, ColumnName = "secret_note", DataType = "varchar" },
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["secret_note"] = new MaskingRule { RuleType = "NULLIFY" } },
        };
        await repo.UpsertTableMetadataAsync(metadata).ConfigureAwait(false);

        var governance = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var table = new TableIdentifier("default", "main", SimApiSeed.TableName);
        await governance.ReplaceTableKeyDefinitionAsync(table, ["id"], []).ConfigureAwait(false);
        await governance.SetDatasetVisibilityAsync(table, DatasetVisibility.Full, new Sid("sim-seed")).ConfigureAwait(false);
    }
#endif
}
