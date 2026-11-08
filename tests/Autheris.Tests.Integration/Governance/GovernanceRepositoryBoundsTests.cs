using System;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class GovernanceRepositoryBoundsTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(500, 500)]
    [InlineData(1000, 500)] // Clamped to MaxPageSize = 500
    public void Consent_page_size_is_strictly_bounded_within_allowed_limits(int requestedSize, int expectedClamped)
    {
        var query = new ConsentPageQuery(Tenant: new TenantId("tenant-alpha"), PageSize: requestedSize);
        query.EffectivePageSize.ShouldBe(expectedClamped);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100, 100)]
    [InlineData(500, 500)]
    [InlineData(10000, 500)] // Clamped to MaxPageSize = 500
    public void Audit_page_size_is_strictly_bounded_within_allowed_limits(int requestedSize, int expectedClamped)
    {
        var query = new AuditLogPageQuery(Tenant: new TenantId("tenant-alpha"), PageSize: requestedSize);
        query.EffectivePageSize.ShouldBe(expectedClamped);
    }
}
