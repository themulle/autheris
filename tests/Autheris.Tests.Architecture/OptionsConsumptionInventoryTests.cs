using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT-05: inventory of option properties that no production code reads. For every settable property on a
/// production type whose name ends in "Options", a text scan looks for a member access <c>.PropertyName</c> in any
/// src file other than the file(s) declaring the type. The heuristic over-approximates consumption (a common name
/// read elsewhere counts as read), so it under-reports unconsumed properties but never flags a consumed one. Only new
/// candidates fail; baseline entries that became consumed produce a hint. Current candidates are baselined in
/// Baselines/unconsumed-options.txt ("Namespace.Type.Property"); the baseline may only shrink (ARCH-MNT-06 resolves
/// the dead security options).
/// </summary>
public sealed partial class OptionsConsumptionInventoryTests(Xunit.Abstractions.ITestOutputHelper output)
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

        foreach (var stale in baseline.Except(current, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            output.WriteLine("HINT (not a failure): now consumed or gone, remove from the baseline: " + stale);
        }

        var indirect = ParseIndirect(RepositoryScan.ReadBaseline("options-consumed-indirectly.txt"))
            .Select(e => e.Property).ToHashSet(StringComparer.Ordinal);

        current.Except(baseline, StringComparer.Ordinal).Except(indirect, StringComparer.Ordinal)
            .ShouldBeEmpty("New option properties that no production code reads. Wire them up or delete them (ARCH-MNT-06).");
    }

    /// <summary>
    /// ARCH-MNT-06 SC-08: an entry of Baselines/options-consumed-indirectly.txt hides a property from the ratchet only while its consuming
    /// member really is used outside the defining file(s) and the heuristic still reports the property (otherwise the line is stale).
    /// </summary>
    [Fact]
    public void IndirectConsumers_AreReadOutsideTheDefiningFile()
    {
        var entries = ParseIndirect(RepositoryScan.ReadBaseline("options-consumed-indirectly.txt"));

        entries.Count.ShouldBeGreaterThan(0, "The indirect-consumer file must not be empty; an empty file would hide a broken ratchet.");
        CheckIndirect(entries, ProductionAssemblies.SelectMany(SafeTypes), RepositoryScan.ProductionFiles).ShouldBeEmpty();
    }

    [Fact]
    public void IndirectConsumerCheck_FailsClosed_OnStaleMissingOrMalformedEntries()
    {
        var files = new List<RepositoryScan.SourceFile>
        {
            new("src/X/IndirectOptions.cs", "public sealed class IndirectOptions { public int Direct { get; set; } public int Hidden { get; set; } public int Gone2 { get; set; } public int IsHidden() => Hidden; }"),
            new("src/X/Reader.cs", "var x = options.Value.Direct; var y = options.Value.IsHidden();")
        };
        var types = new[] { typeof(IndirectOptions) };
        string Line(string property, string consumer) => $"{typeof(IndirectOptions).FullName}.{property}|{consumer}";

        CheckIndirect(ParseIndirect([Line("Hidden", "IsHidden")]), types, files).ShouldBeEmpty();
        CheckIndirect(ParseIndirect([Line("Hidden", "NotUsedAnywhere")]), types, files).Count.ShouldBe(1);
        CheckIndirect(ParseIndirect([Line("Direct", "IsHidden")]), types, files).Count.ShouldBe(1);
        CheckIndirect(ParseIndirect([Line("DoesNotExist", "IsHidden")]), types, files).Count.ShouldBe(1);
        CheckIndirect(ParseIndirect(["Missing.Type.Prop|IsHidden"]), types, files).Count.ShouldBe(1);
        Should.Throw<InvalidDataException>(() => ParseIndirect(["no-separator"]));
        Should.Throw<InvalidDataException>(() => ParseIndirect(["A.B|"]));
    }

    private sealed record IndirectEntry(string Property, string TypeName, string PropertyName, string Consumer);

    private static List<IndirectEntry> ParseIndirect(IEnumerable<string> lines)
    {
        var result = new List<IndirectEntry>();
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            var dot = parts[0].LastIndexOf('.');
            if (parts.Length != 2 || dot <= 0 || dot == parts[0].Length - 1 || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new InvalidDataException("Malformed options-consumed-indirectly line (expected Namespace.Type.Property|ConsumingMember): " + line);
            }

            result.Add(new IndirectEntry(parts[0], parts[0][..dot], parts[0][(dot + 1)..], parts[1].Trim()));
        }

        return result;
    }

    private static List<string> CheckIndirect(IReadOnlyList<IndirectEntry> entries, IEnumerable<Type> types, IReadOnlyList<RepositoryScan.SourceFile> files)
    {
        var typeList = types.ToList();
        var index = ReferenceEquals(files, RepositoryScan.ProductionFiles) ? ProductionIndex.Value : BuildIndex(files);
        var unconsumed = FindUnconsumed(typeList, files).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var entry in entries)
        {
            var type = typeList.FirstOrDefault(t => t.FullName == entry.TypeName);
            if (type is null || type.GetProperty(entry.PropertyName) is null)
            {
                problems.Add($"{entry.Property}: the option type or property no longer exists; remove the line.");
                continue;
            }

            if (!unconsumed.Contains(entry.Property))
            {
                problems.Add($"{entry.Property}: the property is now consumed directly (or is not a settable option); remove the line.");
                continue;
            }

            var declaring = index.Declaring.GetValueOrDefault(type.Name) ?? [];
            var usage = new Regex(@"(?:\.|\bnew\s+|<)\s*" + Regex.Escape(entry.Consumer) + @"\b|\b" + Regex.Escape(entry.Consumer) + @"\s*\.", RegexOptions.CultureInvariant);
            if (!files.Any(f => !declaring.Contains(f.RelativePath) && usage.IsMatch(f.Text)))
            {
                problems.Add($"{entry.Property}: the consuming member '{entry.Consumer}' is not used outside the file(s) declaring {type.Name}; the option is dead.");
            }
        }

        return problems;
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

        // A property that becomes consumed simply drops out of the result (and out of the failing set).
        var withReader = files.Append(new("src/X/Reader2.cs", "var y = options.Value.Dead;")).ToList();
        FindUnconsumed([typeof(PlantedOptions)], withReader).ShouldBeEmpty();
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

public sealed class IndirectOptions
{
    public int Direct { get; set; }

    public int Hidden { get; set; }

    public int Gone2 { get; set; }

    public int IsHidden() => Hidden;
}

public sealed class PlantedOptions
{
    public int Used { get; set; }

    public int Dead { get; set; }
}
