namespace Autheris.Application.Identity.Providers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Graph access for the group resolution. Implementations call Graph with the object id and tenant of the validated token only, follow
/// <c>@odata.nextLink</c> on the configured Graph host only, and return for each transitive group its <c>onPremisesSecurityIdentifier</c>
/// or, for a cloud-only group or a synced group without one, its object id (GUID, <c>D</c> format).
/// </summary>
public interface IGraphClientAdapter
{
    Task<IReadOnlyList<string>> GetTransitiveMemberOfAsync(string userKey, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAppRoleAssignmentsAsync(string userKey, CancellationToken ct = default);
}

public sealed class GraphCircuitBreakerOpenException : Exception
{
    public GraphCircuitBreakerOpenException(string message) : base(message) { }
}

public sealed class GraphThrottledException : Exception
{
    public TimeSpan RetryAfter { get; }

    public GraphThrottledException(TimeSpan retryAfter, string message) : base(message)
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>Placeholder until a production Graph adapter is configured: every call fails, so Graph-dependent resolutions are degraded (fail closed).</summary>
public sealed class UnconfiguredGraphClientAdapter : IGraphClientAdapter
{
    public Task<IReadOnlyList<string>> GetTransitiveMemberOfAsync(string userKey, CancellationToken ct = default) =>
        throw new InvalidOperationException("No Graph client adapter is configured.");

    public Task<IReadOnlyList<string>> GetAppRoleAssignmentsAsync(string userKey, CancellationToken ct = default) =>
        throw new InvalidOperationException("No Graph client adapter is configured.");
}
