namespace Autheris.Application.Policy.Lint;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;
using VirtualFilter = Autheris.Domain.Model.VirtualFilter;

/// <summary>The kind of stored predicate a finding refers to.</summary>
public enum PolicyLintKind
{
    AccessProfileRowFilter,
    VirtualFilter
}

/// <summary>Everything the lint reads from the governance store.</summary>
public sealed record PolicyLintInput(
    IReadOnlyList<TableMetadata> Catalog,
    IReadOnlyList<AccessProfile> AccessProfiles,
    IReadOnlyList<VirtualFilter> VirtualFilters)
{
    public IReadOnlyList<string> AdditionalAllowedFunctions { get; init; } = [];

    /// <summary>The active caller attribute registry; null when the feature is off: a placeholder is then a finding.</summary>
    public Autheris.Application.Identity.Attributes.CallerAttributeSnapshot? CallerAttributes { get; init; }
}

/// <summary>One unparsable predicate. Carries ids and a stable code only: never predicate text or parameter values.</summary>
public sealed record PolicyLintFinding(PolicyLintKind Kind, string Table, string PolicyId, string Code);

public sealed record PolicyLintReport(int Checked, IReadOnlyList<PolicyLintFinding> Findings)
{
    /// <summary>Predicates whose target matches no catalog table; they could not be checked against a table.</summary>
    public int Unchecked { get; init; }

    public bool IsClean => Findings.Count == 0;
}
