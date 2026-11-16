namespace Autheris.Application.Sql.Endpoints.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;

public enum SqlEndpointInvocation
{
    Get,
    Post
}

public interface ISqlEndpointExecutionService
{
    Task<GovernedSqlResult> ExecuteEndpointAsync(
        string endpointName,
        IReadOnlyDictionary<string, object?>? rawInputs,
        ClaimsPrincipal user,
        TenantId tenantId,
        SqlEndpointInvocation invocation,
        CancellationToken ct = default);
}
