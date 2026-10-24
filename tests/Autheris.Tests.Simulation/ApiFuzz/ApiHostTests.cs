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
}
