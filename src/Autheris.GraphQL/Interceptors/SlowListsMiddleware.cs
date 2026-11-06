namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Options;
using HotChocolate.Execution;
using Microsoft.Extensions.Options;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// Hot Chocolate request pipeline middleware that attaches extensions.slowLists containing
/// ranked nested list paths that exceeded execution thresholds (09..11).
/// </summary>
public sealed class SlowListsMiddleware
{
    public const string ContextKey = "Autheris.SlowListsAccumulator";
    private readonly RequestDelegate _next;
    private readonly IOptions<GraphQLOptions> _options;

    public SlowListsMiddleware(RequestDelegate next, IOptions<GraphQLOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        var accumulator = new SlowListsAccumulator();
        context.ContextData[ContextKey] = accumulator;

        var sw = Stopwatch.StartNew();
        await _next(context).ConfigureAwait(false);
        sw.Stop();

        var slowListOptions = _options.Value?.SlowLists;
        if (slowListOptions is null || !slowListOptions.Enabled)
        {
            return;
        }

        if (sw.ElapsedMilliseconds < slowListOptions.ThresholdMs)
        {
            return;
        }

        if (!context.ContextData.TryGetValue("Autheris.ExposeCostExtensions", out var exposeObj) || exposeObj is not true)
        {
            return;
        }

        if (context.Result is not OperationResult opResult)
        {
            return;
        }

        if (opResult.Errors is not null && opResult.Errors.Count > 0)
        {
            return;
        }

        var candidates = accumulator.BuildCandidates(sw.Elapsed);
        if (candidates.Count == 0)
        {
            return;
        }

        var maxEntries = Math.Clamp(slowListOptions.MaxEntries, 1, 5);
        var top = candidates.Take(maxEntries).ToList();

        var slowLists = new List<Dictionary<string, object?>>(top.Count);
        for (int i = 0; i < top.Count; i++)
        {
            slowLists.Add(new Dictionary<string, object?>
            {
                ["path"] = top[i].Path,
                ["rank"] = i + 1
            });
        }

        var extensions = opResult.Extensions ?? HotChocolate.Collections.Immutable.ImmutableOrderedDictionary<string, object?>.Empty;
        opResult.Extensions = extensions.SetItem("slowLists", slowLists);
    }
}

/// <summary>
/// Accumulator collecting tree execution reports for slow-list ranking across root fields.
/// </summary>
public sealed class SlowListsAccumulator
{
    private readonly ConcurrentBag<(string RootResponseName, TreeExecutionReport Report)> _reports = new();

    public ITreeExecutionObserver ForRoot(string rootResponseName) => new RootObserver(this, rootResponseName);

    public void AddReport(string rootResponseName, TreeExecutionReport report)
    {
        _reports.Add((rootResponseName, report));
    }

    private sealed class RootObserver(SlowListsAccumulator accumulator, string rootResponseName) : ITreeExecutionObserver
    {
        public void OnReport(TreeExecutionReport report) => accumulator.AddReport(rootResponseName, report);
    }

    internal sealed record Candidate(
        string Path,
        int StaticPriority,
        long RowWeight,
        TimeSpan Elapsed);

    internal List<Candidate> BuildCandidates(TimeSpan requestElapsed)
    {
        var candidates = new List<Candidate>();
        var halfElapsed = TimeSpan.FromMilliseconds(requestElapsed.TotalMilliseconds * 0.5);

        foreach (var (rootName, report) in _reports)
        {
            foreach (var list in report.Lists)
            {
                var fullPath = rootName + "." + string.Join(".", list.Path);
                int priority = list.Strategy switch
                {
                    TreeListStrategy.CorrelatedUnindexed => 1,
                    TreeListStrategy.SetBased => 2,
                    _ => 3
                };
                long weight = (long)report.ParentPageSize * list.Limit;
                candidates.Add(new Candidate(fullPath, priority, weight, report.StatementElapsed));
            }

            foreach (var cross in report.CrossSourceExecutions)
            {
                var fullPath = rootName + "." + string.Join(".", cross.Path);
                bool dominant = cross.Elapsed >= halfElapsed;
                int priority = dominant ? 0 : 1;
                long weight = (long)report.ParentPageSize * cross.Limit;
                candidates.Add(new Candidate(fullPath, priority, weight, cross.Elapsed));
            }
        }

        candidates.Sort((a, b) =>
        {
            if (a.StaticPriority != b.StaticPriority)
            {
                return a.StaticPriority.CompareTo(b.StaticPriority);
            }
            if (a.StaticPriority == 0)
            {
                var elapsedCmp = b.Elapsed.CompareTo(a.Elapsed);
                if (elapsedCmp != 0) return elapsedCmp;
            }
            var weightCmp = b.RowWeight.CompareTo(a.RowWeight);
            if (weightCmp != 0) return weightCmp;

            return string.Compare(a.Path, b.Path, StringComparison.Ordinal);
        });

        return candidates;
    }
}
