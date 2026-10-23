using System.Text.RegularExpressions;

namespace Autheris.Tests.Parity.Matrix;

/// <summary>Rule for a matrix <see cref="Pending"/> reason: <c>inexpressible: &lt;reason&gt;</c> or a reference to a follow-up id registered in the plan (fail closed on unknown ids).</summary>
public static partial class PendingReasons
{
    private const string PlanFile = "2026-10-23-plan-nightly-parity-tests.md";

    [GeneratedRegex(@"^###\s+(PARITY-FU-\d{3})\s*$", RegexOptions.Multiline)]
    private static partial Regex FollowUpHeading();

    [GeneratedRegex(@"PARITY-FU-\d{3}")]
    private static partial Regex FollowUpRef();

    public static IReadOnlyList<string> FollowUpIds(string plan) =>
        FollowUpHeading().Matches(plan).Select(m => m.Groups[1].Value).Distinct().ToList();

    public static bool IsValid(string reason, IReadOnlyCollection<string> followUps)
    {
        if (reason.StartsWith("inexpressible: ", StringComparison.Ordinal) && reason.Length > "inexpressible: ".Length)
        {
            return true;
        }

        var refs = FollowUpRef().Matches(reason).Select(m => m.Value).ToList();
        return refs.Count > 0 && refs.All(followUps.Contains);
    }

    public static string ReadPlan()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "plans", PlanFile);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Plan {PlanFile} not found above {AppContext.BaseDirectory}.");
    }
}
