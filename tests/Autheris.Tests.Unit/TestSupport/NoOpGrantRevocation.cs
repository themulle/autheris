namespace Autheris.Tests.Unit.TestSupport;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Common;

/// <summary>Grant revocation double for deprovisioning tests that do not exercise grants: revokes nothing and reports a complete run.</summary>
internal sealed class NoOpGrantRevocation : IGrantRevocationService
{
    public static readonly NoOpGrantRevocation Instance = new();

    public Task<GrantRevocationResult> RevokeAllForSubjectAsync(TenantId tenant, Sid subject, string reason, CancellationToken ct = default) =>
        Task.FromResult(new GrantRevocationResult(0, 0, 0, 0, false));
}
