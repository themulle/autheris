using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Autheris.Api.Hosting.Warmup;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Options.Observability;
using Autheris.Domain.Options.Plugins;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Configuration;

namespace Autheris.Api.Configuration;

/// <summary>
/// ARCH-MNT-06: the typed shape of the <c>Gateway</c> configuration root, used by <see cref="UnknownGatewayKeyGuard"/>. The root binds to
/// <see cref="GatewayOptions"/>; a number of subsections are additionally bound to their own options types (side-bound), and several
/// intermediate nodes (<c>Gateway:Startup</c>) exist only through those. A key is known when some type bound at its parent path declares it.
/// </summary>
public static class GatewayConfigurationSchema
{
    public const string Root = "Gateway";

    /// <summary>Side-bound <c>(path, type)</c> pairs. Several types may share one path (their properties are merged).</summary>
    public static IReadOnlyList<(string Path, Type Type)> SideBound { get; } =
    [
        ("Gateway:Startup:ColdPathTiming", typeof(ColdPathTimingOptions)),
        ("Gateway:Startup:Warmup", typeof(StartupWarmupOptions)),
        ("Gateway:Catalog", typeof(CatalogDiscoveryOptions)),
        ("Gateway:Catalog", typeof(CatalogLocalizationOptions)),
        ("Gateway:CatalogSearch", typeof(CatalogSearchOptions)),
        ("Gateway:CdcWebhooks", typeof(CdcWebhooksOptions)),
        ("Gateway:Casbin:DecisionCache", typeof(CasbinDecisionCacheOptions)),
        ("Gateway:DataSources:Configuration", typeof(DataSourceConfigurationOptions)),
        ("Gateway:Events", typeof(EventBusOptions)),
        ("Gateway:ExtensibilityPipeline", typeof(ExtensibilityPipelineOptions)),
        ("Gateway:GdprReport", typeof(GdprReportOptions)),
        ("Gateway:Governance:AccessPlanning", typeof(AccessPlanOptions)),
        ("Gateway:Governance:QueryAnalysis", typeof(GovernanceQueryAnalysisOptions)),
        ("Gateway:Governance:IndividualGrants", typeof(IndividualGrantOptions)),
        ("Gateway:Governance:RestrictedScopes", typeof(RestrictedScopeOptions)),
        ("Gateway:Plugins:Auth", typeof(AuthPluginOptions)),
        ("Gateway:Telemetry", typeof(TelemetryOptions)),
        ("Gateway:Subscriptions", typeof(RevocationWatchOptions)),
        ("Gateway:Audit:Retention", typeof(AuditRetentionOptions)),
        ("Gateway:RowFilters:KeySetCache", typeof(RowFilterKeySetCacheOptions)),
        ("Gateway:State", typeof(StateOptions)),
        ("Gateway:TableCache", typeof(TableCacheOptions)),
        ("Gateway:AsyncJobs", typeof(AsyncJobOptions)),
        ("Gateway:Tenancy", typeof(TenancyOptions))
    ];

    /// <summary>
    /// Keys (and subtrees) that are not options properties but are owned by a dedicated guard or validator that reports a specific message.
    /// The unknown-key guard must never pre-empt those messages.
    /// </summary>
    public static IReadOnlyList<string> OwnedByOtherGuards { get; } =
    [
        "Gateway:WebSql:SqlRewriterEngine",
        "Gateway:WebSql:PlanCache",
        "Gateway:WebSql:CrossSource:MaxMemory",
        "Gateway:WebSql:CrossSource:MaxTempDirectorySize",
        "Gateway:Telemetry:Otlp:Headers"
    ];

    /// <summary>
    /// Keys that production code reads straight from <see cref="IConfiguration"/> without an options property. They are known to the guard;
    /// <c>SourceConfigurationKeyLiterals_AreKnownToTheSchema</c> fails when a raw read is missing here.
    /// </summary>
    public static IReadOnlyList<string> RawReadKeys { get; } =
    [
        "Gateway:GovernanceDb:AuditHmacKey",
        "Gateway:Audit:AuditHmacKey",
        "Gateway:Audit:ActorPseudonymizationKey",
        "Gateway:Cluster:Secret",
        // DefaultEnvironmentSecretProvider resolves these well-known secret references from configuration.
        "Gateway:DataMasking:HmacSecret",
        "Gateway:Itsm:WebhookSecret",
        "Gateway:Secrets:VaultHostSuffixes"
    ];

    /// <summary>
    /// The only subtrees (configuration paths) whose content is not checked key by key (<c>object</c>, <c>IConfigurationSection</c>,
    /// dictionaries with scalar values). None may sit under a security root; <c>ARCH-MNT-06-T-08</c> pins this list.
    /// </summary>
    public static IReadOnlyList<string> FreeFormSubtrees() => FindFreeForm();

    internal static IReadOnlyList<Type> TypesAt(string path) =>
        SideBound.Where(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)).Select(s => s.Type).ToList();

    internal static bool IsVirtualPrefix(string path) =>
        SideBound.Select(s => s.Path).Concat(OwnedByOtherGuards).Concat(RawReadKeys)
            .Any(known => known.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase));

    internal static bool IsOwnedByOtherGuard(string path) =>
        OwnedByOtherGuards.Concat(RawReadKeys).Any(o => string.Equals(path, o, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(o + ":", StringComparison.OrdinalIgnoreCase));

    internal enum Shape { Scalar, Complex, List, Dictionary, FreeForm }

    internal static Shape Classify(Type type, out Type? element)
    {
        element = null;
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(object) || typeof(IConfiguration).IsAssignableFrom(type) || type.FullName?.StartsWith("System.Text.Json.", StringComparison.Ordinal) == true)
        {
            return Shape.FreeForm;
        }

        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
            || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri)
            || type == typeof(Version) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Type))
        {
            return Shape.Scalar;
        }

        var dictionary = DictionaryInterface(type);
        if (dictionary is not null)
        {
            element = dictionary.GetGenericArguments()[1];
            return Shape.Dictionary;
        }

        if (type.IsArray)
        {
            element = type.GetElementType();
            return Shape.List;
        }

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null)
        {
            element = enumerable.GetGenericArguments()[0];
            return Shape.List;
        }

        if (type.IsValueType && type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Length == 0)
        {
            return Shape.Scalar;
        }

        return Shape.Complex;
    }

    internal static PropertyInfo? FindProperty(Type type, string name) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 0 && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static Type? DictionaryInterface(Type type)
    {
        var candidates = type.IsInterface ? type.GetInterfaces().Append(type) : type.GetInterfaces();
        return candidates.FirstOrDefault(i => i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
    }

    private static IReadOnlyList<string> FindFreeForm()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        Walk(typeof(GatewayOptions), Root, found, visited);
        foreach (var (path, type) in SideBound)
        {
            Walk(type, path, found, visited);
        }

        return found.ToList();
    }

    private static void Walk(Type type, string path, SortedSet<string> found, HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            return;
        }

        try
        {
            WalkProperties(type, path, found, visited);
        }
        finally
        {
            visited.Remove(type);
        }
    }

    private static void WalkProperties(Type type, string path, SortedSet<string> found, HashSet<Type> visited)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var propertyPath = path + ":" + property.Name;
            var current = property.PropertyType;
            for (var depth = 0; depth < 4; depth++)
            {
                var shape = Classify(current, out var element);
                if (shape == Shape.FreeForm || (shape == Shape.Dictionary && element is not null && Classify(element, out _) is Shape.Scalar or Shape.FreeForm))
                {
                    found.Add(propertyPath);
                    break;
                }

                if (shape == Shape.Complex)
                {
                    Walk(Nullable.GetUnderlyingType(current) ?? current, propertyPath, found, visited);
                    break;
                }

                if (shape is Shape.List or Shape.Dictionary && element is not null)
                {
                    current = element;
                    continue;
                }

                break;
            }
        }
    }
}
