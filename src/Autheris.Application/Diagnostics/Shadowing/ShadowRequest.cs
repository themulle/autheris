namespace Autheris.Application.Diagnostics.Shadowing;

using System.Collections.Generic;

/// <summary>
/// F-OPS-01: Bounded immutable container representing an intercepted production request queued for dark replay.
/// </summary>
public sealed record ShadowRequest(
    string Method,
    string PathAndQuery,
    IReadOnlyDictionary<string, string> Headers,
    string? Body
);
