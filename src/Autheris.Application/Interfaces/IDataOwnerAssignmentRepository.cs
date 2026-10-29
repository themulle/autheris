namespace Autheris.Application.Interfaces;

/// <summary>
/// Write path of table owners for the dbt governance import (issue #393 part b), separate from <see cref="IDataOwnershipRepository"/> (ISP).
/// One transaction per call: resolves tables and owners, never updates or deletes an existing DATA_OWNERS row, creates owners and
/// <c>dbt:</c>-provenance TABLE_OWNERS rows, and in replace mode removes only <c>dbt:</c> rows of the listed tables. A failure or a
/// <see cref="DataOwnerAssignmentRequest.DryRun"/> leaves the database unchanged.
/// </summary>
public interface IDataOwnerAssignmentRepository
{
    Task<DataOwnerAssignmentResult> ApplyTableOwnerAssignmentsAsync(DataOwnerAssignmentRequest request, CancellationToken ct = default);
}
