namespace Autheris.Application.Sql.Internal;

using System.Collections.Generic;
using Autheris.Domain.Common;
using TrinoSqlEngine.Analysis;

/// <param name="DeliveredRowLimit">Rows handed to the caller; the statement reads one more as probe (0 = no probe).</param>
/// <param name="VirtualFilters">Virtual filters applied per referenced table (audit).</param>
internal sealed record GovernedRewrite(
    string Sql,
    IReadOnlyDictionary<string, object?> InternalParameters,
    IReadOnlyList<TableIdentifier> AccessedTables,
    string DataSourceName,
    long DeliveredRowLimit = 0,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? VirtualFilters = null);

/// <summary>
/// Collects the DML classification during governance so that executed AND rejected DML can be audited.
/// </summary>
internal sealed class DmlAuditContext
{
    public SqlStatementType? StatementType { get; set; }

    public IReadOnlyList<string> Tables { get; set; } = [];

    public bool IsDml => StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete;
}
