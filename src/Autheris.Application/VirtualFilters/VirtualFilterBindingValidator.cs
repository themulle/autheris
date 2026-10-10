namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>A binding option is rejected when the profile is written; <see cref="Code"/> is a stable <see cref="VirtualFilterErrorCodes"/> value.</summary>
public sealed class VirtualFilterBindingException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Write-time checks of the federation options of a binding (<c>strategy</c>, <c>pushdown_format</c>,
/// <c>max_pushdown_keys</c>). They fail closed: an invalid value is a 400 and nothing is stored. The format check reuses
/// <see cref="HttpKeyPushdownCapability"/>, the logic the planner applies at query time; the planner still decides per query.
/// </summary>
public static class VirtualFilterBindingValidator
{
    private static readonly IReadOnlyDictionary<VirtualFilterExecutionStrategy, string> StrategyNames = new Dictionary<VirtualFilterExecutionStrategy, string>
    {
        [VirtualFilterExecutionStrategy.Adaptive] = "adaptive",
        [VirtualFilterExecutionStrategy.ShortCircuitOnly] = "short_circuit_only",
        [VirtualFilterExecutionStrategy.DuckDbHashJoin] = "duckdb_hash_join",
        [VirtualFilterExecutionStrategy.PushdownOnly] = "pushdown_only"
    };

    private static readonly IReadOnlyDictionary<PushdownParameterFormat, string> FormatNames = new Dictionary<PushdownParameterFormat, string>
    {
        [PushdownParameterFormat.CommaSeparated] = "comma_separated",
        [PushdownParameterFormat.RepeatedParam] = "repeated_param",
        [PushdownParameterFormat.ODataIn] = "odata_in",
        [PushdownParameterFormat.PostBatch] = "post_batch"
    };

    private static readonly IReadOnlyDictionary<UncoveredPolicy, string> UncoveredNames = new Dictionary<UncoveredPolicy, string>
    {
        [UncoveredPolicy.Deny] = "deny",
        [UncoveredPolicy.Skip] = "skip"
    };

    private static IReadOnlyDictionary<T, string> NamesOf<T>() where T : struct, Enum =>
        (IReadOnlyDictionary<T, string>?)(typeof(T) switch
        {
            var t when t == typeof(VirtualFilterExecutionStrategy) => (object)StrategyNames,
            var t when t == typeof(PushdownParameterFormat) => FormatNames,
            var t when t == typeof(UncoveredPolicy) => UncoveredNames,
            _ => null
        }) ?? throw new NotSupportedException($"{typeof(T).Name} has no binding name map.");

    /// <summary>
    /// Parses a binding enum from its documented snake_case name (<c>pushdown_only</c>). Only the exact name is accepted:
    /// no other case, no stripped underscores, no numbers. <paramref name="ignoreCase"/> relaxes only the casing (kept for the
    /// pre-existing <c>uncovered</c> field). Anything else is <see cref="VirtualFilterErrorCodes.BindingInvalid"/>.
    /// </summary>
    public static T ParseEnum<T>(string value, string field, bool ignoreCase = false) where T : struct, Enum
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var (candidate, name) in NamesOf<T>())
        {
            if (string.Equals(name, value, comparison))
            {
                return candidate;
            }
        }

        throw new VirtualFilterBindingException(
            VirtualFilterErrorCodes.BindingInvalid,
            $"Unknown {field} '{value}' ({string.Join(", ", NamesOf<T>().Values)}).");
    }

    /// <summary>The documented snake_case name of a binding enum value (the inverse of <see cref="ParseEnum{T}"/>).</summary>
    public static string FormatEnum<T>(T value) where T : struct, Enum =>
        NamesOf<T>().TryGetValue(value, out var name)
            ? name
            : throw new InvalidOperationException($"{typeof(T).Name}.{value} has no binding name.");

    /// <param name="profiles">Profiles about to be written (API, file repository sync, dbt import).</param>
    /// <param name="federation">The gateway-wide <c>Gateway:VirtualFilters:Federation</c> options.</param>
    /// <param name="catalog">Catalog used to check an exact target's endpoint capability; null skips that check.</param>
    public static async Task ValidateAsync(IEnumerable<VirtualFilterAccessProfile> profiles, VirtualFilterFederationOptions federation, ITableMetadataRepository? catalog, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(federation);
        int gatewayLimit = federation.MaxPushdownKeys;

        foreach (var profile in profiles)
        {
            foreach (var binding in profile.Bindings)
            {
                if (binding.MaxPushdownKeys < 1 || binding.MaxPushdownKeys > FilterBinding.HardMaxPushdownKeys || ExceedsGatewayLimit(binding, gatewayLimit))
                {
                    throw new VirtualFilterBindingException(
                        VirtualFilterErrorCodes.PushdownKeyBudgetInvalid,
                        $"max_pushdown_keys of '{binding.FilterName}' must be between 1 and {Math.Min(FilterBinding.HardMaxPushdownKeys, gatewayLimit)}.");
                }

                if (binding.PushdownFormat is { } format)
                {
                    await ValidateFormatAsync(binding, format, catalog, ct).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// The gateway-wide limit is a request limit: a binding that does not set <c>max_pushdown_keys</c> carries the model
    /// default and is capped by the planner at query time, so it must not fail at write time. Only a value other than the
    /// default is treated as an explicit request.
    /// </summary>
    private static bool ExceedsGatewayLimit(FilterBinding binding, int gatewayLimit) =>
        binding.MaxPushdownKeys != FilterBinding.DefaultMaxPushdownKeys && binding.MaxPushdownKeys > gatewayLimit;

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
