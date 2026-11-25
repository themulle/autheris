namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Identity.Attributes;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CallerAttributeAdminEndpointSecurityTests
{
    private static readonly TenantId Tenant = CallerAttributeTestSupport.Tenant;

    [Theory]
    [InlineData("appid", "11111111-1111-1111-1111-111111111111", "entra|99999999-9999-9999-9999-999999999999", "11111111-1111-1111-1111-111111111111")]
    [InlineData("azp", "spn-app-azp", "default|spn-oid", "spn-app-azp")]
    [InlineData("client_id", "22222222-2222-2222-2222-222222222222", "entra|other-oid", "22222222-2222-2222-2222-222222222222")]
    [InlineData("appid", "unrelated-client", "default|my-service-app", "my-service-app")]
    public async Task PutServiceSetAsync_AppOnlyPrincipalWithRealmSidAndClientClaim_RefusesSelfWidening(
        string clientClaimType,
        string clientClaimValue,
        string realmPrimarySid,
        string targetApplicationId)
    {
        var repo = new InMemoryCallerAttributeRepository();
        var registry = new InMemoryCallerAttributeRegistry();
        var audit = new InMemoryAuditLogRepository();
        var options = Options.Create(new GatewayOptions { CallerAttributes = CallerAttributeTestSupport.Options() });
        var service = new CallerAttributeAdministrationService(repo, registry, audit, options, TimeProvider.System, []);

        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, realmPrimarySid, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline),
            new("idtyp", "app"),
            new("oid", "99999999-9999-9999-9999-999999999999"),
            new(clientClaimType, clientClaimValue),
            new("tenant_id", Tenant.Value)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        var sp = new ServiceCollection()
            .AddLogging()
            .AddAuditWriterOver(audit)
            .BuildServiceProvider();

        var ctx = new DefaultHttpContext { User = principal, RequestServices = sp };
        ctx.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid(realmPrimarySid),
            TenantId = Tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>([nameof(GatewayRole.CallerAttributeApprover)]),
            ClusterRoles = new HashSet<string>([nameof(GatewayRole.GovernanceAdmin)]),
            AuthenticationScheme = "Bearer",
            GroupResolutionStatus = GroupResolutionStatus.Complete
        };
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            tenant = Tenant.Value,
            application_id = targetApplicationId,
            attribute = "cost_center",
            values = new[] { "cc-admin" }
        }));
        ctx.Response.Body = new MemoryStream();

        var result = await CallerAttributeAdminEndpoints.PutServiceSetAsync(ctx, service);
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        repo.ServiceSetSaves.ShouldBe(0);
    }

    private sealed class InMemoryCallerAttributeRepository : ICallerAttributeRepository
    {
        public int ServiceSetSaves { get; private set; }

        public Task<CallerAttributeStoreSnapshot> LoadCallerAttributeSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(CallerAttributeStoreSnapshot.Empty);

        public Task<long> GetCallerAttributeGenerationAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public Task<CallerAttributeChangeResult> SaveDefinitionAsync(
            CallerAttributeDefinition definition, string actor, DateTimeOffset at, AuditLogEntry? audit = null, CancellationToken ct = default) =>
            Task.FromResult(CallerAttributeChangeResult.Applied);

        public Task<CallerAttributeChangeResult> DeleteDefinitionAsync(
            TenantId tenant, string name, AuditLogEntry? audit = null, CancellationToken ct = default) =>
            Task.FromResult(CallerAttributeChangeResult.Applied);

        public Task<CallerAttributeChangeResult> SaveServiceSetAsync(
            TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at, AuditLogEntry? audit = null, CancellationToken ct = default)
        {
            ServiceSetSaves++;
            return Task.FromResult(CallerAttributeChangeResult.Applied);
        }
    }

    private sealed class InMemoryCallerAttributeRegistry : ICallerAttributeRegistry
    {
        public ValueTask<CallerAttributeSnapshot> GetAsync(CancellationToken ct) =>
            ValueTask.FromResult(new CallerAttributeSnapshot(
                1,
                [CallerAttributeTestSupport.Definition() with { Status = FilterApprovalStatus.Active }],
                []));

        public void Invalidate() { }
    }
}
