namespace Autheris.Application.Sql.Internal;

using System.Collections.Generic;
using Autheris.Domain.Common;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;

/// <param name="Compiled">The only SQL text that may reach a command: the output of <c>ISqlEngine.Compile</c>, every value bound.</param>
/// <param name="DeliveredRowLimit">Rows handed to the caller; the statement reads one more as probe (0 = no probe).</param>
/// <param name="VirtualFilters">Virtual filters applied per referenced table (audit).</param>
/// <param name="CallerAttributes">REQ-GOV-129 (BC-129-15): the caller attributes resolved per referenced table (audit only: ids, outcomes, reason codes).</param>
/// <param name="KeySets">Issue #107: per site outcome of the row-filter key-set cache (audit details and Debug log only, never a client surface: SEC-RF107-09 c).</param>
internal sealed record GovernedRewrite(
    CompiledSql Compiled,
    IReadOnlyList<TableIdentifier> AccessedTables,
    string DataSourceName,
    long DeliveredRowLimit = 0,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? VirtualFilters = null,
    IReadOnlyList<Autheris.Application.Sql.Policy.RowFilterKeySets.RowFilterKeySetOutcome>? KeySets = null,
    IReadOnlyDictionary<string, IReadOnlyList<Autheris.Domain.Model.CallerAttributeAuditEntry>>? CallerAttributes = null);

/// <summary>
/// Collects the DML classification during governance so that executed AND rejected DML can be audited.
/// </summary>
internal sealed class DmlAuditContext
{
    public SqlStatementType? StatementType { get; set; }

    public IReadOnlyList<string> Tables { get; set; } = [];

    /// <summary>SEC-277-09: the analysis found a <c>WITH RECURSIVE</c>; known before any compile, so rejection audits can carry the flag.</summary>
    public bool HasRecursiveCte { get; set; }

    public bool IsDml => StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete;
}
