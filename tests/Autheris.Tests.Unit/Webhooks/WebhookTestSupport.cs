namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>Settable clock (a local test double; avoids an extra package).</summary>
internal sealed class WebhookManualTime(DateTimeOffset start) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private readonly object _gate = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Number of live timers (e.g. one per worker poll loop).</summary>
    public int TimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Moves the clock and fires every timer that became due (periodic timers may fire repeatedly).</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> snapshot;
        lock (_gate)
        {
            _now += by;
            snapshot = [.. _timers];
        }

        foreach (var timer in snapshot)
        {
            timer.FireDue(_now);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(WebhookManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _next;
        private TimeSpan _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            _next = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void FireDue(DateTimeOffset now)
        {
            while (_next is { } next && next <= now)
            {
                _next = _period > TimeSpan.Zero ? next + _period : null;
                callback(state);
            }
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Handler that records requests (with bodies) and answers with a configurable response.</summary>
internal sealed class WebhookCapturingHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        return Respond(request);
    }
}

/// <summary>Logger that keeps every formatted message and exception text, to assert nothing sensitive is logged.</summary>
internal sealed class WebhookCapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
        if (exception is not null)
        {
            Messages.Add(exception.ToString());
        }
    }

    public string All => string.Join("\n", Messages);
}
