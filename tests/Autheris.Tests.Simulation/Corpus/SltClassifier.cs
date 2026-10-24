using System.Text.Json;
using Autheris.Tests.Simulation.Infrastructure;

namespace Autheris.Tests.Simulation.Corpus;

public enum SltVerdict
{
    Equal,
    AcceptedDifference,
    UnsupportedByDesign,
    KnownGap,
    Finding,
    PolicyLeak,
    Crash,
    Hang,
    HarnessMismatch,
}

/// <summary>What one comparison saw. <see cref="Kind"/> is null for equal results.</summary>
public sealed record SltObservation(FindingKind? Kind, string ErrorClass, bool HarnessMismatch = false, bool RowFilterScenario = false);

/// <summary>
/// REQ-SIM-04: the verdict of one observation. Security classes (policy leak, crash, hang) are decided first and can never be downgraded by an
/// accepted-difference or unsupported entry or by a KnownGap with the same error class (SEC-SIM-17).
/// </summary>
public sealed class SltClassifier(IReadOnlySet<string> acceptedDifferences, IReadOnlySet<string> unsupportedByDesign, SimKnownGaps gaps)
{
    public SltVerdict Classify(SltObservation observation)
    {
        if (observation.HarnessMismatch)
        {
            return SltVerdict.HarnessMismatch;
        }

        if (observation.Kind is null)
        {
            return SltVerdict.Equal;
        }

        var kind = observation.Kind.Value;
        switch (kind)
        {
            case FindingKind.PolicyLeak:
                return SltVerdict.PolicyLeak;
            case FindingKind.Hang:
                return SltVerdict.Hang;
            case FindingKind.Crash or FindingKind.ServerError:
                return SltVerdict.Crash;
        }

        // a wrong result under the row filter may hide a leaked or dropped row: it is always a finding, never an accepted difference or a KnownGap
        if (kind == FindingKind.WrongResult && observation.RowFilterScenario)
        {
            return SltVerdict.Finding;
        }

        if (kind == FindingKind.WrongResult && acceptedDifferences.Contains(observation.ErrorClass))
        {
            return SltVerdict.AcceptedDifference;
        }

        if (kind == FindingKind.UnexpectedRejection && unsupportedByDesign.Contains(observation.ErrorClass))
        {
            return SltVerdict.UnsupportedByDesign;
        }

        var finding = new SimFinding(kind, observation.ErrorClass, string.Empty, string.Empty, string.Empty, string.Empty);
        return gaps.IsKnown(finding) ? SltVerdict.KnownGap : SltVerdict.Finding;
    }

    public static bool IsRed(SltVerdict verdict) => verdict is SltVerdict.PolicyLeak or SltVerdict.Crash or SltVerdict.Hang or SltVerdict.Finding;

    /// <summary>Loads <c>slt-accepted-differences.json</c> (<c>{"entries":[{"errorClass","reason"}]}</c>) or <c>slt-unsupported.json</c> (<c>docRef</c>); fail-closed on unknown keys.</summary>
    public static IReadOnlySet<string> LoadClassFile(string path, string requiredKey)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} must be an object with an 'entries' array.");
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries.EnumerateArray())
        {
            var unknown = e.EnumerateObject().Select(p => p.Name).Except(["errorClass", requiredKey], StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException($"{Path.GetFileName(path)} has unknown keys: {string.Join(",", unknown)}.");
            }

            var cls = e.TryGetProperty("errorClass", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var why = e.TryGetProperty(requiredKey, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
            if (string.IsNullOrWhiteSpace(cls) || string.IsNullOrWhiteSpace(why))
            {
                throw new InvalidOperationException($"{Path.GetFileName(path)} entries need errorClass and {requiredKey}.");
            }

            if (!result.Add(cls))
            {
                throw new InvalidOperationException($"{Path.GetFileName(path)} lists {cls} twice.");
            }
        }

        return result;
    }
}

/// <summary>Turns an engine outcome plus the reference results into an <see cref="SltObservation"/>.</summary>
public static class SltComparison
{
    public static SltObservation Observe(
        SltEngineOutcome actual,
        SltQuery query,
        SltScenario scenario,
        IReadOnlyList<string> expectedValues,
        IReadOnlyList<string>? unfilteredValues,
        out IReadOnlyList<string> actualValues)
    {
        actualValues = [];
        switch (actual)
        {
            case SltEngineCrashed c:
                return new SltObservation(FindingKind.Crash, SimErrorClass.Build(FindingKind.Crash, $"{c.Stage}/{c.ExceptionType}", c.Message));
            case SltEngineHung h:
                return new SltObservation(FindingKind.Hang, SimErrorClass.Build(FindingKind.Hang, h.Stage, null));
            case SltEngineRejected r:
                return new SltObservation(FindingKind.UnexpectedRejection, SimErrorClass.Build(FindingKind.UnexpectedRejection, $"{r.Stage}/{r.ExceptionType}", r.Message));
            case SltEngineRows rows:
                try
                {
                    actualValues = SltResultFormatter.Format(rows.Rows, query.Types, query.Sort);
                }
                catch (InvalidOperationException)
                {
                    return new SltObservation(FindingKind.WrongResult, SimErrorClass.Build(FindingKind.WrongResult, "ResultMismatch", $"column count differs (scenario {Name(scenario)})"), RowFilterScenario: scenario == SltScenario.RowFilter);
                }

                if (actualValues.SequenceEqual(expectedValues, StringComparer.Ordinal))
                {
                    return new SltObservation(null, string.Empty);
                }

                if (scenario == SltScenario.RowFilter && unfilteredValues is not null && actualValues.SequenceEqual(unfilteredValues, StringComparer.Ordinal))
                {
                    return new SltObservation(FindingKind.PolicyLeak, SimErrorClass.Build(FindingKind.PolicyLeak, "RowFilter", "result of the unfiltered table returned"));
                }

                var why = actualValues.Count != expectedValues.Count ? "value count differs" : "values differ";
                return new SltObservation(FindingKind.WrongResult, SimErrorClass.Build(FindingKind.WrongResult, "ResultMismatch", $"{why} (scenario {Name(scenario)})"), RowFilterScenario: scenario == SltScenario.RowFilter);
            default:
                throw new InvalidOperationException("unknown engine outcome.");
        }
    }

    public static string Name(SltScenario scenario) => scenario == SltScenario.Permissive ? "A" : "B";
}
