using System.Text.RegularExpressions;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Infrastructure;

/// <summary>Aspect of a parity cell. Security cells can never carry a KnownGap (SEC-PAR-05a).</summary>
public enum ParityAspect
{
    None = 0,
    Security = 1,
}

/// <summary>Method-level strict xfail marker: the test must fail until the gap is fixed; then it must be removed.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class KnownGapAttribute(string gapId, string reason) : Attribute
{
    public string GapId { get; } = gapId;

    public string Reason { get; } = reason;
}

/// <summary>Marks a failure as hard: never converted to a known gap (SEC-PAR-05b/c). The message prefix survives serialization.</summary>
public class ParityHardFailureException(string message) : Exception(HardPrefix + message)
{
    public const string HardPrefix = "PARITY-HARD: ";
}

/// <summary>A request the oracle expects denied or masked returned data (SEC-PAR-05c).</summary>
public sealed class DeniedRequestSucceededException(string message) : ParityHardFailureException(message);

/// <summary>The body of a gapped cell failed as expected.</summary>
public sealed class KnownGapObservedException(string gapId, string reason) : Exception($"{gapId}: {reason}")
{
    public string GapId { get; } = gapId;
}

/// <summary>The cell is registered but its channel/capability is not implemented yet.</summary>
public sealed class PendingCellException(string channel, string capability, string? reason = null)
    : Exception(reason is null ? $"PENDING: {channel}/{capability}" : $"PENDING: {channel}/{capability} ({reason})");

public static partial class KnownGap
{
    [GeneratedRegex(@"^PARITY-GAP-\d{3}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>
    /// Strict xfail. No gap id: runs the body normally. Gap id: the body must fail (reported as skipped by the bus);
    /// a passing body fails with "is fixed". Hard failures and security cells are never gapped.
    /// </summary>
    public static async Task RunAsync(string? gapId, Func<Task> body, ParityAspect aspect = ParityAspect.None)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (gapId is null)
        {
            await body();
            return;
        }

        if (!IsValidId(gapId))
        {
            throw new InvalidOperationException($"Invalid gap id '{gapId}'; expected PARITY-GAP-NNN.");
        }

        if (aspect == ParityAspect.Security)
        {
            throw new InvalidOperationException($"{gapId}: KnownGap is not allowed on a security cell.");
        }

        try
        {
            await body();
        }
        catch (ParityHardFailureException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new KnownGapObservedException(gapId, ex.Message);
        }

        throw new XunitException($"{gapId} is fixed: remove the KnownGap");
    }
}
