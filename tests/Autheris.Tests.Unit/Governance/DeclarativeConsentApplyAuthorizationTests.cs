using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>#667: the apply service checks the same canonical roles as the endpoint, nothing else.</summary>
[Trait("Category", "Security")]
public sealed class DeclarativeConsentApplyAuthorizationTests
{
    private static DeclarativeConsentApplyService Service(FakeConsentRepository repo)
    {
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata
            {
                Table = new Autheris.Domain.Model.Table { Id = Guid.NewGuid() },
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "id" }]
            });
        return new DeclarativeConsentApplyService(tables, repo, Substitute.For<IAuditLogRepository>());
    }

    private static DeclarativeConsentManifest Manifest() =>
        new("1", [new DeclarativeConsentPolicy("default", "sales", "orders", "Group", "S-1-5-21-GROUP-DS", "Allow")]);

    private static ClaimsPrincipal Caller(params Claim[] claims) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim("tenant_id", "tenant-a"), .. claims], "test"));

    [Theory]
    [InlineData("GovernanceAdmin")]
    [InlineData("ClusterAdmin")]
    public async Task Apply_CanonicalAdminRole_IsAccepted(string role)
    {
        var repo = new FakeConsentRepository();

        await Service(repo).ApplyManifestAsync(Caller(new Claim(ClaimTypes.Role, role)), Manifest());

        repo.ReplaceCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("GOVERNANCE_ADMIN")]
    [InlineData("CLUSTER_ADMIN")]
    [InlineData("DATA_GOVERNANCE_REVIEWER")]
    [InlineData("Consumer")]
    public async Task Apply_NonCanonicalOrUnprivilegedRole_IsRejected_WithForbiddenException(string role)
    {
        var repo = new FakeConsentRepository();

        await Should.ThrowAsync<ConsentApplyForbiddenException>(
            () => Service(repo).ApplyManifestAsync(Caller(new Claim(ClaimTypes.Role, role)), Manifest()));

        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_ScopeContainingGovernanceAdminSubstring_IsRejected()
    {
        var repo = new FakeConsentRepository();

        await Should.ThrowAsync<ConsentApplyForbiddenException>(
            () => Service(repo).ApplyManifestAsync(Caller(new Claim("scope", "x-governance:admin-y")), Manifest()));

        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_ForbiddenException_IsNotASecurityException()
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<ConsentApplyForbiddenException>(
            () => Service(repo).ApplyManifestAsync(Caller(), Manifest()));

        ex.ShouldNotBeAssignableTo<System.Security.SecurityException>();
    }
}
