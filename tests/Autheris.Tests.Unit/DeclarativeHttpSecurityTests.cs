namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security;
using System.Security.Claims;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class DeclarativeHttpSecurityTests
{
    private readonly DeclarativeHttpDataSourceExecutor _executor = new(
        new MockHttpClientFactory(),
        NullLogger<DeclarativeHttpDataSourceExecutor>.Instance);

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..\\windows\\win.ini")]
    [InlineData("subdir/../../secret")]
    [InlineData("/absolute/override")]
    public void BuildUrl_WithPathTraversalPlaceholder_ThrowsSecurityException(string maliciousParam)
    {
        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://internal-service.local/api",
            PathTemplate = "/users/{userId}/profile"
        };

        var arguments = new Dictionary<string, object?>
        {
            ["userId"] = maliciousParam
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        Should.Throw<SecurityException>(() =>
            _executor.BuildUrl(descriptor, arguments, principal));
    }

    [Fact]
    public void BuildUrl_WithDangerousSecurityQueryParams_OmitsSensitiveParams()
    {
        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://internal-service.local/api",
            PathTemplate = "/items"
        };

        var arguments = new Dictionary<string, object?>
        {
            ["search"] = "books",
            ["tenant_id"] = "spoofed_tenant",
            ["isAdmin"] = "true",
            ["role"] = "ClusterAdmin"
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var url = _executor.BuildUrl(descriptor, arguments, principal);

        url.ShouldContain("search=books");
        url.ShouldNotContain("tenant_id=spoofed_tenant");
        url.ShouldNotContain("isAdmin=true");
        url.ShouldNotContain("role=ClusterAdmin");
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
