namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>A binding option is rejected when the profile is written; <see cref="Code"/> is a stable <see cref="VirtualFilterErrorCodes"/> value.</summary>
internal sealed class VirtualFilterBindingException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Write-time checks of the federation options of a binding (<c>strategy</c>, <c>pushdown_format</c>,
/// <c>max_pushdown_keys</c>). They fail closed: an invalid value is a 400 and nothing is stored. The format check reuses
/// <see cref="HttpKeyPushdownCapability"/>, the logic the planner applies at query time; the planner still decides per query.
/// </summary>
internal static class VirtualFilterBindingValidator
{
    /// <summary>Parses a snake_case enum value (<c>pushdown_only</c>); anything else, including numbers, is rejected.</summary>
    public static T ParseEnum<T>(string value, string field) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(FormatEnum(candidate).Replace("_", string.Empty, StringComparison.Ordinal), value.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        var allowed = string.Join(", ", Array.ConvertAll(Enum.GetValues<T>(), FormatEnum));
        throw new ArgumentException($"Unknown {field} '{value}' ({allowed}).");
    }

    public static string FormatEnum<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }

    public static async Task ValidateAsync(IEnumerable<VirtualFilterAccessProfile> profiles, HttpContext context)
    {
        var services = context.RequestServices;
        int gatewayLimit = (services.GetService<IOptions<GatewayOptions>>()?.Value.VirtualFilters?.Federation ?? new VirtualFilterFederationOptions()).MaxPushdownKeys;
        var catalog = services.GetService<ITableMetadataRepository>();

        foreach (var profile in profiles)
        {
            foreach (var binding in profile.Bindings)
            {
                if (binding.MaxPushdownKeys < 1 || binding.MaxPushdownKeys > FilterBinding.HardMaxPushdownKeys || binding.MaxPushdownKeys > gatewayLimit)
                {
                    throw new VirtualFilterBindingException(
                        VirtualFilterErrorCodes.PushdownKeyBudgetInvalid,
                        $"max_pushdown_keys of '{binding.FilterName}' must be between 1 and {Math.Min(FilterBinding.HardMaxPushdownKeys, gatewayLimit)}.");
                }

                if (binding.PushdownFormat is { } format)
                {
                    await ValidateFormatAsync(binding, format, catalog, context.RequestAborted).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task ValidateFormatAsync(FilterBinding binding, PushdownParameterFormat format, ITableMetadataRepository? catalog, CancellationToken ct)
    {
        if (!HttpKeyPushdownCapability.CanEverSatisfy(format))
        {
            throw Unsupported(binding, $"no endpoint can send keys as '{FormatEnum(format)}'");
        }

        // An exact target names one endpoint, so its declared capability can be checked now. A pattern can match many
        // endpoints; the planner then decides per table (format_unsupported: Tier 3, or a denial for pushdown_only).
        if (catalog == null || binding.TargetPattern is not { } target || TargetPatternMatcher.IsRegex(target) ||
            target.Contains('*') || target.Contains('?') || !TableIdentifier.TryParse(target, out var table))
        {
            return;
        }

        var metadata = await catalog.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        if (metadata?.HttpEndpoint != null && !HttpKeyPushdownCapability.Resolve(metadata.HttpEndpoint).Satisfies(format))
        {
            throw Unsupported(binding, $"the endpoint of '{target}' cannot send keys as '{FormatEnum(format)}'");
        }
    }

    private static VirtualFilterBindingException Unsupported(FilterBinding binding, string reason) =>
        new(VirtualFilterErrorCodes.PushdownFormatUnsupported, $"pushdown_format of '{binding.FilterName}' is not supported: {reason}.");
}
