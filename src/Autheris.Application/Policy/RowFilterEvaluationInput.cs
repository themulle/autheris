namespace Autheris.Application.Policy;

/// <summary>
/// Issue #107 (SEC-RF107-14b): strict input handling shared by the REST endpoints and the MCP tools. The strategy is accepted only as the
/// exact strings <c>Exists</c> or <c>KeySetCache</c> (or absent): numeric values, numeric strings, other casings and whitespace are rejected
/// (<c>Enum.TryParse</c> would accept all of them). Range checks against the caps are done by <see cref="RowFilterEvaluationResolver.Validate"/>.
/// </summary>
public static class RowFilterEvaluationInput
{
    public const string ExistsName = "Exists";
    public const string KeySetCacheName = "KeySetCache";

    /// <summary>Builds the settings from raw input; false with a reason (no echo of the input) when the strategy is not a known name.</summary>
    public static bool TryCreate(string? strategy, int? durationSeconds, int? maxKeys, out RowFilterEvaluationSettings settings, out string? error)
    {
        settings = RowFilterEvaluationSettings.Inherit;
        error = null;

        RowFilterEvaluationStrategy? parsed;
        if (strategy is null)
        {
            parsed = null;
        }
        else if (string.Equals(strategy, ExistsName, StringComparison.Ordinal))
        {
            parsed = RowFilterEvaluationStrategy.Exists;
        }
        else if (string.Equals(strategy, KeySetCacheName, StringComparison.Ordinal))
        {
            parsed = RowFilterEvaluationStrategy.KeySetCache;
        }
        else
        {
            error = "strategy must be 'Exists', 'KeySetCache' or null.";
            return false;
        }

        settings = new RowFilterEvaluationSettings(parsed, durationSeconds, maxKeys);
        return true;
    }

    /// <summary>The name of a stored strategy; an undefined stored value is shown as <c>Invalid</c> (never the number).</summary>
    public static string? Format(RowFilterEvaluationStrategy? strategy) => strategy switch
    {
        null => null,
        RowFilterEvaluationStrategy.Exists => ExistsName,
        RowFilterEvaluationStrategy.KeySetCache => KeySetCacheName,
        _ => "Invalid"
    };
}
