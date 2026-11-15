using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>SR-M-24: every provider must implement the tenant-scoped external reference lookup; no unscoped lookup exists.</summary>
[Trait("Category", "Security")]
public sealed class SrM24TenantScopedTicketLookupTests
{
    private static readonly Type[] ScopedSignature = [typeof(Guid), typeof(string), typeof(TenantId), typeof(CancellationToken)];

    [Theory]
    [InlineData(typeof(GovernanceRepository))]
    public void SR_M_24_Provider_Declares_Tenant_Scoped_Ticket_Lookup(Type provider)
    {
        var method = provider.GetMethod(nameof(IGovernanceRepository.GetConsentRequestByExternalReferenceAsync), ScopedSignature);

        method.ShouldNotBeNull();
        method.DeclaringType.ShouldBe(provider);
    }

    [Fact]
    public void SR_M_24_Interface_Default_Is_Not_Silently_Unscoped()
    {
        var method = typeof(IConsentApprovalRepository).GetMethod(nameof(IConsentApprovalRepository.GetConsentRequestByExternalReferenceAsync), ScopedSignature);

        method.ShouldNotBeNull();
        var unscoped = typeof(IConsentApprovalRepository).GetMethod(nameof(IConsentApprovalRepository.GetConsentRequestByExternalReferenceAsync), [typeof(Guid), typeof(string), typeof(CancellationToken)]);
        unscoped.ShouldBeNull();
    }
}
