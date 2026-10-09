namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Federation.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Federation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class FederationTests
{
    [Fact]
    public void SubgraphContextPropagation_AppliesHeaders_WhenEnabled()
    {
        var options = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableZeroTrustContextForwarding = true,
                SubjectHeaderName = "X-Gateway-Subject",
                TenantHeaderName = "X-Tenant-ID",
                RolesHeaderName = "X-Gateway-Roles"
            }
        });

        var service = new SubgraphContextPropagationService(options, NullLogger<SubgraphContextPropagationService>.Instance);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://subgraph.corp.internal.example.org/graphql");

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-123"),
            new Claim(ClaimTypes.Role, "FinanceUser"),
            new Claim(ClaimTypes.Role, "Auditor")
        ], "TestAuth"));

        service.ApplySecurityHeaders(request, "FinanceSubgraph", principal, "tenant-alpha");

        request.Headers.Contains("X-Gateway-Subject").ShouldBeTrue();
        request.Headers.GetValues("X-Gateway-Subject").ShouldContain("S-1-5-21-USER-123");

        request.Headers.Contains("X-Tenant-ID").ShouldBeTrue();
        request.Headers.GetValues("X-Tenant-ID").ShouldContain("tenant-alpha");

        request.Headers.Contains("X-Gateway-Roles").ShouldBeTrue();
        var rolesVal = string.Join(",", request.Headers.GetValues("X-Gateway-Roles"));
        rolesVal.ShouldContain("FinanceUser");
        rolesVal.ShouldContain("Auditor");

        request.Headers.Contains("X-Correlation-ID").ShouldBeTrue();
    }

    [Fact]
    public void SubgraphContextPropagation_BlocksRestrictedIp_SSRF()
    {
        var options = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions { Enabled = true }
        });

        var service = new SubgraphContextPropagationService(options, NullLogger<SubgraphContextPropagationService>.Instance);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5000/graphql");

        Should.Throw<SecurityException>(() =>
        {
            service.ApplySecurityHeaders(request, "LocalExploit", null, null);
        });
    }

    [Fact]
    public void SubgraphResultMasker_MasksSensitiveFields_Recursively()
    {
        var maskingProvider = new ColumnMaskingProvider();
        var options = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true
            }
        });

        var masker = new SubgraphResultMasker(maskingProvider, options, NullLogger<SubgraphResultMasker>.Instance);

        var rawData = new Dictionary<string, object?>
        {
            ["id"] = "CUST-99",
            ["userEmail"] = "alice@company.com",
            ["customerIban"] = "DE89 3704 0044 0532 0130 00",
            ["phoneNumber"] = "+49 170 1234567",
            ["salary"] = "95000",
            ["nested"] = new Dictionary<string, object?>
            {
                ["contactEmail"] = "bob@partner.org",
                ["diagnosis"] = "Hypertension"
            },
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["accountEmail"] = "charlie@web.de"
                }
            }
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "StandardReader")
        ], "Test"));

        var masked = masker.MaskResultData(rawData, principal) as IReadOnlyDictionary<string, object?>;
        masked.ShouldNotBeNull();

        masked["id"].ShouldBe("CUST-99");
        masked["userEmail"]!.ToString()!.ShouldStartWith("a***@");
        masked["customerIban"]!.ToString()!.ShouldStartWith("DE");
        masked["customerIban"]!.ToString()!.ShouldEndWith("3000");
        masked["phoneNumber"]!.ToString()!.ShouldContain("***");
        masked["salary"]!.ToString()!.ShouldBe("[REDACTED]");

        var nested = masked["nested"] as IReadOnlyDictionary<string, object?>;
        nested.ShouldNotBeNull();
        nested["contactEmail"]!.ToString().ShouldStartWith("b***@");
        nested["diagnosis"]!.ToString().ShouldBe("[REDACTED]");

        var items = masked["items"] as List<object?>;
        items.ShouldNotBeNull();
        var item0 = items[0] as IReadOnlyDictionary<string, object?>;
        item0.ShouldNotBeNull();
        item0["accountEmail"]!.ToString().ShouldStartWith("c***@");
    }

    [Fact]
    public void SubgraphResultMasker_GovernanceAdmin_BypassesMasking()
    {
        var maskingProvider = new ColumnMaskingProvider();
        var options = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true
            }
        });

        var masker = new SubgraphResultMasker(maskingProvider, options, NullLogger<SubgraphResultMasker>.Instance);

        var rawData = new Dictionary<string, object?>
        {
            ["email"] = "admin@corp.local",
            ["salary"] = "150000"
        };

        var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ], "Test"));

        var result = masker.MaskResultData(rawData, adminPrincipal) as IReadOnlyDictionary<string, object?>;
        result.ShouldNotBeNull();
        result["email"].ShouldBe("admin@corp.local");
        result["salary"].ShouldBe("150000");
    }

    [Fact]
    public void POL_13_SubgraphResultMasker_EvaluatesAdminRolesViaRoleEvaluator_BeforeBypassingMask()
    {
        var maskingProvider = new ColumnMaskingProvider();
        var options = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions
            {
                Enabled = true,
                EnableResultMasking = true
            }
        });

        var roleEvaluatorMock = Substitute.For<IGatewayRoleEvaluator>();
        // Mock explicitly rejects admin role
        roleEvaluatorMock.HasAnyRole(Arg.Any<ClaimsPrincipal>(), Arg.Any<IEnumerable<GatewayRole>>(), Arg.Any<string?>())
            .Returns(false);

        var masker = new SubgraphResultMasker(maskingProvider, options, NullLogger<SubgraphResultMasker>.Instance, roleEvaluatorMock);

        var rawData = new Dictionary<string, object?>
        {
            ["email"] = "admin@corp.local",
            ["salary"] = "150000"
        };

        // Principal has raw claim string, but roleEvaluator rejects it
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ], "Test"));

        var result = masker.MaskResultData(rawData, principal) as IReadOnlyDictionary<string, object?>;
        result.ShouldNotBeNull();
        // Since role evaluator rejected admin, email must be masked, NOT cleartext!
        result["email"]!.ToString().ShouldNotBe("admin@corp.local");
        result["email"]!.ToString().ShouldStartWith("a***@");
    }

    [Fact]
    public async Task SubgraphSecurityDelegatingHandler_AppliesSecurityContext_AndForwardsBearer()
    {
        var propagationService = Substitute.For<ISubgraphContextPropagationService>();
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        var httpContext = new DefaultHttpContext();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-TEST")], "Bearer"));
        httpContext.User = principal;
        httpContext.Items["TenantId"] = "tenant-xyz";
        httpContext.Request.Headers["Authorization"] = "Bearer test-jwt-token-123";
        httpContextAccessor.HttpContext.Returns(httpContext);

        var fedOptions = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions
            {
                ForwardAuthorizationBearer = true
            }
        });

        var innerHandler = new TestHttpMessageHandler();
        var handler = new SubgraphSecurityDelegatingHandler(
            "InventorySubgraph",
            propagationService,
            httpContextAccessor,
            NullLogger<SubgraphSecurityDelegatingHandler>.Instance,
            fedOptions)
        {
            InnerHandler = innerHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://93.184.216.34/graphql"); // SEC I-1: unresolvable hosts fail closed, so use a public IP literal

        await client.SendAsync(request, CancellationToken.None);

        propagationService.Received(1).ApplySecurityHeaders(
            Arg.Any<HttpRequestMessage>(),
            "InventorySubgraph",
            principal,
            "tenant-xyz");

        innerHandler.LastRequest.ShouldNotBeNull();
        innerHandler.LastRequest.Headers.Authorization.ShouldNotBeNull();
        innerHandler.LastRequest.Headers.Authorization.Parameter.ShouldBe("test-jwt-token-123");
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{}}")
            });
        }
    }
}
