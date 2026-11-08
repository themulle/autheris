namespace Autheris.Tests.Unit.Security;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Common.Interfaces;
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
