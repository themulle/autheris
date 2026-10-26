using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;

namespace Autheris.Api.Configuration;

/// <summary>
/// ARCH-MNT-06: a key under the <c>Gateway</c> root that no options type declares is a typo or a removed option, and the binder would
/// silently ignore it, leaving the intended control at its default. This guard fails startup instead, in every environment, with no bypass.
/// It runs right after <see cref="DevConfiguration"/> (preset keys are injected there) and after <see cref="LegacySwitchGuard"/> and
/// <see cref="SerilogSectionGuard"/>, whose specific messages win. The error names keys only, never values, sorted, at most
/// <see cref="MaxListedKeys"/> of them (a mistyped key can itself be a secret, so each name is sanitised and truncated).
/// </summary>
public static class UnknownGatewayKeyGuard
{
    public const int MaxListedKeys = 20;
    public const int MaxKeyLength = 128;

    public static void ThrowIfUnknownKeys(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var unknown = FindUnknownKeys(configuration);
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(BuildMessage(unknown));
        }
    }

    /// <summary>Sorted, distinct unknown keys (full <c>Gateway:...</c> paths, shortest unknown prefix only).</summary>
    public static IReadOnlyList<string> FindUnknownKeys(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configuration.GetSection(GatewayConfigurationSchema.Root).AsEnumerable(makePathsRelative: true))
        {
            if (string.IsNullOrEmpty(entry.Key))
            {
                continue;
            }

            var segments = entry.Key.Split(':');
            var index = FirstUnknownSegment(segments);
            if (index >= 0)
            {
                unknown.Add(GatewayConfigurationSchema.Root + ":" + string.Join(':', segments.Take(index + 1)));
            }
        }

        return unknown
            .Where(k => !unknown.Any(other => k.Length > other.Length && k.StartsWith(other + ":", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The index of the first segment (relative to <c>Gateway</c>) that no type declares, or -1 when the whole key is known.</summary>
    internal static int FirstUnknownSegment(IReadOnlyList<string> segments)
    {
        var path = GatewayConfigurationSchema.Root;
        var current = new List<Type> { typeof(GatewayOptions) };
        current.AddRange(GatewayConfigurationSchema.TypesAt(path));

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var childPath = path + ":" + segment;
            if (GatewayConfigurationSchema.IsOwnedByOtherGuard(childPath))
            {
                return -1;
            }

            var next = new List<Type>();
            foreach (var type in current)
            {
                switch (GatewayConfigurationSchema.Classify(type, out var element))
                {
                    case GatewayConfigurationSchema.Shape.FreeForm:
                        return -1;
                    case GatewayConfigurationSchema.Shape.Dictionary:
                        if (element is null || GatewayConfigurationSchema.Classify(element, out _) is
                            GatewayConfigurationSchema.Shape.Scalar or GatewayConfigurationSchema.Shape.FreeForm)
                        {
                            return -1;
                        }

                        next.Add(element);
                        break;
                    case GatewayConfigurationSchema.Shape.List:
                        if (element is not null && int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                        {
                            next.Add(element);
                        }

                        break;
                    case GatewayConfigurationSchema.Shape.Complex:
                        var property = GatewayConfigurationSchema.FindProperty(type, segment);
                        if (property is not null)
                        {
                            next.Add(property.PropertyType);
                        }

                        break;
                }
            }

            next.AddRange(GatewayConfigurationSchema.TypesAt(childPath));
            if (next.Count == 0 && !GatewayConfigurationSchema.IsVirtualPrefix(childPath))
            {
                return i;
            }

            path = childPath;
            current = next;
        }

        return -1;
    }

    internal static string BuildMessage(IReadOnlyList<string> unknownKeys)
    {
        var builder = new StringBuilder();
        builder.Append("Unknown configuration keys under 'Gateway' (").Append(unknownKeys.Count).Append("): ");
        var listed = unknownKeys.Take(MaxListedKeys).Select(Sanitize).ToList();
        builder.Append(string.Join(", ", listed.Select(k => $"{k} ({k.Replace(":", "__", StringComparison.Ordinal)})")));
        if (unknownKeys.Count > MaxListedKeys)
        {
            builder.Append(" and ").Append(unknownKeys.Count - MaxListedKeys).Append(" more");
        }

        builder.Append(". Remove or correct them. The Gateway never starts with keys it does not know, because a misspelled security setting would otherwise be ignored. Only key names are listed, never values.");
        return builder.ToString();
    }

    internal static string Sanitize(string key)
    {
        var builder = new StringBuilder(Math.Min(key.Length, MaxKeyLength));
        foreach (var c in key)
        {
            if (builder.Length >= MaxKeyLength)
            {
                builder.Append("...");
                break;
            }

            builder.Append(char.IsControl(c) || char.IsSurrogate(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator ? '?' : c);
        }

        return builder.ToString();
    }
}
