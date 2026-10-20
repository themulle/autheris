namespace Autheris.Domain.Plugins.Auth;

using System.Collections.Generic;

public sealed record PluginAuthorizationResult
{
    public required PluginAuthorizationOutcome Outcome { get; init; }
    public string? ReasonCode { get; init; }
    public string? ClientMessage { get; init; }
    public IReadOnlyCollection<string>? DeniedColumns { get; init; }
    public string? AdditionalRowFilterSql { get; init; }
    public string? HookName { get; init; }

    public static PluginAuthorizationResult Allow(string? hookName = null) =>
        new() { Outcome = PluginAuthorizationOutcome.Allow, HookName = hookName };

    public static PluginAuthorizationResult Deny(string reasonCode, string? clientMessage = null, string? hookName = null) =>
        new()
        {
            Outcome = PluginAuthorizationOutcome.Deny,
            ReasonCode = reasonCode,
            ClientMessage = clientMessage,
            HookName = hookName
        };

    public static PluginAuthorizationResult Restrict(
        IReadOnlyCollection<string>? deniedColumns = null,
        string? additionalRowFilterSql = null,
        string? hookName = null) =>
        new()
        {
            Outcome = PluginAuthorizationOutcome.Restrict,
            DeniedColumns = deniedColumns,
            AdditionalRowFilterSql = additionalRowFilterSql,
            HookName = hookName
        };
}
