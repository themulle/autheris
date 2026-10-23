namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Domain.Connectors;
using Autheris.Domain.Model;

/// <summary>Request counters of one path lookup read (audit only; never carries key values).</summary>
internal sealed class PathLookupStats
{
    private int _requests;
    private int _keysNotFound;

    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Keys the upstream answered with 404 (key not present).</summary>
    public int KeysNotFound => Volatile.Read(ref _keysNotFound);

    internal void Request() => Interlocked.Increment(ref _requests);

    internal void NotFound() => Interlocked.Increment(ref _keysNotFound);
}

/// <summary>
/// F-GOV-14 (CR-01, principle 1): reads a path-lookup endpoint so that the rows no longer depend on what the upstream
/// does with the key. One request is sent per authorized key (throttled); each response is restricted to the rows whose
/// key equals the requested key, and the union is de-duplicated by that key. An upstream that ignores the key and answers
/// every request with all rows therefore yields exactly the rows a full read would, and a key the upstream does not know
/// (404) simply contributes no row. Every other failure fails the read. The gateway enforcement still filters the result.
/// </summary>
internal sealed class PathLookupConnector : IAutherisConnector
{
    private readonly IAutherisConnector _inner;
    private readonly VirtualFilterPathLookup _lookup;
    private readonly int _maxConcurrency;

    public PathLookupConnector(IAutherisConnector inner, VirtualFilterPathLookup lookup, int maxConcurrency)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _maxConcurrency = Math.Max(1, maxConcurrency);
        RecordSource = new LookupRecordSource(this);
    }

    public PathLookupStats Stats { get; } = new();

    public string ConnectorId => _inner.ConnectorId;

    public string ConnectorType => _inner.ConnectorType;

    public ConnectorCapabilities Capabilities => _inner.Capabilities;

    public IConnectorMetadata Metadata => _inner.Metadata;

    public IConnectorSplitManager SplitManager => _inner.SplitManager;

    public IConnectorRecordSource RecordSource { get; }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class LookupRecordSource(PathLookupConnector owner) : IConnectorRecordSource
    {
        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadSplitAsync(
            ConnectorSplit split,
            ConnectorSessionContext session,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var row in await ReadBatchAsync(split, session, ct).ConfigureAwait(false))
            {
                yield return row;
            }
        }

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadBatchAsync(
            ConnectorSplit split, ConnectorSessionContext session, CancellationToken ct = default)
        {
            var lookup = owner._lookup;
            using var throttle = new SemaphoreSlim(owner._maxConcurrency, owner._maxConcurrency);
            var perKey = new List<IReadOnlyDictionary<string, object?>>?[lookup.Values.Count];

            // The first failure cancels the sibling requests still in flight (CR-18).
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = new Task[lookup.Values.Count];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = ReadKeyGuardedAsync(i, split, session, throttle, perKey, linked);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // VF-07: the values are distinct, but one key may match several upstream rows (a full fetch returns all of them),
            // so every matching row of every key is kept.
            var rows = new List<IReadOnlyDictionary<string, object?>>(perKey.Length);
            foreach (var keyRows in perKey)
            {
                if (keyRows != null)
                {
                    rows.AddRange(keyRows);
                }
            }

            return rows;
        }

        private async Task ReadKeyGuardedAsync(
            int index,
            ConnectorSplit split,
            ConnectorSessionContext session,
            SemaphoreSlim throttle,
            List<IReadOnlyDictionary<string, object?>>?[] results,
            CancellationTokenSource linked)
        {
            try
            {
                await ReadKeyAsync(index, split, session, throttle, results, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await linked.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task ReadKeyAsync(
            int index,
            ConnectorSplit split,
            ConnectorSessionContext session,
            SemaphoreSlim throttle,
            List<IReadOnlyDictionary<string, object?>>?[] results,
            CancellationToken ct)
        {
            var lookup = owner._lookup;
            var key = lookup.Values[index];
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var arguments = new Dictionary<string, object?>(session.Arguments, StringComparer.OrdinalIgnoreCase)
                {
                    [lookup.Argument] = key
                };

                // Own copy of the items per request: the shared dictionary is not thread-safe (CR-18).
                var single = session with { Arguments = arguments, Items = new Dictionary<string, object?>(session.Items) };
                owner.Stats.Request();
                IReadOnlyList<IReadOnlyDictionary<string, object?>> batch;
                try
                {
                    batch = await owner._inner.RecordSource.ReadBatchAsync(split, single, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // The upstream does not know this key: no row for it, the query continues (ST-26). It is counted for the audit.
                    owner.Stats.NotFound();
                    return;
                }

                foreach (var row in batch)
                {
                    if (HasKey(row, lookup, key))
                    {
                        // Identical repeats of one upstream row collapse; different rows of the same key all stay (VF-07).
                        var keyRows = results[index] ??= [];
                        if (!keyRows.Exists(existing => SameRow(existing, row)))
                        {
                            keyRows.Add(row);
                        }
                    }
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        private static bool SameRow(IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a.Count != b.Count)
            {
                return false;
            }

            foreach (var (column, value) in a)
            {
                if (!b.TryGetValue(column, out var other) || !Equals(value, other))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasKey(IReadOnlyDictionary<string, object?> row, VirtualFilterPathLookup lookup, string requested) =>
            row != null &&
            row.TryGetValue(lookup.Column, out var value) &&
            VirtualFilterKeyCanonicalizer.TryCanonicalize(lookup.KeyType, value, out var canonical) &&
            string.Equals(VirtualFilterKeyCanonicalizer.FormatCanonical(canonical), requested, StringComparison.Ordinal);
    }
}
