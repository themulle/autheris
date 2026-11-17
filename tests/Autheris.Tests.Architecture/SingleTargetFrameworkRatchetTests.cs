using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed partial class SingleTargetFrameworkRatchetTests
{
    [GeneratedRegex(@"<TargetFrameworks?\b")]
    private static partial Regex TargetFrameworkElementRegex();

    [GeneratedRegex(@"<TargetFramework>net(\d+)\.0</TargetFramework>")]
    private static partial Regex PropsTargetFrameworkRegex();

    [GeneratedRegex(@"""version""\s*:\s*""(\d+)\.")]
    private static partial Regex SdkMajorRegex();

    [Fact]
    public void No_project_file_declares_a_target_framework()
    {
        string root = ParityIsolationArchitectureTests.FindRepoRoot();

        var offenders = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(static path => !IsBuildOutput(path))
            .Where(path => TargetFrameworkElementRegex().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "the target framework is declared once in Directory.Build.props; remove it from: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void Directory_build_props_target_framework_matches_the_global_json_sdk_major()
    {
        string root = ParityIsolationArchitectureTests.FindRepoRoot();

        var props = PropsTargetFrameworkRegex().Match(File.ReadAllText(Path.Combine(root, "Directory.Build.props")));
        var sdk = SdkMajorRegex().Match(File.ReadAllText(Path.Combine(root, "global.json")));

        props.Success.ShouldBeTrue("Directory.Build.props must declare a single TargetFramework netN.0");
        sdk.Success.ShouldBeTrue("global.json must pin an SDK version");
        props.Groups[1].Value.ShouldBe(sdk.Groups[1].Value);
    }

    private static bool IsBuildOutput(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
            || path.Contains($"{separator}bin{separator}", StringComparison.Ordinal);
    }
}
