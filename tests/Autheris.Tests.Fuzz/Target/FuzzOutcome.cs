using Antlr4.Runtime.Misc;

namespace Autheris.Tests.Fuzz.Target;

/// <summary>Result class of one fuzz input (REQ-SIMTEST P3).</summary>
public enum FuzzOutcome
{
    /// <summary>The input parsed and compiled, or was refused with a documented TrinoSqlEngine rejection.</summary>
    Accepted,

    /// <summary>An exception type that is not a documented rejection.</summary>
    Crash,

    /// <summary>The input took longer than <see cref="FuzzOutcomeClassifier.HangLimit"/> or hit the engine parse budget.</summary>
    Hang,
}

/// <summary>Decides crash, hang or accepted for one input (SEC-SIM-20: 2 s hang limit).</summary>
public static class FuzzOutcomeClassifier
{
    public static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(2);

    public static FuzzOutcome Classify(Exception? exception, TimeSpan elapsed)
    {
        if (elapsed > HangLimit)
        {
            return FuzzOutcome.Hang;
        }

        if (exception is null)
        {
            return FuzzOutcome.Accepted;
        }

        // ParseCanceledException derives from OperationCanceledException: syntax and policy rejections are accepted, the budget timeout is a hang.
        if (exception is ParseCanceledException parse)
        {
            return parse.InnerException is TimeoutException ? FuzzOutcome.Hang : FuzzOutcome.Accepted;
        }

        // Compile is cancelled only by the 2 s compile budget of the target.
        if (exception is OperationCanceledException)
        {
            return FuzzOutcome.Hang;
        }

        return AcceptedRejections.IsAccepted(exception) ? FuzzOutcome.Accepted : FuzzOutcome.Crash;
    }
}
