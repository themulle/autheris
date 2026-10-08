namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security Expert Test Suite verifying Phase 1 (Unified Security Context) and Phase 2 (Governed Data Pipeline Kernel)
/// as well as the repeated security review remediations (RR-L1 through RR-L7).
/// </summary>
public sealed class Phase1AndPhase2SecurityExpertTests
{
    // =========================================================================
    // Domain 1: Unified Security Context & Identity Isolation (Phase 1, RR-L2-01, RR-L2-02, RR-L4-02)
    // =========================================================================

    [Fact]
    public void SEC_EXP_USC_01_NonClusterAdmin_CannotCrossTenant_ViaHeader_ThrowsSecurityException()
    {
        // Arrange: Regular GatewayAdmin with tenant_id "tenant-acme" attempts to query "tenant-cyber"
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-cyber";
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-USER-100"),
            new Claim("tenant_id", "tenant-acme"),
            new Claim(ClaimTypes.Role, "GatewayAdmin") // Tenant admin role cannot cross tenant boundaries
        }, "Bearer");
        httpContext.User = new ClaimsPrincipal(identity);

        // Act & Assert
        var ex = Should.Throw<SecurityException>(() => SecurityContextFactory.CreateFromHttpContext(httpContext));
        ex.Message.ShouldContain("Cross-tenant access forbidden");
    }

    [Fact]
    public void SEC_EXP_USC_02_ClusterAdmin_AllowedCrossTenant_ViaHeader()
    {
        // Arrange: ClusterAdmin with token tenant "tenant-system" specifies target tenant "tenant-tenant42"
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-tenant42";
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-CLUSTER-ADMIN"),
            new Claim("tenant_id", "tenant-system"),
            new Claim(ClaimTypes.Role, "ClusterAdmin")
        }, "Bearer");
        httpContext.User = new ClaimsPrincipal(identity);

        // Act
        var ctx = SecurityContextFactory.CreateFromHttpContext(httpContext);

        // Assert
        ctx.TenantId.Value.ShouldBe("tenant-tenant42");
        ctx.IsClusterAdmin.ShouldBeTrue();
    }

    [Fact]
    public void SEC_EXP_USC_03_ForwardAuth_ClusterAdminRole_Stripped()
    {
        // Arrange: Proxy injects ClusterAdmin role via ForwardAuth header
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "target-tenant";
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-PROXY-CLIENT"),
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.Role, "ClusterAdmin")
        }, "ForwardAuth");
        httpContext.User = new ClaimsPrincipal(identity);

        // Act & Assert: ClusterAdmin is stripped for ForwardAuth, resulting in cross-tenant rejection
        var ex = Should.Throw<SecurityException>(() => SecurityContextFactory.CreateFromHttpContext(httpContext));
        ex.Message.ShouldContain("Cross-tenant access forbidden");
    }

    [Fact]
    public void SEC_EXP_USC_04_Unauthenticated_Caller_Yields_Null_CallerIdentity()
    {
        // Arrange: Anonymous HttpContext (User.Identity is null or unauthenticated)
        var httpContext = new DefaultHttpContext();

        // Act
        var caller = EndpointSecurity.GetCallerIdentity(httpContext);

        // Assert: Must NOT return "ANONYMOUS" as a valid caller SID, but null so endpoints return 401
        caller.ShouldBeNull();
    }

    // =========================================================================
    // Domain 2: Governed Data Pipeline Kernel & Catalog Guardrails (Phase 2, RR-L3-06, RR-L5-02)
    // =========================================================================

    [Theory]
    [InlineData("Sales.Public.Orders", DatabaseDialect.PostgreSql, "sales", "public", "orders")]
    [InlineData("sales.public.orders", DatabaseDialect.Oracle, "SALES", "PUBLIC", "ORDERS")]
    [InlineData("MyDomain.MySchema.MyTable", DatabaseDialect.Databricks, "mydomain", "myschema", "mytable")]
    [InlineData("PreserveCase.Dbo.Users", DatabaseDialect.SqlServer, "PreserveCase", "Dbo", "Users")]
    public void SEC_EXP_KERNEL_01_TableNormalizer_DialectCaseFolding(
        string raw, DatabaseDialect dialect, string expectedDomain, string expectedSchema, string expectedTable)
    {
        var normalized = TableIdentifierNormalizer.Normalize(raw, dialect: dialect);
        normalized.Domain.ShouldBe(expectedDomain);
        normalized.Schema.ShouldBe(expectedSchema);
        normalized.TableName.ShouldBe(expectedTable);
    }

    [Fact]
    public void SEC_EXP_KERNEL_02_TableNormalizer_SinglePart_ExpandsCanonicalDefaults()
    {
        var normalized = TableIdentifierNormalizer.Normalize("invoices", defaultDomain: "erp", defaultSchema: "finance");
        normalized.Domain.ShouldBe("erp");
        normalized.Schema.ShouldBe("finance");
        normalized.TableName.ShouldBe("invoices");
    }

    // =========================================================================
    // Domain 3: Row Filter Polarity & Consent Engine (RR-L4-01)
    // =========================================================================

    [Fact]
    public void SEC_EXP_CONSENT_01_DenyFilter_Polarity_FailsClosedUnderNot()
    {
        var builder = new RowFilterSqlBuilder();

        var allowConsent = new Consent
        {
            Id = Guid.NewGuid(),
            GranteeSid = new Sid("S-1-5-21-USER"),
            TenantId = new TenantId("tenant-1"),
            TableIdentifier = new TableIdentifier("default", "public", "data"),
            Effect = ConsentEffect.Allow,
            RowFilters = new List<ConsentRowFilter>
            {
                new() { ColumnName = "tenant_id", Operator = "EQ", ValueType = "string", ValueJson = "\"tenant-1\"" }
            }
        };

        // Deny consent with user-attribute filter that cannot be resolved (or invalid)
        var denyConsent = new Consent
        {
            Id = Guid.NewGuid(),
            GranteeSid = new Sid("S-1-5-21-USER"),
            TenantId = new TenantId("tenant-1"),
            TableIdentifier = new TableIdentifier("default", "public", "data"),
            Effect = ConsentEffect.Deny,
            RowFilters = new List<ConsentRowFilter>
            {
                new()
                {
                    ColumnName = "dept",
                    Operator = "EQ",
                    ValueType = "string",
                    ValueJson = "\"DEPT\"",
                    ValueSource = "USER_ATTRIBUTE" // Unresolved attribute
                }
            }
        };

        // For deny filters, unresolvable filters fail-closed with 1 = 1 so that NOT (1 = 1) -> FALSE
        var sql = builder.BuildCombinedRowFilter(new[] { allowConsent }, new[] { denyConsent }, DatabaseDialect.SqlServer);

        sql.ShouldNotBeNull();
        sql.ShouldContain("NOT (");
        // Under NOT, 1 = 1 ensures that rows are DENIED rather than leaked
        sql.ShouldContain("1 = 1");
        sql.ShouldNotContain("NOT (1 = 0)");
    }

    // =========================================================================
    // Domain 4: Data Privacy, Masking & HMAC (RR-L6-01, RR-L6-03)
    // =========================================================================

    [Theory]
    [InlineData("12345", "*****")]           // < 8 chars: completely masked with '*' (RR-L6-01: prevents leaking 40-80% of PIN/PLZ)
    [InlineData("123456", "******")]         // < 8 chars: completely masked
    [InlineData("1234567", "*******")]       // < 8 chars: completely masked
    [InlineData("1234", "****")]             // < 8 chars: completely masked
    [InlineData("12", "**")]                 // < 8 chars: completely masked
    [InlineData("12345678", "1******8")]     // 8 chars: retains 1 at start, 1 at end (<= 25% exposed)
    [InlineData("1234567890123456", "12************56")] // >= 16 chars: retains 2 at start, 2 at end
    public void SEC_EXP_MASKING_01_ShortString_LeakageDefense(string raw, string expected)
    {
        var provider = new ColumnMaskingProvider();
        var rule = new MaskingRule { RuleType = "REGEX", PatternOrFormat = null, Replacement = null };

        var masked = provider.MaskValue("zip_code", raw, rule);
        masked.ShouldBe(expected);
    }

    // =========================================================================
    // Domain 5: Transport & Infrastructure Hardening (RR-L1-01, RR-L3-01, RR-L4-04)
    // =========================================================================

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void SEC_EXP_REVERSEPROXY_01_WildcardNetworks_Rejected(string wildcardNetwork)
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "https://vault.azure.net/secrets/hmac-secret" },
            ReverseProxy = new ReverseProxyOptions
            {
                Enabled = true,
                KnownNetworks = new List<string> { wildcardNetwork }
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));

        ex.Message.ShouldContain("Wildcard-Netzwerk (/0)");
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("invalid-cidr-network")]
    public void API_08_ForwardAuth_TrustedNetworks_WildcardOrInvalid_Rejected(string network)
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "https://vault.azure.net/secrets/hmac-secret" },
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    SharedSecret = "01234567890123456789012345678901",
                    RequireTrustedProxy = true,
                    TrustedNetworks = [network]
                }
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));

        ex.Message.ShouldContain("TrustedNetworks");
    }

    [Fact]
    public void API_08_ForwardAuth_TrustedProxies_InvalidIp_Rejected()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "https://vault.azure.net/secrets/hmac-secret" },
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    SharedSecret = "01234567890123456789012345678901",
                    RequireTrustedProxy = true,
                    TrustedProxies = ["not-an-ip-address"]
                }
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));

        ex.Message.ShouldContain("TrustedProxies");
    }

    [Fact]
    public void DEP_14_StartupValidation_RejectsPlaintextBasicAuthPasswordOutsideDevelopment()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "https://vault.azure.net/secrets/hmac-secret" },
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                RequireKerberosOnly = false,
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Users = [new BasicAuthUserConfig { Username = "admin", Password = "PlainTextPassword123!" }]
                }
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));

        ex.Message.ShouldContain("Plaintext passwords are prohibited");
    }

    [Fact]
    public void DEP_14_StartupValidation_AcceptsArgon2idBasicAuthPasswordOutsideDevelopment()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "https://vault.azure.net/secrets/hmac-secret" },
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                RequireKerberosOnly = false,
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Users = [new BasicAuthUserConfig { Username = "admin", Password = "$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNo" }]
                }
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));
    }

    [Fact]
    public async Task DEP_16_Cors_LocalhostFallback_PresentInDevelopmentWhenTrustedOriginsEmpty()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        services.AddSingleton<IHostEnvironment>(env);

        services.AddGatewayInfrastructure(options);
        using var sp = services.BuildServiceProvider();
        var policyProvider = sp.GetRequiredService<ICorsPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), null);

        policy.ShouldNotBeNull();
        policy.Origins.ShouldContain("http://localhost:5000");
        policy.Origins.ShouldContain("https://localhost:5001");
    }

    [Fact]
    public async Task DEP_16_Cors_LocalhostFallback_RemovedInProductionWhenTrustedOriginsEmpty()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        services.AddSingleton<IHostEnvironment>(env);

        services.AddGatewayInfrastructure(options);
        using var sp = services.BuildServiceProvider();
        var policyProvider = sp.GetRequiredService<ICorsPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), null);

        policy.ShouldNotBeNull();
        policy.Origins.ShouldNotContain("http://localhost:5000");
        policy.Origins.ShouldNotContain("https://localhost:5001");
    }

    [Fact]
    public void SEC_EXP_HA_01_Rebac_MultiNode_Requires_Redis()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "https://vault.azure.net/secrets/hmac-secret" },
            HighAvailability = new HighAvailabilityOptions { MultiNodeClusterMode = true },
            Rebac = new RebacOptions { Enabled = true },
            Caching = new CachingOptions { Redis = new RedisOptions { Enabled = false } }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));

        ex.Message.ShouldContain("ReBAC in MultiNodeClusterMode requires Caching.Redis.Enabled = true");
    }

    [Fact]
    public async Task SEC_EXP_DUCKDB_01_LockConfiguration_SetOnConnection()
    {
        var options = Options.Create(new GatewayOptions
        {
            Profile = "Strict",
            DuckDbOlap = new DuckDbOlapOptions
            {
                Enabled = true,
                MaxMemory = "256MB",
                MaxThreads = 2
            }
        });

        var engine = new DuckDbOlapEngine(options, NullLogger<DuckDbOlapEngine>.Instance);
        var table = new TableIdentifier("sales", "public", "orders");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = new[]
            {
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "total", DataType = "double" }
            }
        };

        var source = new OlapTableSource(table, new List<IReadOnlyDictionary<string, object?>>(), meta);

        // Attempting to unlock or change configuration inside a query will fail because lock_configuration = true
        var query = new OlapQueryRequest("SET threads = 16; SELECT 1 AS num", new[] { source });
        await Should.ThrowAsync<Exception>(() => engine.ExecuteOlapQueryAsync(query, CancellationToken.None));
    }

    private static SecurityPrincipalContext CreateTestSecurityContext(string tenant, string user) => new()
    {
        UserSid = new Sid(user),
        TenantId = new TenantId(tenant),
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string> { "DataViewer" },
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Bearer",
        IsAuthenticated = true
    };
}
