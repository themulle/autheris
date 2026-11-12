namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mesh.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed class EnvoyExtAuthzSecurityTests
{
    private readonly IAccessDecisionPoint _policyService;
    private readonly EnvoyExtAuthzService _service;

    public EnvoyExtAuthzSecurityTests()
    {
        _policyService = Substitute.For<IAccessDecisionPoint>();
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(ci => MetadataOf(ci.Arg<TableIdentifier>()));
        _service = new EnvoyExtAuthzService(
            _policyService,
            tables,
            NullLogger<EnvoyExtAuthzService>.Instance);
    }

    private static TableMetadata MetadataOf(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, IsActive = true },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    [Fact]
    public async Task CheckHttpAsync_AuthorizedSubject_ReturnsOkWithSecurityHeaders()
    {
        // Arrange
        var table = new TableIdentifier("default", "public", "customers");
        var allowedDecision = new TableAccessDecision(
            Table: table,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: null,
            DeniedReasons: Array.Empty<string>(),
            HasUnconstrainedColumnAllow: true);

        _policyService
            .DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(allowedDecision);

        const string method = "GET";
        const string path = "/api/v1/customers";
        var headers = new Dictionary<string, string>();

        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "alice"),
                new System.Security.Claims.Claim("tenant_id", "corp")
            },
            "MeshTls"));

        // Act
        var response = await _service.CheckHttpAsync(method, path, headers, caller);

        // Assert
        Assert.Equal(0, response.Status.Code);
        Assert.NotNull(response.HttpResponse.OkResponse);

        var responseHeaders = response.HttpResponse.OkResponse.Headers;
        Assert.Contains(responseHeaders, h => h.Header.Key == "x-autheris-decision" && h.Header.Value == "allowed");
        Assert.Contains(responseHeaders, h => h.Header.Key == "x-autheris-principal" && h.Header.Value == "alice");
        Assert.Contains(responseHeaders, h => h.Header.Key == "x-autheris-tenant" && h.Header.Value == "corp");
        // SEC C-1: Never return RLS SQL filter to caller/proxy
        Assert.DoesNotContain(responseHeaders, h => h.Header.Key == "x-autheris-rls-filter");
    }

    [Fact]
    public async Task CheckHttpAsync_DeniedSubject_ReturnsPermissionDenied403()
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
            .DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(deniedDecision);

        const string method = "GET";
        const string path = "/api/v1/salaries";
        var headers = new Dictionary<string, string>();

        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "intern-bob"),
                new System.Security.Claims.Claim("tenant_id", "corp")
            },
            "MeshTls"));

        // Act
        var response = await _service.CheckHttpAsync(method, path, headers, caller);

        // Assert
        Assert.Equal(7, response.Status.Code); // PERMISSION_DENIED
        Assert.NotNull(response.HttpResponse.DeniedResponse);
        Assert.Equal(403, response.HttpResponse.DeniedResponse.Status.Code);
        // Security review: denied reasons describe policy internals; they are logged, not returned to the caller.
        Assert.DoesNotContain("insufficient clearance level", response.HttpResponse.DeniedResponse.Body);
        Assert.Equal(EnvoyExtAuthzService.DeniedByPolicyMessage, response.HttpResponse.DeniedResponse.Body);
    }

    private static System.Security.Claims.ClaimsPrincipal MeshCaller(string sid) =>
        new(new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, sid),
                new System.Security.Claims.Claim("tenant_id", "corp")
            },
            "MeshTls"));

    public static TheoryData<string, string?, ColumnAccessLevel?, bool> RestrictedDecisions => new()
    {
        // case, row filter, column access level, hasUnconstrainedColumnAllow
        { "row filter", "tenant_id = 'corp'", null, true },
        { "masked column", null, ColumnAccessLevel.Mask, true },
        { "denied column", null, ColumnAccessLevel.Deny, true },
        { "no unconstrained column allow", null, null, false },
    };

    [Theory]
    [MemberData(nameof(RestrictedDecisions))]
    public async Task CheckHttpAsync_AllowedButRestrictedDecision_FailsClosedWith403(
        string restriction, string? rowFilter, ColumnAccessLevel? columnLevel, bool unconstrained)
    {
        var columns = new Dictionary<string, ColumnAccessLevel>();
        if (columnLevel is { } level)
        {
            columns["salary"] = level;
        }

        var decision = new TableAccessDecision(
            Table: new TableIdentifier("default", "public", "customers"),
            IsAllowed: true,
            ColumnAccess: columns,
            CombinedRowFilterSql: rowFilter,
            DeniedReasons: new[] { "internal: rule p42 matched" },
            HasUnconstrainedColumnAllow: unconstrained);
        _policyService
            .DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(decision);

        var response = await _service.CheckHttpAsync("GET", "/api/v1/customers", new Dictionary<string, string>(), MeshCaller("alice"));

        Assert.True(response.Status.Code != 0, restriction);
        Assert.NotNull(response.HttpResponse.DeniedResponse);
        Assert.Equal(403, response.HttpResponse.DeniedResponse.Status.Code);
        Assert.Equal(EnvoyExtAuthzService.RestrictedMessage, response.HttpResponse.DeniedResponse.Body);
        Assert.DoesNotContain("p42", response.HttpResponse.DeniedResponse.Body);
        Assert.DoesNotContain("tenant_id", response.HttpResponse.DeniedResponse.Body);
    }

    [Fact]
    public async Task CheckHttpAsync_AllowedWithOnlyClearColumns_IsAllowed()
    {
        var decision = new TableAccessDecision(
            Table: new TableIdentifier("default", "public", "customers"),
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel> { ["name"] = ColumnAccessLevel.Clear },
            CombinedRowFilterSql: null,
            DeniedReasons: Array.Empty<string>(),
            HasUnconstrainedColumnAllow: true);
        _policyService
            .DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(decision);

        var response = await _service.CheckHttpAsync("GET", "/api/v1/customers", new Dictionary<string, string>(), MeshCaller("alice"));

        Assert.Equal(0, response.Status.Code);
    }

    [Theory]
    [InlineData("/api/vat/1", "vat")]
    [InlineData("/api/v1/customers/123", "customers")]
    [InlineData("/api/V2/orders", "orders")]
    [InlineData("/v10/api/items", "items")]
    [InlineData("/api/val/7", "val")]
    [InlineData("/api/v1x/7", "v1x")]
    public async Task CheckHttpAsync_ResourceFromPath_SkipsOnlyRealVersionSegments(string path, string expectedTable)
    {
        AccessRequest? capturedContext = null;
        _policyService
            .DecideAsync(
                Arg.Do<AccessRequest>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>())
            .Returns(ci => TableAccessDecision.Allowed(ci.Arg<AccessRequest>().Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        await _service.CheckHttpAsync("GET", path, new Dictionary<string, string>(), MeshCaller("alice"));

        Assert.NotNull(capturedContext);
        Assert.Equal(expectedTable, capturedContext.Object.Identifier.TableName);
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
    public async Task CheckHttpAsync_PathTraversal_NormalizesProperly()
    {
        // Arrange
        var allowedDecision = new TableAccessDecision(
            Table: new TableIdentifier("default", "public", "admin"),
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: null,
            DeniedReasons: Array.Empty<string>(),
            HasUnconstrainedColumnAllow: true);

        AccessRequest? capturedContext = null;
        _policyService
            .DecideAsync(
                Arg.Do<AccessRequest>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>())
            .Returns(allowedDecision);

        const string method = "GET";
        const string path = "/api/v1/public/../../admin/orders";
        var headers = new Dictionary<string, string>();

        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "admin-user"),
                new System.Security.Claims.Claim("tenant_id", "corp")
            },
            "MeshTls"));

        // Act
        var response = await _service.CheckHttpAsync(method, path, headers, caller);

        // Assert
        Assert.Equal(0, response.Status.Code);
        Assert.NotNull(capturedContext);
        // Normalized path should resolve out the traversal
        Assert.Equal("admin", capturedContext.Object.Identifier.TableName);
        Assert.Equal("admin", capturedContext.Object.Identifier.TableName);
    }

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repo root not found");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExtAuthz_Unauthenticated_Is_Denied()
    {
        // DEF-T-10 / DEF-SC-10 (F2): SEC C-1 headers like x-autheris-principal and x-tenant-id are never trusted, and an unauthenticated caller
        // no longer becomes "anonymous" in tenant "default": it is denied before any policy evaluation.
        var headers = new Dictionary<string, string>
        {
            ["x-autheris-principal"] = "spoofed-admin",
            ["x-roles"] = "ClusterAdmin",
            ["x-tenant-id"] = "evil-tenant"
        };

        var unauthenticated = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        foreach (var caller in new System.Security.Claims.ClaimsPrincipal?[] { null, unauthenticated })
        {
            var response = await _service.CheckHttpAsync("GET", "/api/v1/orders", headers, caller);

            Assert.Equal(401, response.HttpResponse.DeniedResponse!.Status.Code);
            Assert.DoesNotContain("spoofed-admin", response.HttpResponse.DeniedResponse.Body);
        }

        await _policyService.DidNotReceive().DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExtAuthz_Authenticated_Without_Sid_Is_Denied_Instead_Of_Anonymous()
    {
        // DEF-SC-10: an authenticated principal without a SID claim must not become "ANONYMOUS".
        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[] { new System.Security.Claims.Claim("tenant_id", "corp") }, "MeshTls"));

        var response = await _service.CheckHttpAsync("GET", "/api/v1/orders", new Dictionary<string, string>(), caller);

        Assert.Equal(403, response.HttpResponse.DeniedResponse!.Status.Code);
        Assert.DoesNotContain("corp", response.HttpResponse.DeniedResponse.Body);
        Assert.DoesNotContain("ANONYMOUS", response.HttpResponse.DeniedResponse.Body, StringComparison.OrdinalIgnoreCase);
        await _policyService.DidNotReceive().DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public void ExtAuthz_ContextExtensions_Cannot_Select_Tenant_Or_User()
    {
        // DEF-T-10 / DEF-SC-10: the evaluation takes no identity input other than the validated principal (no source principal parameter)
        // and the type carries no "default" tenant or "anonymous" user literal.
        var evaluate = typeof(EnvoyExtAuthzService).GetMethod("EvaluateInternalAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.DoesNotContain(evaluate.GetParameters(), p => p.Name == "sourcePrincipal");

        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Autheris.Application", "Mesh", "Services", "EnvoyExtAuthzService.cs"));
        Assert.DoesNotContain("TryGetValue(\"tenant\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetValue(\"user\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new TenantId(\"default\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Sid(\"anonymous\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ANONYMOUS\"", source, StringComparison.Ordinal);
    }

    private static TableAccessDecision Allowed(string? rowFilter = null, bool unconstrained = true) =>
        new(
            Table: new TableIdentifier("default", "public", "orders"),
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: rowFilter,
            DeniedReasons: Array.Empty<string>(),
            HasUnconstrainedColumnAllow: unconstrained);

    [Trait("Category", "Security")]
    [Fact]
    public async Task NoGrant_Denied()
    {
        // SEC-AZ-10: the decision point denies (default deny, no grant); the caller learns nothing about the policy.
        _policyService.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Denied(new TableIdentifier("default", "public", "orders"), "no grant"));

        var response = await _service.CheckHttpAsync("GET", "/api/v1/orders", new Dictionary<string, string>(), MeshCaller("alice"));

        Assert.Equal(403, response.HttpResponse.DeniedResponse!.Status.Code);
        Assert.Equal(EnvoyExtAuthzService.DeniedByPolicyMessage, response.HttpResponse.DeniedResponse.Body);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task GrantWithRowFilter_DeniedAsRestricted()
    {
        _policyService.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>()).Returns(Allowed(rowFilter: "region = 'EU'"));

        var response = await _service.CheckHttpAsync("GET", "/api/v1/orders", new Dictionary<string, string>(), MeshCaller("alice"));

        Assert.Equal(403, response.HttpResponse.DeniedResponse!.Status.Code);
        Assert.Equal(EnvoyExtAuthzService.RestrictedMessage, response.HttpResponse.DeniedResponse.Body);
        Assert.DoesNotContain("EU", response.HttpResponse.DeniedResponse.Body);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task GrantUnrestricted_Allowed()
    {
        _policyService.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>()).Returns(Allowed());

        var response = await _service.CheckHttpAsync("GET", "/api/v1/orders", new Dictionary<string, string>(), MeshCaller("alice"));

        Assert.Equal(0, response.Status.Code);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("GET", AccessOperation.Read)]
    [InlineData("HEAD", AccessOperation.Read)]
    [InlineData("POST", AccessOperation.Insert)]
    [InlineData("PUT", AccessOperation.Update)]
    [InlineData("PATCH", AccessOperation.Update)]
    [InlineData("DELETE", AccessOperation.Delete)]
    public async Task Decides_via_decision_point_and_denies_restricted(string method, AccessOperation expected)
    {
        // SEC-AZ-01/03/10: one PDP request per check, transport EnvoyExtAuthz, the operation follows the method, and the subject carries no
        // role as a grantee (the role claims are not in GroupSids).
        AccessRequest? captured = null;
        _policyService.DecideAsync(Arg.Do<AccessRequest>(r => captured = r), Arg.Any<CancellationToken>()).Returns(Allowed(unconstrained: false));
        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "alice"),
                new System.Security.Claims.Claim("tenant_id", "corp"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "ClusterAdmin"),
                new System.Security.Claims.Claim("roles", "GovernanceAdmin")
            },
            "MeshTls"));

        var response = await _service.CheckHttpAsync(method, "/api/v1/orders", new Dictionary<string, string>(), caller);

        Assert.NotNull(captured);
        Assert.Equal(AccessTransport.EnvoyExtAuthz, captured.Transport);
        Assert.Equal(expected, captured.Operation);
        Assert.Equal("alice", captured.Subject.UserSid.Value);
        Assert.DoesNotContain(captured.Subject.GroupSids, g => g.Value is "ClusterAdmin" or "GovernanceAdmin");
        // The decision restricts columns (no unconstrained allow): ext_authz cannot enforce that, so it denies.
        Assert.Equal(EnvoyExtAuthzService.RestrictedMessage, response.HttpResponse.DeniedResponse!.Body);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task UnknownTable_Denied_WithoutDecision()
    {
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns((TableMetadata?)null);
        var service = new EnvoyExtAuthzService(_policyService, tables, NullLogger<EnvoyExtAuthzService>.Instance);

        var response = await service.CheckHttpAsync("GET", "/api/v1/orders", new Dictionary<string, string>(), MeshCaller("alice"));

        Assert.Equal(403, response.HttpResponse.DeniedResponse!.Status.Code);
        await _policyService.DidNotReceive().DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>());
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
    public void GenerateIstioEnvoyFilterYaml_StripsClientSuppliedAutherisHeadersBeforeExtAuthz()
    {
        var yaml = _service.GenerateIstioEnvoyFilterYaml();

        // Review C-1 (rest): the strip filter must exist and be ordered before the ext_authz patch.
        var luaIndex = yaml.IndexOf("envoy.filters.http.lua", StringComparison.Ordinal);
        var authzIndex = yaml.IndexOf("envoy.filters.http.ext_authz", StringComparison.Ordinal);
        Assert.True(luaIndex >= 0, "Lua header-strip filter missing");
        Assert.True(luaIndex < authzIndex, "Header-strip filter must be applied before ext_authz");
        Assert.Contains("x-autheris-", yaml[luaIndex..authzIndex]);
        Assert.Contains("headers:remove(key)", yaml);
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

    [Fact]
    public void API_17_GenerateIstioWasmPluginYaml_UsesPinnedVersionTag_AndRejectsLatest()
    {
        var yaml = _service.GenerateIstioWasmPluginYaml();
        Assert.DoesNotContain(":latest", yaml);
        Assert.Contains(":v1.0.0", yaml);

        var badOpts = new EnvoyFilterExportOptions { WasmPluginTag = "latest" };
        Assert.Throws<ArgumentException>(() => _service.GenerateIstioWasmPluginYaml(badOpts));
    }
}
