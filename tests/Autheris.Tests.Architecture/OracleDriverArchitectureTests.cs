using System.Reflection;
using NetArchTest.Rules;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// CR-ADG-07 / SEC-ADG-03: ODP.NET binds by position unless <c>BindByName</c> is set. The raw driver types may only be used by the
/// connection wrapper and the factory; everything else gets commands that always bind by name.
/// </summary>
public sealed class OracleDriverArchitectureTests
{
    private const string Driver = "Oracle.ManagedDataAccess";
    private const string PersistenceNamespace = "Autheris.Infrastructure.Persistence";

    private static readonly Assembly[] SourceAssemblies =
    [
        typeof(Autheris.Domain.Common.Sid).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
        typeof(Autheris.GraphQL.Subscriptions.CdcSubscriptionGovernor).Assembly,
        typeof(Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator).Assembly,
        typeof(TrinoSqlEngine.FastSqlEngine).Assembly
    ];

    [Fact]
    public void OracleDriver_IsOnlyUsedInsideTheInfrastructurePersistenceNamespace()
    {
        var violations = new List<string>();
        foreach (var assembly in SourceAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .That().DoNotResideInNamespace(PersistenceNamespace)
                .ShouldNot().HaveDependencyOn(Driver)
                .GetResult();
            violations.AddRange(result.FailingTypeNames ?? []);
        }

        violations.ShouldBeEmpty($"Types outside {PersistenceNamespace} reference the Oracle driver: {string.Join(", ", violations)}");
    }

    [Fact]
    public void RawOracleCommand_IsOnlyUsedByTheBindByNameWrapper()
    {
        var result = Types.InAssembly(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly)
            .That().DoNotHaveName("BindByNameOracleCommand")
            .ShouldNot().HaveDependencyOn("Oracle.ManagedDataAccess.Client.OracleCommand")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"A raw OracleCommand bypasses BindByName enforcement: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void RawOracleConnection_IsOnlyUsedByTheFactoryTheWrapperAndTheConnectionPolicy()
    {
        string[] allowed = ["SqlConnectionFactory", "BindByNameOracleConnection", "BindByNameOracleCommand", "OracleConnectionStringPolicy", "OracleSessionInitialization"];
        var result = Types.InAssembly(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly)
            .That().DoNotHaveNameStartingWith("SqlConnectionFactory")
            .And().DoNotHaveNameStartingWith("BindByNameOracle")
            .And().DoNotHaveNameStartingWith("OracleConnectionStringPolicy")
            .And().DoNotHaveNameStartingWith("OracleSessionInitialization")
            .ShouldNot().HaveDependencyOn("Oracle.ManagedDataAccess.Client.OracleConnection")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Only {string.Join(", ", allowed)} may use OracleConnection: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void RawOracleTransaction_IsOnlyUsedByTheWrapper()
    {
        var result = Types.InAssembly(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly)
            .That().DoNotHaveNameStartingWith("BindByNameOracle")
            .ShouldNot().HaveDependencyOn("Oracle.ManagedDataAccess.Client.OracleTransaction")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"A raw OracleTransaction exposes the unwrapped connection (CR-ADG-28): {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    /// <summary>
    /// WP-X0o / CR-ADG-18 / SEC-ADG-24: restore must require signed packages and trust only the pinned nuget.org repository
    /// certificate. The Oracle package carries no author signature (only the nuget.org repository signature), so an Oracle author
    /// signer cannot be pinned; the repository signer is the only trust anchor.
    /// </summary>
    [Fact]
    public void NuGetConfig_RequiresSignatures_TrustsOnlyPinnedRepositoryCertificate()
    {
        var config = LoadNuGetConfig();

        var mode = config.Root!.Element("config")?.Elements("add")
            .FirstOrDefault(e => (string?)e.Attribute("key") == "signatureValidationMode");
        ((string?)mode?.Attribute("value")).ShouldBe("require");

        var repositories = config.Root.Element("trustedSigners")?.Elements("repository").ToList();
        repositories.ShouldNotBeNull();
        repositories.Count.ShouldBe(1);
        var repository = repositories[0];
        ((string?)repository.Attribute("name")).ShouldBe("nuget.org");
        ((string?)repository.Attribute("serviceIndex")).ShouldBe("https://api.nuget.org/v3/index.json");

        var certificates = repository.Elements("certificate").ToList();
        certificates.ShouldNotBeEmpty();
        foreach (var certificate in certificates)
        {
            ((string?)certificate.Attribute("hashAlgorithm")).ShouldBe("SHA256");
            ((string?)certificate.Attribute("allowUntrustedRoot")).ShouldBe("false");
            ((string?)certificate.Attribute("fingerprint")).ShouldNotBeNull().ShouldMatch("^[0-9A-F]{64}$");
        }

        config.Root.Element("trustedSigners")!.Elements("author").ShouldBeEmpty(
            "Oracle.ManagedDataAccess.Core has no author signature; an unverifiable author signer must not be trusted.");
    }

    [Fact]
    public void NuGetConfig_StillMapsEveryPackageToNuGetOrgOnly()
    {
        var config = LoadNuGetConfig();

        config.Root!.Element("packageSources")!.Elements("add").Select(e => (string?)e.Attribute("key")).ShouldBe(["nuget.org"]);
        config.Root.Element("packageSources")!.Elements("clear").ShouldHaveSingleItem();
    }

    private static XDocument LoadNuGetConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NuGet.config")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("NuGet.config not found above the test output directory.");
        return XDocument.Load(Path.Combine(directory.FullName, "NuGet.config"));
    }
}
