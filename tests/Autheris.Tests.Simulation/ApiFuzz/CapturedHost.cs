using System.Diagnostics;

namespace Autheris.Tests.Simulation.ApiFuzz;

/// <summary>A sim host child process started by <see cref="ApiHostTests"/> with its stderr captured, for the shutdown exit-code tests (SEC-LB-02..06).</summary>
internal sealed class CapturedHost(Process process, string root, string dataDir, int port, Task<string> stderr) : IDisposable
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
