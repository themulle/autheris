namespace Autheris.Application.VirtualFilters.Federation;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// F-GOV-14 (M4): the authorized key set of a structured Virtual Filter for a tenant. Structured filters contain no
/// principal reference, so the set does not depend on the caller (which filters apply is decided by grantee matching in
/// the resolver). Every failure throws a <see cref="Autheris.Domain.Exceptions.GatewaySecurityException"/> with a
/// <see cref="VirtualFilterErrorCodes"/> code; a key set is never truncated or guessed.
/// </summary>
public interface IVirtualFilterKeySetProvider
{
    /// <param name="filter">The applied filter (from the resolver's snapshot).</param>
    /// <param name="keyTypes">Key type of each key column, from the catalog type of the protected column it maps to.</param>
    /// <param name="tenant">The caller's tenant; must be the filter's tenant.</param>
    /// <param name="ct">Cancellation (the request timeout covers key loading).</param>
    ValueTask<VirtualFilterKeySet> GetAsync(
        VirtualFilter filter,
        IReadOnlyList<VirtualFilterKeyType> keyTypes,
        TenantId tenant,
        CancellationToken ct = default);
}
