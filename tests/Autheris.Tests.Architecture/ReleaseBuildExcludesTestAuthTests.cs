using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SEC2-08 / SC-04: a Release build of the API host must never contain the TestAuth handler or scheme. The check inspects the
/// compiled assembly metadata (type definitions and user strings), not the source, so it cannot be bypassed by renaming a flag.
/// </summary>
[Trait("Category", "ReleaseBuild")]
public sealed class ReleaseBuildExcludesTestAuthTests
{
    private const string TestAuthTypeName = "TestAuthHandler";
    private const string TestAuthSchemeName = "TestAuth";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public void DefaultReleaseEvaluation_DoesNotDefineTestAuth()
    {
        string repoRoot = FindRepoRoot();
        (int exit, string output) = RunDotnet(
            repoRoot,
            ["msbuild", "src/Autheris.Api/Autheris.Api.csproj", "-p:Configuration=Release", "-getProperty:AutherisIncludeTestAuth", "-getProperty:DefineConstants"]);

        exit.ShouldBe(0, output);
        output.ShouldContain("\"AutherisIncludeTestAuth\": \"false\"");
        output.ShouldNotContain("AUTHERIS_TEST_AUTH");
    }

    [Fact]
    public void ReleaseBuild_DoesNotContainTestAuthHandler()
    {
        string? assembly = Environment.GetEnvironmentVariable("AUTHERIS_RELEASE_API_ASSEMBLY");
        string? tempRoot = null;
        try
        {
            if (string.IsNullOrWhiteSpace(assembly))
            {
                tempRoot = Path.Combine(Path.GetTempPath(), "autheris-release-" + Guid.NewGuid().ToString("N"));
                string bin = Path.Combine(tempRoot, "bin") + Path.DirectorySeparatorChar;
                (int exit, string output) = RunDotnet(
                    FindRepoRoot(),
                    [
                        "build", "src/Autheris.Api/Autheris.Api.csproj", "-c", "Release", "--no-restore",
                        $"-p:OutputPath={bin}",
                    ]);
                exit.ShouldBe(0, output);
                assembly = Path.Combine(bin, "Autheris.Api.dll");
            }

            File.Exists(assembly).ShouldBeTrue($"Release API assembly not found: {assembly}");
            ContainsTestAuth(assembly).ShouldBeFalse("Release API assembly contains the TestAuth handler or scheme");
        }
        finally
        {
            if (tempRoot is not null && Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public void Scanner_DetectsTestAuth_WhenCompiledIn()
    {
        // Control: the scanner must be able to find the handler, otherwise a green Release scan proves nothing.
        var apiAssembly = typeof(Program).Assembly;
        if (apiAssembly.GetType("Autheris.Api.Security.TestAuthHandler") is null)
        {
            return;
        }

        ContainsTestAuth(apiAssembly.Location).ShouldBeTrue();
    }

    private static bool ContainsTestAuth(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();

        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            if (reader.GetString(reader.GetTypeDefinition(handle).Name) == TestAuthTypeName)
            {
                return true;
            }
        }

        UserStringHandle current = default;
        while (true)
        {
            current = reader.GetNextHandle(current);
            if (current.IsNil)
            {
                break;
            }

            if (reader.GetUserString(current) == TestAuthSchemeName)
            {
                return true;
            }
        }

        return false;
    }

    private static (int ExitCode, string Output) RunDotnet(string workingDirectory, string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Evaluate with the repository defaults: no test-auth opt-in may leak in from the surrounding test run.
        psi.Environment.Remove("AutherisIncludeTestAuth");
        psi.Environment.Remove("AUTHERISINCLUDETESTAUTH");

        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', args)} exceeded {ProcessTimeout}");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Autheris.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Autheris.sln not found");
    }
}
