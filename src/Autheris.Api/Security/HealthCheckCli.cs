using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace Autheris.Api.Security;

/// <summary>
/// Lightweight CLI healthcheck tool for container HEALTHCHECK commands in distroless/minimal containers without curl/wget.
/// </summary>
public static class HealthCheckCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var targetUrl = "http://127.0.0.1:8080/health/live";

        // Check if custom URL was provided in arguments or environment
        if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
        {
            targetUrl = args[1];
        }
        else
        {
            var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
            var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
            if (!string.IsNullOrWhiteSpace(ports) && int.TryParse(ports.Split(';')[0], out var port))
            {
                targetUrl = $"http://127.0.0.1:{port}/health/live";
            }
            else if (!string.IsNullOrWhiteSpace(urls))
            {
                var firstUrl = urls.Split(';')[0].Replace("+", "127.0.0.1").Replace("*", "127.0.0.1").TrimEnd('/');
                targetUrl = $"{firstUrl}/health/live";
            }
        }

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var client = new HttpClient();
            var response = await client.GetAsync(targetUrl, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                return 0;
            }

            Console.Error.WriteLine($"[HealthCheck] Failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HealthCheck] Connection error to {targetUrl}: {ex.Message}");
            return 1;
        }
    }
}
