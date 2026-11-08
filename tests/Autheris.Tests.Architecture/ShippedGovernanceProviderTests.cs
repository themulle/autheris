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
}
