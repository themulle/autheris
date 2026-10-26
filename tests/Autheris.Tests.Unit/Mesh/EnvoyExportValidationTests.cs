namespace Autheris.Tests.Unit.Mesh;

using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Application.Mesh.Services;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class EnvoyExportValidationTests
{
    // Former regexes, retained here as the oracle. NonBacktracking: linear, no wall-clock dependence.
    private static readonly Regex OldDns = new("^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$", RegexOptions.NonBacktracking);
    private static readonly Regex OldPath = new("^/[A-Za-z0-9/_.-]{0,200}$", RegexOptions.NonBacktracking);
    private static readonly Regex OldVersion = new("^v[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly string[] Atoms =
    [
        "a", "b", "z", "0", "9", "A", "Z", "-", "_", ".", "/", "//", "..", "%2e", "\n", "\r", "\0", " ", "v", "V", "é", "K", "١", "ａ", "‮",
        "abc", "xn--", "api", "1",
    ];

    private static bool OnlyTrailingNewlineDifference(string s, bool oldResult, bool newResult, Regex old) =>
        oldResult == newResult
        || (oldResult && !newResult && s.EndsWith('\n') && old.IsMatch(s[..^1]) && !s[..^1].EndsWith('\n'));

    private static string[] Corpus(int seed, int count)
    {
        var rnd = new Random(seed);
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var sb = new StringBuilder();
            var parts = rnd.Next(0, 12);
            if (rnd.Next(4) == 0) { sb.Append('a', rnd.Next(55, 70)); }
            if (rnd.Next(6) == 0) { sb.Append('/').Append('a', rnd.Next(195, 206)); }
            for (var p = 0; p < parts; p++) { sb.Append(Atoms[rnd.Next(Atoms.Length)]); }
            result[i] = sb.ToString();
        }

        return result;
    }

    private static readonly string[] Boundaries =
    [
        "", "a", "-", "a-", "-a", "a-a", "A", "a.b", "a\n", "\n", "a\n\n", new string('a', 63), new string('a', 64), "a" + new string('-', 61) + "a", "a" + new string('-', 62) + "a",
        "/", "//", "/\n", "/" + new string('a', 200), "/" + new string('a', 201), "/" + new string('a', 200) + "\n", "/a/../b", "/%2e%2e", "/a b", "v1", "V12", "v", "v1\n", "vv1", "v١", "K1",
    ];

    [Fact]
    public void IsDnsLabel_AgreesWithFormerRegex()
    {
        foreach (var s in Boundaries.Concat(Corpus(2701, 12_000)))
        {
            OnlyTrailingNewlineDifference(s, OldDns.IsMatch(s), EnvoyExportValidation.IsDnsLabel(s), OldDns)
                .ShouldBeTrue($"input: {Escape(s)}");
        }

        EnvoyExportValidation.IsDnsLabel(new string('a', 63)).ShouldBeTrue();
        EnvoyExportValidation.IsDnsLabel(new string('a', 64)).ShouldBeFalse();
        EnvoyExportValidation.IsDnsLabel("a\n").ShouldBeFalse();
        EnvoyExportValidation.IsDnsLabel("").ShouldBeFalse();
    }

    [Fact]
    public void IsAuthzPath_AgreesWithFormerRegex()
    {
        foreach (var s in Boundaries.Concat(Corpus(2702, 12_000)))
        {
            OnlyTrailingNewlineDifference(s, OldPath.IsMatch(s), EnvoyExportValidation.IsAuthzPath(s), OldPath)
                .ShouldBeTrue($"input: {Escape(s)}");
        }

        EnvoyExportValidation.IsAuthzPath("/" + new string('a', 200)).ShouldBeTrue();
        EnvoyExportValidation.IsAuthzPath("/" + new string('a', 201)).ShouldBeFalse();
        EnvoyExportValidation.IsAuthzPath("/a\n").ShouldBeFalse();
    }

    [Fact]
    public void VersionSegment_AgreesWithFormerRegex()
    {
        foreach (var s in Boundaries.Concat(Corpus(2704, 12_000)))
        {
            OnlyTrailingNewlineDifference(s, OldVersion.IsMatch(s), EnvoyExportValidation.IsVersionSegment(s), OldVersion)
                .ShouldBeTrue($"input: {Escape(s)}");
        }

        EnvoyExportValidation.IsVersionSegment("V2").ShouldBeTrue();
        EnvoyExportValidation.IsVersionSegment("vat").ShouldBeFalse();
    }

    [Fact]
    public void EnvoyExport_RejectsHostileInputs_Without500()
    {
        var svc = (EnvoyExtAuthzService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EnvoyExtAuthzService));
        foreach (var hostile in new[] { "x\n", "a\0b", "A", "", new string('a', 64), "a..b", "é", "a b" })
        {
            Should.Throw<ArgumentException>(() => svc.GenerateIstioEnvoyFilterYaml(new EnvoyFilterExportOptions { MeshNamespace = hostile }));
            Should.Throw<ArgumentException>(() => svc.GenerateIstioEnvoyFilterYaml(new EnvoyFilterExportOptions { FilterName = hostile }));
            Should.Throw<ArgumentException>(() => svc.GenerateIstioEnvoyFilterYaml(new EnvoyFilterExportOptions { ServiceHost = hostile }));
        }

        foreach (var path in new[] { "/a\n", "a", "", "/%2e%2e", "/" + new string('a', 201), "/a b", "/é" })
        {
            Should.Throw<ArgumentException>(() => svc.GenerateIstioEnvoyFilterYaml(new EnvoyFilterExportOptions { AuthzPath = path }));
        }
    }

    private static string Escape(string s) => s.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\0", "\\0", StringComparison.Ordinal);
}
