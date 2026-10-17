namespace TrinoSqlEngine.Tests.TimeZones;

using Xunit;

/// <summary>BC-130-6 / ST-130-16 / SEC-130-06: supply-chain checks of the embedded zone data.</summary>
[Trait("Category", "Security")]
public sealed class IanaWindowsZonesReproducibilityTests
{
    private static string Cldr(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TimeZones", "Cldr", name));

    private static string Embedded() => TimeZoneRegistry.LoadResourceText()!;

    private static IEnumerable<(string Iana, string Windows)> Entries(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l[0] != '#').Select(l => l.Split(';')).Select(p => (p[0], p[1]));

    [Fact]
    public void ST_130_16_BC_130_6a_TheCheckedInCldrSourcesMatchTheirHashes()
    {
        Assert.Equal(IanaWindowsZonesGenerator.WindowsZonesSha256, IanaWindowsZonesGenerator.Sha256Hex(Cldr("windowsZones.xml")));
        Assert.Equal(IanaWindowsZonesGenerator.TimezoneSha256, IanaWindowsZonesGenerator.Sha256Hex(Cldr("timezone.xml")));
    }

    [Fact]
    public void ST_130_16_BC_130_6a_RegenerationFromTheCheckedInSourceIsByteIdentical()
    {
        string generated = IanaWindowsZonesGenerator.Generate(Cldr("windowsZones.xml"), Cldr("timezone.xml"));

        // Regeneration path of a CLDR bump: AUTHERIS_TZ_REGENERATE_TO=<path> writes the resource text (LF, UTF-8 without BOM).
        if (Environment.GetEnvironmentVariable("AUTHERIS_TZ_REGENERATE_TO") is { Length: > 0 } target)
        {
            File.WriteAllBytes(target, new System.Text.UTF8Encoding(false).GetBytes(generated));
        }

        Assert.Equal(generated, Embedded());
        Assert.Equal(TimeZoneRegistry.ResourceSha256, TimeZoneRegistry.Sha256Of(generated));
    }

    [Fact]
    public void ST_130_16_ASwappedMappingFailsReproducibilityEvenWhenTheHashConstantIsUpdatedToMatch()
    {
        // Swap two mappings that are not anchors, update the hash to match: the self-test and the pattern checks still pass ...
        string tampered = Embedded()
            .Replace("Europe/London;GMT Standard Time", "Europe/London;@@", StringComparison.Ordinal)
            .Replace("Europe/Paris;Romance Standard Time", "Europe/Paris;GMT Standard Time", StringComparison.Ordinal)
            .Replace("Europe/London;@@", "Europe/London;Romance Standard Time", StringComparison.Ordinal);
        Assert.NotEqual(Embedded(), tampered);
        Assert.True(TimeZoneRegistry.SelfTest(tampered).Passed);

        // ... but the reproducibility comparison fails.
        string generated = IanaWindowsZonesGenerator.Generate(Cldr("windowsZones.xml"), Cldr("timezone.xml"));
        Assert.NotEqual(generated, tampered);
    }

    [Fact]
    public void ST_130_16_ASwappedMappingFailsTheTzDataCrossCheck()
    {
        var entries = Entries(Embedded()).Select(e => e.Iana == "Europe/London" ? (e.Iana, "Eastern Standard Time") : e).ToList();
        var problems = TzDataCrossCheck.Run(entries.Where(e => e.Iana is "Europe/London" or "Europe/Berlin"));
        Assert.Contains(problems, p => p.StartsWith("Europe/London", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.StartsWith("Europe/Berlin", StringComparison.Ordinal));
    }

    [CiOnlyFact]
    [Trait("Category", "TzDataCrossCheck")]
    public void ST_130_16_BC_130_6b_EveryEntryAgreesWithTheOperatingSystemTimeZoneData()
    {
        var problems = TzDataCrossCheck.Run(Entries(Embedded()));
        Assert.True(problems.Count == 0, "Registry entries that disagree with TimeZoneInfo: " + string.Join("; ", problems));
    }

    [Fact]
    public void BC_130_6e_NoRuntimeFileOrNetworkSourceExistsInTheRegistry()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "TrinoSqlEngine", "TimeZones", "TimeZoneRegistry.cs"));
        Assert.Contains("GetManifestResourceStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BC_130_6c_TheResourceAndTheHashConstantAreCodeOwned()
    {
        string owners = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "CODEOWNERS"));
        Assert.Contains("src/TrinoSqlEngine/TimeZones/", owners, StringComparison.Ordinal);
        Assert.Contains("tests/TrinoSqlEngine.Tests/TimeZones/Cldr/", owners, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
