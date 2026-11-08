namespace Autheris.Tests.Unit.Configuration;

using Autheris.Api.Configuration;
using Autheris.Tests.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

[Collection("GovernanceEnvOverride")]
[Trait("Category", "Security")]
public sealed class ProgramGuardOrderTests
{
    [Fact]
    public void Governance_guard_runs_before_service_registration()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Autheris.Api", "Program.cs"));

        var dev = program.IndexOf("DevConfiguration.Apply(", StringComparison.Ordinal);
        var guard = program.IndexOf("GovernanceProviderGuard.ThrowIfUnsupported(builder.Configuration, builder.Environment)", StringComparison.Ordinal);
        var unknownKeys = program.IndexOf("UnknownGatewayKeyGuard.ThrowIfUnknownKeys(", StringComparison.Ordinal);
        var firstService = program.IndexOf("builder.Services.AddGateway", StringComparison.Ordinal);

        dev.ShouldBeGreaterThan(-1);
        guard.ShouldBeGreaterThan(dev, "the guard must see the configuration after DevConfiguration.Apply");
        unknownKeys.ShouldBeGreaterThan(guard);
        firstService.ShouldBeGreaterThan(guard, "no service registration may read GovernanceDb before the guard");

        // no configuration source may be added after the guard call
        program[guard..].ShouldNotContain("builder.Configuration.Add");
    }

    [Fact]
    public void Governance_guard_sees_env_override()
    {
        using var env = new EnvVarScope(
            ("GovernanceDb__Provider", "Sqlite"),
            ("GovernanceDb__ConnectionString", "Host=localhost;Database=a;Username=u;Password=p"));

        var builder = new ConfigurationManager();
        // the shipped appsettings value sits below the environment variables
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["GovernanceDb:Provider"] = "PostgreSql" });
        builder.AddEnvironmentVariables();

        var ex = Should.Throw<InvalidOperationException>(() =>
            GovernanceProviderGuard.ThrowIfUnsupported(builder, new Env()));
        ex.Message.ShouldContain("OD-1");
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

[CollectionDefinition("GovernanceEnvOverride", DisableParallelization = true)]
public sealed class GovernanceEnvOverrideCollectionDefinition;
