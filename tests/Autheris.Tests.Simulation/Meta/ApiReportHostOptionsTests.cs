using Autheris.Sim.ApiHost;
using Shouldly;

namespace Autheris.Tests.Simulation.Meta;

/// <summary>Tests of the pure option parser of <c>Autheris.Sim.ApiHost</c> (compiled into this project as a linked file; the host itself is not referenced).</summary>
public sealed class ApiReportHostOptionsTests
{
    private static SimApiHostOptions Parse(string[] args, Dictionary<string, string>? env = null) =>
        SimApiHostOptions.Parse(args, name => env is not null && env.TryGetValue(name, out var v) ? v : null);

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-01
    public void ApiHost_BindsLoopbackOnlyAndRefusesNonLoopbackUrls()
    {
        var ok = Parse(["--data-dir", "/tmp/sim-data-x/data"]);
        ok.Port.ShouldBe(5071);
        ok.Profile.ShouldBe(SimApiProfile.Read);
        Parse(["--data-dir", "/tmp/sim-data-x/data", "--bind", "127.0.0.1", "--port", "5072", "--profile", "write", "--ready-file", "/tmp/r"]).Profile.ShouldBe(SimApiProfile.Write);
        Parse(["--data-dir", "/tmp/d", "--bind", "localhost"]).Port.ShouldBe(5071);
        Parse(["--data-dir", "/tmp/d"], new() { ["ASPNETCORE_URLS"] = "http://127.0.0.1:5071;http://localhost:5072" }).Port.ShouldBe(5071);

        foreach (var bind in new[] { "0.0.0.0", "+", "*", "::", "[::]", "127.0.0.1.evil.com", "127.0.0.2", "evil.com", string.Empty })
        {
            Should.Throw<SimApiHostConfigurationException>(() => Parse(["--data-dir", "/tmp/d", "--bind", bind]), $"--bind {bind}");
        }

        foreach (var url in new[]
                 {
                     "http://0.0.0.0:5071", "http://+:5071", "http://*:5071", "http://[::]:5071", "http://127.0.0.1.evil.com:5071",
                     "http://user@127.0.0.1:5071", "http://127.0.0.1:5071;http://0.0.0.0:80", "http://evil.example",
                 })
        {
            foreach (var variable in new[] { "ASPNETCORE_URLS", "DOTNET_URLS", "Urls" })
            {
                Should.Throw<SimApiHostConfigurationException>(() => Parse(["--data-dir", "/tmp/d"], new() { [variable] = url }), $"{variable}={url}");
            }
        }

        foreach (var variable in new[] { "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTP_PORTS" })
        {
            Should.Throw<SimApiHostConfigurationException>(() => Parse(["--data-dir", "/tmp/d"], new() { [variable] = "5071" }), variable);
        }

        foreach (var port in new[] { "80", "1023", "65536", "70000", "abc", "5071x", "+5071", "-5071", "5 071", string.Empty, "0x1F90", "050710" })
        {
            Should.Throw<SimApiHostConfigurationException>(() => Parse(["--data-dir", "/tmp/d", "--port", port]), $"--port {port}");
        }

        Should.Throw<SimApiHostConfigurationException>(() => Parse([]), "--data-dir is required");
        Should.Throw<SimApiHostConfigurationException>(() => Parse(["--data-dir", "/tmp/d", "--unknown", "x"]));
        Should.Throw<SimApiHostConfigurationException>(() => Parse(["--data-dir", "/tmp/d", "--data-dir", "/tmp/e"]));
        Should.Throw<SimApiHostConfigurationException>(() => Parse(["--data-dir", "/tmp/d", "--profile", "admin"]));
        Should.Throw<SimApiHostConfigurationException>(() => Parse(["--data-dir"]));
    }
}
