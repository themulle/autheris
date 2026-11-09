using System;
using Autheris.Application.Governance.Paging;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

[Trait("Category", "Security")]
public sealed class GovernanceRepositoryBoundsTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(500, 500)]
    [InlineData(1000, 500)] // Clamped to MaxPageSize = 500
    public void Page_size_and_batch_caps_unchanged(int requestedSize, int expectedClamped)
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

    [Fact]
    public void Subject_chunk_limit_unchanged()
    {
        // SEC-SIMPL-23: statements never carry more than 1000 subject parameters.
        ConsentSubjectFilter.MaxSubjectsPerStatement.ShouldBe(1000);
        var sids = Enumerable.Range(0, 2500).Select(i => "S-" + i).ToArray();
        ConsentSubjectFilter.Chunks(sids).Select(c => c.Count).ShouldBe([1000, 1000, 500]);
    }

    [Fact]
    public void ConsentGrants_hydrate_batch_cap_unchanged()
    {
        // SEC-SIMPL-23: the consent-grants hydration IN list stays at 500 ids per statement.
        ConsentSqlCore.ConsentHydrateBatchSize.ShouldBe(500);
    }
}
