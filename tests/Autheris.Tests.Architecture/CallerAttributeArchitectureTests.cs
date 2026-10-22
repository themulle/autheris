using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// REQ-GOV-129 (PLAN-GOV-129, ST-129-14, ST-129-23, BC-129-01, BC-129-05 c, BC-129-16): structural guards of the caller attribute feature. They read the
/// sources, so a new construction site, reader or reference that breaks a binding condition fails the build instead of a review.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class CallerAttributeArchitectureTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Autheris.Application")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

    private static IEnumerable<(string Path, string Text)> Sources(string relativeDirectory, bool recursive = true) =>
        Directory.EnumerateFiles(Path.Combine(Root(), relativeDirectory), "*.cs", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetRelativePath(Root(), f).Replace('\\', '/'), StripComments(File.ReadAllText(f))));

    private static string StripComments(string text) => string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private const string AttributeSources = "src/Autheris.Application/Identity/Attributes";

    [Fact]
    public void BC_129_01_NothingUnderIdentityAttributes_UsesGetUserSidOrAHumanClaimAsAKey()
    {
        var offenders = new List<string>();
        foreach (var (path, text) in Sources(AttributeSources))
        {
            if (text.Contains("GetUserSid", StringComparison.Ordinal) || text.Contains("GetAllUserSids", StringComparison.Ordinal))
            {
                offenders.Add($"{path}: GetUserSid/GetAllUserSids");
            }

            // The subject factory names the human claim types only to tell a person from a service principal (never as a key).
            if (!path.EndsWith("CallerSubjectFactory.cs", StringComparison.Ordinal) &&
                (text.Contains("ClaimTypes.Upn", StringComparison.Ordinal) || text.Contains("ClaimTypes.Email", StringComparison.Ordinal) ||
                 text.Contains("\"upn\"", StringComparison.Ordinal) || text.Contains("\"email\"", StringComparison.Ordinal) || text.Contains("preferred_username", StringComparison.Ordinal)))
            {
                offenders.Add($"{path}: upn/email claim");
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void ST_129_23_EveryTableAccessQueryConstruction_PassesThePrincipal()
    {
        // The unified PDP (MCP RAG) has no ClaimsPrincipal in its SecurityPrincipalContext: an attribute filter there is the fail-closed constant.
        string[] allowlist = ["src/Autheris.Application/Policy/UnifiedPolicyDecisionPoint.cs"];
        var offenders = new List<string>();
        foreach (var (path, text) in Sources("src"))
        {
            foreach (Match match in TableAccessQueryConstruction().Matches(text))
            {
                int start = match.Index;
                int depth = 0;
                int end = start;
                for (; end < text.Length; end++)
                {
                    if (text[end] == '(') depth++;
                    else if (text[end] == ')' && --depth == 0) break;
                }

                var statement = text[start..Math.Min(end + 1, text.Length)];
                if (!statement.Contains("Principal", StringComparison.Ordinal) && !allowlist.Contains(path))
                {
                    offenders.Add(path);
                }
            }
        }

        offenders.ShouldBeEmpty("Every TableAccessQuery must carry the caller's principal (REQ-GOV-129): add Principal: or allowlist the site with a reason.");
    }

    [GeneratedRegex(@"new\s+(?:Autheris\.Application\.Policy\.)?TableAccessQuery\s*\(")]
    private static partial Regex TableAccessQueryConstruction();

    [Fact]
    public void BC_129_16_OnlyTheBinderCreatesCallerParameters()
    {
        var offenders = Sources("src")
            .Where(s => s.Text.Contains("AddCallerAttribute(", StringComparison.Ordinal))
            .Select(s => s.Path)
            .Where(p => !p.EndsWith("Sql/Policy/PolicyParameterNamer.cs", StringComparison.Ordinal) && !p.EndsWith("Identity/Attributes/CallerAttributeBinder.cs", StringComparison.Ordinal))
            .ToList();
        offenders.ShouldBeEmpty();

        var slotCreators = Sources("src")
            .Where(s => s.Text.Contains("CreateSlot(", StringComparison.Ordinal))
            .Select(s => s.Path)
            .Where(p => !p.EndsWith("Identity/Attributes/CallerAttributeBinder.cs", StringComparison.Ordinal) && !p.EndsWith("VirtualFilters/SqlFilterCompiler.cs", StringComparison.Ordinal))
            .ToList();
        slotCreators.ShouldBeEmpty();
    }

    [Fact]
    public void BC_129_16_CallerAttributeCode_NeverUsesTheClientParameterPaths()
    {
        var offenders = new List<string>();
        foreach (var (path, text) in Sources(AttributeSources).Append(("CallerPlaceholders.cs", File.ReadAllText(Path.Combine(Root(), "src/Autheris.Application/VirtualFilters/CallerPlaceholders.cs")))))
        {
            foreach (var forbidden in new[] { "ParameterOrigin.ClientNamed", "ParameterOrigin.ClientPositional", "ClientListExpander", "ClientListShapes" })
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                {
                    offenders.Add($"{path}: {forbidden}");
                }
            }
        }

        offenders.ShouldBeEmpty("Policy lists are expanded as policy parameters of origin Policy, never through the client list path (PLAN-SQL-128 A-10).");
    }

    [Fact]
    public void BC_129_16_TheReservedPrefix_IsRefusedForClientParametersOfTheWebSqlBinder()
    {
        var executor = File.ReadAllText(Path.Combine(Root(), "src/Autheris.Application/Sql/Services/GovernedSqlExecutor.cs"));
        executor.ShouldContain("CallerAttributeParameterName.IsReserved(trimmed)");
    }

    [Fact]
    public void BC_129_05_ReadersOfTheVirtualFilterPredicateText_AreAllowlisted_WithTheirCallerAttributeBehaviour()
    {
        // Each reader of the text projection of a Virtual Filter predicate and what it does with a caller attribute filter (whose text is the constant (1 = 0)).
        var allowlist = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Autheris.Api/Endpoints/VirtualFilterEndpoints.cs"] = "effective-filters explanation for administrators: shows the constant",
            ["src/Autheris.Application/Analytics/NoisyCacheKey.cs"] = "noisy cache key (#112 SEC-112-12/-26): hashes the predicate text with the bound caller-attribute values and the Generation; an unresolved attribute skips the cache",
            ["src/Autheris.Application/Data/Changes/GovernedChangeDetectionService.Cursors.cs"] = "known-key disclosure check only (presence of a filter); row evaluation goes through InMemoryRowFilterEvaluator.RowPassesDecision",
            ["src/Autheris.Application/Data/Relations/CrossSourceRelationResolver.cs"] = "cross-source child read: an applying Virtual Filter refuses the request (VF-02, presence check; no row evaluation of the text)",
            ["src/Autheris.Application/Data/Services/GovernedTableReadPlanner.Keyset.cs"] = "keyset pagination query planner: appends mandatory row predicate to WHERE clause",
            ["src/Autheris.Application/Mesh/Services/EnvoyExtAuthzService.cs"] = "an applying Virtual Filter denies (ext_authz cannot enforce row filters)",
            ["src/Autheris.Application/Policy/TableAccessPolicy.cs"] = "the decision point: a caller attribute outcome without a typed predicate denies the table (BC-129-05)",
            ["src/Autheris.Application/Policy/TableAccessPolicy.Consent.cs"] = "delegated source coarse gate: clears row-level predicates for native source pushdown (SEC-SC-13)",
            ["src/Autheris.Application/Procedures/Services/GovernedProcedureExecutionService.cs"] = "consent bypass without consent services: text only, so a caller attribute filter denies the table (F-4)",
            ["src/Autheris.Application/Services/InMemoryRowFilterEvaluator.cs"] = "in-memory typed first; a Virtual Filter in memory is refused or denies; the text path rejects markers",
            ["src/Autheris.Application/Sql/Policy/GovernancePolicyFactory.cs"] = "typed/text agreement check of the compile path: the typed predicate is authoritative",
            ["src/Autheris.Application/Sql/Services/FederatedDuckDbExecutionService.cs"] = "consent bypass without consent services: text only, so a caller attribute filter denies the table (F-4)",
            ["src/Autheris.Application/Sql/SqlSecurityValidator.cs"] = "length limit of the predicate text",
            ["src/Autheris.Application/Streaming/Services/StreamRlsPolicyEnforcer.cs"] = "CDC: an applying Virtual Filter drops the event",
            ["src/Autheris.Application/VirtualFilters/Federation/VirtualFilterFederationPlanner.cs"] = "planner of the gateway-side enforcement: presence check only",
            ["src/Autheris.Application/VirtualFilters/MandatoryRowFilterResolver.cs"] = "producer: marks CallerAttributesTextConstant",
            ["src/Autheris.Domain/Interfaces/TableAccessDecision.cs"] = "the decision itself",
            ["src/Autheris.Extensions/Lakehouse/Services/IcebergRestCatalogFederationService.cs"] = "an applying Virtual Filter denies (not enforceable on the catalog)"
        };

        var readers = Sources("src")
            .Where(s => ReaderPattern().IsMatch(s.Text))
            .Select(s => s.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        var unlisted = readers.Where(r => !allowlist.ContainsKey(r)).ToList();
        unlisted.ShouldBeEmpty("A new reader of the Virtual Filter predicate text must be added to the allowlist with its caller attribute behaviour (BC-129-05 c): " + string.Join(", ", unlisted));
        allowlist.Keys.Where(k => !readers.Contains(k)).ShouldBeEmpty("A stale allowlist entry no longer reads the predicate text; remove it.");
    }

    [GeneratedRegex(@"MandatoryRowPredicateSql|\.PredicateSql\b|RowFiltersAfterVirtualFilters|MandatoryPredicateSql")]
    private static partial Regex ReaderPattern();

    [Fact]
    public void ST_129_23_TheAstSecurityVisitor_KnowsNothingOfCallerAttributes()
    {
        foreach (var (path, text) in Sources("src/TrinoSqlEngine/Ast/Visitors").Where(s => s.Path.Contains("AstSecurityVisitor", StringComparison.Ordinal)))
        {
            text.ShouldNotContain("CallerAttribute", Case.Insensitive, path);
            text.ShouldNotContain("__autheris_caller", Case.Insensitive, path);
            text.ShouldNotContain("{{caller", Case.Insensitive, path);
            text.ShouldNotContain("__gql_caller", Case.Insensitive, path);
        }
    }

    /// <summary>
    /// ST-129-10, BC-129-15: no caller-visible projection reads the audit details. Only the audit chain, the repositories (Infrastructure) and the
    /// canonicalizer (Domain) read <c>.DetailsJson</c>; the API, GraphQL and Application layers only write it, and only the governed SQL
    /// executor writes the <c>caller_attributes</c> fields (other executors do not write them: a forensic gap, plan section 14).
    /// </summary>
    [Fact]
    public void ST_129_10_NoCallerVisibleLayer_ReadsTheAuditDetails_OrNamesTheCallerAttributeAuditFields()
    {
        var offenders = new List<string>();
        foreach (var dir in new[] { "src/Autheris.Api", "src/Autheris.GraphQL", "src/Autheris.Application" })
        {
            foreach (var (path, text) in Sources(dir))
            {
                if (Regex.IsMatch(text, @"\.DetailsJson\b"))
                {
                    offenders.Add($"{path}: reads .DetailsJson");
                }

                bool writer = path.EndsWith("GovernedSqlExecutor.cs", StringComparison.Ordinal) || path.Contains("/Identity/Attributes/", StringComparison.Ordinal);
                if (!writer && (text.Contains("\"caller_attributes\"", StringComparison.Ordinal) || text.Contains("\"caller_attribute_binds\"", StringComparison.Ordinal)))
                {
                    offenders.Add($"{path}: names a caller attribute audit field");
                }
            }
        }

        offenders.ShouldBeEmpty();
    }
}
