using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>WP-ARC-B-13: one state directory for the governance database and the audit anchor; no in-memory database outside Development/Test.</summary>
[Trait("Category", "Security")]
public sealed class GatewayStatePathsTests
{
    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static GatewayStatePaths Paths(string environment, string directory = "./state") =>
        new(Options.Create(new StateOptions { StateDirectory = directory }), Env(environment));

    [Fact]
    public void StatePaths_ResolveRelativeToStateDirectory()
    {
        var paths = Paths("Production", "./some/state");

        paths.StateDirectory.ShouldBe(Path.GetFullPath("./some/state"));
        paths.GovernanceDatabaseFile.ShouldBe(Path.Combine(Path.GetFullPath("./some/state"), "governance.db"));
    }

    [Fact]
    public void StatePaths_ResolveRelativeDirectoryAgainstTheContentRoot()
    {
        var env = Env("Production");
        env.ContentRootPath.Returns("/srv/autheris");

        var paths = new GatewayStatePaths(Options.Create(new StateOptions { StateDirectory = "state" }), env);

        paths.StateDirectory.ShouldBe("/srv/autheris/state");
        new GatewayStatePaths(Options.Create(new StateOptions { StateDirectory = "/var/x" }), env).StateDirectory.ShouldBe("/var/x");
    }

    [Fact]
    public void ExplicitConnectionString_Wins()
    {
        var paths = Paths("Production");

        paths.ResolveGovernanceConnectionString("Data Source=/data/custom.db").ShouldBe("Data Source=/data/custom.db");
        Paths("Development").ResolveGovernanceConnectionString("Data Source=/data/custom.db").ShouldBe("Data Source=/data/custom.db");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public void DevelopmentAndTest_NoConnectionString_UsesMemory(string environment)
    {
        var connectionString = Paths(environment).ResolveGovernanceConnectionString(null);

        connectionString.ShouldContain("Mode=Memory");
        connectionString.ShouldContain("Cache=Shared");
        Paths(environment).ResolveGovernanceConnectionString("  ").ShouldNotBe(connectionString, "each memory database gets its own name");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Production_NoConnectionString_UsesStateFile(string environment)
    {
        var paths = Paths(environment, "./state-x");

        paths.ResolveGovernanceConnectionString(null).ShouldBe($"Data Source={paths.GovernanceDatabaseFile}");
        paths.ResolveGovernanceConnectionString("").ShouldBe($"Data Source={paths.GovernanceDatabaseFile}");
    }

    [Fact]
    public void ExplicitChainAnchorPath_Wins()
    {
        var anchorPath = Path.Combine(Path.GetTempPath(), $"explicit-anchor-{Guid.NewGuid():N}.json");
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = string.Empty },
            Audit = new AuditOptions { ChainAnchorPath = anchorPath }
        };

        var store = AuditChainAnchorStoreFactory.CreateForGovernance(options, Env("Production"), Substitute.For<IKeyVaultSecretProvider>(), null, null, Paths("Production", "/var/state"));

        store.ShouldBeOfType<FileAuditChainAnchorStore>().FilePath.ShouldBe(Path.GetFullPath(anchorPath));
    }

    [Fact]
    public void ResolveDirectory_PrefersConfiguredThenFallback()
    {
        GatewayStatePaths.ResolveDirectory("/a", ".data").ShouldBe("/a");
        GatewayStatePaths.ResolveDirectory(null, ".data").ShouldBe(".data");
        GatewayStatePaths.ResolveDirectory("  ", ".data").ShouldBe(".data");
    }
}
