using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>
/// Issue #281: runtime-managed trusted GraphQL documents bound to an app identity (<c>X-Autheris-App: appId/version</c>) and an allowed
/// Origin. Requires <c>GraphQL:PersistedQueriesOnly</c>; the global directory path stays unchanged for requests without the header.
/// </summary>
public sealed class AppScopedTrustedDocumentsOptions
{
    public const string DefaultHeaderName = "X-Autheris-App";

    public bool Enabled { get; init; }

    /// <summary>Name of the app identity request header (an RFC 7230 token).</summary>
    [RegularExpression("^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,64}$")]
    public string HeaderName { get; init; } = DefaultHeaderName;

    /// <summary>Maximum number of document hashes of one app version (SEC-281-8).</summary>
    [Range(1, 5000)] public int MaxHashesPerVersion { get; init; } = 500;

    [Range(1, 200)] public int MaxActiveVersionsPerApp { get; init; } = 20;

    [Range(1, 50)] public int MaxAllowedOriginsPerApp { get; init; } = 10;

    /// <summary>Interval in seconds in which each instance checks the generation counter for changes made by other instances.</summary>
    [Range(1, 60)] public int CacheRefreshSeconds { get; init; } = 5;
}
