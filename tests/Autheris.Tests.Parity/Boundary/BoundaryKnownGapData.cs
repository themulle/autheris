namespace Autheris.Tests.Parity.Boundary;

/// <summary>Generated from the first WP2 sweep: exact case ids per gap draft (strict xfail needs exact ids). Edit when a gap is fixed or found.</summary>
internal static class BoundaryKnownGapData
{
    /// <summary>(kind, gap id, channel, case ids). Ids are exact; see section 5 of the plan for the drafts.</summary>
    public static readonly (BoundaryClass Kind, string? GapId, string Channel, string[] Ids)[] Groups =
    [
        // Intentionally empty: every earlier gap and observed server error is fixed (2026-10-23, fix/parity-gaps). A new gap or
        // observed 5xx is added here with exact case ids.
    ];

    private static string[] Split(string raw) => raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
