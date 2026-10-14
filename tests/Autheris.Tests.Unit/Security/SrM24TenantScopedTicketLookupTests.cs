using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>SR-M-24: every provider must implement the tenant-scoped ITSM ticket lookup; the interface default must not filter an unscoped lookup.</summary>
public sealed class SrM24TenantScopedTicketLookupTests
{
    private static readonly Type[] ScopedSignature = [typeof(string), typeof(TenantId), typeof(CancellationToken)];

    [Theory]
    [InlineData(typeof(SqliteGovernanceRepository))]
    [InlineData(typeof(PostgreSqlGovernanceRepository))]
    [InlineData(typeof(SqlServerGovernanceRepository))]
    public void SR_M_24_Provider_Declares_Tenant_Scoped_Ticket_Lookup(Type provider)
    {
        var method = provider.GetMethod(nameof(IGovernanceRepository.GetConsentRequestByTicketIdAsync), ScopedSignature);

        method.ShouldNotBeNull();
        method.DeclaringType.ShouldBe(provider);
    }

    [Fact]
    public void SR_M_24_Interface_Default_Is_Not_Silently_Unscoped()
    {
        var method = typeof(IGovernanceRepository).GetMethod(nameof(IGovernanceRepository.GetConsentRequestByTicketIdAsync), ScopedSignature);

        method.ShouldNotBeNull();
        // Default interface method (has a body) that fails closed: its body must not call the unscoped overload.
        method.IsAbstract.ShouldBeFalse();
        var il = method.GetMethodBody()!.GetILAsByteArray()!;
        var unscoped = typeof(IGovernanceRepository).GetMethod(nameof(IGovernanceRepository.GetConsentRequestByTicketIdAsync), [typeof(string), typeof(CancellationToken)])!;
        var token = BitConverter.GetBytes(unscoped.MetadataToken);
        il.AsSpan().IndexOf(token).ShouldBe(-1);
    }
}
