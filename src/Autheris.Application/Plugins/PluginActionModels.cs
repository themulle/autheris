namespace Autheris.Application.Plugins;

using System.Collections.Generic;

/// <summary>
/// Execution context provided when invoking a custom plugin action.
/// </summary>
public sealed record PluginActionContext(
    string ActionName,
    IReadOnlyDictionary<string, object?> Parameters,
    string? Principal = null,
    string? TenantId = null);

/// <summary>
/// Result returned by a plugin action execution.
/// </summary>
public sealed record PluginActionResult(
    bool Success,
    object? Data = null,
    string? ErrorMessage = null)
{
    public static PluginActionResult Succeeded(object? data = null) => new(true, data, null);
    public static PluginActionResult Failed(string errorMessage) => new(false, null, errorMessage);
}
