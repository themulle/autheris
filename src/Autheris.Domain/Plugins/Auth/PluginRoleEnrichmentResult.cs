namespace Autheris.Domain.Plugins.Auth;

using System.Collections.Generic;
using Autheris.Domain.Common;

public sealed record PluginRoleEnrichmentResult
{
    public static readonly PluginRoleEnrichmentResult Empty = new([], []);

    public IReadOnlyCollection<string> AddedRoles { get; init; }
    public IReadOnlyCollection<Sid> AddedGroupSids { get; init; }

    public PluginRoleEnrichmentResult(
        IReadOnlyCollection<string>? addedRoles = null,
        IReadOnlyCollection<Sid>? addedGroupSids = null)
    {
        AddedRoles = addedRoles ?? [];
        AddedGroupSids = addedGroupSids ?? [];
    }
}
