namespace TrinoSqlEngine.Parsing;

using System;
using System.Collections.Concurrent;
using System.Threading;

/// <summary>
/// One unit of parser work. It carries the <see cref="ExecutionContext"/> of the submitting caller so ambient state (for example the
/// scoped compiler hooks) behaves exactly as it did when every parse ran on a freshly created thread.
/// </summary>
internal sealed class ParseWork
{
    private const int Queued = 0;
    private const int Running = 1;
    private const int Done = 2;
    private const int Abandoned = 3;

    private readonly Action _execute;
    private readonly Action? _onCanceledWhileQueued;
    private readonly ExecutionContext? _context;
    private int _state;

    public ParseWork(Action execute, Action? onCanceledWhileQueued = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _onCanceledWhileQueued = onCanceledWhileQueued;
        _context = ExecutionContext.Capture();
    }

    /// <summary>Runs the work on the calling worker thread. Returns false when the caller abandoned the work while it ran.</summary>
    internal bool Run()
    {
        if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued)
        {
            // Abandoned while still queued: skip execution and keep this worker thread alive.
            return true;
        }

        if (_context is null)
        {
            _execute();
        }
        else
        {
            ExecutionContext.Run(_context, static state => ((ParseWork)state!)._execute(), this);
        }

        return Interlocked.CompareExchange(ref _state, Done, Running) == Running;
    }

    /// <summary>Marks running work as abandoned (the caller timed out). Returns true when the work was running.</summary>
    internal bool TryAbandon()
    {
        if (Interlocked.CompareExchange(ref _state, Abandoned, Queued) == Queued)
        {
            _onCanceledWhileQueued?.Invoke();
            return false;
        }

        return Interlocked.CompareExchange(ref _state, Abandoned, Running) == Running;
    }
}

/// <summary>
/// ARC-S-03: long-lived dedicated parser threads (own large stack, background) fed by a bounded queue. It replaces the thread that
/// was created for every parse. Threads are created lazily up to <see cref="Size"/>. A worker whose work the caller abandoned
/// (time budget exceeded) is retired as soon as that work returns and is replaced, so a slow parse can neither starve the pool
/// nor leak a thread. The number of running parses stays bounded by the engine's concurrency limiter, which the worker releases.
/// </summary>
internal sealed class ParseWorkerPool : IDisposable
{
    private static readonly ConcurrentDictionary<int, ParseWorkerPool> Shared = new();

    private readonly BlockingCollection<ParseWork> _queue;
    private readonly object _gate = new();
    private readonly int _stackSize;
    private int _live;
    private int _idle;
    private int _created;

    internal ParseWorkerPool(int size, int stackSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stackSize, FastSqlEngine.MinParseThreadStackSize);
        Size = size;
        _stackSize = stackSize;
        _queue = new BlockingCollection<ParseWork>(new ConcurrentQueue<ParseWork>(), Math.Max(16, size * 4));
    }

    /// <summary>The process-wide pool for a parser thread stack size.</summary>
    public static ParseWorkerPool ForStackSize(int stackSize) =>
        Shared.GetOrAdd(stackSize, static s => new ParseWorkerPool(FastSqlEngine.DefaultMaxConcurrentParses, s));

    /// <summary>Maximum number of serving worker threads.</summary>
    public int Size { get; }

    /// <summary>Total number of worker threads ever created by this pool (diagnostics and tests).</summary>
    public int ThreadsCreated => Volatile.Read(ref _created);

    /// <summary>
    /// Queues <paramref name="work"/>, waiting at most <paramref name="timeoutMilliseconds"/> for room in the bounded queue.
    /// Returns false when the queue stayed full (the caller rejects the parse, fail closed).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while waiting.</exception>
    public bool TrySubmit(ParseWork work, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (!_queue.TryAdd(work, timeoutMilliseconds, cancellationToken))
        {
            return false;
        }

        EnsureWorker();
        return true;
    }

    /// <summary>The caller gave up on <paramref name="work"/>; a running worker is retired after the work returns and replaced lazily.</summary>
    public void Abandon(ParseWork work)
    {
        if (work.TryAbandon())
        {
            lock (_gate)
            {
                _live--;
            }

            EnsureWorker();
        }
    }

    /// <summary>Stops the workers after the queued work. The shared process-wide pools are never disposed.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _queue.Dispose();
    }

    private void EnsureWorker()
    {
        lock (_gate)
        {
            int queueCount;
            try
            {
                queueCount = _queue.Count;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (_live >= Size || queueCount <= Volatile.Read(ref _idle))
            {
                return;
            }

            _live++;
            _created++;
            Interlocked.Increment(ref _idle);
            try
            {
                using (ExecutionContext.SuppressFlow())
                {
                    new Thread(WorkerLoop, _stackSize) { IsBackground = true, Name = "FastSqlEngine.Parse" }.Start();
                }
            }
            catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
            {
                // No thread could be started: the queued work is rejected by its caller when the time budget runs out (fail closed).
                Interlocked.Decrement(ref _idle);
                _live--;
                _created--;
            }
        }
    }

    private void WorkerLoop()
    {
        bool idleCounted = true;
        while (true)
        {
            if (!idleCounted)
            {
                Interlocked.Increment(ref _idle);
            }

            idleCounted = false;
            ParseWork work;
            try
            {
                work = _queue.Take();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                return; // the pool was disposed (completed or disposed queue)
            }
            finally
            {
                Interlocked.Decrement(ref _idle);
            }

            int queueCount;
            try
            {
                queueCount = _queue.Count;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (queueCount > Volatile.Read(ref _idle))
            {
                EnsureWorker();
            }

            if (!work.Run())
            {
                // Abandoned while running: Abandon already removed this thread from the live count and may have started a replacement.
                return;
            }
        }
    }
}
