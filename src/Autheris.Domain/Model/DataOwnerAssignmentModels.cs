namespace Autheris.Domain.Model;

using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>
/// One owner of a table as requested by the dbt governance import (issue #393 part b). <see cref="LookupOnly"/> marks a bare string:
/// <see cref="Sid"/> then is only a lookup key (sid, account or e-mail) that must resolve to exactly one active row; it never creates a row.
/// <see cref="Role"/> is <c>PRIMARY</c> or <c>DELEGATE</c>.
/// </summary>
public sealed record DataOwnerSpec(
    string Sid,
    string? Account,
    string? DisplayName,
    string? Email,
    string Role,
    bool LookupOnly = false);

/// <summary>The owners requested for one table. An empty list is meaningful in replace mode (removes every <c>dbt:</c> row of the table).</summary>
public sealed record TableOwnerAssignmentSpec(TableIdentifier Table, IReadOnlyList<DataOwnerSpec> Owners);

/// <summary>
/// Request of <c>IDataOwnerAssignmentRepository.ApplyTableOwnerAssignmentsAsync</c>. <see cref="DryRun"/> runs every check and returns the change set
/// but never commits. <see cref="MaxNewOwners"/> bounds the DATA_OWNERS rows one request may create (I393B-SC-06).
/// </summary>
public sealed record DataOwnerAssignmentRequest(
    IReadOnlyList<TableOwnerAssignmentSpec> Tables,
    bool Replace,
    bool DryRun,
    int MaxNewOwners = 1000);

public enum DataOwnerChangeKind
{
    OwnerCreated,
    OwnerAssigned,
    OwnerRemoved
}

/// <summary>
/// One change of an assignment run. <see cref="OwnerSid"/> is for in-process identity verification only; it must never reach audit details or logs
/// (I393B-SC-03, SC-10). <see cref="RemovedAll"/> is set on removals that leave the table without any owner (I393B-SC-08).
/// </summary>
public sealed record DataOwnerChange(
    DataOwnerChangeKind Kind,
    TableIdentifier Table,
    string OwnerId,
    string OwnerSid,
    string Role,
    bool RemovedAll = false);

/// <summary>Outcome of an assignment run. <see cref="ErrorCode"/> is a governed code; <see cref="ErrorTable"/> names the table, never an identifier value.</summary>
public sealed record DataOwnerAssignmentResult(
    bool Success,
    IReadOnlyList<DataOwnerChange> Changes,
    string? ErrorCode = null,
    string? ErrorTable = null)
{
    public static DataOwnerAssignmentResult Failure(string code, string? table = null) => new(false, [], code, table);
}
