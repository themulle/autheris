namespace Autheris.Domain.Options.Plugins;

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

public sealed class AuthPluginOptions
{
    public const string SectionName = "Gateway:Plugins:Auth";

    public bool Enabled { get; init; } = true;

    [Range(50, 5000)]
    public int ExecutionTimeoutMilliseconds { get; init; } = 500;

    [Range(1, 256)]
    public int MaxEnrichedRolesPerCaller { get; init; } = 64;

    [Range(1, 1024)]
    public int MaxEnrichedGroupSidsPerCaller { get; init; } = 256;

    public bool AllowRoleEnrichment { get; init; } = true;

    public bool AllowAuthorizationHooks { get; init; } = true;

    public List<string> ProhibitedRoles { get; init; } =
    [
        "ClusterAdmin",
        "GovernanceAdmin",
        "SecurityAdmin"
    ];
}
