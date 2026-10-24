using System.Text;
using TrinoSqlEngine;

namespace Autheris.Tests.Fuzz.Target;

/// <summary>
/// The fuzz target: parse, then governed compile, over a UTF-8 input of at most <see cref="MaxInputBytes"/> bytes. It performs no
/// file, network or process I/O (SEC-SIM-20); the only input is the byte array or stream the caller passes.
/// </summary>
public static class FuzzTarget
{
    /// <summary>64 KB, equal to the engine <c>MaxQueryLength</c> (SEC-SIM-20).</summary>
    public const int MaxInputBytes = 65_536;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly FastSqlEngine Engine = new();

    /// <summary>Reads at most <see cref="MaxInputBytes"/> bytes (extra input is ignored).</summary>
    public static byte[] ReadCapped(Stream input)
    {
        var buffer = new byte[MaxInputBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = input.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    /// <summary>Runs one input and lets every exception escape (the classifier decides). Invalid UTF-8 and oversize input return.</summary>
    public static void Execute(byte[] input)
    {
        if (input.Length > MaxInputBytes)
        {
            return;
        }

        string sql;
        try
        {
            sql = StrictUtf8.GetString(input);
        }
        catch (DecoderFallbackException)
        {
            return;
        }

        var memory = sql.AsMemory();
        Engine.Parse(memory);

        using var cts = new CancellationTokenSource(FuzzOutcomeClassifier.HangLimit);
        Engine.Compile(memory, FuzzCompileContext.Request, cts.Token);
    }

    /// <summary>
    /// AFL entry point (<c>Fuzzer.OutOfProcess.Run</c>): a documented rejection is swallowed; a crash rethrows (the process aborts and AFL
    /// records it); a hang ends the process with <see cref="Environment.FailFast(string?)"/> (the hung thread is abandoned only in a
    /// process that then exits, SEC-SIM-20).
    /// </summary>
    public static void Run(Stream input)
    {
        var result = FuzzRunner.RunOne(ReadCapped(input));
        switch (result.Outcome)
        {
            case FuzzOutcome.Crash:
                throw new InvalidOperationException("fuzz crash: " + result.ExceptionType, result.Exception);
            case FuzzOutcome.Hang:
                Environment.FailFast("fuzz hang");
                break;
        }
    }
}

/// <summary>The result of one in-process run.</summary>
public sealed record FuzzRunResult(FuzzOutcome Outcome, string? ExceptionType, TimeSpan Elapsed, Exception? Exception);

/// <summary>Runs one input on a dedicated thread (16 MB stack like <c>FastSqlEngine.DefaultParseThreadStackSize</c>) with a 2 s <c>Join</c>.</summary>
public static class FuzzRunner
{
    /// <param name="input">The input bytes.</param>
    /// <param name="target">Test seam; the default is <see cref="FuzzTarget.Execute"/>.</param>
    public static FuzzRunResult RunOne(byte[] input, Action<byte[]>? target = null)
    {
        target ??= FuzzTarget.Execute;
        Exception? error = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    target(input);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            },
            FastSqlEngine.DefaultParseThreadStackSize)
        {
            IsBackground = true,
            Name = "fuzz-input",
        };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        thread.Start();
        if (!thread.Join(FuzzOutcomeClassifier.HangLimit))
        {
            // The thread is abandoned (background thread); the caller exits the process on a hang in AFL mode.
            return new FuzzRunResult(FuzzOutcome.Hang, "Timeout", clock.Elapsed, null);
        }

        var elapsed = clock.Elapsed;
        var outcome = FuzzOutcomeClassifier.Classify(error, elapsed);
        return new FuzzRunResult(outcome, error?.GetType().FullName, elapsed, error);
    }
}
