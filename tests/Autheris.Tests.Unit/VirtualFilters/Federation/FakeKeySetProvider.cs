namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Key set source for planner and pipeline tests (F-GOV-14 WI-6): authorized key tuples per filter name, a call log,
/// an optional delay (honours the cancellation token unless <see cref="IgnoreCancellation"/>) and an optional failure.
/// </summary>
internal sealed class FakeKeySetProvider : IVirtualFilterKeySetProvider
{
    private readonly ConcurrentDictionary<string, object?[][]> _tuples = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Call> _calls = new();

    public TimeSpan Delay { get; set; }

    public bool IgnoreCancellation { get; set; }

    public Exception? Failure { get; set; }

    public IReadOnlyList<Call> Calls => _calls.ToArray();

    public IReadOnlyList<string> CalledFilters => _calls.Select(c => c.FilterName).ToArray();

    public FakeKeySetProvider With(string filterName, params object?[][] tuples)
    {
        _tuples[filterName] = tuples;
        return this;
    }

    public async ValueTask<VirtualFilterKeySet> GetAsync(
        VirtualFilter filter,
        IReadOnlyList<VirtualFilterKeyType> keyTypes,
        TenantId tenant,
        CancellationToken ct = default)
    {
        _calls.Enqueue(new Call(filter.Name, keyTypes.ToArray(), tenant));
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, IgnoreCancellation ? CancellationToken.None : ct);
        }

        if (Failure != null)
        {
            throw Failure;
        }

        return VirtualFilterKeySet.Create(keyTypes, _tuples.GetValueOrDefault(filter.Name) ?? []);
    }

    internal sealed record Call(string FilterName, VirtualFilterKeyType[] KeyTypes, TenantId Tenant);
}
