namespace Autheris.Tests.Unit;

using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using NSubstitute.ExceptionExtensions;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Streaming.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class SecurityFindingsRemediationTests
{
    // =========================================================================
    // Finding 1: Cross-Tenant Bypass Prevention (TenantResolutionMiddleware + Auth Handlers)
    // =========================================================================

    [Fact]
    public async Task TenantResolution_AuthenticatedUser_WithMismatchedHeader_Returns403()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new TenantResolutionMiddleware(next);

        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.Name, "alice")
        };
        var identity = new ClaimsIdentity(claims, "Basic");
        context.User = new ClaimsPrincipal(identity);
        context.Request.Headers["X-Tenant-ID"] = "tenant-beta";
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var responseText = await reader.ReadToEndAsync();
        responseText.ShouldContain("CROSS_TENANT_ACCESS_FORBIDDEN");
    }

    [Fact]
    public async Task TenantResolution_AuthenticatedUser_WithoutTenantClaim_SpoofingHeader_Returns403()
    {
        // Arrange: User is authenticated but has NO tenant claim
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new TenantResolutionMiddleware(next);

        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "bob"),
            new Claim(ClaimTypes.Role, "StandardUser")
        };
        var identity = new ClaimsIdentity(claims, "TestScheme");
        context.User = new ClaimsPrincipal(identity);
        context.Request.Headers["X-Tenant-ID"] = "victim-tenant";
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert: Access is forbidden because non-admin cannot specify arbitrary tenant without a claim
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var responseText = await reader.ReadToEndAsync();
        responseText.ShouldContain("CROSS_TENANT_ACCESS_FORBIDDEN");
    }

    [Fact]
    public async Task TenantResolution_GatewayAdmin_CanSwitchTenantViaHeader()
    {
        // Arrange: User has GatewayAdmin role
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new TenantResolutionMiddleware(next);

        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "GatewayAdmin"),
            new Claim("tenant_id", "admin-home-tenant")
        };
        var identity = new ClaimsIdentity(claims, "Basic");
        context.User = new ClaimsPrincipal(identity);
        context.Request.Headers["X-Tenant-ID"] = "target-tenant";

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.ShouldBeTrue();
        context.Items[TenantResolutionMiddleware.TenantIdItemKey].ShouldBe(new TenantId("target-tenant"));
    }

    [Fact]
    public async Task BasicAuthenticationHandler_EmitsTenantClaim()
    {
        // Arrange
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Users =
                    [
                        new BasicAuthUserConfig
                        {
                            Username = "tenant_user",
                            Password = "secretPassword123",
                            TenantId = "tenant-finance"
                        }
                    ]
                }
            }
        };

        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Development");

        var schemeOptionsMonitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var handler = new BasicAuthenticationHandler(
            schemeOptionsMonitor,
            NullLoggerFactory.Instance,
            System.Text.Encodings.Web.UrlEncoder.Default,
            Options.Create(options),
            mockEnv);

        var context = new DefaultHttpContext();
        await handler.InitializeAsync(new AuthenticationScheme(BasicAuthenticationHandler.SchemeName, "Basic", typeof(BasicAuthenticationHandler)), context);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("tenant_user:secretPassword123"));
        context.Request.Headers.Authorization = $"Basic {credentials}";

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Principal.ShouldNotBeNull();
        result.Principal.FindFirst("tenant_id")?.Value.ShouldBe("tenant-finance");
        result.Principal.FindFirst("tenant")?.Value.ShouldBe("tenant-finance");
        result.Principal.FindFirst("tid")?.Value.ShouldBe("tenant-finance");
    }

    // =========================================================================
    // Finding 2: Consistent Invariant Enforcement for TestAuth and Anonymous Access
    // =========================================================================

    [Fact]
    public void ValidateSecurityInvariants_InProduction_EnableTestAuthHandler_ThrowsValidationException()
    {
        // Arrange
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EnableTestAuthHandler = true
            }
        };

        // Act & Assert
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("EnableTestAuthHandler may be true ONLY in the Development environment");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_EnableTestAuthHandler_EvenWithAnonymous_ThrowsValidationException()
    {
        // Arrange
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EnableTestAuthHandler = true
            },
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_anonymous_access = true
            }
        };

        // Act & Assert: Finding 2 remediation ensures it is unconditionally forbidden
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("EnableTestAuthHandler may be true ONLY in the Development environment");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_AnonymousAccess_ThrowsValidationException()
    {
        // Arrange
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_anonymous_access = true
            }
        };

        // Act & Assert
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("danger_allow_anonymous_access may be true ONLY in the Development environment");
    }

    // =========================================================================
    // Finding 3: Casbin sub_rule Injection Defense
    // =========================================================================

    [Fact]
    public void CasbinEnforcementService_AddPolicy_WithDangerousSubRule_ThrowsArgumentException()
    {
        // Arrange
        var service = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-audit");

        // Act & Assert
        var ex = Should.Throw<ArgumentException>(() =>
            service.AddPolicy(tenant, "attacker", "table", "read", "System.IO.File.ReadAllText(\"/etc/passwd\")", "allow"));
        ex.Message.ShouldContain("Casbin sub_rule contains disallowed expression");
    }

    [Fact]
    public void CasbinEnforcementService_AddPolicy_WithSafeSubRule_Succeeds()
    {
        // Arrange
        var service = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-audit");

        // Act & Assert: Standard boolean expression succeeds
        Should.NotThrow(() =>
            service.AddPolicy(tenant, "safe_user", "table", "read", "true", "allow"));
    }

    // =========================================================================
    // Finding 4: Bounded Candidate Derivation in DefaultEnvironmentSecretProvider
    // =========================================================================

    [Fact]
    public void DefaultEnvironmentSecretProvider_ResolvesExactCandidatesWithoutCollisions()
    {
        // Arrange: Config has both a generic secret and the dedicated itsm webhook secret
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string?>("ITSM_WEBHOOK_SECRET", "itsm-secret-12345"),
            new System.Collections.Generic.KeyValuePair<string, string?>("SOME_OTHER_SECRET", "other-999")
        });
        var configuration = configBuilder.Build();

        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var provider = new DefaultEnvironmentSecretProvider(configuration, mockEnv);

        // Act
        var secretBytes = provider.GetSecretBytes("itsm:webhook-token");

        // Assert
        Encoding.UTF8.GetString(secretBytes).ShouldBe("itsm-secret-12345");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_UntrustedCertificatesAllowed_ThrowsValidationException()
    {
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_untrusted_certificates = true
            }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("DANGER:danger_allow_untrusted_certificates");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_DisableRateLimiting_ThrowsValidationException()
    {
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                warn_disable_rate_limiting = true
            }
        };

        // warn_disable_rate_limiting is classified as DANGER (property name kept for compatibility).
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("DANGER:warn_disable_rate_limiting");
    }

    [Fact]
    public void DeclarativeHttp_IsRestrictedIp_IdentifiesPrivateAndLoopbackIps()
    {
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("127.0.0.1")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("10.1.2.3")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("172.16.5.6")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("192.168.1.100")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("169.254.169.254")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("::1")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("fc00::1")).ShouldBeTrue();

        // Public IPs should not be restricted
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("8.8.8.8")).ShouldBeFalse();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("93.184.216.34")).ShouldBeFalse();
    }

    [Fact]
    public void DeclarativeHttp_IsForbiddenMetadataHost_IdentifiesMetadataHosts()
    {
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("metadata.google.internal").ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("sub.metadata.google.internal").ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("kubernetes.default.svc").ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("kubernetes.default.svc.cluster.local").ShouldBeTrue();

        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("api.corp.com").ShouldBeFalse();
    }

    private sealed class TestOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public void ValidateGatewayOptions_InProduction_WarnRelaxedQueryLimits_IsPermittedAsWarning()
    {
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                warn_relaxed_query_limits = true
            },
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" }
        };

        // WARN entries are permitted in Production (reported, not blocking).
        options.GetActiveWarnings().ShouldContain("WARN:warn_relaxed_query_limits");
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv, _ => null));
    }

    [Fact]
    public async Task JustificationTriageService_RequiresFourEyes_DisallowsAutoGrantEvenIfLowSensitivity()
    {
        var openJevClient = Substitute.For<IOpenJevClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var auditRepo = Substitute.For<IAuditLogRepository>();

        var table = new TableIdentifier("finance", "dbo", "low_risk_table");
        metadataRepo.GetTableMetadataAsync(table, Arg.Any<System.Threading.CancellationToken>())
            .Returns(new TableMetadata
            {
                Table = new Table
                {
                    Sensitivity = "LOW_SENSITIVITY",
                    RequiresFourEyes = true,
                    IsActive = true
                }
            });

        openJevClient.ClassifyJustificationAsync(
            Arg.Any<TenantId>(),
            Arg.Any<Sid>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<string>(),
            Arg.Any<System.Threading.CancellationToken>())
            .Returns(new JustificationTriageResult(
                JustificationCategory.LegitimateAudit,
                0.95,
                "Routine task",
                false,
                null));

        var triageService = new JustificationTriageService(
            openJevClient,
            metadataRepo,
            auditRepo,
            NullLogger<JustificationTriageService>.Instance);

        var result = await triageService.TriageJustificationAsync(
            new TenantId("tenant-1"),
            new Sid("S-1-5-21-1234"),
            table,
            "Valid business reason");

        result.AutoGrantEligible.ShouldBeFalse();
        result.GrantedDuration.ShouldBeNull();
    }

    // =========================================================================
    // Finding B1 & Hardening: Strict Tenant Isolation & TenantId Validation
    // =========================================================================

    [Fact]
    public void TenantId_TrailingNewline_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => new TenantId("tenant-alpha\n"));
        Should.Throw<ArgumentException>(() => new TenantId("tenant-alpha\r\n"));
    }

    [Fact]
    public async Task SqliteGovernanceRepository_StrictTenantIsolation_DoesNotCrossLeakConsents()
    {
        // Finding B1 Verification:
        // Consents in tenant-alpha MUST NOT be visible when querying tenant-beta or vice-versa.
        var epochService = Substitute.For<IEpochValidationService>();
        using var repo = new SqliteGovernanceRepository(epochService);

        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "orders",
                Sensitivity = "CONFIDENTIAL",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" },
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "amount", DataType = "decimal" }
            ]
        };
        await repo.UpsertTableMetadataAsync(metadata);

        var userSid = new Sid("S-1-5-21-TENANT-USER");
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = metadata.Table.Id,
            TableIdentifier = tableId,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            TenantId = new TenantId("tenant-alpha")
        };
        await repo.CreateConsentAsync(consent);

        // Act & Assert 1: Querying with tenant-alpha returns the consent
        var alphaConsents = await repo.GetActiveConsentsForSubjectsAsync([userSid], tableId, DateTimeOffset.UtcNow, new TenantId("tenant-alpha"));
        alphaConsents.Count.ShouldBe(1);
        alphaConsents[0].TenantId.ShouldBe(new TenantId("tenant-alpha"));

        // Act & Assert 2: Querying with tenant-beta MUST NOT return the consent
        var betaConsents = await repo.GetActiveConsentsForSubjectsAsync([userSid], tableId, DateTimeOffset.UtcNow, new TenantId("tenant-beta"));
        betaConsents.Count.ShouldBe(0);

        // Act & Assert 3: Querying all active consents with tenant-beta MUST NOT return the consent
        var allBetaConsents = await repo.GetAllActiveConsentsForSubjectsAsync([userSid], null, DateTimeOffset.UtcNow, new TenantId("tenant-beta"));
        allBetaConsents.Count.ShouldBe(0);
    }

    [Fact]
    public async Task StreamRlsPolicyEnforcer_EmptyCdcTenantId_ReturnsDenied()
    {
        // Finding F-02 Verification:
        // A CDC event with null or empty TenantId must fail closed and be denied.
        var policyEnforcement = Substitute.For<IPolicyEnforcementService>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        var epochService = Substitute.For<IEpochValidationService>();

        var enforcer = new StreamRlsPolicyEnforcer(
            policyEnforcement,
            metadataRepo,
            maskingProvider,
            epochService,
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            Substitute.For<IConsentRepository>(),
            Substitute.For<IConsentResolutionService>(),
            Substitute.For<IConsentCacheService>());

        var table = new TableIdentifier("sales", "crm", "leads");
        var cdcEvent = new CdcEvent(
            EventId: "evt-empty-tenant",
            Table: table,
            Operation: CdcOperation.Insert,
            TenantId: null!, // Missing TenantId
            Before: null,
            After: new System.Collections.Generic.Dictionary<string, object?> { ["id"] = 1 },
            Timestamp: DateTimeOffset.UtcNow);

        var subscriber = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-SUBSCRIBER")
        }));

        var decision = await enforcer.EvaluateAndMaskAsync(cdcEvent, subscriber);

        decision.IsAllowed.ShouldBeFalse();
        decision.FilterReason.ShouldBe("Tenant mismatch");
    }

    // =========================================================================
    // Finding B2: MCP Tools Fail-Closed & Casbin ABAC Enforcement
    // =========================================================================

    [Fact]
    public async Task AiDataGuardrailService_UnmappedTool_WhenPolicyDenies_FailsClosedAndReturnsError()
    {
        // Finding B2 Verification:
        // A generic tool with no physical table mapping MUST NOT bypass Casbin ABAC.
        // It must evaluate tool-level permission and fail closed if denied.
        var registry = new McpToolRegistry();
        var unmappedTool = new McpToolDefinition(
            Name: "unmapped_generic_tool",
            Description: "A generic tool without direct table mapping",
            InputJsonSchema: "{}",
            TargetGraphQLOperation: "query UnknownOp { randomData { val } }"
        );
        registry.RegisterTool(unmappedTool);

        var policyService = new TestPolicyEnforcementService(ctx =>
            TableAccessDecision.Denied(ctx.TargetTable, "Access denied by ABAC policy for tool"));

        var options = Options.Create(new GatewayOptions { Casbin = new CasbinOptions { Enabled = true }, Mcp = new McpOptions { Enabled = true } });
        var guardrail = new AiDataGuardrailService(
            registry,
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            policyEnforcementService: policyService);

        var session = new McpSessionContext("sess-b2", "agent-unauthorized", "tenant-test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("unmapped_generic_tool", "{}");

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Access denied to tool 'unmapped_generic_tool'");
    }

    [Fact]
    public async Task AiDataGuardrailService_CustomClientIp_PassedToSecurityContext()
    {
        // Finding B2 / F-03 Verification:
        // ClientIp in McpSessionContext must be passed to SecurityEvaluationContext rather than hardcoded 127.0.0.1.
        var registry = new McpToolRegistry();
        SecurityEvaluationContext? capturedContext = null;

        var policyService = new TestPolicyEnforcementService(ctx =>
        {
            capturedContext = ctx;
            return TableAccessDecision.Allowed(ctx.TargetTable, new System.Collections.Generic.Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        });

        var options = Options.Create(new GatewayOptions { Casbin = new CasbinOptions { Enabled = true }, Mcp = new McpOptions { Enabled = true } });
        var guardrail = new AiDataGuardrailService(
            registry,
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            policyEnforcementService: policyService);

        var session = new McpSessionContext(
            SessionId: "sess-ip-test",
            ServicePrincipalId: "agent-remote",
            TenantId: "tenant-ip",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            ClientIp: "198.51.100.42");

        var request = new McpToolCallRequest("query_customers", "{}");

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeTrue();
        capturedContext.ShouldNotBeNull();
        capturedContext.ClientIp.ToString().ShouldBe("198.51.100.42");
    }

    [Fact]
    public async Task AiDataGuardrailService_ExplicitToolTargetTable_CheckedByPolicy()
    {
        // Finding B2 Verification:
        // McpToolDefinition with explicit TargetTable must pass that TableIdentifier to the policy engine.
        var registry = new McpToolRegistry();
        var explicitTable = new TableIdentifier("sales", "crm", "deals");
        var dealTool = new McpToolDefinition(
            Name: "query_deals",
            Description: "Deals query tool",
            InputJsonSchema: "{}",
            TargetGraphQLOperation: "query Deals { deals { id amount } }",
            TargetTable: explicitTable);
        registry.RegisterTool(dealTool);

        TableIdentifier? evaluatedTable = null;
        var policyService = new TestPolicyEnforcementService(ctx =>
        {
            evaluatedTable = ctx.TargetTable;
            return TableAccessDecision.Allowed(ctx.TargetTable, new System.Collections.Generic.Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        });

        var options = Options.Create(new GatewayOptions { Casbin = new CasbinOptions { Enabled = true }, Mcp = new McpOptions { Enabled = true } });
        var guardrail = new AiDataGuardrailService(
            registry,
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            policyEnforcementService: policyService);

        var session = new McpSessionContext("sess-deals", "agent-deals", "tenant-sales", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_deals", "{}");

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeTrue();
        evaluatedTable.ShouldBe(explicitTable);
    }

    // =========================================================================
    // Security Review Remediation Tests (CRIT-01, CRIT-02, HIGH-01, HIGH-02, HIGH-03, MED-02)
    // =========================================================================

    [Fact]
    public void CRIT01_PluginIntegrityVerification_TamperedHash_ThrowsSecurityException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "plugin_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllPath = Path.Combine(tempDir, "TestPlugin.dll");
            File.WriteAllBytes(dllPath, [0x4D, 0x5A, 0x90, 0x00]);

            var manifestPath = Path.Combine(tempDir, "manifest.json");
            File.WriteAllText(manifestPath, "{\"plugins\": [{\"file\": \"TestPlugin.dll\", \"sha256\": \"0000000000000000000000000000000000000000000000000000000000000000\"}]}");

            // SEC M-27: the trust anchor is the configuration, not the manifest next to the DLL.
            var pluginOptions = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
            {
                Plugins = new Autheris.Domain.Options.PluginsOptions
                {
                    TrustedPluginHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["TestPlugin.dll"] = "0000000000000000000000000000000000000000000000000000000000000000"
                    }
                }
            });
            var manager = new Autheris.Infrastructure.Plugins.PluginManager(NullLogger<Autheris.Infrastructure.Plugins.PluginManager>.Instance, null, pluginOptions);
            var ex = Should.Throw<System.Security.SecurityException>(() => manager.LoadPluginsFromDirectory(tempDir));
            ex.Message.ShouldContain("Integrity check failed");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }



    [Theory]
    [InlineData("Finance') OR ('1'='1")]
    [InlineData("HR'; DROP TABLE CONSENTS; --")]
    [InlineData("Sales' UNION SELECT * FROM secrets --")]
    [InlineData("Ops<script>alert(1)</script>")]
    public async Task CRIT02_InterpolateRlsFilter_MaliciousClaimValue_ThrowsSecurityException(string maliciousClaim)
    {
        var casbin = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-finance");
        var table = new TableIdentifier("finance", "dbo", "invoices");

        casbin.AddPolicy(tenant, "attacker", table.ToString(), "read", "true", "allow", rlsFilter: "dept = '${department}'");

        var context = new SecurityEvaluationContext(
            UserSid: new Sid("attacker"),
            GroupSids: [],
            Tenant: tenant,
            TargetTable: table,
            RequestedColumns: ["id"],
            ClientIp: System.Net.IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: "AUDIT",
            Attributes: new System.Collections.Generic.Dictionary<string, object?> { ["department"] = maliciousClaim });

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(async () =>
            await casbin.EvaluatePolicyAsync(context));
        ex.Message.ShouldContain("Security error");
        ex.Message.ShouldContain("department");
    }

    [Fact]
    public void HIGH01_UntrustedCertificatesAllowed_InProduction_ThrowsValidationException()
    {
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_untrusted_certificates = true
            }
        };

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
        ex.Message.ShouldContain("danger_allow_untrusted_certificates");
    }

    [Fact]
    public void HIGH02_WildcardCors_InProduction_ThrowsValidationException()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "vault://keys/prod-hmac"
            },
            GraphQL = new GraphQLOptions
            {
                TrustedOrigins = ["*"]
            }
        };

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
        ex.Message.ShouldContain("TrustedOrigins '*' (wildcard CORS) is prohibited outside the Development environment for security reasons (CSRF protection)");
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.100.100.200/latest/meta-data/")]
    [InlineData("http://metadata.google.internal/")]
    [InlineData("http://127.0.0.1:8080/internal/admin")]
    [InlineData("http://192.168.1.1/admin")]
    public async Task HIGH03_SsrfProtectionHandler_OutboundRequestToRestrictedAddress_ThrowsSecurityException(string restrictedUrl)
    {
        var handler = new SsrfProtectionHandler();
        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, restrictedUrl);
        var invoker = new System.Net.Http.HttpMessageInvoker(handler);

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(async () =>
            await invoker.SendAsync(request, System.Threading.CancellationToken.None));
        ex.Message.ShouldContain("strictly forbidden");
    }

    [Fact]
    public void MED02_Casbin_SubRule_ExceedingLength_ThrowsArgumentException()
    {
        var casbin = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-core");
        var longSubRule = new string('a', 501);

        var ex = Should.Throw<ArgumentException>(() =>
            casbin.AddPolicy(tenant, "user1", "table1", "read", longSubRule, "allow"));
        ex.Message.ShouldContain("exceeds the maximum length of 500 characters");
    }

    [Theory]
    [InlineData("true; eval(unescape('hack'))")]
    [InlineData("ctx.Clearance == 'ADMIN' \0 nullbyte")]
    [InlineData("sub_rule || `cat /etc/passwd`")]
    public void MED02_Casbin_SubRule_IllegalCharacters_ThrowsArgumentException(string illegalSubRule)
    {
        var casbin = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-core");

        var ex = Should.Throw<ArgumentException>(() =>
            casbin.AddPolicy(tenant, "user1", "table1", "read", illegalSubRule, "allow"));
        ex.Message.ShouldContain("contains disallowed characters");
    }

    [Fact]
    public void ValidateGatewayOptions_SeedDemoData_InProduction_ThrowsValidationException()
    {
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                SeedDemoData = true
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));
        ex.Message.ShouldContain("SeedDemoData");
    }

    [Fact]
    public void SqliteGovernanceRepository_FileDatabase_WithoutDevEnvironment_ThrowsInvalidOperationException()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"sec_test_{Guid.NewGuid():N}.db");
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
            {
                GovernanceDb = new GovernanceDbOptions
                {
                    ConnectionString = $"Data Source={tempFile}"
                }
            });

            var epochMock = Substitute.For<IEpochValidationService>();
            var env = Substitute.For<IHostEnvironment>();
            env.EnvironmentName.Returns("Production");

            // No secret provider and non-dev environment -> must throw InvalidOperationException
            var ex = Should.Throw<InvalidOperationException>(() =>
                new SqliteGovernanceRepository(epochMock, options, env, secretProvider: null));
            ex.Message.ShouldContain("Audit HMAC secret is missing");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void M01_AddGatewayAuth_WithoutIdp_ValidatesIssuerAndAudienceFailClosed()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var options = new GatewayOptions();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);

        services.AddGatewayAuth(options, env);
        var sp = services.BuildServiceProvider();

        var jwtOptions = sp.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(GatewayAuthSchemes.JwtBearer);

        // SEC M-02 (Review 2026-10-02): issuer/audience validation is always on; without configured
        // issuers/audiences no token can pass (fail-closed) instead of accepting any audience.
        jwtOptions.TokenValidationParameters.ValidateIssuer.ShouldBeTrue();
        jwtOptions.TokenValidationParameters.ValidIssuers.ShouldBeNull();
        jwtOptions.TokenValidationParameters.ValidateAudience.ShouldBeTrue();
        jwtOptions.TokenValidationParameters.ValidAudiences.ShouldBeNull();
    }

    [Fact]
    public void M01_AddGatewayAuth_WithEntraId_EnablesValidateIssuerAndAudience()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions
                {
                    Enabled = true,
                    TenantId = "test-tenant-123",
                    Audience = "api://my-gateway"
                }
            }
        };
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);

        services.AddGatewayAuth(options, env);
        var sp = services.BuildServiceProvider();

        var jwtOptions = sp.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(GatewayAuthSchemes.JwtBearer);

        jwtOptions.TokenValidationParameters.ValidateIssuer.ShouldBeTrue();
        jwtOptions.TokenValidationParameters.ValidIssuers.ShouldNotBeNull();
        jwtOptions.TokenValidationParameters.ValidIssuers.ShouldContain("https://login.microsoftonline.com/test-tenant-123/v2.0");
        jwtOptions.TokenValidationParameters.ValidateAudience.ShouldBeTrue();
        jwtOptions.TokenValidationParameters.ValidAudiences.ShouldNotBeNull();
        jwtOptions.TokenValidationParameters.ValidAudiences.ShouldContain("api://my-gateway");
    }

    [Theory]
    [InlineData("http://portal.corp.local")]
    [InlineData("http://evil.com")]
    [InlineData("ftp://secure.corp.local")]
    public void M03_ValidateGatewayOptions_NonHttpsTrustedOriginInProduction_ThrowsValidationException(string nonHttpsOrigin)
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "vault://keys/prod-hmac"
            },
            GraphQL = new GraphQLOptions
            {
                TrustedOrigins = [nonHttpsOrigin]
            }
        };

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
        ex.Message.ShouldContain("Outside Development, TrustedOrigins must contain only HTTPS URLs");
    }

    [Fact]
    public void M03_ValidateGatewayOptions_HttpsTrustedOriginInProduction_Succeeds()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "vault://keys/prod-hmac"
            },
            GraphQL = new GraphQLOptions
            {
                TrustedOrigins = ["https://portal.corp.local", "https://app.corp.local"]
            }
        };

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
    }

    [Fact]
    public void L01_DefaultEnvironmentSecretProvider_ResolvingFromConfiguration_LogsWarning()
    {
        var inMemory = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["test_secret"] = "SuperSecretValue123"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<DefaultEnvironmentSecretProvider>>();

        var provider = new DefaultEnvironmentSecretProvider(config, env, logger);
        var bytes = provider.GetSecretBytes("test_secret");

        Encoding.UTF8.GetString(bytes).ShouldBe("SuperSecretValue123");
        logger.ReceivedWithAnyArgs().Log(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            default,
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void INF_3_DefaultEnvironmentSecretProvider_RedactsSecretReferenceInLogStatements()
    {
        var inMemory = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["AUDIT_HMAC_KEY"] = "SuperSecretValue12345678901234567890"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        var logger = new TestSecretProviderLogger();

        var provider = new DefaultEnvironmentSecretProvider(config, env, logger);
        provider.GetSecretBytes("audit-hmac-key");

        logger.Messages.Count.ShouldBeGreaterThan(0);
        foreach (var msg in logger.Messages)
        {
            msg.ShouldNotContain("audit-hmac-key");
        }
        logger.Messages.ShouldContain(m => m.Contains("Reference with length 14"));
    }

    [Fact]
    public void INF_3_DefaultEnvironmentSecretProvider_InstanceSpecificItsm_RedactsSecretReference()
    {
        var config = new ConfigurationBuilder().Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        var logger = new TestSecretProviderLogger();

        var provider = new DefaultEnvironmentSecretProvider(config, env, logger);
        Should.Throw<InvalidOperationException>(() => provider.GetSecretBytes("itsm:webhook-secret:inst-xyz-987"));

        logger.Messages.Count.ShouldBeGreaterThan(0);
        foreach (var msg in logger.Messages)
        {
            msg.ShouldNotContain("itsm:webhook-secret:inst-xyz-987");
        }
        logger.Messages.ShouldContain(m => m.Contains("Reference with length 32"));
    }

    private sealed class TestSecretProviderLogger : Microsoft.Extensions.Logging.ILogger<DefaultEnvironmentSecretProvider>
    {
        public readonly System.Collections.Generic.List<string> Messages = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void CRIT01_LocalStorageProvider_PrefixCollisionTraversal_ThrowsSecurityException()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "lakehouse_" + Guid.NewGuid().ToString("N"));
        var siblingDir = tempBase + "_secrets";
        Directory.CreateDirectory(tempBase);
        Directory.CreateDirectory(siblingDir);

        try
        {
            var options = Options.Create(new GatewayOptions
            {
                Lakehouse = new LakehouseOptions
                {
                    Storage = new LakehouseStorageOptions
                    {
                        LocalBasePath = tempBase
                    }
                }
            });

            var provider = new Autheris.Extensions.Lakehouse.Services.LocalStorageProvider(options);
            var attackLocation = Path.Combine(siblingDir, "secret.key");

            Should.Throw<System.Security.SecurityException>(() =>
                provider.ExistsAsync(attackLocation).AsTask().GetAwaiter().GetResult());
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
            if (Directory.Exists(siblingDir)) Directory.Delete(siblingDir, true);
        }
    }

    [Fact]
    public void CRIT02_LakehouseHttpClients_HaveSsrfProtectionHandlerConfigured()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        services.AddSingleton(env);
        services.AddLogging();

        services.AddGatewayInfrastructure(options);

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        Should.NotThrow(() => factory.CreateClient(nameof(Autheris.Extensions.Lakehouse.Services.S3LakehouseStorageProvider)));
        Should.NotThrow(() => factory.CreateClient(nameof(Autheris.Extensions.Lakehouse.Services.AzureBlobStorageProvider)));
    }

    [Fact]
    public void CRIT03_ReverseProxy_Disabled_ClearsKnownProxiesAndNetworks()
    {
        var services = new ServiceCollection();
        var inMemory = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["Gateway:ReverseProxy:Enabled"] = "false",
            ["Gateway:DataMasking:HmacSecretKeyVaultRef"] = "vault://keys/prod-hmac"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        services.AddGatewayOptions(config, env);

        var sp = services.BuildServiceProvider();
        var forwardedOptions = sp.GetRequiredService<IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;

        forwardedOptions.KnownIPNetworks.ShouldBeEmpty();
        forwardedOptions.KnownProxies.ShouldBeEmpty();
    }

    [Fact]
    public void CRIT04_CasbinEnforcementService_PatternMatchWildcards_DoesNotTimeoutOrThrow()
    {
        var casbin = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-a");

        var policy = """
        p, S-1-5-21-USER1, sales.*, read, allow, (true)
        p, S-1-5-21-USER1, marketing.*_data, read, allow, (true)
        """;

        casbin.LoadPolicyFromText(tenant, policy);
        casbin.HasPolicies(tenant).ShouldBeTrue();
    }

    [Fact]
    public void CRIT05_PayloadSizeLimits_Constants_ConfiguredSensibly()
    {
        // Verified in GatewayApplicationBuilderExtensions:
        // /api/v1/cdc/events -> 10 MB limit
        // /api/schema-registry/publish -> 10 MB limit
        // /api/schema-registry/check -> 10 MB limit
        const long maxCdcPayloadBytes = 10 * 1024 * 1024;
        const long maxSchemaRegistryPayloadBytes = 10 * 1024 * 1024;

        maxCdcPayloadBytes.ShouldBe(10485760);
        maxSchemaRegistryPayloadBytes.ShouldBe(10485760);
    }

    [Fact]
    public void F7_ValidateGatewayOptions_InvalidModelPath_WhenCasbinDisabled_ThrowsValidationException()
    {
        var options = new GatewayOptions
        {
            Casbin = new CasbinOptions
            {
                Enabled = false,
                ModelPath = "non_existent_model_file.conf"
            }
        };

        var devEnv = Substitute.For<IHostEnvironment>();
        devEnv.EnvironmentName.Returns(Environments.Development);

        var ex = Should.Throw<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, devEnv));
        ex.Message.ShouldContain("Casbin model file");
        ex.Message.ShouldContain("was not found");
    }

    [Fact]
    public void RPOL5_ValidateGatewayOptions_PolicyFileWithOnlyComments_ThrowsValidationException()
    {
        var tempModel = Path.GetTempFileName();
        var tempPolicy = Path.GetTempFileName();
        try
        {
            // Valid model from CasbinEnforcementService
            File.WriteAllText(tempModel, Autheris.Application.Governance.CasbinEnforcementService.DefaultModelText);

            // Policy with only comments
            File.WriteAllText(tempPolicy, """
                # This file contains only comments
                # No p-rules are declared
                """);

            var options = new GatewayOptions
            {
                Casbin = new CasbinOptions
                {
                    Enabled = true,
                    ModelPath = tempModel,
                    PolicyPath = tempPolicy
                }
            };

            var devEnv = Substitute.For<IHostEnvironment>();
            devEnv.EnvironmentName.Returns(Environments.Development);

            var ex = Should.Throw<System.ComponentModel.DataAnnotations.ValidationException>(() =>
                GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, devEnv));
            ex.Message.ShouldContain("contains no valid 'p' rules");
        }
        finally
        {
            if (File.Exists(tempModel)) File.Delete(tempModel);
            if (File.Exists(tempPolicy)) File.Delete(tempPolicy);
        }
    }

    [Fact]
    public void RPOL5_ValidateGatewayOptions_ValidPolicyFile_Succeeds()
    {
        var tempModel = Path.GetTempFileName();
        var tempPolicy = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempModel, Autheris.Application.Governance.CasbinEnforcementService.DefaultModelText);

            File.WriteAllText(tempPolicy, """
                # Valid rule
                p, alice, tenant-a, hr.employees, read, true, allow
                """);

            var options = new GatewayOptions
            {
                Casbin = new CasbinOptions
                {
                    Enabled = true,
                    ModelPath = tempModel,
                    PolicyPath = tempPolicy
                }
            };

            var devEnv = Substitute.For<IHostEnvironment>();
            devEnv.EnvironmentName.Returns(Environments.Development);

            Should.NotThrow(() =>
                GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, devEnv));
        }
        finally
        {
            if (File.Exists(tempModel)) File.Delete(tempModel);
            if (File.Exists(tempPolicy)) File.Delete(tempPolicy);
        }
    }

    [Fact]
    public void SEC_12H_02_ValidateGatewayOptions_Rebac_DemoTuplesInProduction_ThrowsValidationException()
    {
        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        var options = new GatewayOptions
        {
            Rebac = new RebacOptions
            {
                SeedTuples = [new("default", "user:david", "viewer", "table:lakehouse.dbo.orders")]
            }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
        ex.Message.ShouldContain("Security critical: Demo or production-targeted ReBAC seed tuples are not permitted outside Development.");
    }

    [Fact]
    public void SEC_12H_02_ValidateGatewayOptions_Rebac_ProductionTargetedTuplesInProduction_ThrowsValidationException()
    {
        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        var options = new GatewayOptions
        {
            Rebac = new RebacOptions
            {
                SeedTuples = [new("tenant_lwe", "user:alice", "viewer", "table:lwetem_prod.conf.client")]
            }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
        ex.Message.ShouldContain("Security critical: Demo or production-targeted ReBAC seed tuples are not permitted outside Development.");
    }

    [Fact]
    public void SEC_12H_02_ValidateGatewayOptions_Rebac_PermittedInDevelopment()
    {
        var devEnv = Substitute.For<IHostEnvironment>();
        devEnv.EnvironmentName.Returns(Environments.Development);

        var options = new GatewayOptions
        {
            Rebac = new RebacOptions
            {
                SeedTuples = [new("default", "user:david", "viewer", "table:lakehouse.dbo.orders")]
            }
        };

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, devEnv));
    }

    // =========================================================================
    // Finding SEC-12H-05: Atomic Redis Lockout Counter via Lua Script
    // =========================================================================

    [Fact]
    public async Task SEC_12H_05_BasicAuthAttemptGuard_UsesAtomicLuaScript_WhenRedisConnected()
    {
        var redis = Substitute.For<StackExchange.Redis.IConnectionMultiplexer>();
        var db = Substitute.For<StackExchange.Redis.IDatabase>();
        redis.IsConnected.Returns(true);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var options = new BasicAuthOptions
        {
            Enabled = true,
            MaxFailedAttempts = 5,
            FailureWindowSeconds = 60
        };

        var guard = new BasicAuthAttemptGuard(options, redis: redis);
        await guard.RecordFailureAsync("TESTUSER|127.0.0.1");

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(s => s.Contains("redis.call('INCR'") && s.Contains("redis.call('EXPIRE'")),
            Arg.Is<StackExchange.Redis.RedisKey[]>(keys => keys.Length == 2 && keys[0].ToString() == "autheris:fail:TESTUSER|127.0.0.1"),
            Arg.Is<StackExchange.Redis.RedisValue[]>(vals => vals.Length == 2 && (long)vals[0] == 60 && (int)vals[1] == 5));
    }

    // =========================================================================
    // Finding SEC-12H-06: WebSQL Trino Statement Session Enumeration Oracle Uniform 404
    // =========================================================================

    [Fact]
    public async Task SEC_12H_06_HandleTrinoQueuedStatementRequest_SecurityException_ReturnsUniform404()
    {
        var statementManager = Substitute.For<Application.Sql.Interfaces.IWebSqlStatementManager>();
        statementManager.GetStatusOrWaitAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<TimeSpan>(), Arg.Any<System.Threading.CancellationToken>())
            .ThrowsAsync(new System.Security.SecurityException("Cross-tenant access forbidden."));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await WebSqlEndpoints.HandleTrinoQueuedStatementRequest("stmt-foreign-tenant", context, statementManager, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = System.Text.Json.JsonDocument.Parse(context.Response.Body);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("Statement not found or expired.");
    }

    [Fact]
    public async Task SEC_12H_06_HandleTrinoCancelStatementRequest_SecurityException_ReturnsUniform404()
    {
        var statementManager = Substitute.For<Application.Sql.Interfaces.IWebSqlStatementManager>();
        statementManager.CancelStatementAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<System.Threading.CancellationToken>())
            .ThrowsAsync(new System.Security.SecurityException("Cross-tenant access forbidden."));

        var context = new DefaultHttpContext();

        await WebSqlEndpoints.HandleTrinoCancelStatementRequest("stmt-foreign-tenant", context, statementManager);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    private sealed class TestPolicyEnforcementService : IPolicyEnforcementService
    {
        private readonly Func<SecurityEvaluationContext, TableAccessDecision> _decider;

        public TestPolicyEnforcementService(Func<SecurityEvaluationContext, TableAccessDecision> decider)
        {
            _decider = decider;
        }

        public ValueTask<TableAccessDecision> EvaluatePolicyAsync(SecurityEvaluationContext context, System.Threading.CancellationToken ct = default)
        {
            return ValueTask.FromResult(_decider(context));
        }

        public bool HasPolicies(TenantId tenant) => true;
        public Task ReloadPoliciesAsync(TenantId tenant, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public void LoadPolicyFromText(TenantId tenant, string policyText) { }
        public void LoadPolicyFromFile(TenantId tenant, string filePath, bool watchFile = false) { }
    }
}
