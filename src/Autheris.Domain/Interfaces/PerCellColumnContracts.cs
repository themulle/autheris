namespace Autheris.Domain.Interfaces;

using System.Collections.Generic;

/// <summary>
/// Column-level access signature paired with the union of grant row scopes that share that signature.
/// </summary>
public sealed record GrantSignatureScope(
    string SignatureKey,
    IReadOnlyDictionary<string, ColumnAccessLevel> ColumnAccess,
    IRowPolicy? ScopePolicy = null);

/// <summary>
/// Opaque per-cell column policy carried by a <see cref="TableAccessDecision"/> when contributing
/// allow grants yield multiple column-level signatures across row scopes.
/// </summary>
public interface IPerCellColumnPolicy
{
    /// <summary>Hash of the signature predicates and column branch assignments; values are omitted.</summary>
    string Fingerprint { get; }

    /// <summary>Distinct effective column signatures and their unioned row scopes, in deterministic order.</summary>
    IReadOnlyList<GrantSignatureScope> Signatures { get; }

    /// <summary>Columns whose effective access varies across signatures.</summary>
    IReadOnlySet<string> RowDependentColumns { get; }

    /// <summary>True when at least one signature covers all rows without a row predicate.</summary>
    bool AnySignatureUnfiltered { get; }

    /// <summary>
    /// Re-projects this per-cell policy under narrowed effective column access (for example,
    /// from an access profile or post-consent restriction), returning null if no column remains row-dependent.
    /// </summary>
    IPerCellColumnPolicy? Narrow(
        IReadOnlyDictionary<string, ColumnAccessLevel> effectiveColumnAccess,
        IReadOnlySet<string>? explicitMaskColumns = null);

    /// <summary>
    /// Applies an additional mandatory or profile row predicate to narrow all rows returned by this per-cell policy.
    /// </summary>
    IPerCellColumnPolicy WithNarrowing(IRowPolicy narrowingPolicy);
}
