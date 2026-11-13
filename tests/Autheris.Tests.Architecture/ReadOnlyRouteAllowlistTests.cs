using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// A read-only token may call every GET and HEAD route, so each of them must be free of side effects. The routes found in the sources are
/// compared with the reviewed allowlist: a new GET route needs a review entry, a removed route a removed entry.
/// </summary>
public sealed partial class ReadOnlyRouteAllowlistTests
{
    private const int MinimumRoutes = 100;

    // Development-only anonymous persona login: never mapped outside Development and no read-only token can reach it.
    private static readonly string[] SideEffectingExemptions = ["src/Autheris.Api/Endpoints/DevEndpoints.cs|/login/{persona}"];

    [GeneratedRegex("""\.Map(?<kind>Get|Methods)\s*\(\s*(?<route>@?"(?:[^"\\]|\\.)*"|[^,)]+)\s*(?:,\s*(?<methods>new\s*(?:string)?\[\]\s*\{[^}]*\}|\[[^\]]*\]|"[^"]*"))?""", RegexOptions.Singleline)]
    private static partial Regex RouteRegex();

    [GeneratedRegex("\"(GET|HEAD)\"")]
    private static partial Regex ReadMethodRegex();

    private static SortedSet<string> RoutesInSources()
    {
        var routes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var code = string.Join('\n', RepositoryScan.CodeLines(file.Text));
            foreach (Match match in RouteRegex().Matches(code))
            {
                if (match.Groups["kind"].Value == "Methods" && !ReadMethodRegex().IsMatch(match.Groups["methods"].Value))
                {
                    continue;
                }

                var route = match.Groups["route"].Value.Trim();
                routes.Add($"{file.RelativePath}|{(route.StartsWith('"') ? route.Trim('"') : route)}");
            }
        }

        return routes;
    }

    [Fact]
    public void Every_get_route_is_on_side_effect_free_allowlist()
    {
        var actual = RoutesInSources();
        var allowlist = RepositoryScan.ReadBaseline("readonly-get-routes.txt").ToHashSet(StringComparer.Ordinal);

        actual.Count.ShouldBeGreaterThanOrEqualTo(MinimumRoutes, "The route scanner found too few GET routes; the scan is broken.");
        var unreviewed = actual.Where(r => !allowlist.Contains(r) && !SideEffectingExemptions.Contains(r)).ToList();
        var stale = allowlist.Where(r => !actual.Contains(r)).ToList();

        SideEffectingExemptions.Length.ShouldBe(1, "The exemption set must not grow.");
        actual.ShouldContain(SideEffectingExemptions[0], "The exempted route no longer exists; remove the exemption.");
        allowlist.ShouldNotContain(SideEffectingExemptions[0]);

        unreviewed.ShouldBeEmpty("A GET or HEAD route is not on the side-effect-free allowlist. Review the handler: a handler that changes state must not be a GET; otherwise add '<file>|<route>' to Baselines/readonly-get-routes.txt: " + string.Join(", ", unreviewed));
        stale.ShouldBeEmpty("The allowlist lists a GET route that no longer exists; remove the entry: " + string.Join(", ", stale));
    }
}
