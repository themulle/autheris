using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Application.Governance.Protection;

/// <summary>Pure fail-closed resolver computing effective container, table, and column protection metadata.</summary>
public static class EffectiveProtectionResolver
{
    public static EffectiveProtection Resolve(
        ContainerDefaults? container,
        ObjectProtection? table,
        IReadOnlyDictionary<string, ObjectProtection>? columnOverrides,
        IReadOnlyList<string>? columnNames)
    {
        var validContainer = container is { IsValid: true };
        var containerRef = validContainer ? container!.Container : new ContainerRef(ContainerKind.Domain, Guid.Empty);
        var ownerGroupSid = validContainer ? container!.OwnerGroupSid : null;
        var containerDefault = validContainer && container!.DefaultClass is { } dc ? dc.NormalizeOrConfidential() : ProtectionClass.Confidential;
        var visibility = validContainer ? container!.Visibility.NormalizeOrOnRequest() : CatalogVisibilityMode.OnRequest;

        var declaredTableClass = table?.Class is { } tc ? tc.NormalizeOrConfidential() : containerDefault;
        var tableMaskCategory = NormalizeMaskCategory(table?.MaskCategory);
        var normalizedOverrides = BuildCaseInsensitiveOverrides(columnOverrides);
        var orderedNames = CollectDistinctColumnNames(columnNames, normalizedOverrides);

        var rolledUpTableClass = declaredTableClass;
        var columns = new Dictionary<string, ColumnProtection>(orderedNames.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var columnName in orderedNames)
        {
            ObjectProtection? colOverride = null;
            normalizedOverrides?.TryGetValue(columnName, out colOverride);

            var overrideClass = colOverride?.Class is { } cc ? cc.NormalizeOrConfidential() : declaredTableClass;
            var colEffective = ProtectionClassExtensions.Max(declaredTableClass, overrideClass);
            var colMask = NormalizeMaskCategory(colOverride?.MaskCategory) ?? tableMaskCategory;

            columns[columnName] = new ColumnProtection(colEffective, colMask);
            rolledUpTableClass = ProtectionClassExtensions.Max(rolledUpTableClass, colEffective);
        }

        return new EffectiveProtection(containerRef, ownerGroupSid, rolledUpTableClass, declaredTableClass, tableMaskCategory, columns.AsReadOnly(), visibility);
    }

    private static Dictionary<string, ObjectProtection>? BuildCaseInsensitiveOverrides(IReadOnlyDictionary<string, ObjectProtection>? columnOverrides)
    {
        if (columnOverrides is not { Count: > 0 })
        {
            return null;
        }

        var result = new Dictionary<string, ObjectProtection>(columnOverrides.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, value) in columnOverrides)
        {
            if (string.IsNullOrWhiteSpace(rawKey) || value is null)
            {
                continue;
            }

            result[rawKey] = result.TryGetValue(rawKey, out var existing) ? MergeOverride(existing, value) : value;
        }

        return result;
    }

    private static ObjectProtection MergeOverride(ObjectProtection existing, ObjectProtection incoming)
    {
        ProtectionClass? mergedClass = (existing.Class?.NormalizeOrConfidential(), incoming.Class?.NormalizeOrConfidential()) switch
        {
            ({ } a, { } b) => ProtectionClassExtensions.Max(a, b),
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null
        };
        return new ObjectProtection(mergedClass, NormalizeMaskCategory(existing.MaskCategory) ?? NormalizeMaskCategory(incoming.MaskCategory));
    }

    private static List<string> CollectDistinctColumnNames(IReadOnlyList<string>? columnNames, IReadOnlyDictionary<string, ObjectProtection>? overrides)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        AddNames(columnNames, seen, ordered);
        if (overrides is not null)
        {
            AddNames(overrides.Keys, seen, ordered);
        }

        return ordered;
    }

    private static void AddNames(IEnumerable<string>? names, HashSet<string> seen, List<string> ordered)
    {
        if (names is null)
        {
            return;
        }

        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                ordered.Add(name);
            }
        }
    }

    private static string? NormalizeMaskCategory(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
