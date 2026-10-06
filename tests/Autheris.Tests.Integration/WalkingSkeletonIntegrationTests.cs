using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

public class WalkingSkeletonIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WalkingSkeletonIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "500");
        });
    }

    private HttpClient CreateClient(bool antiCsrf = true)
    {
        var client = _factory.CreateClient();
        if (antiCsrf)
        {
            client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        }
        return client;
    }

    [Fact]
    public async Task HealthLiveProbe_Returns200Ok()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Live");
    }

    [Fact]
    public async Task HealthReadyProbe_Returns200Ok_Initially()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Ready");
    }

    [Fact]
    public async Task TrafficDrain_WhenDrainingInitiated_HealthReadyReturns503_WhileLiveRemains200()
    {
        // Custom factory for isolated drain testing
        using var isolatedFactory = _factory.WithWebHostBuilder(_ => { });
        var controller = isolatedFactory.Services.GetRequiredService<ITrafficDrainController>();
        var client = isolatedFactory.CreateClient();

        // 1. Initially ready
        var initialReady = await client.GetAsync("/health/ready");
        initialReady.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 2. Initiate shutdown (Phase 1 & 2 of NF-HA-01)
        controller.InitiateGracefulShutdown();

        // 3. /health/ready immediately turns to 503 Service Unavailable
        var drainingReady = await client.GetAsync("/health/ready");
        drainingReady.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        // 4. /health/live still returns 200 OK
        var liveResponse = await client.GetAsync("/health/live");
        liveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GraphQLQuery_WithoutConsent_ReturnsForbidden()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-NO-ACCESS-USER");

        var query = new
        {
            query = @"query { table(domain: ""finance"", name: ""finance_table_1"") { tableName totalCount jsonRows } }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldContain("FORBIDDEN");
        body.ShouldContain("Zero Trust");
    }

    [Fact]
    public async Task GraphQLQuery_WithConsent_ReturnsData_AndMasksSensitiveColumns()
    {
        var userSid = new Sid("S-1-5-21-FINANCE-READER-1");
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value);

        // Seed an active consent for this user on finance.dbo.finance_table_1
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var meta = await repo.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1"));
            meta.ShouldNotBeNull();

            var consent = new Consent
            {
                TableId = meta.Table.Id,
                TableIdentifier = meta.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules = new[]
                {
                    new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
                }
            };
            await repo.CreateConsentAsync(consent);
        }

        var query = new
        {
            query = @"query { table(domain: ""finance"", name: ""finance_table_1"", first: 5) { tableName totalCount jsonRows } }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("FORBIDDEN");
        body.ShouldContain("finance.dbo.finance_table_1");
        body.ShouldContain("totalCount");
        body.ShouldContain("jsonRows");
        // Verify email column was masked
        body.ShouldContain("u***@");
        body.ShouldNotContain("user1@corp.local");
    }

    [Fact]
    public async Task GraphQLMutation_RequestAndApproveConsent_LifecycleFlow()
    {
        var requesterSid = new Sid("S-1-5-21-AUDITOR-99");
        var approverSid = new Sid("S-1-5-21-DATAOWNER-APPROVER");

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", requesterSid.Value);

        // 1. Submit access request
        var requestMutation = new
        {
            query = @"mutation {
                requestTableAccess(
                    domain: ""sales"",
                    schema: ""dbo"",
                    tableName: ""sales_table_1"",
                    justification: ""Auditing 2026 sales pipeline"",
                    durationDays: 14,
                    idempotencyKey: ""key-req-001""
                ) {
                    requestId
                    status
                    message
                }
            }"
        };

        var reqResponse = await client.PostAsJsonAsync("/graphql", requestMutation);
        reqResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reqBody = await reqResponse.Content.ReadAsStringAsync();
        reqBody.ShouldContain("requestId");
        reqBody.ShouldContain("PENDING");

        using var doc = JsonDocument.Parse(reqBody);
        var requestId = doc.RootElement
            .GetProperty("data")
            .GetProperty("requestTableAccess")
            .GetProperty("requestId")
            .GetGuid();

        // 2. Legitimate Data Owner approves request
        var approverClient = CreateClient();
        approverClient.DefaultRequestHeaders.Add("X-Test-User-Sid", approverSid.Value);
        approverClient.DefaultRequestHeaders.Add("X-Test-Roles", "DataOwner");

        var approveMutation = new
        {
            query = $@"mutation {{
                approveConsentRequest(
                    requestId: ""{requestId}"",
                    idempotencyKey: ""key-appr-001""
                ) {{
                    requestId
                    status
                    message
                }}
            }}"
        };

        var apprResponse = await approverClient.PostAsJsonAsync("/graphql", approveMutation);
        apprResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var apprBody = await apprResponse.Content.ReadAsStringAsync();
        apprBody.ShouldContain("APPROVED");

        // 3. Requester now queries sales_table_1 -> Access Allowed!
        var query = new
        {
            query = @"query { table(domain: ""sales"", name: ""sales_table_1"", first: 3) { tableName totalCount } }"
        };

        var finalResponse = await client.PostAsJsonAsync("/graphql", query);
        finalResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var finalBody = await finalResponse.Content.ReadAsStringAsync();
        finalBody.ShouldNotContain("FORBIDDEN");
        finalBody.ShouldContain("sales.dbo.sales_table_1");
    }

    [Fact]
    public async Task NestedRelationQuery_WhenUserHasConsentForParentAndChild_ReturnsNestedDataWithChildMasking()
    {
        var client = CreateClient();
        var userSid = new Sid("S-1-5-21-REL-PARENT-AND-CHILD");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value);

        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var parentMeta = await repo.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1"));
            var childMeta = await repo.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_items"));
            parentMeta.ShouldNotBeNull();
            childMeta.ShouldNotBeNull();

            // Consent for parent
            await repo.CreateConsentAsync(new Consent
            {
                TableId = parentMeta.Table.Id,
                TableIdentifier = parentMeta.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules = new[]
                {
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "name", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
                }
            });

            // Consent for child (with sensitive_note MASKED / REDACTED)
            await repo.CreateConsentAsync(new Consent
            {
                TableId = childMeta.Table.Id,
                TableIdentifier = childMeta.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules = new[]
                {
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "product_name", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "price", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "sensitive_note", AccessLevel = ColumnAccessLevel.Mask }
                }
            });
        }

        var query = new
        {
            query = @"query {
                finance {
                    invoicesWithItems(first: 2) {
                        id
                        amount
                        vendor
                        items {
                            id
                            productName
                            price
                            sensitiveNote
                        }
                    }
                }
            }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("FORBIDDEN");
        body.ShouldContain("invoicesWithItems");
        body.ShouldContain("items");
        body.ShouldContain("Enterprise License Pack");
        // sensitiveNote should be redacted by rule
        body.ShouldContain("[CONFIDENTIAL NOTE]");
    }

    [Fact]
    public async Task NestedRelationQuery_WhenUserHasConsentForParentOnly_ReturnsParentDataWithNullChild()
    {
        var client = CreateClient();
        var userSid = new Sid("S-1-5-21-REL-PARENT-ONLY");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value);

        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var parentMeta = await repo.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1"));
            parentMeta.ShouldNotBeNull();

            // Consent ONLY for parent, NO consent for child (finance_items)
            await repo.CreateConsentAsync(new Consent
            {
                TableId = parentMeta.Table.Id,
                TableIdentifier = parentMeta.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules = new[]
                {
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "name", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
                }
            });
        }

        var query = new
        {
            query = @"query {
                finance {
                    invoicesWithItems(first: 2) {
                        id
                        amount
                        items {
                            id
                            productName
                        }
                    }
                }
            }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        // Parent data MUST be present
        body.ShouldContain("invoicesWithItems");
        // Child field MUST be null because user has no consent on finance_items (Zero-Trust Field Guard)
        body.ShouldContain("\"items\":null");
    }

    [Fact]
    public async Task NestedRelationQuery_WhenParentBatchIsLarge_DispatchesAndAggregatesChunkedResultsSuccessfully()
    {
        var client = CreateClient();
        var userSid = new Sid("S-1-5-21-REL-LARGE-BATCH");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value);
        client.DefaultRequestHeaders.Add("X-Test-Tier", "Enterprise");

        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var parentMeta = await repo.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1"));
            var childMeta = await repo.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_items"));
            parentMeta.ShouldNotBeNull();
            childMeta.ShouldNotBeNull();

            // Grant parent consent
            await repo.CreateConsentAsync(new Consent
            {
                TableId = parentMeta.Table.Id,
                TableIdentifier = parentMeta.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30)
            });

            // Grant child consent
            await repo.CreateConsentAsync(new Consent
            {
                TableId = childMeta.Table.Id,
                TableIdentifier = childMeta.Identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules = new[]
                {
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "product_name", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "sensitive_note", AccessLevel = ColumnAccessLevel.Mask }
                }
            });
        }

        var query = new
        {
            query = @"query {
                finance {
                    invoicesWithItems(first: 15) {
                        id
                        amount
                        vendor
                        items {
                            id
                            productName
                            sensitiveNote
                        }
                    }
                }
            }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("FORBIDDEN");
        body.ShouldContain("invoicesWithItems");
        body.ShouldContain("items");
        body.ShouldContain("[CONFIDENTIAL NOTE]");
    }

    [Fact]
    public async Task GraphQL_AnonymousRequest_ReturnsUnauthorizedChallenge()
    {
        // Finding 14: Anonymous requests without authentication header must receive HTTP 401 Unauthorized
        var client = CreateClient();
        // Send request WITHOUT X-Test-User-Sid or auth header
        var query = new
        {
            query = @"query { catalog { tableName } }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void Startup_WhenEnvironmentIsStaging_AndTestAuthEnabled_ThrowsValidationException()
    {
        // Finding 2 & 23: TestAuthHandler must NEVER be allowed outside of Development environment
        using var stagingFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Staging");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
        });

        // Building the host or creating client must throw OptionsValidationException
        Should.Throw<Exception>(() =>
        {
            stagingFactory.CreateClient();
        });
    }

    [Fact]
    public void Startup_WhenConfigurationViolatesDataAnnotations_ThrowsValidationException()
    {
        // Finding 1: Configuration validation must not be bypassed by imperative Get<GatewayOptions>()
        using var invalidFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Caching:L1MemoryCache:SizeLimitMb", "8"); // [Range(16, 4096)]
        });

        var ex = Should.Throw<Exception>(() =>
        {
            invalidFactory.CreateClient();
        });

        ex.Message.ShouldContain("SizeLimitMb");
    }

    [Fact]
    public async Task GraphQL_PostWithoutAntiCsrfHeader_ReturnsBadRequestOrForbidden()
    {
        // Finding E: CSRF Protection on GraphQL endpoint requires custom preflight header
        var client = CreateClient(antiCsrf: false);
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-CSRF");
        // Deliberately omit GraphQL-Preflight or X-Requested-With header

        var query = new
        {
            query = @"mutation { reloadSchema }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("CSRF");
    }

    [Fact]
    public async Task GraphQL_PostWithUntrustedOrigin_ReturnsForbidden()
    {
        // Finding 6: Untrusted Origin/Referer headers must be rejected with HTTP 403 Forbidden
        var client = CreateClient(antiCsrf: true);
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-CSRF-ORIGIN");
        client.DefaultRequestHeaders.Add("Origin", "https://malicious-attacker.com");

        var query = new
        {
            query = @"query { catalog { tableName } }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Origin");
    }

    [Fact]
    public async Task GraphQL_PostWithTrustedOrigin_Succeeds()
    {
        using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:GraphQL:TrustedOrigins:0", "https://trusted-portal.corp.local");
        });
        var client = customFactory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-CSRF-ORIGIN");
        client.DefaultRequestHeaders.Add("Origin", "https://trusted-portal.corp.local");

        var query = new
        {
            query = @"query { catalog { tableName } }"
        };

        var response = await client.PostAsJsonAsync("/graphql", query);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: body);
    }

    [Fact]
    public void ReverseProxy_WhenConfiguredWithKnownNetworks_SetsUpForwardedHeadersCorrectly()
    {
        using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:ReverseProxy:Enabled", "true");
            builder.UseSetting("Gateway:ReverseProxy:KnownNetworks:0", "10.0.0.0/8");
            builder.UseSetting("Gateway:ReverseProxy:KnownNetworks:1", "192.168.1.0/24");
            builder.UseSetting("Gateway:ReverseProxy:KnownProxies:0", "172.16.0.1");
        });

        // Trigger host build
        _ = customFactory.CreateClient();

        var fwdOptions = customFactory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        fwdOptions.KnownIPNetworks.ShouldContain(net => net.BaseAddress.ToString() == "10.0.0.0" && net.PrefixLength == 8);
        fwdOptions.KnownIPNetworks.ShouldContain(net => net.BaseAddress.ToString() == "192.168.1.0" && net.PrefixLength == 24);
        fwdOptions.KnownProxies.ShouldContain(ip => ip.ToString() == "172.16.0.1");
    }

    [Fact]
    public async Task BasicAuth_LoginEndpoint_WithValidCredentials_Returns200AndUserInfo()
    {
        using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:BasicAuth:Enabled", "true");
            builder.UseSetting("Gateway:Authentication:BasicAuth:Realm", "TestRealm");
            builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:Username", "testadmin");
            builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:Password", "Password123!");
            builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:Sid", "S-1-5-21-999-ADMIN");
            builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:Roles:0", "GovernanceAdmin");
            builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:GroupSids:0", "S-1-5-21-999-GROUP");
        });

        var client = customFactory.CreateClient();
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("testadmin:Password123!"));
        client.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");

        var response = await client.PostAsync("/api/auth/login", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("authenticated").GetBoolean().ShouldBeTrue();
        json.GetProperty("user").GetString().ShouldBe("testadmin");
        json.GetProperty("sid").GetString().ShouldBe("S-1-5-21-999-ADMIN");
    }

    [Fact]
    public async Task BasicAuth_LoginEndpoint_WithInvalidCredentials_Returns401Unauthorized()
    {
        using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:BasicAuth:Enabled", "true");
            builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:Username", "testadmin");
            builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:Password", "Password123!");
        });

        var client = customFactory.CreateClient();
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("testadmin:WrongPassword"));
        client.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");

        var response = await client.GetAsync("/api/auth/login");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ForwardAuth_TraefikHeaders_AuthenticatesSuccessfully()
    {
        using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:ForwardAuth:Enabled", "true");
            builder.UseSetting("Gateway:Authentication:ForwardAuth:RequireTrustedProxy", "true");
            builder.UseSetting("Gateway:Authentication:ForwardAuth:TrustedProxies:0", "127.0.0.1");
        });

        var client = customFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-User", "traefik_k8s_user");
        client.DefaultRequestHeaders.Add("X-Forwarded-Roles", "FinanceReader");
        client.DefaultRequestHeaders.Add("X-Forwarded-Groups", "S-1-5-21-TRAEFIK-GRP");

        var response = await client.GetAsync("/api/auth/login");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("authenticated").GetBoolean().ShouldBeTrue();
        json.GetProperty("user").GetString().ShouldBe("traefik_k8s_user");
        json.GetProperty("sid").GetString().ShouldBe("S-1-5-21-FORWARD-USR-TRAEFIK_K8S_USER");
    }
}


