using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Services;

public sealed class RlsFilterGenerator : IRlsFilterGenerator
{
    private readonly RowFilterSubqueryStrategy _defaultStrategy;

    public static readonly RlsFilterGenerator Instance = new();

    public RlsFilterGenerator(IOptions<GatewayOptions>? options = null)
    {
        _defaultStrategy = options?.Value.RowFilters?.SubqueryStrategy ?? RowFilterSubqueryStrategy.Exists;
    }

    public string BuildCorrelatedSubquery(
        ConsentRowFilter filter,
        DatabaseDialect dialect = DatabaseDialect.SqlServer,
        RowFilterSubqueryStrategy? strategy = null,
        bool isDeny = false)
    {
        return AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, dialect, strategy ?? _defaultStrategy, isDeny);
    }

    public string BuildCrossSourceSetFilter(ConsentRowFilter filter, int maxBatchSize = 500, DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        return AdvancedRlsFilterGenerator.BuildCrossSourceSetFilter(filter, maxBatchSize, dialect);
    }

    public PolicyPredicate BuildCorrelatedPredicate(ConsentRowFilter filter, string parameterPrefix = "__gql_rls_")
    {
        return AdvancedRlsFilterGenerator.BuildCorrelatedPredicate(filter, parameterPrefix);
    }

    public PolicyPredicate BuildCrossSourceSetPredicate(ConsentRowFilter filter, DatabaseDialect dialect, string parameterPrefix = "__gql_rls_")
    {
        return AdvancedRlsFilterGenerator.BuildCrossSourceSetPredicate(filter, dialect, parameterPrefix);
    }
}
