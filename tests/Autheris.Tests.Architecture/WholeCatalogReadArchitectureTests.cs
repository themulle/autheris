using System.Text.RegularExpressions;
using Mono.Cecil;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// #1063 §3: Guards against whole-catalog reads (<see cref="Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync"/>,
/// <c>ListTablesPageAsync</c>, and <see cref="Autheris.Application.Common.Interfaces.ITableMetadataRepository.GetAllTablesAsync"/>) in per-request paths.
/// Only allowlisted snapshot builders, startup reporters, and explicit non-snapshot fallbacks listed in
/// <c>Baselines/whole-catalog-read-allowlist.txt</c> with a rationale may reference them.
/// </summary>
[Trait("Category", "Security")]
public sealed class WholeCatalogReadArchitectureTests
{
    private const string BaselineFile = "whole-catalog-read-allowlist.txt";
    private const string PageReaderType = "Autheris.Application.Catalog.CatalogTablePageReader";
    private const string MetadataRepoInterface = "Autheris.Application.Common.Interfaces.ITableMetadataRepository";
    private const string GovernanceRepoType = "Autheris.Infrastructure.Persistence.GovernanceRepository";

    private static readonly Regex GeneratedName = new(@"^<+([^<>|]+)>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IEnumerable<System.Reflection.Assembly> ProductionAssemblies()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var names = new List<string>();
        foreach (var dir in new[] { "src", "modules" })
        {
            var fullDir = Path.Combine(root, dir);
            if (!Directory.Exists(fullDir))
            {
                continue;
            }

            foreach (var csproj in Directory.EnumerateFiles(fullDir, "*.csproj", SearchOption.AllDirectories))
            {
                if (csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    csproj.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    csproj.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var asmName = System.Xml.Linq.XDocument.Load(csproj)
                    .Descendants("AssemblyName")
                    .FirstOrDefault()?.Value.Trim() ?? Path.GetFileNameWithoutExtension(csproj);
                if (asmName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                names.Add(asmName);
            }
        }

        return names
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(n => System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(n)));
    }

    [Fact]
    public void WholeCatalogReads_AreLimitedToAllowlistedBuildersReportersAndFallbacks()
    {
        var allowlist = ParseAllowlist();
        var actual = FindCallSites(ProductionAssemblies().Select(CecilModuleCache.For))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        actual.ShouldNotBeEmpty("Positive control: snapshot builder allowlist entries must be detected.");

        var unauthorized = actual.Where(c => !allowlist.ContainsKey(c)).ToList();
        unauthorized.ShouldBeEmpty(
            "Un-allowlisted whole-catalog read call site(s) detected (use ICatalogSnapshotProvider or bounded keyset queries instead): "
            + string.Join(", ", unauthorized));

        var stale = allowlist.Keys.Where(k => !actual.Contains(k, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
        stale.ShouldBeEmpty(
            $"Stale entry/entries in Baselines/{BaselineFile} should be removed now that those call sites are gone: "
            + string.Join(", ", stale));
    }

    [Fact]
    public void ExternalCatalogReader_And_DiscoveryServiceScope_HaveZeroWholeCatalogReadCallSites()
    {
        var actual = FindCallSites([CecilModuleCache.For(typeof(Autheris.Application.Catalog.External.ExternalCatalogReader).Assembly)]);
        actual.Where(c => c.StartsWith("Autheris.Application.Catalog.External.ExternalCatalogReader::", StringComparison.Ordinal))
            .ShouldBeEmpty();
        actual.Where(c => c.StartsWith("Autheris.Application.Catalog.Discovery.DiscoveryService::", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Scanner_FlagsSyntheticWholeCatalogReadCallSite()
    {
        using var selfModule = ModuleDefinition.ReadModule(typeof(WholeCatalogReadArchitectureTests).Assembly.Location);
        var detected = FindCallSites([selfModule]);
        var syntheticPrefix = typeof(SyntheticWholeCatalogReaderOffender).FullName!.Replace('+', '/') + "::";
        detected.ShouldContain(c => c == syntheticPrefix + nameof(SyntheticWholeCatalogReaderOffender.ReadViaPageReaderAsync));
        detected.ShouldContain(c => c == syntheticPrefix + nameof(SyntheticWholeCatalogReaderOffender.ReadViaRepoAllAsync));
    }

    internal static List<string> FindCallSites(IEnumerable<ModuleDefinition> modules)
    {
        var hits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            foreach (var type in AllTypes(module.Types))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.Operand is MethodReference target && IsGuardedWholeCatalogRead(target))
                        {
                            hits.Add(NormalizeCaller(type, method));
                        }
                    }
                }
            }
        }

        return hits.Order(StringComparer.Ordinal).ToList();
    }

    private static bool IsGuardedWholeCatalogRead(MethodReference target)
    {
        var declaring = target.DeclaringType.GetElementType().FullName;
        return (declaring == PageReaderType && target.Name == "ReadAllAsync")
            || target.Name == "ListTablesPageAsync"
            || (target.Name == "GetAllTablesAsync" && (declaring == MetadataRepoInterface || declaring == GovernanceRepoType));
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (var t in roots)
        {
            yield return t;
            if (t.HasNestedTypes)
            {
                foreach (var nested in AllTypes(t.NestedTypes))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string NormalizeCaller(TypeDefinition declaringType, MethodDefinition method)
    {
        var root = declaringType;
        string? enclosingMethodName = null;
        while (root.DeclaringType is not null && root.Name.StartsWith('<'))
        {
            if (enclosingMethodName is null && GeneratedName.Match(root.Name) is { Success: true } typeMatch)
            {
                enclosingMethodName = typeMatch.Groups[1].Value;
            }

            root = root.DeclaringType;
        }

        var methodName = GeneratedName.Match(method.Name) is { Success: true } m
            ? m.Groups[1].Value
            : enclosingMethodName ?? method.Name;
        return $"{root.FullName}::{methodName}";
    }

    private static Dictionary<string, string> ParseAllowlist()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in RepositoryScan.ReadBaseline(BaselineFile))
        {
            var sep = line.IndexOf('|');
            sep.ShouldBeGreaterThan(0, $"Allow-list line in {BaselineFile} must use 'Type::Method | rationale': {line}");
            var key = line[..sep].Trim();
            var rationale = line[(sep + 1)..].Trim();
            key.Contains("::", StringComparison.Ordinal).ShouldBeTrue($"Key must be 'Type::Method': {line}");
            rationale.ShouldNotBeNullOrWhiteSpace($"Rationale missing for {key}");
            map.Add(key, rationale);
        }

        return map;
    }

    private sealed class SyntheticWholeCatalogReaderOffender
    {
        public static async Task<IReadOnlyList<Autheris.Domain.Model.TableMetadata>> ReadViaPageReaderAsync(
            Autheris.Application.Common.Interfaces.ITableMetadataRepository repo) =>
            await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
                repo, new Autheris.Application.Catalog.CatalogTableQuery()).ConfigureAwait(false);

        public static Task<IReadOnlyList<Autheris.Domain.Model.TableMetadata>> ReadViaRepoAllAsync(
            Autheris.Application.Common.Interfaces.ITableMetadataRepository repo) =>
            repo.GetAllTablesAsync();
    }
}
