namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DeclarativeConsentApplyAuthorizationTests
{
    private const string CallerTenant = "tenant-alpha";
    private const string OtherTenant = "tenant-beta";
    private const string ActorSid = "S-1-5-21-9001";

    private sealed class CountingTableMetadataRepository : ITableMetadataRepository
    {
        public int LookupCalls { get; private set; }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            LookupCalls++;
            var id = Guid.NewGuid();
            return Task.FromResult<TableMetadata?>(new TableMetadata
            {
                Identifier = table,
                Table = new Table
                {
                    Id = id,
                    SourceName = table.Domain,
                    SchemaName = table.Schema,
                    TableName = table.TableName,
                    DisplayName = table.TableName,
                    IsActive = true
                },
                Columns = [new TableColumn { TableId = id, ColumnName = "id", DataType = "integer" }]
            });
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([]);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class ThrowingApplyService(Exception toThrow) : IDeclarativeConsentApplyService
    {
        public Task<DeclarativeApplyResult> ApplyManifestAsync(
            ClaimsPrincipal caller,
            DeclarativeConsentManifest manifest,
            bool dryRun = false,
            Autheris.Domain.Security.SecurityPrincipalContext? callerContext = null,
            DeclarativePruneMode pruneMode = DeclarativePruneMode.False,
            CancellationToken ct = default) =>
            Task.FromException<DeclarativeApplyResult>(toThrow);
    }

    private static (DeclarativeConsentApplyService Service, CountingTableMetadataRepository Tables, FakeConsentRepository Consents) CreateSut()
    {
        var tables = new CountingTableMetadataRepository();
        var consents = new FakeConsentRepository();
        var service = new DeclarativeConsentApplyService(
            tables,
            consents,
            new NullAuditLogRepository(),
            roleEvaluator: new GatewayRoleEvaluator());
        return (service, tables, consents);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private static ClaimsPrincipal Admin(string roleClaim, string? tenant = CallerTenant, string? sid = ActorSid)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, roleClaim) };
        if (sid is not null)
        {
            claims.Add(new(ClaimTypes.PrimarySid, sid));
        }

        if (tenant is not null)
        {
            claims.Add(new("tenant_id", tenant));
        }

        return Principal([.. claims]);
    }

    private static DeclarativeConsentManifest SinglePolicyManifest(string? manifestTenant = null, string? policyTenant = null) =>
        new(
            "1",
            [
                new DeclarativeConsentPolicy(
                    "default",
                    "sales",
                    "orders",
                    "Group",
                    "S-1-5-21-GROUP-1",
                    "Allow",
                    ValidUntil: DateTimeOffset.UtcNow.AddDays(30),
                    TenantId: policyTenant)
            ],
            TenantId: manifestTenant);

    private static DefaultHttpContext BuildHttpContext(ClaimsPrincipal user, string body = "{ \"version\": \"1\", \"policies\": [] }")
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .BuildServiceProvider(),
            User = user
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Apply_CanonicalGovernanceAdmin_IsAccepted()
    {
        var (svc, _, consents) = CreateSut();

        var result = await svc.ApplyManifestAsync(Admin("GovernanceAdmin"), SinglePolicyManifest());

        result.Applied.ShouldBe(1);
        consents.ReplaceCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Apply_CanonicalClusterAdmin_IsAccepted()
    {
        var (svc, _, consents) = CreateSut();

        var result = await svc.ApplyManifestAsync(Admin("ClusterAdmin", tenant: null), SinglePolicyManifest());

        result.Applied.ShouldBe(1);
        consents.ReplaceCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Apply_LegacyUpperCaseRoleOnly_IsRejected()
    {
        var (svc, tables, consents) = CreateSut();

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(Admin("GOVERNANCE_ADMIN"), SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_DataGovernanceReviewerString_IsRejected()
    {
        var (svc, tables, consents) = CreateSut();

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(Admin("DATA_GOVERNANCE_REVIEWER"), SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_ScopeContainingGovernanceAdminSubstring_IsRejected()
    {
        var (svc, tables, consents) = CreateSut();
        var caller = Principal(
            new(ClaimTypes.PrimarySid, ActorSid),
            new("tenant_id", CallerTenant),
            new("scope", "x-not-governance:admin-y"));

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(caller, SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("governance:admin")]
    [InlineData("xgovernance:admin")]
    [InlineData("governance:administrator")]
    public async Task Apply_ScopeSubstring_VariantsRejected(string scopeValue)
    {
        var (svc, tables, consents) = CreateSut();
        var caller = Principal(
            new(ClaimTypes.PrimarySid, ActorSid),
            new("tenant_id", CallerTenant),
            new("scope", scopeValue));

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(caller, SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_ConsumerRole_IsRejected_WithAccessDenied_NotSqlPolicyViolation()
    {
        var (svc, _, _) = CreateSut();
        var consumer = Admin("Consumer");

        var ex = await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(consumer, SinglePolicyManifest()));
        ex.ShouldNotBeOfType<System.Security.SecurityException>();

        var context = BuildHttpContext(consumer);
        var (status, body) = await ExecuteAsync(
            await GovernanceEndpoints.ApplyConsentManifestAsync(context, svc, CancellationToken.None),
            context);

        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("ACCESS_DENIED");
        body.ShouldNotContain("SQL_POLICY_VIOLATION");
    }

    [Fact]
    public async Task Apply_TenantBoundGovernanceAdmin_WritesOnlyCallerTenant()
    {
        var (svc, _, consents) = CreateSut();

        await svc.ApplyManifestAsync(Admin("GovernanceAdmin", CallerTenant), SinglePolicyManifest());

        var created = consents.Active(DateTimeOffset.UtcNow).ShouldHaveSingleItem();
        created.TenantId.Value.ShouldBe(CallerTenant);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Apply_TenantBoundAdminWithoutTenantClaim_IsRejected(string? tenantClaim)
    {
        var (svc, tables, consents) = CreateSut();
        var caller = Admin("GovernanceAdmin", tenant: tenantClaim);

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(caller, SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(OtherTenant, null)]
    [InlineData(null, OtherTenant)]
    [InlineData(CallerTenant, OtherTenant)]
    public async Task Apply_ManifestNamingOtherTenant_IsRejected(string? manifestTenant, string? policyTenant)
    {
        var (svc, tables, consents) = CreateSut();
        var caller = Admin("GovernanceAdmin", CallerTenant);

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(caller, SinglePolicyManifest(manifestTenant, policyTenant)));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_DenialBody_ContainsNoRoleOrTenantName()
    {
        var (svc, _, _) = CreateSut();
        var adminCaller = Admin("GovernanceAdmin", CallerTenant);
        var serviceEx = await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(adminCaller, SinglePolicyManifest(manifestTenant: OtherTenant)));

        serviceEx.Message.ShouldNotContain("GovernanceAdmin");
        serviceEx.Message.ShouldNotContain("GOVERNANCE_ADMIN");
        serviceEx.Message.ShouldNotContain("ClusterAdmin");
        serviceEx.Message.ShouldNotContain("governance:admin");
        serviceEx.Message.ShouldNotContain(CallerTenant);
        serviceEx.Message.ShouldNotContain(OtherTenant);

        // Endpoint mapping of ConsentApplyForbiddenException produces 403 ACCESS_DENIED without leaking role, scope, or tenant.
        var context = BuildHttpContext(adminCaller, $$"""{ "version": "1", "tenantId": "{{OtherTenant}}", "policies": [] }""");
        var (status, body) = await ExecuteAsync(
            await GovernanceEndpoints.ApplyConsentManifestAsync(
                context,
                new ThrowingApplyService(new ConsentApplyForbiddenException()),
                CancellationToken.None),
            context);

        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("ACCESS_DENIED");
        body.ShouldNotContain("SQL_POLICY_VIOLATION");
        body.ShouldNotContain("GovernanceAdmin");
        body.ShouldNotContain("GOVERNANCE_ADMIN");
        body.ShouldNotContain("ClusterAdmin");
        body.ShouldNotContain("governance:admin");
        body.ShouldNotContain(CallerTenant);
        body.ShouldNotContain(OtherTenant);
    }

    [Fact]
    public async Task Apply_DryRun_RequiresSameAuthority()
    {
        var (svc, tables, consents) = CreateSut();

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(Admin("GOVERNANCE_ADMIN"), SinglePolicyManifest(), dryRun: true));
        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(Admin("GovernanceAdmin", tenant: null), SinglePolicyManifest(), dryRun: true));
        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(Admin("GovernanceAdmin", CallerTenant), SinglePolicyManifest(manifestTenant: OtherTenant), dryRun: true));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_UnauthenticatedCaller_IsRejected()
    {
        var (svc, tables, consents) = CreateSut();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(anonymous, SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_CallerWithoutSid_IsRejectedBeforeRepositoryCalls()
    {
        var (svc, tables, consents) = CreateSut();
        var noSid = Admin("GovernanceAdmin", CallerTenant, sid: null);

        await Should.ThrowAsync<System.Security.SecurityException>(() =>
            svc.ApplyManifestAsync(noSid, SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_TenantPrefixedGovernanceAdmin_IsRejected()
    {
        var (svc, tables, consents) = CreateSut();

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(Admin($"{CallerTenant}:GovernanceAdmin", CallerTenant), SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_TenantPrefixedGovernanceAdminWithMismatchedClaim_IsRejected()
    {
        var (svc, tables, consents) = CreateSut();

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(Admin($"{OtherTenant}:GovernanceAdmin", CallerTenant), SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_WildcardGovernanceAdmin_IsRejected()
    {
        var (svc, tables, consents) = CreateSut();

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(Admin("*:GovernanceAdmin", CallerTenant), SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_InvalidTenantClaim_EvenWithClusterAdmin_IsRejected()
    {
        var (svc, tables, consents) = CreateSut();

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            svc.ApplyManifestAsync(Admin("ClusterAdmin", "evil<script>tenant"), SinglePolicyManifest()));

        tables.LookupCalls.ShouldBe(0);
        consents.ReplaceCalls.ShouldBe(0);
    }

#if AUTHERIS_TEST_AUTH
    [Fact]
    public async Task Apply_TestAuthPipelineGovernanceAdmin_WithDiRealmRegistry_IsAccepted()
    {
        var registry = new Autheris.Application.Identity.IdentityRealmRegistry();
        var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions());
        var pipeline = new Autheris.Api.Security.IdentityPipeline(
            registry,
            new Autheris.Application.Identity.GroupSidResolver(null, [], options),
            options: options);

        var raw = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, ActorSid),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", CallerTenant)
        ], Autheris.Api.Security.TestAuthHandler.SchemeName));

        var processed = await pipeline.ProcessAsync(raw, Autheris.Api.Security.TestAuthHandler.SchemeName, null, CancellationToken.None);

        var tables = new CountingTableMetadataRepository();
        var consents = new FakeConsentRepository();
        var svc = new DeclarativeConsentApplyService(
            tables,
            consents,
            new NullAuditLogRepository(),
            roleEvaluator: new GatewayRoleEvaluator(registry));

        var result = await svc.ApplyManifestAsync(processed, SinglePolicyManifest());

        result.Applied.ShouldBe(1);
        consents.ReplaceCalls.ShouldBe(1);
    }
#endif
}
