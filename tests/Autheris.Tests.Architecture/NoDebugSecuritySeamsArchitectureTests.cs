using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARC-S-06: Debug and Release compile the same security code. No <c>#if DEBUG</c> block may exist in production sources, and
/// production code never installs compiler hooks (<c>CompilerHooks.Use</c> is called by test code only), so no production path can
/// switch a security control off.
/// </summary>
[Trait("Category", "Security")]
public sealed class NoDebugSecuritySeamsArchitectureTests
{
    [Fact]
    public void Src_ContainsNoPreprocessorDebugBlocks()
    {
        string src = FindSrcDirectory();
        var violations = new List<string>();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            int lineNumber = 0;
            foreach (string raw in File.ReadLines(file))
            {
                lineNumber++;
                string line = raw.Trim();
                if (line.StartsWith("#if", StringComparison.Ordinal) && line.Contains("DEBUG", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetRelativePath(src, file)}:{lineNumber}");
                }
            }
        }

        violations.ShouldBeEmpty("No #if DEBUG in production sources (ARC-S-06): " + string.Join(", ", violations));
    }

    [Fact]
    public void ProductionAssembly_HasNoCompilerTestSeamsType() =>
        typeof(TrinoSqlEngine.FastSqlEngine).Assembly.GetType("TrinoSqlEngine.CompilerTestSeams").ShouldBeNull();

    [Fact]
    public void ProductionCode_NeverInstallsCompilerHooks()
    {
        var callers = new List<string>();
        foreach (var assembly in new[]
                 {
                     typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
                     typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
                     typeof(Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator).Assembly,
                     typeof(TrinoSqlEngine.FastSqlEngine).Assembly
                 })
        {
            var module = CecilModuleCache.For(assembly);
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                            instruction.Operand is MethodReference called &&
                            called.Name == "Use" && called.DeclaringType.FullName == "TrinoSqlEngine.CompilerHooks")
                        {
                            callers.Add($"{assembly.GetName().Name}: {type.FullName}.{method.Name}");
                        }
                    }
                }
            }
        }

        callers.ShouldBeEmpty("Production code must not install compiler hooks (ARC-S-06): " + string.Join(", ", callers));
    }

    private static string FindSrcDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "TrinoSqlEngine");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "src");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src from the test output directory.");
    }
}
