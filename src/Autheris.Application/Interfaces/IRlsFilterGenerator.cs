using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Interfaces;

public interface IRlsFilterGenerator
{
    string BuildCorrelatedSubquery(
        ConsentRowFilter filter,
        DatabaseDialect dialect = DatabaseDialect.SqlServer,
        RowFilterSubqueryStrategy? strategy = null,
        bool isDeny = false);
    string BuildCrossSourceSetFilter(ConsentRowFilter filter, int maxBatchSize = 500, DatabaseDialect dialect = DatabaseDialect.SqlServer);
}
