namespace TrinoSqlEngine.Parsing;

using System.Diagnostics.Metrics;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;

/// <summary>
/// ARCH-MNT-15: owns the current <see cref="AntlrCacheGeneration"/> and is the only place that constructs <c>SqlBaseLexer</c> and
/// <c>SqlBaseParser</c> (SC-01, enforced by an architecture test). Every recognizer is bound to the generation that is current when it is created.
/// When the state count of a generation passes the cap, a fresh generation is published with one compare-exchange; in-flight work keeps the old one.
/// No lock on the parse path, no <c>ClearDFA</c> (SC-02).
/// </summary>
public sealed class AntlrCacheGenerations : IDisposable
{
    /// <summary>Meter name of the swap metrics. No tags: only counters and gauges (SC-09).</summary>
    public const string MeterName = "Autheris.SqlParserCache";

    private readonly object _configureLock = new();
    private Func<AntlrCacheGeneration, long> _stateCounter;
    private bool _configured;
    private readonly Meter _meter;
    private readonly Counter<long> _swaps;
    private AntlrCacheOptions _options;
    private AntlrCacheGeneration _current = new(1);
    private long _swapCount;
    private long _lastStateCount;

    /// <summary>Creates a manager; <paramref name="stateCounter"/> is a test seam for the state count.</summary>
    /// <exception cref="ArgumentException">The options are outside their bounds (fail closed, SC-04).</exception>
    public AntlrCacheGenerations(AntlrCacheOptions options, Func<AntlrCacheGeneration, long>? stateCounter = null)
    {
        _options = Validated(options);
        _stateCounter = stateCounter ?? AntlrCacheGeneration.CountStates;
        _meter = new Meter(MeterName);
        _swaps = _meter.CreateCounter<long>("autheris.sql.parser_cache.swaps");
        _meter.CreateObservableGauge("autheris.sql.parser_cache.generation", () => Current.Number);
        _meter.CreateObservableGauge("autheris.sql.parser_cache.states", () => Volatile.Read(ref _lastStateCount));
    }

    /// <summary>The process-wide instance used by <see cref="FastSqlEngine()"/> and the raw lexer call sites.</summary>
    public static AntlrCacheGenerations Shared { get; } = new(new AntlrCacheOptions());

    /// <summary>The generation that new recognizers are bound to.</summary>
    public AntlrCacheGeneration Current => Volatile.Read(ref _current);

    /// <summary>How many times a generation was replaced.</summary>
    public long SwapCount => Volatile.Read(ref _swapCount);

    /// <summary>
    /// Sets the bounds once (at start-up). Invalid options throw. A second call with different options throws too (fail closed): the bounds
    /// of a running process never change; the same options again are accepted (a second host in one process).
    /// </summary>
    /// <exception cref="ArgumentException">The options are outside their bounds.</exception>
    /// <exception cref="InvalidOperationException">Already configured with different options.</exception>
    public void Configure(AntlrCacheOptions options)
    {
        var valid = Validated(options);
        lock (_configureLock)
        {
            if (_configured && !valid.Equals(_options))
            {
                throw new InvalidOperationException("The ANTLR cache generations are already configured with different options.");
            }

            _configured = true;
            Volatile.Write(ref _options, valid);
        }
    }

    /// <summary>Test seam: back to the default options, unconfigured, with an optional replacement state counter.</summary>
    internal void ResetForTests(AntlrCacheOptions? options = null, Func<AntlrCacheGeneration, long>? stateCounter = null)
    {
        lock (_configureLock)
        {
            _configured = false;
            Volatile.Write(ref _options, Validated(options ?? new AntlrCacheOptions()));
            Volatile.Write(ref _stateCounter, stateCounter ?? AntlrCacheGeneration.CountStates);
        }
    }

    /// <summary>
    /// Publishes a fresh generation now (what a swap does). The previous one stays valid for its in-flight users. Used by the fuzz harness to bound
    /// the caches of a long run; it clears nothing in place.
    /// </summary>
    public void ForceSwap() => _ = Publish(Current);

    /// <summary>A lexer bound to the current generation. Counts the use first, which may swap (SC-03, SC-10).</summary>
    /// <exception cref="ParseCanceledException">Binding or counting failed; the caller must treat the input as rejected (SC-05).</exception>
    public SqlBaseLexer CreateLexer(ICharStream input)
    {
        try
        {
            var generation = Note(Current);
            var lexer = new SqlBaseLexer(input);
            lexer.BindGeneration(generation);
            return lexer;
        }
        catch (Exception)
        {
            throw Rejected();
        }
    }

    /// <summary>A parser bound to the current generation, in SLL mode (the caller restores listeners and the error strategy).</summary>
    /// <exception cref="ParseCanceledException">Binding failed; never falls back to the static arrays (SC-05).</exception>
    public SqlBaseParser CreateParser()
    {
        try
        {
            var parser = new SqlBaseParser(null);
            Bind(parser, Current);
            return parser;
        }
        catch (Exception)
        {
            throw Rejected();
        }
    }

    /// <summary>Rebuilds the prediction simulator of <paramref name="parser"/> on the current generation, in SLL mode.</summary>
    internal void Rebind(SqlBaseParser parser)
    {
        try
        {
            Bind(parser, Current);
        }
        catch (Exception)
        {
            throw Rejected();
        }
    }

    /// <summary>Counts one use of the parser's generation and swaps when it is over the cap (called when a parser goes back to the pool).</summary>
    internal void Release(SqlBaseParser parser)
    {
        try
        {
            _ = Note(parser.CacheGeneration ?? throw new InvalidOperationException("The parser is not bound to a generation."));
        }
        catch (Exception)
        {
            throw Rejected();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static void Bind(SqlBaseParser parser, AntlrCacheGeneration generation)
    {
        parser.BindGeneration(generation);
    }

    /// <summary>Counts one use of <paramref name="used"/>, swaps when due, and returns the generation that is current afterwards.</summary>
    private AntlrCacheGeneration Note(AntlrCacheGeneration used)
    {
        var options = Volatile.Read(ref _options);
        long uses = Interlocked.Increment(ref used.UsesCounter);
        if (uses % options.GenerationCheckInterval != 0)
        {
            return Current;
        }

        long states = Volatile.Read(ref _stateCounter)(used);
        Volatile.Write(ref _lastStateCount, states);
        if (states <= options.MaxStatesPerGeneration)
        {
            return Current;
        }

        // The hard ceiling overrides the minimum number of uses, so a flood of distinct statements cannot outgrow it for long.
        bool overHardCap = states > (long)options.MaxStatesPerGeneration * options.HardCapFactor;
        if (uses < options.MinParsesPerGeneration && !overHardCap)
        {
            return Current;
        }

        return Publish(used);
    }

    /// <summary>Publishes the successor of <paramref name="replaced"/> unless another thread already did; returns the current generation.</summary>
    private AntlrCacheGeneration Publish(AntlrCacheGeneration replaced)
    {
        var next = new AntlrCacheGeneration(replaced.Number + 1);
        if (ReferenceEquals(Interlocked.CompareExchange(ref _current, next, replaced), replaced))
        {
            Interlocked.Increment(ref _swapCount);
            _swaps.Add(1);
            return next;
        }

        return Current;
    }

    private static AntlrCacheOptions Validated(AntlrCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = options.Validate();
        if (failures.Count > 0)
        {
            throw new ArgumentException("Invalid ANTLR cache options: " + string.Join(" ", failures), nameof(options));
        }

        return options;
    }

    // Generic on purpose: no SQL text, hash or identifier (SC-05, SC-09).
    private static ParseCanceledException Rejected() => new("SQL parsing was rejected: the parser cache is unavailable.");
}
