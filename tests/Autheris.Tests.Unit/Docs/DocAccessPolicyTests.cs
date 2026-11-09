namespace Autheris.Tests.Unit.Docs;

using Autheris.Application.Docs.Services;
using Shouldly;
using Xunit;

public sealed class DocAccessPolicyTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Null_session_cannot_deposit()
    {
        var caller = new DocCaller(
            IsCanonicalClusterAdmin: false,
            IsGovernanceAdmin: true,
            TenantId: "tenant-a",
            AuthorSid: "user-1",
            HasSession: false,
            IsReadOnlySession: false);

        var writer = DocAccessPolicy.ResolveWriter(caller, "my-app", out var denial);

        writer.ShouldBeNull();
        denial.ShouldNotBeNull();
        denial.Code.ShouldBe("FORBIDDEN");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Read_only_session_cannot_deposit()
    {
        var caller = new DocCaller(
            IsCanonicalClusterAdmin: true,
            IsGovernanceAdmin: true,
            TenantId: "tenant-a",
            AuthorSid: "admin-1",
            HasSession: true,
            IsReadOnlySession: true);

        var writer = DocAccessPolicy.ResolveWriter(caller, "my-app", out var denial);

        writer.ShouldBeNull();
        denial.ShouldNotBeNull();
        denial.Code.ShouldBe("FORBIDDEN");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Canonical_cluster_admin_writes_official()
    {
        var caller = new DocCaller(
            IsCanonicalClusterAdmin: true,
            IsGovernanceAdmin: false,
            TenantId: "tenant-a",
            AuthorSid: "cluster-admin",
            HasSession: true,
            IsReadOnlySession: false);

        var writer = DocAccessPolicy.ResolveWriter(caller, "my-app", out var denial);

        writer.ShouldNotBeNull();
        writer.TenantId.ShouldBeNull();
        writer.IsOfficial.ShouldBeTrue();
        writer.AuthorSid.ShouldBe("cluster-admin");
        denial.ShouldBeNull();

        // ClusterAdmin can also write reserved global applications
        var globalWriter = DocAccessPolicy.ResolveWriter(caller, "autheris", out var globalDenial);
        globalWriter.ShouldNotBeNull();
        globalWriter.TenantId.ShouldBeNull();
        globalWriter.IsOfficial.ShouldBeTrue();
        globalDenial.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Governance_admin_writes_only_own_tenant()
    {
        var caller = new DocCaller(
            IsCanonicalClusterAdmin: false,
            IsGovernanceAdmin: true,
            TenantId: "tenant-a",
            AuthorSid: "gov-admin",
            HasSession: true,
            IsReadOnlySession: false);

        var writer = DocAccessPolicy.ResolveWriter(caller, "tenant-app", out var denial);

        writer.ShouldNotBeNull();
        writer.TenantId.ShouldBe("tenant-a");
        writer.IsOfficial.ShouldBeFalse();
        writer.AuthorSid.ShouldBe("gov-admin");
        denial.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Governance_admin_without_tenant_is_denied()
    {
        var callerNullTenant = new DocCaller(
            IsCanonicalClusterAdmin: false,
            IsGovernanceAdmin: true,
            TenantId: null,
            AuthorSid: "gov-admin",
            HasSession: true,
            IsReadOnlySession: false);

        var writerNull = DocAccessPolicy.ResolveWriter(callerNullTenant, "app", out var denialNull);
        writerNull.ShouldBeNull();
        denialNull.ShouldNotBeNull();

        var callerEmptyTenant = new DocCaller(
            IsCanonicalClusterAdmin: false,
            IsGovernanceAdmin: true,
            TenantId: "   ",
            AuthorSid: "gov-admin",
            HasSession: true,
            IsReadOnlySession: false);

        var writerEmpty = DocAccessPolicy.ResolveWriter(callerEmptyTenant, "app", out var denialEmpty);
        writerEmpty.ShouldBeNull();
        denialEmpty.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("autheris")]
    [InlineData("AUTHERIS")]
    [InlineData("global")]
    [InlineData("Global")]
    [InlineData("system")]
    [Trait("Category", "Security")]
    public void Non_cluster_admin_cannot_write_reserved_apps(string reservedAppId)
    {
        var caller = new DocCaller(
            IsCanonicalClusterAdmin: false,
            IsGovernanceAdmin: true,
            TenantId: "tenant-a",
            AuthorSid: "gov-admin",
            HasSession: true,
            IsReadOnlySession: false);

        var writer = DocAccessPolicy.ResolveWriter(caller, reservedAppId, out var denial);

        writer.ShouldBeNull();
        denial.ShouldNotBeNull();
        denial.Code.ShouldBe("FORBIDDEN");
        denial.Reason.ShouldContain("global application requires ClusterAdmin");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Reader_scope_never_contains_another_tenant()
    {
        var tenantCaller = new DocCaller(
            IsCanonicalClusterAdmin: false,
            IsGovernanceAdmin: false,
            TenantId: "tenant-x",
            AuthorSid: "user-1",
            HasSession: true,
            IsReadOnlySession: false);

        var tenantScope = DocAccessPolicy.ResolveReader(tenantCaller);
        tenantScope.TenantId.ShouldBe("tenant-x");

        var clusterAdminWithTenant = new DocCaller(
            IsCanonicalClusterAdmin: true,
            IsGovernanceAdmin: false,
            TenantId: "tenant-admin",
            AuthorSid: "admin-1",
            HasSession: true,
            IsReadOnlySession: false);

        var clusterScope = DocAccessPolicy.ResolveReader(clusterAdminWithTenant);
        clusterScope.TenantId.ShouldBe("tenant-admin");

        var clusterAdminWithoutTenant = new DocCaller(
            IsCanonicalClusterAdmin: true,
            IsGovernanceAdmin: false,
            TenantId: null,
            AuthorSid: "admin-1",
            HasSession: true,
            IsReadOnlySession: false);

        var officialOnlyScope = DocAccessPolicy.ResolveReader(clusterAdminWithoutTenant);
        officialOnlyScope.TenantId.ShouldBeNull();
    }
}
