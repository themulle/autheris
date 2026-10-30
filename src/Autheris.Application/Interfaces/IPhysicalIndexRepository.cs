using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Interfaces;

/// <summary>
/// Issue #404: the narrow write seam of the physical (source-observed) non-unique indexes of a table, separate from the declared key
/// definition (<see cref="IGovernanceRepository.ReplaceTableKeyDefinitionAsync"/>, ISP). One transaction per call: the stored list is
/// replaced wholesale (caps and the ordinal contract are re-validated, a violation throws and writes nothing) and the table epoch is
/// bumped in the same transaction. Returns false when the table does not exist.
/// </summary>
public interface IPhysicalIndexRepository
{
    Task<bool> ReplacePhysicalIndexesAsync(TableIdentifier table, IReadOnlyList<PhysicalIndexDefinition> indexes, CancellationToken ct = default);
}
