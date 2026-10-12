namespace Autheris.Application.Governance.Interfaces;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IEffectiveAccessExplanationService
{
    Task<EffectiveTableAccessDto> ExplainMyAccessAsync(
        ClaimsPrincipal principal,
        TableIdentifier table,
        CancellationToken ct = default);
}
