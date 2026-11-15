using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Common.Interfaces;

public interface IRlsFilterGenerator
{
    string BuildCrossSourceSetFilter(ConsentRowFilter filter, int maxBatchSize = 500, DatabaseDialect dialect = DatabaseDialect.SqlServer);

    /// <summary>Typed cross-source set filter (one policy parameter per value).</summary>
    PolicyPredicate BuildCrossSourceSetPredicate(ConsentRowFilter filter, DatabaseDialect dialect, string parameterPrefix = "__gql_rls_");
}
