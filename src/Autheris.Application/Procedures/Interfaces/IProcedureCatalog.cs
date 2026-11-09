namespace Autheris.Application.Procedures.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The single visibility path to registered procedures: only Active procedures the caller may see are returned.
/// Unknown, inactive and invisible procedures are indistinguishable (<c>null</c>).
/// </summary>
public interface IProcedureCatalog
{
    Task<IReadOnlyList<RegisteredProcedure>> ListVisibleAsync(ClaimsPrincipal user, TenantId tenantId, CancellationToken ct);

    Task<RegisteredProcedure?> FindVisibleAsync(string name, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct);
}
