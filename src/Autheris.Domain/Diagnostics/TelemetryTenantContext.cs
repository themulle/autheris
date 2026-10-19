namespace Autheris.Domain.Diagnostics;

using System;
using System.Threading;
using Autheris.Domain.Common;

/// <summary>
/// Ambient tenant context for telemetry enrichment, scoped per asynchronous execution path.
/// Controlled strictly by SecurityContextResolutionMiddleware and background work loops.
/// </summary>
public static class TelemetryTenantContext
{
    private static readonly AsyncLocal<string?> CurrentTenant = new();

    /// <summary>
    /// Gets the ambient tenant id, if one has been bound to the current execution context.
    /// </summary>
    public static string? CurrentTenantId => CurrentTenant.Value;

    /// <summary>
    /// Enters an ambient tenant scope. Disposing the returned handle restores the prior tenant context.
    /// </summary>
    public static IDisposable Enter(string? tenantId)
    {
        var prior = CurrentTenant.Value;
        CurrentTenant.Value = tenantId;
        return new Scope(prior);
    }

    /// <summary>
    /// Enters an ambient tenant scope for the specified validated <see cref="TenantId"/>.
    /// </summary>
    public static IDisposable Enter(TenantId tenantId) => Enter(tenantId.Value);

    private sealed class Scope(string? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                CurrentTenant.Value = prior;
                _disposed = true;
            }
        }
    }
}
