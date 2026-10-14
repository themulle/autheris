using System.Reflection;
using Xunit;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>
/// Plan 16.5 M-5: security-tagged tests are never retried, so a flaky security test fails the gate. The gate owns the only source
/// of randomness (the logged random seed) and the pinned seeds make every other run reproducible.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class FlakePolicyTests
{
    [Fact]
    public void TestAssembly_HasNoRetryOrFlakyAttribute()
    {
        var offenders = typeof(FlakePolicyTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Prepend(t))
            .SelectMany(m => m.GetCustomAttributesData().Select(a => (Member: m, Attribute: a.AttributeType.Name)))
            .Where(x => x.Attribute.Contains("Retry", StringComparison.OrdinalIgnoreCase) || x.Attribute.Contains("Flaky", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Member.Name}: {x.Attribute}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void NoRetryRunnerSetting_IsConfigured()
    {
        string dir = AppContext.BaseDirectory;
        foreach (var file in Directory.EnumerateFiles(dir, "xunit.runner.json").Concat(Directory.EnumerateFiles(dir, "*.runsettings")))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("retry", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryPropertyTestClass_CarriesTheSecurityAndGateTraits()
    {
        var classes = typeof(FlakePolicyTests).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(FlakePolicyTests).Namespace && t.IsPublic && t.GetMethods().Any(m => m.GetCustomAttributes<FactAttribute>().Any()));
        foreach (var type in classes)
        {
            var categories = type.GetCustomAttributesData()
                .Where(a => a.AttributeType.Name == nameof(TraitAttribute))
                .Select(a => (string?)a.ConstructorArguments[1].Value)
                .ToList();
            Assert.True(categories.Contains("Security") && categories.Contains("SqlGate"), $"{type.Name} must be tagged Security and SqlGate");
        }
    }

    [Fact]
    public void Seeds_ArePinned_AndTheRandomSeedIsLogged()
    {
        Assert.Equal(new ulong[] { 20261010UL, 7UL, 90210UL }, GateProfile.PinnedSeeds);
        Assert.NotEqual(0UL, GateProfile.RandomSeed);
    }
}
