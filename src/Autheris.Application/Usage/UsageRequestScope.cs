namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;

public sealed class UsageRequestScope : IDisposable
{
    public const int MaxFacts = 64;

    private static readonly AsyncLocal<UsageRequestScope?> CurrentScope = new();

    private readonly UsageRequestScope? _previous;
    private readonly IUsageAggregator? _aggregator;
    private readonly object _lock = new();
    private readonly UsageFact[] _facts = new UsageFact[MaxFacts];
    private int _factCount;
    private int _completed;
    private bool _disposed;

    public static UsageRequestScope? Current => CurrentScope.Value;

    public static IUsageAggregator? DefaultAggregator { get; set; }

    public UsageIdentity Identity { get; }

    public DataSourceTransportFlags Transport { get; }

    public CallerContext? Caller { get; }

    public bool IsCompleted => Volatile.Read(ref _completed) == 1;

    public int FactCount
    {
        get
        {
            lock (_lock)
            {
                return _factCount;
            }
        }
    }

    public IReadOnlyList<UsageFact> Facts
    {
        get
        {
            lock (_lock)
            {
                return _facts.Take(_factCount).ToArray();
            }
        }
    }

    private UsageRequestScope(
        UsageIdentity identity,
        DataSourceTransportFlags transport,
        CallerContext? caller,
        IUsageAggregator? aggregator)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Transport = transport;
        Caller = caller;
        _aggregator = aggregator ?? DefaultAggregator;
        _previous = CurrentScope.Value;
        CurrentScope.Value = this;
    }

    public static UsageRequestScope Begin(
        UsageIdentity identity,
        DataSourceTransportFlags transport,
        CallerContext? caller = null,
        IUsageAggregator? aggregator = null)
    {
        return new UsageRequestScope(identity, transport, caller, aggregator);
    }

    public void AddFact(in UsageFact fact)
    {
        lock (_lock)
        {
            if (_factCount < MaxFacts)
            {
                _facts[_factCount++] = fact;
            }
            else
            {
                int targetIndex = -1;
                for (int i = _factCount - 1; i >= 0; i--)
                {
                    if (_facts[i].Table.Equals(fact.Table))
                    {
                        targetIndex = i;
                        break;
                    }
                }

                if (targetIndex < 0)
                {
                    targetIndex = MaxFacts - 1;
                }

                _facts[targetIndex] = _facts[targetIndex].MergeWith(fact);
            }
        }
    }

    public void Complete(long durationMs, long bytes, int statusCode)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        UsageFact[] factsSnapshot;
        lock (_lock)
        {
            factsSnapshot = _facts.Take(_factCount).ToArray();
        }

        _aggregator?.Record(Identity, Transport, Caller, durationMs, bytes, statusCode, factsSnapshot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CurrentScope.Value = _previous;
    }
}
