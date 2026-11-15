namespace Autheris.Application.Policy.Lint;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine.Governance;

/// <summary>
/// Parses every stored row predicate through the production parser setup (<see cref="PolicyParserSetup"/>) and reports the ones
/// that would deny their table. Findings carry table and policy ids and a stable code, never predicate text.
/// </summary>
public sealed class PolicyPredicateLinter
{
    public const string CodeParseRejected = "PARSE_REJECTED";
    public const string CodeUnknownDialect = "UNKNOWN_DIALECT";
    public const string CodeVirtualFilterRejected = "VIRTUAL_FILTER_REJECTED";

    /// <summary>129 (A-8, ST-129-18): a caller placeholder in an access profile predicate; only Virtual Filters may carry one, so a shared decision never embeds an attribute.</summary>
    public const string CodeCallerPlaceholderNotAllowed = "CALLER_PLACEHOLDER_NOT_ALLOWED";

    private readonly SqlDataSourceOptions? _dataSources;

    public PolicyPredicateLinter(IPolicyExpressionParser? parser = null, SqlDataSourceOptions? dataSources = null)
    {
        _ = parser;
        _dataSources = dataSources;
    }

    public PolicyLintReport Lint(PolicyLintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var findings = new List<PolicyLintFinding>();
        int checkedCount = 0;
        int unchecked_ = 0;

        foreach (var filter in input.VirtualFilters.Where(f => f.Sql != null))
        {
            checkedCount++;
            try
            {
                SqlFilterCompiler.Validate(filter, input.Catalog, null, _dataSources?.RequireTenantColumn == true, _dataSources?.TenantColumnExemptTables, input.CallerAttributes);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
            {
                findings.Add(new PolicyLintFinding(PolicyLintKind.VirtualFilter, filter.Source, filter.Name, CodeVirtualFilterRejected));
            }
        }

        return new PolicyLintReport(checkedCount, findings) { Unchecked = unchecked_ };
    }
}
