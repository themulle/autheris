namespace Autheris.Tests.Unit.Security;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ArchitectureAndStartupHardeningTests
{
    [Fact]
    public void NoConstructor_InInfrastructureCache_Or_ApplicationPolicy_Takes_IServiceProvider()
    {
        // AR-14 / AR-15: Beseitigung von Service-Locator
        var infraAssembly = typeof(Autheris.Infrastructure.Cache.EpochValidationService).Assembly;
        var appAssembly = typeof(Autheris.Application.Policy.TableAccessPolicy).Assembly;

        var targetTypes = infraAssembly.GetTypes()
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("Autheris.Infrastructure.Cache", StringComparison.Ordinal))
            .Concat(appAssembly.GetTypes()
                .Where(t => t.Namespace != null && t.Namespace.StartsWith("Autheris.Application.Policy", StringComparison.Ordinal)));

        foreach (var type in targetTypes)
        {
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var hasServiceProvider = ctor.GetParameters().Any(p => p.ParameterType == typeof(IServiceProvider));
                hasServiceProvider.ShouldBeFalse($"Type {type.FullName} constructor {ctor} must not take IServiceProvider.");
            }
        }
    }

    [Fact]
    public void No_GetAwaiter_GetResult_In_ApiSecurity()
    {
        // AR-14 / AR-15: Kein Sync-over-Async in src/Autheris.Api/Security/**
        var repoRoot = FindRepoRoot();
        var securityDir = Path.Combine(repoRoot, "src", "Autheris.Api", "Security");
        var csFiles = Directory.GetFiles(securityDir, "*.cs", SearchOption.AllDirectories);

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            content.Contains(".GetAwaiter().GetResult()").ShouldBeFalse(
                $"File {Path.GetFileName(file)} contains prohibited sync-over-async .GetAwaiter().GetResult()");
        }
    }

    [Fact]
    public void No_Direct_Dialect_Switch_Outside_SqlDialectMapper()
    {
        // AR-13: Kein DatabaseDialect.* => TargetSqlDialect.*-Switch außerhalb SqlDialectMapper
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("SqlDialectMapper.cs", StringComparison.OrdinalIgnoreCase));

        var switchPattern = new Regex(@"DatabaseDialect\.\w+\s*=>\s*TargetSqlDialect\.\w+", RegexOptions.Compiled);

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            var match = switchPattern.Match(content);
            match.Success.ShouldBeFalse($"File {file} contains direct DatabaseDialect => TargetSqlDialect switch: {match.Value}");
        }
    }

    [Fact]
    public void Startup_SqliteGovernanceProvider_InProduction_LogsExactlyOneWarning()
    {
        // AR-08: Startup-Warnung, wenn GovernanceDb:Provider = sqlite außerhalb Development verwendet wird
        var services = new ServiceCollection();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "Sqlite",
                ConnectionString = "Data Source=prod-gov.db;Cache=Shared"
            }
        };

        var loggerFactory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);

        services.AddSingleton(loggerFactory);
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(Substitute.For<IRlsFilterGenerator>());

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        GatewayServiceCollectionExtensions.AddGatewayInfrastructure(services, options, env);

        // Verify that a warning was logged regarding SQLite in production
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("SQLite", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Autheris.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }
}
