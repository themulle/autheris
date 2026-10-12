namespace Autheris.Application.VirtualFilters.Services;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Provides authorized key sets and tuples from virtual filter data sources (e.g. MSSQL view)
/// for short-circuit evaluation, pushdown filtering, and DuckDB federated joins.
/// </summary>
public interface IVirtualFilterKeyProvider
{
    ValueTask<IReadOnlySet<string>> GetAllowedKeysAsync(
        VirtualFilter filter,
        ClaimsPrincipal user,
        TenantId tenantId,
        string keyColumn,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GetAllowedKeyTuplesAsync(
        VirtualFilter filter,
        ClaimsPrincipal user,
        TenantId tenantId,
        IReadOnlyList<string> keyColumns,
        CancellationToken ct = default);
}
