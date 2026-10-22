namespace Autheris.Application.Governance.Interfaces;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public interface IDeclarativeConsentApplyService
{
    /// <param name="callerContext">
    /// The resolved request security context of <paramref name="caller"/>. Its group SIDs and roles (including plugin-enriched ones)
    /// join the claims of the principal in the self-grant check; null uses the claims only.
    /// </param>
    Task<DeclarativeApplyResult> ApplyManifestAsync(
        ClaimsPrincipal caller,
        DeclarativeConsentManifest manifest,
        bool dryRun = false,
        SecurityPrincipalContext? callerContext = null,
        CancellationToken ct = default);
}
