namespace Autheris.Application.Procedures.Interfaces;

using System;
using System.Threading;

/// <summary>The transports that may execute a write procedure.</summary>
public enum ProcedureCallTransport
{
    /// <summary>No transport declared (every caller that does not enter a scope): write procedures are refused.</summary>
    Unspecified = 0,
    Rest = 1,
    Mcp = 2
}

/// <summary>
/// The transport of the procedure call in flight, set by the wired entry points (REST POST route, MCP <c>call_procedure</c>) around the
/// execution. Code that does not enter a scope runs as <see cref="ProcedureCallTransport.Unspecified"/>, so a new caller can never run a write
/// procedure by accident.
/// </summary>
public static class ProcedureCallScope
{
    private static readonly AsyncLocal<ProcedureCallTransport> Current = new();

    public static ProcedureCallTransport Transport => Current.Value;

    public static IDisposable Enter(ProcedureCallTransport transport)
    {
        var previous = Current.Value;
        Current.Value = transport;
        return new Restore(previous);
    }

    private sealed class Restore(ProcedureCallTransport previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
