namespace Autheris.Application.State;

using System;
using System.Threading;

/// <summary>
/// A renewed, fenced distributed lease. The holder owns the resource until it disposes the lease or
/// <see cref="LeaseLost"/> is cancelled; once it is cancelled the holder must stop all work immediately (fail closed).
/// </summary>
public interface IDistributedLease : IAsyncDisposable
{
    /// <summary>The logical resource the lease protects.</summary>
    string ResourceKey { get; }

    /// <summary>Fencing token. Strictly increasing per resource across all acquisitions; hand it to every downstream write.</summary>
    long Token { get; }

    /// <summary>Cancelled when the lease can no longer be guaranteed (stolen, expired or renewal failing for too long).</summary>
    CancellationToken LeaseLost { get; }

    /// <summary>The last time the store confirmed ownership (acquisition or successful renewal).</summary>
    DateTimeOffset LastConfirmedAt { get; }
}
