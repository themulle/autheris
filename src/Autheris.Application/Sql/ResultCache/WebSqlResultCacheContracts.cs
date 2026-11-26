namespace Autheris.Application.Sql.ResultCache;

using System;

/// <summary>How a request wants to use the WebSQL result cache.</summary>
public enum WebSqlResultCacheMode
{
    /// <summary>Look the result up and store a complete result on a miss.</summary>
    Default = 0,

    /// <summary>Skip the lookup, re-execute and replace the caller's own entry.</summary>
    Bypass = 1
}

/// <summary>What the cache did for one request.</summary>
public enum WebSqlResultCacheStatus
{
    Hit,
    Miss,
    Store,
    Bypass,
    Ineligible
}

/// <summary>Outcome of the cache decision of one request. <paramref name="Reason"/> is one of <see cref="WebSqlResultCacheReasons"/>.</summary>
public sealed record WebSqlResultCacheOutcome(WebSqlResultCacheStatus Status, int? AgeSeconds = null, string? Reason = null);

/// <summary>
/// Marks a governed query as a participant of the WebSQL result cache. <paramref name="Format"/> is the output format
/// (<c>json</c> or <c>parquet</c>); <paramref name="OnOutcome"/> receives the outcome once it is known.
/// </summary>
public sealed record WebSqlResultCacheRequest(
    WebSqlResultCacheMode Mode,
    string Format,
    Action<WebSqlResultCacheOutcome>? OnOutcome);
