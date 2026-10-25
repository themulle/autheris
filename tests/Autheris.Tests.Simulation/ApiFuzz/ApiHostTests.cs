using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Autheris.Tests.Simulation.Infrastructure;
using Shouldly;

namespace Autheris.Tests.Simulation.ApiFuzz;

/// <summary>
/// Guarded tests of <c>Autheris.Sim.ApiHost</c> (run only with <c>AUTHERIS_SIM_RUN=1</c>; the host must be built with
/// <c>dotnet build tests/Autheris.Sim.ApiHost -c Release -p:AutherisIncludeTestAuth=true</c>). Each test starts the host as a child process.
/// </summary>
public sealed class ApiHostTests : SimulationTestBase
{
    private static readonly string[] Canaries = ["SIMCANARY-MASK-7f3a91", "SIMCANARY-TENANT-9b1c42"];

    [Fact]
    public async Task ApiHost_StartsAndServesSchemasWithTestAuth()
    {
        using var host = await SimApiHostProcess.StartAsync("read");
        using var client = host.CreateClient();

        (await client.GetAsync(new Uri("/odata/v4/$metadata", UriKind.Relative))).IsSuccessStatusCode.ShouldBeTrue();

        using var odata = await client.GetAsync(new Uri("/odata/v4/$openapi", UriKind.Relative));
        odata.IsSuccessStatusCode.ShouldBeTrue();
        using (var doc = JsonDocument.Parse(await odata.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("paths").EnumerateObject().Any(p => p.Name.Contains("sim_accounts", StringComparison.Ordinal)).ShouldBeTrue();
        }

        (await client.GetAsync(new Uri("/api/v1/sql/openapi.json", UriKind.Relative))).IsSuccessStatusCode.ShouldBeTrue();

        using var introspection = await client.PostAsync(new Uri("/graphql", UriKind.Relative),
            new StringContent("""{"query":"{ __schema { queryType { name } } }"}""", Encoding.UTF8, "application/json"));
        introspection.IsSuccessStatusCode.ShouldBeTrue();

        // the credentials are the synthetic test-auth headers only: without them the same route is refused
        using var anonymous = host.CreateClient(authenticated: false);
        using var refused = await anonymous.GetAsync(new Uri("/odata/v4/default/main/sim_accounts", UriKind.Relative));
        ((int)refused.StatusCode).ShouldBeOneOf(401, 403);

        // SIGTERM wipes the data dir (SEC-SIM-23)
        File.Exists(Path.Combine(host.DataDir, "canaries.txt")).ShouldBeTrue();
        (await host.StopAsync()).ShouldBe(0);
        Directory.Exists(host.DataDir).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-14, SEC-SIM-04
    public async Task ApiHost_CanariesNeverInResponsesToReaderProfile()
    {
        using var host = await SimApiHostProcess.StartAsync("read");
        using var client = host.CreateClient();
        var canaryFile = await File.ReadAllLinesAsync(Path.Combine(host.DataDir, "canaries.txt"));
        canaryFile.ShouldBe(Canaries, ignoreOrder: true);

        var bodies = new List<string>();
        foreach (var path in new[] { "/odata/v4/default/main/sim_accounts", "/odata/v4/default/main/sim_accounts?$select=secret_note,name", "/odata/v4/default/main/sim_accounts?$filter=tenant_id eq 'tenant_forbidden'" })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            bodies.Add(await response.Content.ReadAsStringAsync() + string.Join('\n', response.Headers.Select(h => string.Join(',', h.Value))));
        }

        using var sql = await client.PostAsync(new Uri("/api/v1/sql", UriKind.Relative),
            new StringContent("""{"sql":"SELECT * FROM main.sim_accounts"}""", Encoding.UTF8, "application/json"));
        bodies.Add(await sql.Content.ReadAsStringAsync());

        using var gql = await client.PostAsync(new Uri("/graphql", UriKind.Relative),
            new StringContent("""{"query":"{ __schema { types { name } } }"}""", Encoding.UTF8, "application/json"));
        bodies.Add(await gql.Content.ReadAsStringAsync());

        bodies.Count(b => b.Contains("Account 1", StringComparison.Ordinal)).ShouldBeGreaterThan(0, "no request reached the seeded table, the canary check would be vacuous");
        foreach (var body in bodies)
        {
            foreach (var canary in Canaries)
            {
                body.ShouldNotContain(canary);
            }

            body.ShouldNotContain("tenant_forbidden");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-01
    public async Task ApiHost_RefusesNonLoopbackUrlEnvironmentAtProcessLevel()
    {
        foreach (var url in new[] { "http://0.0.0.0:5071", "http://+:5071", "http://[::]:5071", "http://127.0.0.1.evil.com:5071" })
        {
            var psi = SimApiHostProcess.StartInfo(["--port", "5071", "--data-dir", Path.Combine(Path.GetTempPath(), "sim-refused-" + Guid.NewGuid().ToString("N"))]);
            psi.Environment["ASPNETCORE_URLS"] = url;
            using var process = Process.Start(psi)!;
            _ = process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.ShouldBe(2, url);
            stderr.ShouldContain("refusing to start");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-02, SEC-LB-04, SEC-LB-05
    public async Task ApiHost_SigtermUnderConcurrentTraffic_ExitsZero_Repeated()
    {
        string[] paths = ["/odata/v4/default/main/sim_accounts", "/odata/v4/$metadata", "/api/v1/sql/openapi.json"];
        for (var cycle = 0; cycle < 10; cycle++)
        {
            using var host = await StartCapturingAsync();
            using var client = host.Client();
            using var stopTraffic = new CancellationTokenSource();
            var loops = Enumerable.Range(0, 8).Select(n => Task.Run(async () =>
            {
                while (!stopTraffic.IsCancellationRequested)
                {
                    try
                    {
                        using var response = n % 3 == 1
                            ? await client.PostAsync(new Uri("/graphql", UriKind.Relative), new StringContent("""{"query":"{ __schema { queryType { name } } }"}""", Encoding.UTF8, "application/json"), stopTraffic.Token)
                            : n % 3 == 2
                                ? await client.PostAsync(new Uri("/api/v1/sql", UriKind.Relative), new StringContent("""{"sql":"SELECT * FROM main.sim_accounts"}""", Encoding.UTF8, "application/json"), stopTraffic.Token)
                                : await client.GetAsync(new Uri(paths[n % paths.Length], UriKind.Relative), stopTraffic.Token);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
                    {
                        // the host is going away
                    }
                }
            })).ToArray();

            await Task.Delay(300);
            var code = await host.TermAsync(second: false);
            await stopTraffic.CancelAsync();
            await Task.WhenAll(loops);

            code.ShouldBe(0, $"cycle {cycle}");
            Directory.Exists(host.DataDir).ShouldBeFalse();
            host.Stderr.ShouldNotContain("unhandled", Case.Insensitive);
            host.Stderr.ShouldNotContain("shutdown failed", Case.Insensitive);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-04, SEC-LB-06
    public async Task ApiHost_SecondSigterm_DuringShutdown_ExitsZero()
    {
        using var host = await StartCapturingAsync();

        var code = await host.TermAsync(second: true);

        code.ShouldBe(0);
        Directory.Exists(host.DataDir).ShouldBeFalse();
        host.Stderr.ShouldNotContain("unhandled", Case.Insensitive);
        host.Stderr.ShouldNotContain("shutdown failed", Case.Insensitive);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-LB-02, SEC-LB-05
    public async Task ApiHost_StartupFailure_PortInUse_ExitsOne_WipesDataDir()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var dataDir = Path.Combine(Path.GetTempPath(), "sim-startfail-" + Guid.NewGuid().ToString("N"), "data");
        try
        {
            using var process = Process.Start(SimApiHostProcess.StartInfo(["--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--data-dir", dataDir, "--profile", "read"]))!;
            _ = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await process.WaitForExitAsync(cts.Token);

            process.ExitCode.ShouldBe(1);
            (await stderr).ShouldContain("startup failed");
            Directory.Exists(dataDir).ShouldBeFalse();
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(dataDir)!, true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private static async Task<CapturedHost> StartCapturingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "sim-apihost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ready = Path.Combine(root, "ready");
        var port = SimApiHostProcess.FreeLoopbackPort();
        var dataDir = Path.Combine(root, "data");
        var process = Process.Start(SimApiHostProcess.StartInfo(["--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--data-dir", dataDir, "--ready-file", ready, "--profile", "read"]))!;
        _ = process.StandardOutput.ReadToEndAsync();
        var host = new CapturedHost(process, root, dataDir, port, process.StandardError.ReadToEndAsync());
        for (var i = 0; i < 240 && !File.Exists(ready); i++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The sim host exited early with code {process.ExitCode}.");
            }

            await Task.Delay(500);
        }

        File.Exists(ready).ShouldBeTrue("the sim host did not become ready within 120 s");
        return host;
    }

    private sealed class CapturedHost(Process process, string root, string dataDir, int port, Task<string> stderr) : IDisposable
    {
        public string DataDir { get; } = dataDir;

        public string Stderr => stderr.IsCompleted ? stderr.Result : string.Empty;

        public HttpClient Client()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-SIM");
            client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
            client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_sim");
            client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
            return client;
        }

        public async Task<int> TermAsync(bool second)
        {
            await Kill();
            if (second)
            {
                await Task.Delay(50);
                if (!process.HasExited)
                {
                    await Kill();
                }
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(cts.Token);
            await stderr;
            return process.ExitCode;
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            process.Dispose();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        private async Task Kill()
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", $"-TERM {process.Id}") { UseShellExecute = false })!;
            await kill.WaitForExitAsync();
        }
    }
}
