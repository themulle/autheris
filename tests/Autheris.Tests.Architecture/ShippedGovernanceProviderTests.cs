using System.Text.Json;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>SEC-SIMPL-10/11: the shipped configuration never defaults the governance store to SQLite and ships no connection string.</summary>
[Trait("Category", "Security")]
public sealed class ShippedGovernanceProviderTests
{
    [Theory]
    [InlineData("src/Autheris.Api/appsettings.json")]
    [InlineData("src/Autheris.Api/appsettings.Development.json")]
    public void Default_governance_provider_is_PostgreSql(string relativePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryScan.RepoRoot, relativePath)));
        var governanceDb = doc.RootElement.GetProperty("Gateway").GetProperty("GovernanceDb");

        governanceDb.GetProperty("Provider").GetString().ShouldBe("PostgreSql");
        governanceDb.TryGetProperty("ConnectionString", out var connectionString)
            .ShouldBeTrue("the shipped config must carry an empty ConnectionString so startup fails closed until it is supplied");
        connectionString.GetString().ShouldBeNullOrEmpty();
    }

    [Fact]
    public void Default_governance_provider_is_PostgreSql_in_helm_values()
    {
        var values = File.ReadAllLines(Path.Combine(RepositoryScan.RepoRoot, "deploy/helm/autheris/values.yaml"));
        var index = Array.FindIndex(values, l => l.TrimStart().StartsWith("governanceDb:", StringComparison.Ordinal));
        index.ShouldBeGreaterThanOrEqualTo(0);

        var provider = values.Skip(index + 1).Take(8).FirstOrDefault(l => l.TrimStart().StartsWith("provider:", StringComparison.Ordinal));
        provider.ShouldNotBeNull();
        provider.Split(':', 2)[1].Trim().Trim('"').ShouldBe("PostgreSql");
    }

    [Fact]
    public void Default_governance_provider_is_PostgreSql_in_podman_compose()
    {
        var compose = File.ReadAllLines(Path.Combine(RepositoryScan.RepoRoot, "deploy/podman-compose.yaml"));
        var provider = compose.FirstOrDefault(l => l.TrimStart().StartsWith("Gateway__GovernanceDb__Provider:", StringComparison.Ordinal));
        provider.ShouldNotBeNull("the compose gateway must set the governance provider explicitly");
        provider.Split(':', 2)[1].Trim().Trim('"').ShouldBe("PostgreSql");

        compose.Any(l => l.TrimStart().StartsWith("postgres:", StringComparison.Ordinal)).ShouldBeTrue();
        compose.Any(l => l.Contains("governance-seed", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }
}
