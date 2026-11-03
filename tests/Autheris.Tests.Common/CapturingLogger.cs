namespace Autheris.Tests.Common;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared thread-safe <see cref="ILogger"/> test double that records log entries for assertions.
/// </summary>
public class CapturingLogger : ILogger
{
    public readonly record struct Entry(LogLevel Level, EventId EventId, string Message, Exception? Exception)
    {
        public string Text => Exception is null ? Message : $"{Message}{Exception}";

        public void Deconstruct(out LogLevel level, out string message, out Exception? exception)
        {
            level = Level;
            message = Message;
            exception = Exception;
        }
    }

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public IReadOnlyList<string> Messages => _entries.Select(static e => e.Message).ToArray();

    public IReadOnlyList<string> Lines => _entries.Select(static e => e.Exception is null ? e.Message : $"{e.Message} {e.Exception}").ToArray();

    public IReadOnlyList<string> Warnings => _entries.Where(static e => e.Level == LogLevel.Warning).Select(static e => e.Message).ToArray();

    public int Count(LogLevel level) => _entries.Count(e => e.Level == level);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue(new Entry(logLevel, eventId, formatter(state, exception), exception));
    }
}

/// <summary>
/// Typed <see cref="ILogger{TCategoryName}"/> wrapper over <see cref="CapturingLogger"/>.
/// </summary>
public sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>;
