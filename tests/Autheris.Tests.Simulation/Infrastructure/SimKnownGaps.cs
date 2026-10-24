using System.Text.RegularExpressions;

namespace Autheris.Tests.Simulation.Infrastructure;

public sealed record SimKnownGap(string Id, FindingKind Kind, string ErrorClass);

/// <summary>
/// REQ-SIM-04/05: registry of accepted gaps (strict xfail). <see cref="Register"/> refuses the security kinds (SEC-SIM-17);
/// a registered gap that no longer reproduces fails via <see cref="EnsureNoStale"/>.
/// </summary>
public sealed partial class SimKnownGaps
{
    private readonly Dictionary<string, SimKnownGap> _byClass = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^SIM-GAP-\d{3}$")]
    private static partial Regex IdPattern();

    public IReadOnlyCollection<SimKnownGap> Gaps => _byClass.Values;

    public SimKnownGap Register(string id, FindingKind kind, string errorClass)
    {
        if (kind.IsSecurityClass())
        {
            throw new InvalidOperationException($"{kind} can never be a KnownGap (SEC-SIM-17); only an owner-approved quarantine entry is possible.");
        }

        if (!IdPattern().IsMatch(id))
        {
            throw new ArgumentException("KnownGap id must match SIM-GAP-###.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(errorClass))
        {
            throw new ArgumentException("Error class is required.", nameof(errorClass));
        }

        if (_byClass.ContainsKey(errorClass) || _byClass.Values.Any(g => g.Id == id))
        {
            throw new InvalidOperationException($"Duplicate KnownGap registration ({id}).");
        }

        var gap = new SimKnownGap(id, kind, errorClass);
        _byClass[errorClass] = gap;
        return gap;
    }

    public bool IsKnown(SimFinding finding) =>
        !finding.Kind.IsSecurityClass() && _byClass.TryGetValue(finding.ErrorClass, out var gap) && gap.Kind == finding.Kind;

    /// <summary>Gaps whose error class was not observed in this run.</summary>
    public IReadOnlyList<SimKnownGap> Stale(IEnumerable<string> observedErrorClasses)
    {
        var seen = new HashSet<string>(observedErrorClasses, StringComparer.Ordinal);
        return _byClass.Values.Where(g => !seen.Contains(g.ErrorClass)).OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
    }

    public void EnsureNoStale(IEnumerable<string> observedErrorClasses)
    {
        var stale = Stale(observedErrorClasses);
        if (stale.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", stale.Select(g => $"remove KnownGap {g.Id}")));
        }
    }
}
