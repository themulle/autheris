namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Services;
using Autheris.Application.Usage;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Options.Security;
using Autheris.Tests.Integration.Governance;
using Autheris.Tests.Integration.Governance.Contract;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ScimGroupSupplementEndToEndTests : IClassFixture<ScimGroupSupplementEndToEndTests.HostFixture>
{
    private const string RealmId = "scim-e2e-entra";
    private const string PartnerRealmId = "scim-e2e-partner";
    private const string ScimSecret = "ScimE2eSecret_1234567890_1234567890_ABCDEF";
    private const string PartnerScimSecret = "ScimPartnerSecret_1234567890_1234567890_ABCDEF";
    private const string UserSid = "S-1-5-21-5100-5200-5300-1001";
    private const string OtherUserSid = "S-1-5-21-5100-5200-5300-1002";
    private const string ScimGroupSid = "S-1-5-21-5100-5200-5300-2001";
    private const string ScimNestedParentSid = "S-1-5-21-5100-5200-5300-2002";
    private const string OwnerGroupSid = "S-1-5-21-5100-5200-5300-9901";
    private const string EntraTenant = "11111111-1111-1111-1111-111111111111";
    private const string EntraClient = "22222222-2222-2222-2222-222222222222";
    private const string Issuer = $"https://login.microsoftonline.com/{EntraTenant}/v2.0";
    private const string PartnerIssuer = "https://partner-idp.example.org/oauth2";
    private const string RequestTenant = "scim-e2e-tenant";
    private const string AliceOid = "44444444-4444-4444-4444-444444444444";
    private const string BobOid = "55555555-5555-5555-5555-555555555555";

    private static readonly SymmetricSecurityKey TestKey = new(Encoding.UTF8.GetBytes("scim-e2e-signing-key-at-least-256-bits-long-123456"));
    private static readonly TableIdentifier OpenTable = new("default", "public", "scim_e2e_open");
    private static readonly TableIdentifier FilteredTable = new("default", "public", "scim_e2e_filtered");
    private static readonly TableIdentifier OwnerTable = new("default", "public", "scim_e2e_owner");

    private readonly HostFixture _fixture;

    public ScimGroupSupplementEndToEndTests(HostFixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class HostFixture : IAsyncLifetime
    {
        public string ConnectionString { get; }
        public WebApplicationFactory<Program> Factory { get; }

        public HostFixture()
        {
            ConnectionString = GovernanceTestDatabase.Create(nameof(ScimGroupSupplementEndToEndTests));
            Factory = CreateHostFactory(ConnectionString);
        }

        public static WebApplicationFactory<Program> CreateHostFactory(string connectionString) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                builder.UseSetting("Gateway:GovernanceDb:ConnectionString", connectionString);
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = RequestTenant }));
                builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");

                builder.UseSetting("Gateway:Authentication:Mode", "Direct");
                builder.UseSetting("Gateway:Authentication:EntraId:Enabled", "true");
                builder.UseSetting("Gateway:Authentication:EntraId:TenantId", EntraTenant);
                builder.UseSetting("Gateway:Authentication:EntraId:ClientId", EntraClient);
                builder.UseSetting("Gateway:Authentication:EntraId:Audience", EntraClient);
                builder.UseSetting("Gateway:Authentication:EntraId:RequireHttpsMetadata", "false");

                builder.ConfigureServices(services =>
                {
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.TokenValidationParameters.IssuerSigningKey = TestKey;
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration());
                    });
                    services.PostConfigure<IdentityRealmsOptions>(options =>
                    {
                        options.Realms[RealmId] = new IdentityRealmConfigOptions
                        {
                            Kind = IdentityRealmKind.EntraWorkforce,
                            Issuers = [Issuer],
                            Audiences = [EntraClient],
                            GroupSource = RealmGroupSource.Token,
                            Provisioning = RealmProvisioningMode.Scim,
                            // The SCIM secret principal takes its tenant only from the realm binding (never from a default tenant).
                            FixedTenantId = RequestTenant,
                            Scim = new ScimRealmSecretOptions
                            {
                                PrimarySecret = ScimSecret,
                                RequireCorrelation = true,
                                AllowUpnCorrelation = false,
                                MaxSyncAgeMinutes = 180,
                                StaleSyncBehavior = ScimStaleSyncBehavior.Degrade
                            }
                        };
                        options.Realms[PartnerRealmId] = new IdentityRealmConfigOptions
                        {
                            Kind = IdentityRealmKind.GenericOidc,
                            Issuers = [PartnerIssuer],
                            Audiences = [EntraClient],
                            GroupSource = RealmGroupSource.Resolver,
                            ResolverChain = ["scim-store"],
                            Provisioning = RealmProvisioningMode.Scim,
                            FixedTenantId = RequestTenant,
                            Scim = new ScimRealmSecretOptions
                            {
                                PrimarySecret = PartnerScimSecret,
                                RequireCorrelation = false,
                                AllowUpnCorrelation = false,
                                MaxSyncAgeMinutes = 180,
                                StaleSyncBehavior = ScimStaleSyncBehavior.Degrade
                            }
                        };
                    });
                });
            });

        public async Task InitializeAsync()
        {
            using var scope = Factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

            foreach (var id in new[] { OpenTable, FilteredTable, OwnerTable })
            {
                await repo.UpsertTableMetadataAsync(new TableMetadata
                {
                    Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "PostgreSql", SourceName = id.Domain, IsActive = true },
                    Identifier = id,
                    Columns =
                    [
                        new TableColumn { ColumnName = "id", DataType = "integer" },
                        new TableColumn { ColumnName = "region", DataType = "varchar" }
                    ]
                });
            }

            var openMeta = await repo.GetTableMetadataAsync(OpenTable);
            await repo.CreateConsentAsync(new Consent
            {
                TableId = openMeta!.Table.Id,
                TableIdentifier = OpenTable,
                TenantId = new TenantId(RequestTenant),
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.Group,
                GranteeSid = new Sid(ScimGroupSid),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(7),
                ColumnRules = []
            });

            var filteredMeta = await repo.GetTableMetadataAsync(FilteredTable);
            await repo.CreateConsentAsync(new Consent
            {
                TableId = filteredMeta!.Table.Id,
                TableIdentifier = FilteredTable,
                TenantId = new TenantId(RequestTenant),
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.Group,
                GranteeSid = new Sid(ScimGroupSid),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(7),
                ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }, new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear }],
                RowFilters = [new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueJson = "\"EU\"" }]
            });
        }

        public Task DisposeAsync()
        {
            Factory.Dispose();
            return Task.CompletedTask;
        }
    }

    private static string SignedJwt(string oid, string? onpremSid = null)
    {
        var claims = new List<Claim>
        {
            new("sub", oid),
            new("oid", oid),
            new("tid", EntraTenant),
            new("azp", EntraClient),
            new("scp", "Data.Read")
        };
        if (!string.IsNullOrEmpty(onpremSid))
        {
            claims.Add(new Claim("onprem_sid", onpremSid));
        }

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = EntraClient,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(TestKey, SecurityAlgorithms.HmacSha256Signature),
            Expires = DateTime.UtcNow.AddHours(1)
        });
        return handler.WriteToken(token);
    }

    private static async Task SeedActiveUserWithGroupAsync(WebApplicationFactory<Program> factory, string userKey, string oid, string? objectSid = null)
    {
        using var scope = factory.Services.CreateScope();
        var scim = scope.ServiceProvider.GetRequiredService<IScimService>();
        var tenant = new TenantId(RequestTenant);

        if (await scim.GetUserAsync(tenant, RealmId, userKey) is null)
        {
            await scim.CreateUserAsync(tenant, RealmId, new ScimUser(
                Id: userKey,
                ExternalId: oid,
                UserName: $"{userKey}@example.com",
                DisplayName: userKey,
                Email: $"{userKey}@example.com",
                Active: true,
                DeactivatedAt: null,
                CreatedAt: null,
                UpdatedAt: null,
                ObjectSid: objectSid,
                EntraObjectId: oid));
        }

        var existingGroup = await scim.GetGroupAsync(tenant, RealmId, "grp-scim-1");
        if (existingGroup is null)
        {
            await scim.CreateGroupAsync(tenant, RealmId, new ScimGroup(
                Id: "grp-scim-1",
                ExternalId: "grp-scim-1",
                DisplayName: "SCIM Group 1",
                Members: [new ScimGroupMember(userKey, Type: ScimMemberTypes.User)],
                CreatedAt: null,
                UpdatedAt: null,
                ObjectSid: ScimGroupSid));
        }
        else if (existingGroup.Members?.Any(m => m.Value == userKey) != true)
        {
            // The group is shared by the tests of this class: a user seeded later must still be a member.
            await scim.UpdateGroupAsync(tenant, RealmId, "grp-scim-1", existingGroup with
            {
                Members = [.. existingGroup.Members ?? [], new ScimGroupMember(userKey, Type: ScimMemberTypes.User)]
            });
        }

        // The sync state is shared as well (the stale test ages it): every test starts from a fresh contact.
        var store = scope.ServiceProvider.GetRequiredService<IIdentityStoreRepository>();
        await store.UpsertRealmSyncStateAsync(tenant, RealmId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Entra_token_without_group_plus_scim_membership_grants_access()
    {
        await SeedActiveUserWithGroupAsync(_fixture.Factory, "alice-e2e", AliceOid, UserSid);

        using var client = _fixture.Factory.CreateClient();
        using var loginReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/login");
        loginReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SignedJwt(AliceOid, UserSid));

        var loginResp = await client.SendAsync(loginReq);
        loginResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
        var groups = json.RootElement.GetProperty("groups").EnumerateArray().Select(e => e.GetString()).ToList();
        groups.ShouldContain(ScimGroupSid);

        using var envoyReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName);
        envoyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SignedJwt(AliceOid, UserSid));
        var envoyResp = await client.SendAsync(envoyReq);
        envoyResp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Scim_group_row_filter_applies_to_writes()
    {
        await SeedActiveUserWithGroupAsync(_fixture.Factory, "alice-e2e", AliceOid, UserSid);

        using var scope = _fixture.Factory.Services.CreateScope();
        var explain = scope.ServiceProvider.GetRequiredService<IEffectiveAccessExplanationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", AliceOid),
            new Claim("oid", AliceOid),
            new Claim("onprem_sid", UserSid),
            new Claim("tid", RequestTenant),
            Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim(ScimGroupSid),
            // A principal built by hand carries the pipeline's group-resolution result; without it the groups count as unresolved (fail-closed).
            Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker(),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(Autheris.Domain.Identity.GroupResolutionStatus.Complete)
        ], "Bearer"));

        var effective = await explain.ExplainMyAccessAsync(principal, FilteredTable);
        // The group's row filter applies, but its definition is only shown to callers allowed to see it (#708): a plain caller gets the notice.
        effective.ActiveRowFilters.ShouldNotBeEmpty();
        effective.ActiveRowFilters.ShouldContain(f => f.StartsWith("Row filter applies", StringComparison.Ordinal));
        effective.ActiveRowFilters.ShouldAllBe(f => !f.Contains("region", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stale_sync_denies_data_access()
    {
        await SeedActiveUserWithGroupAsync(_fixture.Factory, "alice-e2e", AliceOid, UserSid);

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIdentityStoreRepository>();
            await store.UpsertRealmSyncStateAsync(
                new TenantId(RequestTenant),
                RealmId,
                DateTimeOffset.UtcNow.AddMinutes(-300),
                DateTimeOffset.UtcNow.AddMinutes(-300));
        }

        using var client = _fixture.Factory.CreateClient();
        using var deniedReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName);
        deniedReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SignedJwt(AliceOid, UserSid));
        var deniedResp = await client.SendAsync(deniedReq);
        deniedResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Refresh the contact timestamp via an authenticated SCIM write (a read contact is throttled for 30 s per realm).
        var touch = new { schemas = new[] { ScimSchemaUris.PatchOp }, Operations = new[] { new { op = "replace", path = "displayName", value = "alice-e2e" } } };
        using var refreshReq = new HttpRequestMessage(HttpMethod.Patch, $"/scim/v2/{RealmId}/Users/alice-e2e") { Content = JsonContent.Create(touch) };
        refreshReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScimSecret);
        var refreshResp = await client.SendAsync(refreshReq);
        refreshResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var allowedReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName);
        allowedReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SignedJwt(AliceOid, UserSid));
        var allowedResp = await client.SendAsync(allowedReq);
        allowedResp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ambiguous_correlation_denies_data_access()
    {
        await SeedActiveUserWithGroupAsync(_fixture.Factory, "alice-e2e", AliceOid, UserSid);
        await SeedActiveUserWithGroupAsync(_fixture.Factory, "bob-e2e", BobOid, OtherUserSid);

        using var client = _fixture.Factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SignedJwt(BobOid, UserSid));

        var resp = await client.SendAsync(req);
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var scope = _fixture.Factory.Services.CreateScope();
        var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        var logs = await auditRepo.QueryAuditLogsAsync(targetTable: "IDENTITY_USERS", tenantId: new TenantId(RequestTenant), limit: 50);
        logs.Any(e => e.EventType == AuditEventTypes.IdentityCorrelationAmbiguous).ShouldBeTrue();
    }

    [Fact]
    public async Task Deprovisioning_revokes_access_on_second_node_immediately()
    {
        const string charlieOid = "66666666-6666-6666-6666-666666666666";
        const string charlieSid = "S-1-5-21-5100-5200-5300-1003";
        await SeedActiveUserWithGroupAsync(_fixture.Factory, "charlie-e2e", charlieOid, charlieSid);

        using var node2 = HostFixture.CreateHostFactory(_fixture.ConnectionString);
        using var node1Client = _fixture.Factory.CreateClient();
        using var node2Client = node2.CreateClient();

        using var beforeReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName);
        beforeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SignedJwt(charlieOid, charlieSid));
        (await node2Client.SendAsync(beforeReq)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var deactivatePatch = new { schemas = new[] { ScimSchemaUris.PatchOp }, Operations = new[] { new { op = "replace", path = "active", value = false } } };
        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"/scim/v2/{RealmId}/Users/charlie-e2e")
        {
            Content = JsonContent.Create(deactivatePatch)
        };
        patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScimSecret);
        (await node1Client.SendAsync(patchReq)).IsSuccessStatusCode.ShouldBeTrue();

        using var afterReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName);
        afterReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SignedJwt(charlieOid, charlieSid));
        (await node2Client.SendAsync(afterReq)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Usage_is_attributed_to_scim_group()
    {
        await SeedActiveUserWithGroupAsync(_fixture.Factory, "alice-e2e", AliceOid, UserSid);

        using var scope = _fixture.Factory.Services.CreateScope();
        var aggregator = scope.ServiceProvider.GetRequiredService<IUsageAggregator>();
        var tenant = new TenantId(RequestTenant);

        aggregator.Record(
            new UsageIdentity(tenant, new Sid(UserSid), new HashSet<Sid> { new(ScimGroupSid) }),
            default(DataSourceTransportFlags),
            caller: null,
            durationMs: 12,
            bytes: 256,
            statusCode: 200,
            facts: [new UsageFact(OpenTable, OpenTable.Domain, UsageOutcome.Allow, 1, true, 0, [new GrantRef("consent-scim", null, null)], default)]);
        var snapshot = aggregator.Swap();
        snapshot.Series.Keys.Any(k => k.Tenant == tenant && k.TableId == OpenTable.ToString()).ShouldBeTrue();
        snapshot.GroupSets.Values.Any(g => g.Contains(new Sid(ScimGroupSid))).ShouldBeTrue();
    }

    [Fact]
    public async Task Scim_group_with_owner_group_id_grants_no_ownership()
    {
        const string malloryOid = "77777777-7777-7777-7777-777777777777";
        const string mallorySid = "S-1-5-21-5100-5200-5300-1099";
        const string requesterSid = "S-1-5-21-5100-5200-5300-1055";
        const string ownerGroupId = "owner-group-99";
        var tenant = new TenantId(RequestTenant);

        using var scope = _fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var ownerAssignments = scope.ServiceProvider.GetRequiredService<IDataOwnerAssignmentRepository>();
        var ownership = scope.ServiceProvider.GetRequiredService<IDataOwnershipRepository>();
        var scim = scope.ServiceProvider.GetRequiredService<IScimService>();
        var store = scope.ServiceProvider.GetRequiredService<IIdentityStoreRepository>();
        var groupResolver = scope.ServiceProvider.GetRequiredService<IGroupSidResolver>();
        var realmRegistry = scope.ServiceProvider.GetRequiredService<IIdentityRealmRegistry>();
        var decisionPipeline = scope.ServiceProvider.GetRequiredService<IConsentDecisionPipeline>();

        var ownerMeta = await repo.GetTableMetadataAsync(OwnerTable);
        ownerMeta.ShouldNotBeNull();
        var assignResult = await ownerAssignments.ApplyTableOwnerAssignmentsAsync(new DataOwnerAssignmentRequest(
            [new TableOwnerAssignmentSpec(OwnerTable, [new DataOwnerSpec(OwnerGroupSid, null, "Table Owner", "owner@corp.example", "PRIMARY")])],
            Replace: false,
            DryRun: false,
            TenantId: tenant));
        assignResult.Success.ShouldBeTrue();

        // 1. A SCIM client in a foreign GenericOidc realm cannot inject an AD/owner objectSid.
        using var client = _fixture.Factory.CreateClient();
        using var hijackSidReq = new HttpRequestMessage(HttpMethod.Post, $"/scim/v2/{PartnerRealmId}/Groups")
        {
            Content = JsonContent.Create(new
            {
                schemas = new[] { ScimSchemaUris.CoreGroup, ScimSchemaUris.AutherisGroupExtension },
                externalId = OwnerGroupSid,
                displayName = "Aliased Owner Group",
                members = Array.Empty<object>(),
                objectSid = OwnerGroupSid
            })
        };
        hijackSidReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PartnerScimSecret);
        (await client.SendAsync(hijackSidReq)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // 2. Creating a SCIM group in the foreign realm binds its key strictly to (realm_id, group_id).
        if (await scim.GetUserAsync(tenant, PartnerRealmId, "partner-u1") is null)
        {
            await scim.CreateUserAsync(tenant, PartnerRealmId, new ScimUser(
                "partner-u1", malloryOid, "partner-u1@example.org", "Partner U1", "partner-u1@example.org", true, null, null, null, EntraObjectId: malloryOid));
        }

        if (await scim.GetGroupAsync(tenant, PartnerRealmId, ownerGroupId) is null)
        {
            await scim.CreateGroupAsync(tenant, PartnerRealmId, new ScimGroup(
                ownerGroupId, OwnerGroupSid, "Foreign Owner Id Group",
                [new ScimGroupMember("partner-u1", Type: ScimMemberTypes.User)], null, null));
        }

        await store.UpsertRealmSyncStateAsync(tenant, PartnerRealmId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var partnerRealm = realmRegistry.GetRealm(PartnerRealmId)!;
        var partnerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("iss", PartnerIssuer), new Claim("oid", malloryOid)], "Bearer"));
        var partnerGroups = await groupResolver.ResolveAsync(partnerPrincipal, partnerRealm, tenant, "Bearer", CancellationToken.None);
        partnerGroups.Status.ShouldBe(GroupResolutionStatus.Complete);
        partnerGroups.GroupSids.ShouldBe([new Sid($"realm:{PartnerRealmId}:{ownerGroupId}")]);
        partnerGroups.GroupSids.ShouldNotContain(new Sid(OwnerGroupSid));
        (await ownership.IsAuthorizedApproverForTableAsync(OwnerTable, new Sid($"realm:{PartnerRealmId}:{ownerGroupId}"))).ShouldBeFalse();

        // 3. Even inside EntraWorkforce SCIM, membership in a group carrying OwnerGroupSid grants no ownership authority.
        if (await scim.GetUserAsync(tenant, RealmId, "mallory-e2e") is null)
        {
            await scim.CreateUserAsync(tenant, RealmId, new ScimUser(
                "mallory-e2e", malloryOid, "mallory-e2e@example.com", "Mallory E2E", "mallory-e2e@example.com", true, null, null, null, ObjectSid: mallorySid, EntraObjectId: malloryOid));
        }

        if (await scim.GetGroupAsync(tenant, RealmId, ownerGroupId) is null)
        {
            await scim.CreateGroupAsync(tenant, RealmId, new ScimGroup(
                ownerGroupId, ownerGroupId, "Entra SCIM Owner SID Group",
                [new ScimGroupMember("mallory-e2e", Type: ScimMemberTypes.User)], null, null, ObjectSid: OwnerGroupSid));
        }

        await store.UpsertRealmSyncStateAsync(tenant, RealmId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        (await ownership.IsAuthorizedApproverForTableAsync(OwnerTable, new Sid(OwnerGroupSid))).ShouldBeTrue();
        (await ownership.IsAuthorizedApproverForTableAsync(OwnerTable, new Sid(mallorySid))).ShouldBeFalse();

        var createdReq = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = ownerMeta.Table.Id,
            TableIdentifier = OwnerTable,
            RequesterSid = new Sid(requesterSid),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid,
            BusinessJustification = "Ownership isolation test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = tenant,
            RequesterIdentifiers = [requesterSid]
        });

        var malloryApprover = new ClaimsPrincipal(new ClaimsIdentity(
        [
            IdentityClaimIssuers.CreatePrimarySidClaim(mallorySid),
            new Claim("oid", malloryOid),
            new Claim("upn", "mallory-e2e@example.com"),
            new Claim("tid", RequestTenant),
            new Claim("autheris:realm", RealmId),
            new Claim(ClaimTypes.Role, "DataOwner"),
            IdentityClaimIssuers.CreateGroupSidClaim(OwnerGroupSid),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        ], "Bearer"));

        var decisionEx = await Should.ThrowAsync<ConsentDecisionException>(() => decisionPipeline.DecideAsync(
            new ConsentDecisionCommand(createdReq.Id, ConsentDecisionKind.Approve, ConsentDecisionChannel.Internal, malloryApprover)));
        decisionEx.StatusCode.ShouldBe(403);
        decisionEx.ErrorCode.ShouldBe("FORBIDDEN");
        decisionEx.Message.ShouldContain("neither a data owner", Case.Insensitive);
    }

    internal static async Task AssertRecursiveCteMatchesAsync(GovernanceTestStore store)
    {
        var repo = store.Services.GetRequiredService<IIdentityStoreRepository>();
        var tenant = store.TenantA;
        const string realm = "cte-realm";

        await repo.CreateUserAsync(tenant, realm, new ScimUser(
            "u-cte", "u-cte", "cte@example.com", "CTE User", "cte@example.com", true, null, null, null, EntraObjectId: AliceOid));

        await repo.CreateGroupAsync(tenant, realm, new ScimGroup("g-child", "g-child", "Child", null, null, null, ObjectSid: ScimGroupSid));
        await repo.CreateGroupAsync(tenant, realm, new ScimGroup("g-parent", "g-parent", "Parent", null, null, null, ObjectSid: ScimNestedParentSid));
        await repo.AddGroupMemberAsync(tenant, realm, "g-child", "u-cte");
        await repo.AddGroupNestingEdgeAsync(tenant, realm, "g-parent", "g-child");
        // Cyclic edge to verify termination across PostgreSQL and SQL Server
        await repo.AddGroupNestingEdgeAsync(tenant, realm, "g-child", "g-parent");

        var effective = await repo.GetEffectiveGroupSidsForUserAsync(tenant, realm, "u-cte");
        effective.MaxDepthExceeded.ShouldBeFalse();
        effective.HasNonCanonicalSid.ShouldBeFalse();
        effective.GroupObjectSids.ShouldContain(ScimGroupSid);
        effective.GroupObjectSids.ShouldContain(ScimNestedParentSid);
    }
}

[Trait("Category", "Security")]
[Collection(GovernancePostgresCollection.Name)]
public sealed class ScimRecursiveCtePostgreSqlTests(GovernancePostgresFixture fixture)
{
    [Fact]
    public async Task Recursive_cte_matches_on_both_engines()
    {
        await using var store = await GovernanceTestStore.CreatePostgreSqlAsync(fixture, "scim_cte_pg");
        if (!store.IsAvailable)
        {
            return;
        }

        await ScimGroupSupplementEndToEndTests.AssertRecursiveCteMatchesAsync(store);
    }
}

[Trait("Category", "Security")]
[Collection(GovernanceSqlServerCollection.Name)]
public sealed class ScimRecursiveCteSqlServerTests(GovernanceSqlServerFixture fixture)
{
    [Fact]
    public async Task Recursive_cte_matches_on_both_engines()
    {
        await using var store = await GovernanceTestStore.CreateSqlServerAsync(fixture, "scim_cte_mssql");
        if (!store.IsAvailable)
        {
            return;
        }

        await ScimGroupSupplementEndToEndTests.AssertRecursiveCteMatchesAsync(store);
    }
}
