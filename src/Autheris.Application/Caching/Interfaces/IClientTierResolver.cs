namespace Autheris.Application.Caching.Interfaces;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public sealed record ClientQuotaContext(string SubjectId, ClientTier Tier, ClientQuotaPolicy Policy);

public interface IClientTierResolver
{
    Task<ClientQuotaContext> ResolveAsync(ClaimsPrincipal? principal, string? apiKey, string? clientIp, CancellationToken ct = default);
}
