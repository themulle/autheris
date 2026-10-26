using System.Reflection;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT-03 (test only): every production type lives in a namespace rooted at its assembly name. Known violators
/// are listed in Baselines/namespace-assembly-violations.txt ("assembly|type full name") and are fixed in a follow-up.
/// </summary>
public sealed class NamespaceAssemblyConsistencyTests
{
    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(Autheris.Domain.Options.GatewayOptions).Assembly,
        typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
        typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
        typeof(Autheris.GraphQL.Types.DataCatalogSyncPayload).Assembly,
        typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly,
        typeof(Program).Assembly,
        typeof(TrinoSqlEngine.ParserPooledObjectPolicy).Assembly
    ];

    [Fact]
    public void TypeNamespaces_MatchTheirAssemblyRoot()
    {
        var allowed = RepositoryScan.ReadBaseline("namespace-assembly-violations.txt").ToHashSet(StringComparer.Ordinal);
        var violations = FindViolations(ProductionAssemblies.Select(a => (a.GetName().Name!, SafeTypes(a)))).ToList();

        violations.Except(allowed, StringComparer.Ordinal)
            .ShouldBeEmpty("Types must live in a namespace rooted at their assembly name (or be added to the baseline with a reason).");
    }

    [Fact]
    public void Allowlist_HasNoStaleEntries()
    {
        var allowed = RepositoryScan.ReadBaseline("namespace-assembly-violations.txt");
        var violations = FindViolations(ProductionAssemblies.Select(a => (a.GetName().Name!, SafeTypes(a)))).ToHashSet(StringComparer.Ordinal);

        allowed.Where(a => !violations.Contains(a))
            .ShouldBeEmpty("These namespace/assembly allowlist entries are fixed or gone; remove them.");
    }

    [Fact]
    public void Scanner_DetectsPlantedViolation()
    {
        var planted = FindViolations([("Autheris.Domain", new[] { typeof(PlantedInWrongNamespace) })]).ToList();

        planted.ShouldBe(["Autheris.Domain|" + typeof(PlantedInWrongNamespace).FullName]);
        FindViolations([("Autheris.Tests.Architecture", new[] { typeof(PlantedInWrongNamespace) })]).ShouldBeEmpty();
        FindViolations([]).ShouldBeEmpty();
        ProductionAssemblies.Sum(a => SafeTypes(a).Length).ShouldBeGreaterThan(1000, "Scanner must see the production assemblies.");
    }

    private static IEnumerable<string> FindViolations(IEnumerable<(string Assembly, Type[] Types)> assemblies)
    {
        foreach (var (assembly, types) in assemblies)
        {
            foreach (var type in types)
            {
                var ns = type.Namespace;
                if (string.IsNullOrEmpty(ns) || type.IsNested || IsCompilerGenerated(type))
                {
                    continue;
                }

                if (ns != assembly && !ns.StartsWith(assembly + ".", StringComparison.Ordinal))
                {
                    yield return assembly + "|" + type.FullName;
                }
            }
        }
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.Name.Contains('<') || type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false);

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

internal sealed class PlantedInWrongNamespace;
