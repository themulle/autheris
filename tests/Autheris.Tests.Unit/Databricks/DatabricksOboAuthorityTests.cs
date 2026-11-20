namespace Autheris.Tests.Unit.Databricks;

using System.Collections.Generic;
using Autheris.Infrastructure.Databricks;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DatabricksOboAuthorityTests
{
    public static IEnumerable<object[]> RejectedAuthorities() =>
    [
        ["http://login.microsoftonline.com/t"],
        ["https://evil.example/t"],
        ["https://login.microsoftonline.com.evil.example/t"],
        ["https://user@login.microsoftonline.com/t"],
        ["https://user:pass@login.microsoftonline.com/t"],
        ["https://login.microsoftonline.com/t?x=1"],
        ["https://login.microsoftonline.com/t?"],
        ["https://login.microsoftonline.com/t#frag"],
        ["https://login.microsoftonline.com/t#"],
        ["https://login.microsoftonline.com/t/extra"],
        ["https://login.microsoftonline.com/t/oauth2/v2.0/token"],
        ["https://login.microsoftonline.com:8443/t"],
        ["https://login.microsoftonline.com/"],
        ["https://login.microsoftonline.com"],
        ["https://login.microsoftonline.com//t"],
        ["https://login.microsoftonline.com/t//"],
        ["https://login.microsoftonline.com/common"],
        ["https://login.microsoftonline.com/COMMON"],
        ["https://login.microsoftonline.com/organizations"],
        ["https://login.microsoftonline.com/consumers"],
        ["https://login.microsoftonline.com/.."],
        ["https://login.microsoftonline.com/%2e%2e"],
        ["https://login.microsoftonline.com/%74"],
        ["https://login.microsoftonline.com/a/../t"],
        ["https://login.microsoftonline.com/a/%2e%2e/t"],
        ["https://login.microsoftonline.com/a/./t"],
        ["https://login.microsoftonline.com/./t"],
        ["https://login.microsoftonline.com/t/./"],
        ["https://login.microsoftonline.com/a/./../t"],
        ["https://login.microsoftonline.com/t%2fextra"],
        ["https://login.microsoftonline.com/t\\extra"],
        ["https://login.microsoftonline.com/t space"],
        ["not-a-uri"]
    ];

    public static IEnumerable<object[]> InvalidTenantIds() =>
    [
        ["t/../../evil"],
        ["t?x=1"],
        ["t#frag"],
        ["t@evil"],
        ["t:8443"],
        [".."],
        ["."],
        [".tenant"],
        ["tenant."],
        ["t..evil"],
        ["t%2fextra"],
        ["t with spaces"],
        ["t\\evil"]
    ];

    public static IEnumerable<object[]> AcceptedNationalClouds() =>
    [
        ["https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555", "https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/oauth2/v2.0/token"],
        ["https://login.microsoftonline.com/contoso.onmicrosoft.com/", "https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/token"],
        ["https://login.microsoftonline.us/my-gov-tenant", "https://login.microsoftonline.us/my-gov-tenant/oauth2/v2.0/token"],
        ["https://login.partner.microsoftonline.cn/my-cn-tenant", "https://login.partner.microsoftonline.cn/my-cn-tenant/oauth2/v2.0/token"],
        ["  HTTPS://LOGIN.MICROSOFTONLINE.COM:443/tenant-123/  ", "https://login.microsoftonline.com/tenant-123/oauth2/v2.0/token"]
    ];

    public static IEnumerable<object?[]> DefaultAuthorityCases() =>
    [
        [null, null, "https://login.microsoftonline.com/common/oauth2/v2.0/token"],
        ["", "", "https://login.microsoftonline.com/common/oauth2/v2.0/token"],
        ["   ", "   ", "https://login.microsoftonline.com/common/oauth2/v2.0/token"],
        [null, "tenant-123", "https://login.microsoftonline.com/tenant-123/oauth2/v2.0/token"],
        ["", "  11111111-2222-3333-4444-555555555555  ", "https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/oauth2/v2.0/token"],
        [null, "common", "https://login.microsoftonline.com/common/oauth2/v2.0/token"],
        [null, "organizations", "https://login.microsoftonline.com/organizations/oauth2/v2.0/token"]
    ];

    [Theory]
    [MemberData(nameof(RejectedAuthorities))]
    public void Authority_Rejected(string authority)
    {
        var ok = DatabricksOboAuthority.TryNormalize(authority, tenantId: null, out var endpoint, out var error);

        ok.ShouldBeFalse($"Expected authority '{authority}' to be rejected.");
        endpoint.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(InvalidTenantIds))]
    public void NoAuthority_InvalidTenantId_Rejected(string tenantId)
    {
        var ok = DatabricksOboAuthority.TryNormalize(authority: null, tenantId, out var endpoint, out var error);

        ok.ShouldBeFalse($"Expected TenantId '{tenantId}' to be rejected.");
        endpoint.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(AcceptedNationalClouds))]
    public void Authority_Accepted_NationalClouds(string authority, string expectedEndpoint)
    {
        var ok = DatabricksOboAuthority.TryNormalize(authority, tenantId: null, out var endpoint, out var error);

        ok.ShouldBeTrue(error);
        error.ShouldBeEmpty();
        endpoint.ShouldNotBeNull();
        endpoint.AbsoluteUri.ShouldBe(expectedEndpoint);
    }

    [Theory]
    [MemberData(nameof(DefaultAuthorityCases))]
    public void NoAuthority_DefaultsToCommonOrTenant(string? authority, string? tenantId, string expectedEndpoint)
    {
        var ok = DatabricksOboAuthority.TryNormalize(authority, tenantId, out var endpoint, out var error);

        ok.ShouldBeTrue(error);
        error.ShouldBeEmpty();
        endpoint.ShouldNotBeNull();
        endpoint.AbsoluteUri.ShouldBe(expectedEndpoint);
    }
}
