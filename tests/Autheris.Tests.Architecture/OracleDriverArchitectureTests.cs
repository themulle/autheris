using System.Reflection;
using NetArchTest.Rules;
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
}
