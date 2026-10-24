using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Autheris.Tests.Simulation.ApiFuzz;

/// <summary>
/// Runs the built <c>Autheris.Sim.ApiHost</c> as a child process (the Simulation project never enables test auth itself; the host is built
/// with <c>-p:AutherisIncludeTestAuth=true</c> on its own command line, SEC-SIM-03). Output is discarded: it can contain seeded values.
/// </summary>
public sealed class SimApiHostProcess : IDisposable
{
    private readonly Process _process;

    private SimApiHostProcess(Process process, string dataDir, int port)
    {
        _process = process;
        DataDir = dataDir;
        Port = port;
    }

    public string DataDir { get; }

    public int Port { get; }

    public Uri BaseUri => new($"http://127.0.0.1:{Port}");

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
        }
    }

    public static string HostDll => Path.Combine(RepoRoot, "tests", "Autheris.Sim.ApiHost", "bin", "Release", "net10.0", "Autheris.Sim.ApiHost.dll");

    public static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static ProcessStartInfo StartInfo(IEnumerable<string> hostArgs)
    {
        if (!File.Exists(HostDll))
        {
            throw new FileNotFoundException("Build the host first: dotnet build tests/Autheris.Sim.ApiHost -c Release -p:AutherisIncludeTestAuth=true", HostDll);
        }

        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(HostDll);
        foreach (var arg in hostArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    /// <summary>Starts the host with a fresh data dir under the temp path and waits for the ready file (written after the self-probe).</summary>
    public static async Task<SimApiHostProcess> StartAsync(string profile)
    {
        var root = Path.Combine(Path.GetTempPath(), "sim-apihost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dataDir = Path.Combine(root, "data");
        var ready = Path.Combine(root, "ready");
        var port = FreeLoopbackPort();
        var process = Process.Start(StartInfo(["--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--data-dir", dataDir, "--ready-file", ready, "--profile", profile]))!;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        var host = new SimApiHostProcess(process, dataDir, port);
        for (var i = 0; i < 240 && !File.Exists(ready); i++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The sim host exited early with code {process.ExitCode}.");
            }

            await Task.Delay(500);
        }

        if (!File.Exists(ready))
        {
            host.Dispose();
            throw new TimeoutException("The sim host did not become ready within 120 s.");
        }

        return host;
    }

    /// <summary>Client with the synthetic test-auth headers of the host (no bearer secret exists).</summary>
    public HttpClient CreateClient(string roles = "Reader", bool authenticated = true)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(30) };
        if (authenticated)
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-SIM");
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
            client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_sim");
            client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        }

        return client;
    }

    /// <summary>Sends SIGTERM and waits for the exit; returns the exit code.</summary>
    public async Task<int> StopAsync()
    {
        if (!_process.HasExited)
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", $"-TERM {_process.Id}") { UseShellExecute = false })!;
            await kill.WaitForExitAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _process.WaitForExitAsync(cts.Token);
        }

        return _process.ExitCode;
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(true);
        }

        _process.Dispose();
        try
        {
            Directory.Delete(Path.GetDirectoryName(DataDir)!, true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
