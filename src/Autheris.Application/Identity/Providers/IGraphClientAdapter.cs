namespace Autheris.Application.Identity.Providers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
