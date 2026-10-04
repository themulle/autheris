using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Interfaces;

public sealed record ParameterizedRowFilter(string? Sql, IReadOnlyDictionary<string, object?> Parameters);

public interface IRowFilterSqlBuilder
{
    string FormatCondition(ConsentRowFilter filter, DatabaseDialect dialect = DatabaseDialect.SqlServer, bool isDeny = false);

    string? BuildCombinedRowFilter(
        IReadOnlyList<Consent> aConsents,
        IReadOnlyList<Consent> dConsents,
        DatabaseDialect dialect = DatabaseDialect.SqlServer);

    ParameterizedRowFilter BuildCombinedRowFilterParameterized(
        IReadOnlyList<Consent> aConsents,
        IReadOnlyList<Consent> dConsents,
        DatabaseDialect dialect = DatabaseDialect.SqlServer);
}
