using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>Runs only when <c>AUTHERIS_RELEASE_API_ASSEMBLY</c> points at a Release API assembly; otherwise it is reported as skipped.</summary>
public sealed class FactWithReleaseApiAssemblyAttribute : FactAttribute
{
    public FactWithReleaseApiAssemblyAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReleaseBuildExcludesTestAuthTests.AssemblyEnvVar)))
        {
            Skip = $"{ReleaseBuildExcludesTestAuthTests.AssemblyEnvVar} is not set; the publish workflows run this test against the published assembly.";
        }
    }
}

/// <summary>Runs only when the API assembly under test was compiled with the TestAuth handler.</summary>
public sealed class FactWhenTestAuthCompiledInAttribute : FactAttribute
{
    public FactWhenTestAuthCompiledInAttribute()
    {
        if (typeof(Program).Assembly.GetType("Autheris.Api.Security.TestAuthHandler") is null)
        {
            Skip = "The API assembly in this test run was built without AUTHERIS_TEST_AUTH.";
        }
    }
}

/// <summary>
/// SEC2-08 / SC-04: a Release build of the API host must never contain the TestAuth handler or scheme. The check inspects the
/// compiled assembly metadata (all name heaps, user strings and attribute blobs), not the source.
/// </summary>
[Trait("Category", "ReleaseBuild")]
public sealed class ReleaseBuildExcludesTestAuthTests
{
    public const string AssemblyEnvVar = "AUTHERIS_RELEASE_API_ASSEMBLY";

    private static readonly string[] Markers = ["TestAuth", "AUTHERIS_TEST_AUTH"];
    /// <summary>
    /// Identifiers that legitimately remain in a Release build: the always-compiled option <c>Authentication:EnableTestAuthHandler</c>
    /// (the startup validator rejects it outside Development) and its configuration keys/messages. None of them is the handler type or
    /// the "TestAuth" scheme name. Matching is exact on the full identifier or literal, so a new TestAuth reference still fails.
    /// </summary>
    private static readonly HashSet<string> AllowedIdentifiers =
    [
        "get_EnableTestAuthHandler",
        "Authentication:EnableTestAuthHandler",
        "Dev:TestAuthHandler",
        "Security violation: EnableTestAuthHandler may be true ONLY in the Development environment.",
    ];

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public void DefaultReleaseEvaluation_DoesNotDefineTestAuth()
    {
        (int exit, string output) = RunDotnet(
            FindRepoRoot(),
            ["msbuild", "src/Autheris.Api/Autheris.Api.csproj", "-p:Configuration=Release", "-getProperty:AutherisIncludeTestAuth", "-getProperty:DefineConstants"]);

        exit.ShouldBe(0, output);
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement props = doc.RootElement.GetProperty("Properties");
        props.GetProperty("AutherisIncludeTestAuth").GetString().ShouldBe("false");
        props.GetProperty("DefineConstants").GetString()!.ShouldNotContain("AUTHERIS_TEST_AUTH");
    }

    [FactWithReleaseApiAssembly]
    public void ReleaseBuild_DoesNotContainTestAuthHandler()
    {
        string assembly = Environment.GetEnvironmentVariable(AssemblyEnvVar)!;
        File.Exists(assembly).ShouldBeTrue($"Release API assembly not found: {assembly}");

        FindTestAuthMarkers(assembly).ShouldBeEmpty("Release API assembly contains the TestAuth handler or scheme");
    }

    [Fact]
    public void Scanner_DetectsTestAuth_InDecoyFixture()
    {
        // Deterministic control: this test assembly carries a decoy type and string literal, so a green Release scan is meaningful.
        string[] hits = FindTestAuthMarkers(typeof(TestAuthDecoy).Assembly.Location);

        hits.ShouldContain(h => h.Contains("TestAuthDecoy", StringComparison.Ordinal));
        hits.ShouldContain(h => h.StartsWith("userstring:", StringComparison.Ordinal));
    }

    [FactWhenTestAuthCompiledIn]
    public void Scanner_DetectsTestAuth_WhenCompiledIntoApi()
    {
        FindTestAuthMarkers(typeof(Program).Assembly.Location).ShouldNotBeEmpty();
    }

    /// <summary>Decoy fixture for the scanner control test; never used by production code.</summary>
    internal sealed class TestAuthDecoy
    {
        internal static string Scheme() => "TestAuth";
    }

    private static string[] FindTestAuthMarkers(string assemblyPath)
    {
        var hits = new List<string>();
        using FileStream stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();

        // #Strings covers type, namespace, method, field, property, parameter and member-reference names.
        StringHandle sh = default;
        while (!(sh = reader.GetNextHandle(sh)).IsNil)
        {
            Check("name", reader.GetString(sh), hits);
        }

        UserStringHandle ush = default;
        while (!(ush = reader.GetNextHandle(ush)).IsNil)
        {
            Check("userstring", reader.GetUserString(ush), hits);
        }

        // #Blob covers custom attribute arguments and signatures (UTF-8 in attributes, UTF-16 for some literals).
        BlobHandle bh = default;
        while (!(bh = reader.GetNextHandle(bh)).IsNil)
        {
            byte[] blob = reader.GetBlobBytes(bh);
            Check("blob", Encoding.UTF8.GetString(blob), hits);
            Check("blob", Encoding.Unicode.GetString(blob), hits);
        }

        return [.. hits.Distinct()];
    }

    private static void Check(string kind, string value, List<string> hits)
    {
        if (kind != "blob" && AllowedIdentifiers.Contains(value))
        {
            return;
        }

        foreach (string marker in Markers)
        {
            if (value.Contains(marker, StringComparison.Ordinal))
            {
                hits.Add($"{kind}:{(value.Length > 120 ? value[..120] : value)}");
                return;
            }
        }
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

        return (process.ExitCode, stdout.Result);
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
