namespace Autheris.Application.Policy.Lint;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine.Governance;

/// <summary>
/// Parses every stored row predicate through the production parser setup (<see cref="PolicyParserSetup"/>) and reports the ones
/// that would deny their table (SEC-ADG-26, SEC-X1-16). Findings carry table and policy ids and a stable code, never predicate text.
/// </summary>
public sealed partial class PolicyPredicateLinter
{
    public const string CodeParseRejected = "PARSE_REJECTED";
    public const string CodeUnknownDialect = "UNKNOWN_DIALECT";
    public const string CodeCorrelatedRejected = "CORRELATED_FILTER_REJECTED";
    public const string CodeVirtualFilterRejected = "VIRTUAL_FILTER_REJECTED";

    [GeneratedRegex(@"\$\{[a-zA-Z0-9_.]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private readonly IPolicyExpressionParser _parser;
    private readonly IRlsFilterGenerator _rlsFilterGenerator;
    private readonly SqlDataSourceOptions? _dataSources;

    public PolicyPredicateLinter(IPolicyExpressionParser? parser = null, IRlsFilterGenerator? rlsFilterGenerator = null, SqlDataSourceOptions? dataSources = null)
    {
        _dataSources = dataSources;
        _parser = parser ?? PolicyParserSetup.CreateParser();
        _rlsFilterGenerator = rlsFilterGenerator ?? RlsFilterGenerator.Instance;
    }

    public PolicyLintReport Lint(PolicyLintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var findings = new List<PolicyLintFinding>();
        int checkedCount = 0;
        int unchecked_ = 0;
        var catalog = PolicyParserSetup.CreateCatalog(input.Catalog);

        foreach (var profile in input.AccessProfiles.Where(p => !string.IsNullOrWhiteSpace(p.RowFilterPredicate)))
        {
            var tables = input.Catalog.Where(t => profile.MatchesTable(t.Identifier)).ToList();
            if (tables.Count == 0)
            {
                unchecked_++;
                continue;
            }

            foreach (var table in tables)
            {
                checkedCount++;
                CheckText(PolicyLintKind.AccessProfileRowFilter, profile.ProfileId, profile.RowFilterPredicate!, table, catalog, input, findings);
            }
        }

        foreach (var filter in input.VirtualFilters.Where(f => f.Sql != null))
        {
            checkedCount++;
            try
            {
                SqlFilterCompiler.Validate(filter, input.Catalog, null, _dataSources?.RequireTenantColumn == true, _dataSources?.TenantColumnExemptTables);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
            {
                findings.Add(new PolicyLintFinding(PolicyLintKind.VirtualFilter, filter.Source, filter.Name, CodeVirtualFilterRejected));
            }
        }

        foreach (var rule in input.CasbinRules)
        {
            var tables = input.Catalog.Where(t => AccessProfile.MatchesPattern(rule.TableId, t.Identifier)).ToList();
            if (tables.Count == 0)
            {
                unchecked_++;
                continue;
            }

            foreach (var table in tables)
            {
                if (!string.IsNullOrWhiteSpace(rule.RlsFilter))
                {
                    checkedCount++;
                    string text;
                    try
                    {
                        text = SubstitutePlaceholders(rule.RlsFilter);
                    }
                    catch (FormatException)
                    {
                        findings.Add(new PolicyLintFinding(PolicyLintKind.CasbinRowFilter, TableId(table), rule.RuleId, CodeParseRejected));
                        continue;
                    }

                    CheckText(PolicyLintKind.CasbinRowFilter, rule.RuleId, text, table, catalog, input, findings);
                }

                if (rule.CorrelatedRowFilter is { } correlated)
                {
                    checkedCount++;
                    CheckCorrelated(rule.RuleId, correlated, table, findings);
                }
            }
        }

        return new PolicyLintReport(checkedCount, findings) { Unchecked = unchecked_ };
    }

    private void CheckText(
        PolicyLintKind kind, string policyId, string text, TableMetadata table, ITableCatalog catalog,
        PolicyLintInput input, List<PolicyLintFinding> findings)
    {
        if (!PolicyParserSetup.TryCreateContext(table, catalog, input.AdditionalAllowedFunctions, 0, out var context))
        {
            findings.Add(new PolicyLintFinding(kind, TableId(table), policyId, CodeUnknownDialect));
            return;
        }

        try
        {
            _parser.Parse(text, context);
        }
        catch (Exception ex) when (ex is PolicyParseException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            findings.Add(new PolicyLintFinding(kind, TableId(table), policyId, CodeParseRejected));
        }
    }

    private void CheckCorrelated(string ruleId, ConsentRowFilter filter, TableMetadata table, List<PolicyLintFinding> findings)
    {
        try
        {
            _rlsFilterGenerator.BuildCorrelatedPredicate(filter);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
        {
            findings.Add(new PolicyLintFinding(PolicyLintKind.CasbinCorrelatedRowFilter, TableId(table), ruleId, CodeCorrelatedRejected));
        }
    }

    private static string TableId(TableMetadata table) => table.Identifier.ToString();

    /// <summary>
    /// Replaces <c>${name}</c> placeholders the way the Casbin interpolation does: a value is always a string literal, so inside a literal
    /// the placeholder becomes literal content and outside it becomes a quoted literal. The values are irrelevant to the lint.
    /// </summary>
    internal static string SubstitutePlaceholders(string template)
    {
        var sb = new StringBuilder(template.Length + 16);
        bool inLiteral = false;
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '\'')
            {
                if (inLiteral && i + 1 < template.Length && template[i + 1] == '\'')
                {
                    sb.Append("''");
                    i += 2;
                    continue;
                }

                inLiteral = !inLiteral;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                var match = Placeholder().Match(template, i);
                if (!match.Success || match.Index != i)
                {
                    throw new FormatException("Invalid placeholder.");
                }

                sb.Append(inLiteral ? "lint" : "'lint'");
                i += match.Length;
                continue;
            }

            sb.Append(c);
            i++;
        }

        if (inLiteral)
        {
            throw new FormatException("Unterminated literal.");
        }

        return sb.ToString();
    }
}
