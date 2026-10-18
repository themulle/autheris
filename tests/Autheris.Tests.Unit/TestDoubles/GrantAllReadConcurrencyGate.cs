namespace Autheris.Tests.Unit.TestDoubles;

using System;
using Autheris.Application.Interfaces;

/// <summary>Always grants a lease. Test-only: it must never be registered outside tests (it disables the read concurrency limit).</summary>
public sealed class GrantAllReadConcurrencyGate : ITableReadConcurrencyGate
{
    public static readonly GrantAllReadConcurrencyGate Instance = new();

    public IDisposable? TryEnter(string key, int maxConcurrent) => new NoopLease();

    private sealed class NoopLease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
