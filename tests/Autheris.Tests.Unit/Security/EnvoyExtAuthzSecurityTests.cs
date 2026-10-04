namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Autheris.Application.Interfaces;
using Autheris.Application.Mesh.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed class EnvoyExtAuthzSecurityTests
{
    private readonly IPolicyEnforcementService _policyService;
    private readonly EnvoyExtAuthzService _service;

    public EnvoyExtAuthzSecurityTests()
    {
        _policyService = Substitute.For<IPolicyEnforcementService>();
        _service = new EnvoyExtAuthzService(
            _policyService,
            NullLogger<EnvoyExtAuthzService>.Instance);
    }

    [Fact]
    public async Task CheckAsync_AuthorizedSubject_ReturnsOkWithSecurityHeaders()
    {
        // Arrange
        var table = new TableIdentifier("default", "public", "customers");
        var allowedDecision = new TableAccessDecision(
            Table: table,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: "tenant_id = 'corp'",
            DeniedReasons: Array.Empty<string>());

        _policyService
            .EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(allowedDecision);

        var request = new EnvoyCheckRequest
        {
            Attributes = new EnvoyAttributeContext
            {
                Request = new EnvoyRequest
                {
                    Http = new EnvoyHttpRequest
                    {
                        Method = "GET",
                        Path = "/api/v1/customers",
                        Headers = new Dictionary<string, string>()
                    }
                }
            }
        };

        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "alice"),
                new System.Security.Claims.Claim("tenant_id", "corp")
            },
            "MeshTls"));

        // Act
        var response = await _service.CheckAsync(request, caller);

        // Assert
        Assert.Equal(0, response.Status.Code);
        Assert.NotNull(response.HttpResponse.OkResponse);

        var headers = response.HttpResponse.OkResponse.Headers;
        Assert.Contains(headers, h => h.Header.Key == "x-autheris-decision" && h.Header.Value == "allowed");
        Assert.Contains(headers, h => h.Header.Key == "x-autheris-principal" && h.Header.Value == "alice");
        Assert.Contains(headers, h => h.Header.Key == "x-autheris-tenant" && h.Header.Value == "corp");
        // SEC C-1: Never return RLS SQL filter to caller/proxy
        Assert.DoesNotContain(headers, h => h.Header.Key == "x-autheris-rls-filter");
    }

    [Fact]
    public async Task CheckAsync_DeniedSubject_ReturnsPermissionDenied403()
    {
        // Arrange
        var table = new TableIdentifier("default", "public", "salaries");
        var deniedDecision = new TableAccessDecision(
            Table: table,
            IsAllowed: false,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: null,
            DeniedReasons: new[] { "Casbin ABAC: insufficient clearance level" });

        _policyService
            .EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(deniedDecision);

        var request = new EnvoyCheckRequest
        {
            Attributes = new EnvoyAttributeContext
            {
                Request = new EnvoyRequest
                {
                    Http = new EnvoyHttpRequest
                    {
                        Method = "GET",
                        Path = "/api/v1/salaries",
                        Headers = new Dictionary<string, string>()
                    }
                }
            }
        };

        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "intern-bob"),
                new System.Security.Claims.Claim("tenant_id", "corp")
            },
            "MeshTls"));

        // Act
        var response = await _service.CheckAsync(request, caller);

        // Assert
        Assert.Equal(7, response.Status.Code); // PERMISSION_DENIED
        Assert.NotNull(response.HttpResponse.DeniedResponse);
        Assert.Equal(403, response.HttpResponse.DeniedResponse.Status.Code);
        Assert.Contains("insufficient clearance level", response.HttpResponse.DeniedResponse.Body);
    }

    [Fact]
    public async Task CheckAsync_MissingHttpAttributes_FailsClosedWithBadRequest()
    {
        // Arrange: Missing HTTP request attributes
        var request = new EnvoyCheckRequest
        {
            Attributes = new EnvoyAttributeContext()
        };

        // Act
        var response = await _service.CheckAsync(request);

        // Assert
        Assert.Equal(7, response.Status.Code);
        Assert.NotNull(response.HttpResponse.DeniedResponse);
        Assert.Equal(400, response.HttpResponse.DeniedResponse.Status.Code);
    }

    [Fact]
    public async Task CheckHttpAsync_HeaderSmugglingAttempt_RejectsCrlfInjection()
    {
        // Arrange
        var maliciousHeaders = new Dictionary<string, string>
        {
            ["x-tenant-id"] = "corp\r\nInjected-Header: evil"
        };

        // Act
        var response = await _service.CheckHttpAsync("GET", "/api/v1/customers", maliciousHeaders);

        // Assert
        Assert.Equal(7, response.Status.Code);
        Assert.NotNull(response.HttpResponse.DeniedResponse);
        Assert.Equal(400, response.HttpResponse.DeniedResponse.Status.Code);
        Assert.Contains("CRLF", response.HttpResponse.DeniedResponse.Body);
    }

    [Fact]
    public async Task CheckAsync_PathTraversal_NormalizesProperly()
    {
        // Arrange
        var allowedDecision = new TableAccessDecision(
            Table: new TableIdentifier("default", "public", "admin"),
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: null,
            DeniedReasons: Array.Empty<string>());

        SecurityEvaluationContext? capturedContext = null;
        _policyService
            .EvaluatePolicyAsync(
                Arg.Do<SecurityEvaluationContext>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>())
            .Returns(allowedDecision);

        var request = new EnvoyCheckRequest
        {
            Attributes = new EnvoyAttributeContext
            {
                Request = new EnvoyRequest
                {
                    Http = new EnvoyHttpRequest
                    {
                        Method = "GET",
                        Path = "/api/v1/public/../../admin/orders",
                        Headers = new Dictionary<string, string>()
                    }
                }
            }
        };

        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "admin-user"),
                new System.Security.Claims.Claim("tenant_id", "corp")
            },
            "MeshTls"));

        // Act
        var response = await _service.CheckAsync(request, caller);

        // Assert
        Assert.Equal(0, response.Status.Code);
        Assert.NotNull(capturedContext);
        // Normalized path should resolve out the traversal
        Assert.Equal("/api/admin/orders", capturedContext.Attributes?["path"]);
        Assert.Equal("admin", capturedContext.TargetTable.TableName);
    }

    [Fact]
    public async Task CheckAsync_ClientIdentityHeaders_WithoutCaller_DoesNotTrustUntrustedHeaders()
    {
        // SEC C-1: Headers like x-autheris-principal, x-tenant-id must NEVER be trusted from untrusted clients
        var table = new TableIdentifier("default", "public", "orders");
        var allowedDecision = new TableAccessDecision(
            Table: table,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: null,
            DeniedReasons: Array.Empty<string>());

        SecurityEvaluationContext? capturedContext = null;
        _policyService
            .EvaluatePolicyAsync(
                Arg.Do<SecurityEvaluationContext>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>())
            .Returns(allowedDecision);

        var request = new EnvoyCheckRequest
        {
            Attributes = new EnvoyAttributeContext
            {
                Request = new EnvoyRequest
                {
                    Http = new EnvoyHttpRequest
                    {
                        Method = "GET",
                        Path = "/api/v1/orders",
                        Headers = new Dictionary<string, string>
                        {
                            ["x-autheris-principal"] = "spoofed-admin",
                            ["x-roles"] = "ClusterAdmin",
                            ["x-tenant-id"] = "evil-tenant"
                        }
                    }
                }
            }
        };

        // Act: Caller is null (unauthenticated client attempting header forgery)
        var response = await _service.CheckAsync(request, caller: null);

        // Assert: Principal should be anonymous and tenant should be default; forged headers ignored
        Assert.NotNull(capturedContext);
        Assert.Equal("anonymous", capturedContext.UserSid.Value);
        Assert.Equal("default", capturedContext.Tenant.Value);
    }

    [Fact]
    public void GenerateIstioEnvoyFilterYaml_EnforcesFailClosedByDefault()
    {
        // Act
        var yaml = _service.GenerateIstioEnvoyFilterYaml();

        // Assert
        Assert.Contains("kind: EnvoyFilter", yaml);
        Assert.Contains("failure_mode_allow: false", yaml);
        Assert.Contains("envoy.filters.http.ext_authz", yaml);
        Assert.Contains("autheris-gateway.autheris.svc.cluster.local", yaml);
    }

    [Fact]
    public void GenerateIstioWasmPluginYaml_EnforcesFailClosedAndValidStructure()
    {
        // Act
        var yaml = _service.GenerateIstioWasmPluginYaml();

        // Assert
        Assert.Contains("kind: WasmPlugin", yaml);
        Assert.Contains("failClosed: true", yaml);
        Assert.Contains("oci://ghcr.io/autheris/envoy-pdp-wasm", yaml);
    }
}
