namespace Autheris.Domain.Options.Plugins;

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

/// <summary>Authentication and authorization plugin execution limits (<c>Gateway:Plugins:Auth</c>).</summary>
public sealed class AuthPluginOptions
{
    public const string SectionName = "Gateway:Plugins:Auth";

    /// <summary>Enables authentication and authorization plugins.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum execution time in milliseconds allowed per plugin invocation.</summary>
    [Range(50, 5000)]
    public int ExecutionTimeoutMilliseconds { get; init; } = 500;

    /// <summary>Maximum number of enriched roles a plugin may attach to a single caller.</summary>
    [Range(1, 256)]
    public int MaxEnrichedRolesPerCaller { get; init; } = 64;

    /// <summary>Maximum number of enriched group SIDs a plugin may attach to a single caller.</summary>
    [Range(1, 1024)]
    public int MaxEnrichedGroupSidsPerCaller { get; init; } = 256;

    /// <summary>Allows plugins to enrich caller roles and group memberships.</summary>
    public bool AllowRoleEnrichment { get; init; } = true;

    /// <summary>Allows plugins to execute custom authorization hooks.</summary>
    public bool AllowAuthorizationHooks { get; init; } = true;

    /// <summary>Role names that authentication plugins are never permitted to grant.</summary>
    public List<string> ProhibitedRoles { get; init; } =
    [
        "ClusterAdmin",
        "GovernanceAdmin",
        "SecurityAdmin"
    ];
}
