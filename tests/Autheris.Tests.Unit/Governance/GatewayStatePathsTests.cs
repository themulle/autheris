using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions;
using Autheris.Application.Interfaces;
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
    public void AnchorDefaultsNextToGovernanceDb()
    {
        GatewayStatePaths.AuditAnchorFileFor("/var/state/governance.db").ShouldBe("/var/state/governance.db.audit-anchor.json");

        var directory = Path.Combine(Path.GetTempPath(), $"autheris-state-{Guid.NewGuid():N}");
        try
        {
            var paths = Paths("Production", directory);
            var options = new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = string.Empty } };
            var store = AuditChainAnchorStoreFactory.CreateForGovernance(options, Env("Production"), Substitute.For<IKeyVaultSecretProvider>(), new TestSigner(), null, paths);

            store.Save(new AuditChainAnchor(1, "hash", DateTimeOffset.UtcNow, "hmac"));

            File.Exists(GatewayStatePaths.AuditAnchorFileFor(paths.GovernanceDatabaseFile)).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class TestSigner : IAuditAnchorSigner
    {
        public string Sign(ReadOnlySpan<byte> data) => "sig";

        public bool Verify(ReadOnlySpan<byte> data, string signature) => signature == "sig";
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

    [Theory]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("bad\0path", null)]
    [InlineData("./state", "true")]
    [InlineData("/app", "true")]
    [InlineData("/app/", "true")]
    public void StateOptionsValidator_RejectsEmptyAndContainerAppRoot(string directory, string? inContainer)
    {
        var validator = NewValidator("Sqlite", inContainer);

        var result = validator.Validate(null, new StateOptions { StateDirectory = directory });

        result.Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("./state", null)]
    [InlineData("/app/data", "true")]
    [InlineData("/var/lib/autheris", "true")]
    public void StateOptionsValidator_AcceptsUsableDirectories(string directory, string? inContainer)
    {
        NewValidator("Sqlite", inContainer).Validate(null, new StateOptions { StateDirectory = directory }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void StateOptionsValidator_IsNotEnforcedForServerProviders()
    {
        NewValidator("PostgreSql", "true").Validate(null, new StateOptions { StateDirectory = "./state" }).Succeeded.ShouldBeTrue();
    }

    private static StateOptionsValidator NewValidator(string provider, string? inContainer) =>
        new(Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { Provider = provider } }),
            name => name == "DOTNET_RUNNING_IN_CONTAINER" ? inContainer : null);

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=gov_x;Mode=Memory;Cache=Shared")]
    public void StartupValidator_RejectsEffectiveInMemorySqlite_OutsideDevelopment(string connectionString)
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" },
            GovernanceDb = new GovernanceDbOptions { Provider = "Sqlite", ConnectionString = connectionString }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env("Production"), _ => null));

        ex.Message.ShouldContain("In-memory SQLite");
    }

    [Fact]
    public void StartupValidator_AcceptsUnsetConnectionString_OutsideDevelopment_BecauseTheStateFileIsUsed()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" },
            Audit = new AuditOptions { ChainAnchorPath = "/var/state/anchor.json" },
            GovernanceDb = new GovernanceDbOptions { Provider = "Sqlite", ConnectionString = string.Empty }
        };

        Should.NotThrow(() => GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env("Production"), _ => null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Data Source=/var/lib/autheris/governance.db")]
    public void StartupValidator_SqliteWithoutAnAuditAnchor_FailsEarly_OutsideDevelopment(string connectionString)
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" },
            GovernanceDb = new GovernanceDbOptions { Provider = "Sqlite", ConnectionString = connectionString }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env("Production"), _ => null));

        ex.Message.ShouldContain("AU-01/AU-03");
    }
}
