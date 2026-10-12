namespace Autheris.Application.Governance.Interfaces;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface IDeclarativeConsentApplyService
{
    Task<DeclarativeApplyResult> ApplyManifestAsync(
        ClaimsPrincipal caller,
        DeclarativeConsentManifest manifest,
        bool dryRun = false,
        CancellationToken ct = default);
}
