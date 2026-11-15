namespace Autheris.Application.Usage;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>Resolves canonical group SIDs to configured or SCIM-directory display names.</summary>
public interface IUsageGroupNameResolver
{
    Task<string> ResolveGroupNameAsync(TenantId tenant, string groupSid, CancellationToken ct = default);
}
