namespace Autheris.Application.Procedures.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Decides which rows of a procedure result the caller may see when the consent on the result table carries a row filter.
/// The filter cannot be pushed into the procedure, so the keys found in the result are matched against the rows the
/// filter allows (semi-join evaluated by the database, so the filter keeps its exact SQL semantics).
/// </summary>
public interface IProcedureRowScopeResolver
{
    /// <summary>
    /// Returns the subset of <paramref name="candidateKeys"/> (one value per key column, in the order of
    /// <paramref name="keyColumns"/>) that the row filter of <paramref name="decision"/> allows on <paramref name="table"/>.
    /// Keys are returned in the canonical form of <see cref="RowScopeKeys.Normalize"/>.
    /// </summary>
    Task<IReadOnlySet<string>> GetAllowedKeysAsync(
        ProcedureDefinition definition,
        TableMetadata table,
        TableAccessDecision decision,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<object?[]> candidateKeys,
        ProcedureSecurityContext security,
        CancellationToken ct);
}
