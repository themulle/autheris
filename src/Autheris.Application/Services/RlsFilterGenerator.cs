using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Services;

public sealed class RlsFilterGenerator : IRlsFilterGenerator
{
    public static readonly RlsFilterGenerator Instance = new();

    public RlsFilterGenerator(IOptions<GatewayOptions>? options = null)
    {
        _ = options;
    }

    public string BuildCrossSourceSetFilter(ConsentRowFilter filter, int maxBatchSize = 500, DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        return AdvancedRlsFilterGenerator.BuildCrossSourceSetFilter(filter, maxBatchSize, dialect);
    }

    public PolicyPredicate BuildCrossSourceSetPredicate(ConsentRowFilter filter, DatabaseDialect dialect, string parameterPrefix = "__gql_rls_")
    {
        return AdvancedRlsFilterGenerator.BuildCrossSourceSetPredicate(filter, dialect, parameterPrefix);
    }
}
