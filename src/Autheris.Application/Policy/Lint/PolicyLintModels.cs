namespace Autheris.Application.Policy.Lint;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;
using VirtualFilter = Autheris.Domain.Model.VirtualFilter;

/// <summary>The kind of stored predicate a finding refers to.</summary>
public enum PolicyLintKind
{
    AccessProfileRowFilter,
    VirtualFilter,
    CasbinRowFilter,
    CasbinCorrelatedRowFilter
}

/// <summary>A Casbin row filter to lint: a text rls filter and/or a correlated row filter of one rule.</summary>
/// <param name="RuleId">Identifier of the rule or consent (never the predicate text).</param>
/// <param name="TableId">Object pattern of the rule (<c>domain.schema.table</c>, wildcards allowed).</param>
public sealed record CasbinLintRule(string RuleId, string TableId, string? RlsFilter, ConsentRowFilter? CorrelatedRowFilter);

/// <summary>Everything the lint reads from the governance store.</summary>
public sealed record PolicyLintInput(
    IReadOnlyList<TableMetadata> Catalog,
    IReadOnlyList<AccessProfile> AccessProfiles,
    IReadOnlyList<VirtualFilter> VirtualFilters,
    IReadOnlyList<CasbinLintRule> CasbinRules)
{
    public IReadOnlyList<string> AdditionalAllowedFunctions { get; init; } = [];
}

/// <summary>One unparsable predicate. Carries ids and a stable code only: never predicate text or parameter values.</summary>
public sealed record PolicyLintFinding(PolicyLintKind Kind, string Table, string PolicyId, string Code);

public sealed record PolicyLintReport(int Checked, IReadOnlyList<PolicyLintFinding> Findings)
{
    /// <summary>Predicates whose target matches no catalog table; they could not be checked against a table.</summary>
    public int Unchecked { get; init; }

    public bool IsClean => Findings.Count == 0;
}
