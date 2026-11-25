using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Declarative;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Declarative;

/// <summary>Amendment 6 at the HTTP boundary: IT always proposes, an explicit direct apply needs Data Governance, and proposal decisions map to fixed responses.</summary>
[Trait("Category", "Security")]
public sealed class ManifestProposalEndpointTests
{
    private const string Tenant = "tenant-a";
    private static readonly DateTimeOffset ValidUntil = new(DateTimeOffset.UtcNow.AddDays(30).UtcDateTime.Date, TimeSpan.Zero);

    private static readonly string Body =
        "{ \"version\": \"1\", \"name\": \"analytics\", \"policies\": [ { \"domain\": \"default\", \"schema\": \"sales\", \"tableName\": \"orders\", " +
        "\"granteeType\": \"Group\", \"granteeRef\": \"S-1-5-21-GROUP-A\", \"effect\": \"Allow\", \"description\": \"quarterly analytics access\", " +
        "\"validUntil\": \"" + ValidUntil.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\" } ] }";

    private readonly FakeConsentRepository _consents = new();
    private readonly InMemoryManifestProposalRepository _proposals = new();

    private static ClaimsPrincipal Principal(string sid, params string[] roles) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, sid),
            new Claim("tenant_id", Tenant),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker(),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(Autheris.Domain.Identity.GroupResolutionStatus.Complete),
            .. roles.Select(r => new Claim(ClaimTypes.Role, r))
        ], "Bearer"));

    private static DefaultHttpContext Context(ClaimsPrincipal principal, string query = "", string? body = null)
    {
        var context = new DefaultHttpContext
        {
            User = principal,
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Request.QueryString = new QueryString(query);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? Body));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        context.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId(Tenant);
        context.Items[SecurityPrincipalContext.ItemKey] = SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: new TenantId(Tenant));
        return context;
    }

    private (DeclarativeConsentApplyService Apply, ManifestProposalService Proposals) Services()
    {
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid() },
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "id" }]
            });
        var apply = new DeclarativeConsentApplyService(tables, _consents, Substitute.For<IAuditLogRepository>());
        return (apply, new ManifestProposalService(apply, _proposals, new InMemoryAuditWriter()));
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("ClusterAdmin")]
    public async Task It_apply_becomes_a_pending_proposal(string role)
    {
        var (apply, proposals) = Services();
        var context = Context(Principal("S-1-5-21-IT", role));

        var (status, _) = await ExecuteAsync(await GovernanceEndpoints.ApplyOrProposeConsentManifestAsync(context, apply, proposals, CancellationToken.None), context);

        status.ShouldBe(StatusCodes.Status202Accepted);
        _proposals.All.ShouldHaveSingleItem().Status.ShouldBe(ManifestProposalStatus.Pending);
        _consents.ReplaceCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("ClusterAdmin")]
    public async Task ClusterAdmin_direct_apply_refused(string role)
    {
        var (apply, proposals) = Services();
        var context = Context(Principal("S-1-5-21-IT", role), "?mode=apply");

        var (status, _) = await ExecuteAsync(await GovernanceEndpoints.ApplyOrProposeConsentManifestAsync(context, apply, proposals, CancellationToken.None), context);

        status.ShouldBe(StatusCodes.Status403Forbidden);
        _proposals.Count.ShouldBe(0);
        _consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Governance_applies_directly_or_proposes_on_request()
    {
        var (apply, proposals) = Services();

        var direct = Context(Principal("S-1-5-21-DG", "GovernanceAdmin"));
        (await ExecuteAsync(await GovernanceEndpoints.ApplyOrProposeConsentManifestAsync(direct, apply, proposals, CancellationToken.None), direct)).Status
            .ShouldBe(StatusCodes.Status200OK);
        _consents.ReplaceCalls.ShouldBe(1);

        var propose = Context(Principal("S-1-5-21-DG", "GovernanceAdmin"), "?mode=propose");
        (await ExecuteAsync(await GovernanceEndpoints.ApplyOrProposeConsentManifestAsync(propose, apply, proposals, CancellationToken.None), propose)).Status
            .ShouldBe(StatusCodes.Status202Accepted);
        _proposals.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_mode_refused()
    {
        var (apply, proposals) = Services();
        var context = Context(Principal("S-1-5-21-DG", "GovernanceAdmin"), "?mode=force");

        (await ExecuteAsync(await GovernanceEndpoints.ApplyOrProposeConsentManifestAsync(context, apply, proposals, CancellationToken.None), context)).Status
            .ShouldBe(StatusCodes.Status400BadRequest);
        _consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Without_proposal_service_it_is_refused()
    {
        var context = Context(Principal("S-1-5-21-IT", "TenantAdmin"));

        (await ExecuteAsync(await GovernanceEndpoints.ApplyConsentManifestAsync(context, Substitute.For<IDeclarativeConsentApplyService>(), CancellationToken.None), context)).Status
            .ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Confirm_maps_separation_of_duties_stale_and_closed()
    {
        var (apply, proposals) = Services();
        var submitter = Principal("S-1-5-21-IT", "TenantAdmin", "GovernanceAdmin");
        var proposal = await proposals.SubmitAsync(submitter, new DeclarativeConsentManifest("1", [Policy()], Name: "analytics"), ct: CancellationToken.None);

        var own = Context(submitter);
        (await ExecuteAsync(await GovernanceEndpoints.ConfirmManifestProposalAsync(own, proposal.Id, proposals, CancellationToken.None), own)).Status
            .ShouldBe(StatusCodes.Status403Forbidden);

        await apply.ApplyManifestAsync(Principal("S-1-5-21-DG-2", "GovernanceAdmin"), new DeclarativeConsentManifest("2", [Policy()]));
        var stale = Context(Principal("S-1-5-21-DG", "GovernanceAdmin"));
        var (staleStatus, staleBody) = await ExecuteAsync(await GovernanceEndpoints.ConfirmManifestProposalAsync(stale, proposal.Id, proposals, CancellationToken.None), stale);
        staleStatus.ShouldBe(StatusCodes.Status409Conflict);
        staleBody.ShouldContain("MANIFEST_PROPOSAL_STALE");
        var review = Context(Principal("S-1-5-21-DG", "GovernanceAdmin"));
        var (_, reviewBody) = await ExecuteAsync(await GovernanceEndpoints.GetManifestProposalAsync(review, proposal.Id, proposals, CancellationToken.None), review);
        reviewBody.ShouldContain("Unchanged");

        var reject = Context(Principal("S-1-5-21-DG", "GovernanceAdmin"), body: "{ \"reason\": \"duplicate\" }");
        reject.Request.ContentLength = reject.Request.Body.Length;
        (await ExecuteAsync(await GovernanceEndpoints.RejectManifestProposalAsync(reject, proposal.Id, proposals, CancellationToken.None), reject)).Status
            .ShouldBe(StatusCodes.Status200OK);

        var late = Context(Principal("S-1-5-21-DG", "GovernanceAdmin"));
        var (lateStatus, lateBody) = await ExecuteAsync(await GovernanceEndpoints.ConfirmManifestProposalAsync(late, proposal.Id, proposals, CancellationToken.None), late);
        lateStatus.ShouldBe(StatusCodes.Status409Conflict);
        lateBody.ShouldContain("MANIFEST_PROPOSAL_CLOSED");

        var missing = Context(Principal("S-1-5-21-DG", "GovernanceAdmin"));
        (await ExecuteAsync(await GovernanceEndpoints.GetManifestProposalAsync(missing, Guid.NewGuid(), proposals, CancellationToken.None), missing)).Status
            .ShouldBe(StatusCodes.Status404NotFound);
    }

    private static DeclarativeConsentPolicy Policy() =>
        new("default", "sales", "orders", "Group", "S-1-5-21-GROUP-A", "Allow",
            Description: "quarterly analytics access",
            ValidUntil: ValidUntil);
}
