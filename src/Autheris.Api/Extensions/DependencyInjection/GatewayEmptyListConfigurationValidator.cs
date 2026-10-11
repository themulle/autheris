namespace Autheris.Api.Extensions.DependencyInjection;

using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// A JSON <c>[]</c> (or an empty environment value) for a list option binds to <c>null</c>, which makes the built-in
/// default apply. For an allow-list that default is broader than the "none" the operator wrote, so the start fails
/// loudly and names the supported way to switch the feature off. Lists whose fallback is at least as restrictive as
/// "none" only log a warning.
/// </summary>
public sealed class GatewayEmptyListConfigurationValidator : IValidateOptions<GatewayOptions>
{
    private sealed record Rule(string Path, bool FailStartup, string Guidance);

    private static readonly Rule[] Rules =
    [
        new("Authentication:ForwardAuth:AllowedRoles", true,
            "To accept no header roles, set Gateway:Authentication:ForwardAuth:Enabled to false, or list a role name that no proxy asserts."),
        new("Extensibility:BreakGlassAllowedRoles", true,
            "To disable break-glass, set Gateway:Extensibility:EnableBreakGlass to false."),
        new("SystemMetrics:AllowedRoles", true,
            "To switch the system metrics endpoints off, set Gateway:SystemMetrics:Enabled to false."),
        new("Authentication:BasicAuth:Session:AllowedEnvironments", true,
            "To disable the basic-auth session, set Gateway:Authentication:BasicAuth:Session:Enabled to false."),
        new("WebSql:CrossSource:AllowedHttpAuthModes", true,
            "To permit no federated HTTP source, set Gateway:WebSql:CrossSource:Enabled to false."),
        new("Authentication:EntraId:ReadOnlyScopes", false,
            "The default read-only scope (Agent.Read) applies, which is more restrictive than no read-only scope."),
        new("ReverseProxy:KnownNetworks", false,
            "The loopback default applies; loopback is always trusted for X-Forwarded-* headers."),
    ];

    private readonly IConfiguration _configuration;
    private readonly ILogger<GatewayEmptyListConfigurationValidator>? _logger;

    public GatewayEmptyListConfigurationValidator(
        IConfiguration configuration,
        ILogger<GatewayEmptyListConfigurationValidator>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        var gateway = _configuration.GetSection(GatewayOptions.SectionName);
        var failures = new List<string>();

        foreach (var rule in Rules)
        {
            var section = gateway.GetSection(rule.Path);
            if (!IsExplicitlyEmpty(section))
            {
                continue;
            }

            var key = $"{GatewayOptions.SectionName}:{rule.Path}";
            if (rule.FailStartup)
            {
                failures.Add($"{key} is configured as an empty list, which would silently apply the built-in default. {rule.Guidance}");
            }
            else
            {
                _logger?.LogWarning("{Key} is configured as an empty list and is treated as not configured. {Guidance}", key, rule.Guidance);
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // The JSON provider surfaces "[]" as a key with an empty string value and no children.
    private static bool IsExplicitlyEmpty(IConfigurationSection section) =>
        section.Value is { Length: 0 } && !System.Linq.Enumerable.Any(section.GetChildren());
}
