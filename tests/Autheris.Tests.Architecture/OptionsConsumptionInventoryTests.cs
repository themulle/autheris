using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT-05: inventory of option properties that no production code reads. For every settable property on a
/// production type whose name ends in "Options", a text scan looks for a member access <c>.PropertyName</c> in any
/// src file other than the file(s) declaring the type. The heuristic over-approximates consumption (a common name
/// read elsewhere counts as read), so it never flags a consumed property. Current candidates are baselined in
/// Baselines/unconsumed-options.txt ("Namespace.Type.Property"); the baseline may only shrink (ARCH-MNT-06 resolves
/// the dead security options).
/// </summary>
public sealed partial class OptionsConsumptionInventoryTests
{
    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(Autheris.Domain.Options.GatewayOptions).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
        typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly,
        typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly,
        typeof(Program).Assembly
    ];

    [GeneratedRegex(@"\.\s*([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex MemberAccess();

    [Fact]
    public void UnconsumedOptionProperties_DoNotGrow()
    {
        var baseline = RepositoryScan.ReadBaseline("unconsumed-options.txt").ToHashSet(StringComparer.Ordinal);
        var current = FindUnconsumed(ProductionAssemblies.SelectMany(SafeTypes), RepositoryScan.ProductionFiles);

        current.Except(baseline, StringComparer.Ordinal)
            .ShouldBeEmpty("New option properties that no production code reads. Wire them up or delete them (ARCH-MNT-06).");
    }

    [Fact]
    public void Baseline_HasNoStaleEntries()
    {
        var baseline = RepositoryScan.ReadBaseline("unconsumed-options.txt");
        var current = FindUnconsumed(ProductionAssemblies.SelectMany(SafeTypes), RepositoryScan.ProductionFiles).ToHashSet(StringComparer.Ordinal);

        baseline.Where(b => !current.Contains(b))
            .ShouldBeEmpty("These option properties are now consumed or gone; remove them from Baselines/unconsumed-options.txt.");
    }

    [Fact]
    public void Scanner_DetectsPlantedUnconsumedProperty()
    {
        var files = new List<RepositoryScan.SourceFile>
        {
            new("src/X/PlantedOptions.cs", "public sealed class PlantedOptions { public int Used { get; set; } public int Dead { get; set; } }"),
            new("src/X/Reader.cs", "var x = options.Value.Used;\r\n")
        };

        var found = FindUnconsumed([typeof(PlantedOptions)], files);

        found.ShouldBe([typeof(PlantedOptions).FullName + ".Dead"]);
        FindUnconsumed([], files).ShouldBeEmpty();
        FindUnconsumed(ProductionAssemblies.SelectMany(SafeTypes), RepositoryScan.ProductionFiles)
            .Count.ShouldBeGreaterThan(0, "Scanner must see option types; a zero result means it is broken.");
    }

    private static List<string> FindUnconsumed(IEnumerable<Type> types, IReadOnlyList<RepositoryScan.SourceFile> files)
    {
        var index = ReferenceEquals(files, RepositoryScan.ProductionFiles) ? ProductionIndex.Value : BuildIndex(files);
        var accessedByFile = index.Accessed;

        var result = new List<string>();
        foreach (var type in types)
        {
            if (!type.IsClass || type.IsAbstract || !type.Name.EndsWith("Options", StringComparison.Ordinal)
                || type.Name.Contains('<'))
            {
                continue;
            }

            var declaring = index.Declaring.GetValueOrDefault(type.Name) ?? [];

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.GetIndexParameters().Length > 0 || property.SetMethod is not { IsPublic: true })
                {
                    continue;
                }

                var consumed = accessedByFile.Any(kv => !declaring.Contains(kv.Key) && kv.Value.Contains(property.Name));
                if (!consumed)
                {
                    result.Add($"{type.FullName}.{property.Name}");
                }
            }
        }

        return result.Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    private sealed record FileIndex(
        Dictionary<string, HashSet<string>> Accessed,
        Dictionary<string, HashSet<string>> Declaring);

    private static readonly Lazy<FileIndex> ProductionIndex = new(() => BuildIndex(RepositoryScan.ProductionFiles));

    private static FileIndex BuildIndex(IReadOnlyList<RepositoryScan.SourceFile> files)
    {
        var accessed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var declaring = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            accessed[file.RelativePath] = MemberAccess().Matches(file.Text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (Match m in TypeDeclaration().Matches(file.Text))
            {
                var name = m.Groups[1].Value;
                if (!declaring.TryGetValue(name, out var set))
                {
                    declaring[name] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.Add(file.RelativePath);
            }
        }

        return new FileIndex(accessed, declaring);
    }

    [GeneratedRegex(@"\b(?:class|record|struct)\s+([A-Za-z_][A-Za-z0-9_]*Options)\b")]
    private static partial Regex TypeDeclaration();

    private static Type[] SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null).Select(t => t!).ToArray();
        }
    }
}

public sealed class PlantedOptions
{
    public int Used { get; set; }

    public int Dead { get; set; }
}
