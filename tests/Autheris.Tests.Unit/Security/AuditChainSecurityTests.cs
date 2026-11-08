namespace Autheris.Tests.Unit.Security;

using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SEC-SIMPL-12: the audit chain anchor store exists only for PostgreSQL and SQL Server; there is no file, in-memory or SQLite fallback.</summary>
public sealed class AuditChainSecurityTests
{
    private static IHostEnvironment Env(string name = "Development")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static GatewayOptions Options(string? provider) =>
        new() { GovernanceDb = new GovernanceDbOptions { Provider = provider! } };

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Sqlite")]
    [InlineData("sqlite")]
    [InlineData("Oracle")]
    [InlineData("Bogus")]
    public void Unsupported_provider_has_no_default_anchor_store(string provider)
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            AuditChainAnchorStoreFactory.CreateForGovernance(Options(provider), Env(), Substitute.For<IKeyVaultSecretProvider>(), null, null));
        ex.Message.ShouldNotContain("Data Source");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_provider_has_no_default_anchor_store(string? provider)
    {
        Should.Throw<InvalidOperationException>(() =>
            AuditChainAnchorStoreFactory.CreateForGovernance(Options(provider), Env(), Substitute.For<IKeyVaultSecretProvider>(), null, null));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public void Supported_provider_builds_an_anchor_store(string provider)
    {
        AuditChainAnchorStoreFactory.CreateForGovernance(Options(provider), Env(), Substitute.For<IKeyVaultSecretProvider>(), null, null)
            .ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Anchor_store_without_registration_fails_startup()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => GovernanceStartupAssertions.AssertAnchorStoreResolvable(services))
            .Message.ShouldContain("IAuditChainAnchorStore");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Registered_anchor_store_passes_startup_assertion()
    {
        var services = new ServiceCollection()
            .AddSingleton(Substitute.For<IAuditChainAnchorStore>())
            .BuildServiceProvider();

        Should.NotThrow(() => GovernanceStartupAssertions.AssertAnchorStoreResolvable(services));
    }
}
