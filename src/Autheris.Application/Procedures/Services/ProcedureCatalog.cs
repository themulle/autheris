namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Visible Active procedures; an evaluation failure omits that procedure and never reaches the caller.</summary>
public sealed class ProcedureCatalog(IProcedureRegistry registry, IProcedureExecutionService execution) : IProcedureCatalog
{
    public async Task<IReadOnlyList<RegisteredProcedure>> ListVisibleAsync(ClaimsPrincipal user, TenantId tenantId, CancellationToken ct)
    {
        var visible = new List<RegisteredProcedure>();
        foreach (var registered in registry.GetAll())
        {
            if (await IsVisibleAsync(registered, user, tenantId, ct).ConfigureAwait(false))
            {
                visible.Add(registered);
            }
        }

        return visible;
    }

    public async Task<RegisteredProcedure?> FindVisibleAsync(string name, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct) =>
        registry.TryGet(name, out var registered) && registered != null && await IsVisibleAsync(registered, user, tenantId, ct).ConfigureAwait(false)
            ? registered
            : null;

    private async Task<bool> IsVisibleAsync(RegisteredProcedure registered, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct)
    {
        if (registered.State != ProcedureState.Active)
        {
            return false;
        }

        try
        {
            return await execution.IsVisibleAsync(registered, user, tenantId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
