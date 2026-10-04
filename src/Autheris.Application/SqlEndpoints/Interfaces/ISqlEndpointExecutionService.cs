namespace Autheris.Application.SqlEndpoints.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;

public interface ISqlEndpointExecutionService
{
    Task<GovernedSqlResult> ExecuteEndpointAsync(
        string endpointName,
        IReadOnlyDictionary<string, object?>? rawInputs,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);
}
