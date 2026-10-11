using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Interfaces;

public interface IRlsFilterGenerator
{
    string BuildCorrelatedSubquery(
        ConsentRowFilter filter,
        DatabaseDialect dialect = DatabaseDialect.SqlServer,
        RowFilterSubqueryStrategy? strategy = null,
        bool isDeny = false);
    string BuildCrossSourceSetFilter(ConsentRowFilter filter, int maxBatchSize = 500, DatabaseDialect dialect = DatabaseDialect.SqlServer);

    /// <summary>Typed correlated row filter (values are policy parameters, never inline text).</summary>
    PolicyPredicate BuildCorrelatedPredicate(ConsentRowFilter filter, string parameterPrefix = "__gql_rls_");

    /// <summary>Typed cross-source set filter (one policy parameter per value).</summary>
    PolicyPredicate BuildCrossSourceSetPredicate(ConsentRowFilter filter, DatabaseDialect dialect, string parameterPrefix = "__gql_rls_");
}
