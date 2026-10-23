using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>Robin-100 binding security conditions that can be checked on the test sources.</summary>
public sealed class Robin100SecurityTests
{
    /// <summary>SEC-R100-07: the raw JSON helper uses the same authenticated host client as every other helper.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public void Robin100_PostJsonAsyncUsesAuthenticatedHostClient()
    {
        var root = ParityIsolationArchitectureTests.FindRepoRoot();
        var path = Path.Combine(root, "tests", "Autheris.Tests.Parity", "Robin", "RobinClient.cs");
        File.Exists(path).ShouldBeTrue("RobinClient.cs not found: the guard fails closed.");
        var source = File.ReadAllText(path);

        // WP-V2b / SEC-R100-12: exactly one marked region may build the anonymous client; it is guarded by Robin100V2bSecurityTests.
        const string regionStart = "// --- WP-V2b unauthenticated ---";
        const string regionEnd = "// --- end WP-V2b unauthenticated ---";
        Regex.Count(source, Regex.Escape(regionStart)).ShouldBe(1, "exactly one unauthenticated region is allowed.");
        var regionBegin = source.IndexOf(regionStart, StringComparison.Ordinal);
        var regionFinish = source.IndexOf(regionEnd, regionBegin, StringComparison.Ordinal);
        regionFinish.ShouldBeGreaterThan(regionBegin);
        source = source[..regionBegin] + source[(regionFinish + regionEnd.Length)..];

        var start = source.IndexOf("Task<RobinHttpResponse> PostJsonAsync(", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, "PostJsonAsync not found: the guard fails closed.");
        var end = source.IndexOf("// --- end WP-R2 ---", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        var body = source[start..end];

        body.ShouldContain("host.CreateClient()");
        Regex.Count(body, @"CreateClient\(").ShouldBe(1);

        foreach (var forbidden in new[] { "new HttpClient", "HttpClientHandler", "Authorization", "Bearer", "DefaultRequestHeaders", "Headers.Remove", "Headers.Clear" })
        {
            source.Contains(forbidden, StringComparison.Ordinal).ShouldBeFalse($"RobinClient.cs must not use '{forbidden}' (SEC-R100-07).");
        }

        // Every HTTP client in the helper class comes from the one host factory.
        Regex.Count(source, @"\bhost\.CreateClient\(\)").ShouldBe(Regex.Count(source, @"CreateClient\("));
    }
}
