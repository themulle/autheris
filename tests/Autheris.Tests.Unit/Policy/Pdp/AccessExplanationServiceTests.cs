namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AccessExplanationServiceTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier Orders = new("default", "dbo", "orders");

    private readonly IAccessDecisionPoint _pdp = Substitute.For<IAccessDecisionPoint>();
    private readonly ITableMetadataRepository _tables = Substitute.For<ITableMetadataRepository>();
    private readonly ICatalogVisibilityService _visibility = Substitute.For<ICatalogVisibilityService>();
    private readonly IDataOwnershipRepository _owners = Substitute.For<IDataOwnershipRepository>();
    private readonly IConsentRepository _consents = Substitute.For<IConsentRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IExplainSubjectResolver _resolver = Substitute.For<IExplainSubjectResolver>();

    private static SecurityPrincipalContext Ctx(string sid, string tenant = "tenant-a", params string[] clusterRoles) => new()
    {
        UserSid = new Sid(sid),
        TenantId = new TenantId(tenant),
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(clusterRoles),
        AuthenticationScheme = "test"
    };

    private static TableMetadata Meta(string? ownerTenant = "tenant-a") => new()
    {
        Table = new Table { SourceName = "default", SchemaName = "dbo", TableName = "orders", IsActive = true },
        Identifier = Orders,
        OwnerTenantId = ownerTenant
    };

    private AccessExplanationService Create(TableMetadata? meta, bool visible = true)
    {
        _tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(meta);
        _visibility.FindVisibleAsync(Arg.Any<CatalogCaller>(), Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVisibleRow?>(visible && meta is not null ? default(CatalogVisibleRow) : null));
        _pdp.ExplainAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ValueTask<AccessExplanation>(new AccessExplanation(
                true,
                [new DecisionReason(DecisionStep.Grant, "GRANT", "Consent", "g1")],
                [new ColumnStateExplanation("id", ColumnAccessLevel.Clear)],
                new EffectiveGroupsExplanation([], "Token", "Complete"),
                [],
                7)));
        return new AccessExplanationService(_pdp, _tables, _visibility, _owners, _consents, _resolver, _audit);
    }

    private static ExplainRequest Req(string sid) => new(new ExplainSubjectRef(sid, null), "default.dbo.orders", AccessOperation.Read);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Foreign_object_returns_uniform_404_and_no_predicate_values()
    {
        var caller = Ctx("S-1-ADMIN", clusterRoles: "ClusterAdmin");

        var foreign = await Create(Meta("tenant-b")).ExplainAsync(caller, Req("S-1-ADMIN"));
        var hidden = await Create(Meta(), visible: false).ExplainAsync(caller, Req("S-1-ADMIN"));
        var unknown = await Create(null).ExplainAsync(caller, Req("S-1-ADMIN"));

        foreach (var r in new[] { foreign, hidden, unknown })
        {
            r.Outcome.ShouldBe(ExplainOutcome.NotFound);
            r.Explanation.ShouldBeNull();
        }

        await _pdp.DidNotReceiveWithAnyArgs().ExplainAsync(default!, default);
    }

    [Fact]
    public async Task Explain_Self_Allowed_ForAnyUser()
    {
        var result = await Create(Meta()).ExplainAsync(Ctx("S-1-USER"), Req("S-1-USER"));

        result.Outcome.ShouldBe(ExplainOutcome.Ok);
        result.Explanation!.PolicyEpoch.ShouldBe(7);
        await _owners.DidNotReceiveWithAnyArgs().IsAuthorizedApproverForTableAsync(default, default, default);
    }

    [Fact]
    public async Task Explain_OtherSubject_RequiresOwnerOrAdmin()
    {
        var service = Create(Meta());
        _resolver.ResolveAsync("S-1-OTHER", null, TenantA, Arg.Any<CancellationToken>()).Returns(Ctx("S-1-OTHER"));
        _owners.IsAuthorizedApproverForTableAsync(Orders, new Sid("S-1-OWNER"), Arg.Any<CancellationToken>()).Returns(true);

        (await service.ExplainAsync(Ctx("S-1-USER"), Req("S-1-OTHER"))).Outcome.ShouldBe(ExplainOutcome.Forbidden);
        (await service.ExplainAsync(Ctx("S-1-OWNER"), Req("S-1-OTHER"))).Outcome.ShouldBe(ExplainOutcome.Ok);
        (await service.ExplainAsync(Ctx("S-1-ADMIN", clusterRoles: "GovernanceAdmin"), Req("S-1-OTHER"))).Outcome.ShouldBe(ExplainOutcome.Ok);
        (await service.ExplainAsync(Ctx("S-1-AUD", clusterRoles: "SecurityAuditor"), Req("S-1-OTHER"))).Outcome.ShouldBe(ExplainOutcome.Ok);
    }

    [Fact]
    public async Task Explain_OtherSubject_ResolutionErrorDenies()
    {
        var service = Create(Meta());
        _resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SecurityPrincipalContext?>(Task.FromException<SecurityPrincipalContext?>(new InvalidOperationException("boom"))));

        var result = await service.ExplainAsync(Ctx("S-1-ADMIN", clusterRoles: "GovernanceAdmin"), Req("S-1-OTHER"));

        result.Outcome.ShouldBe(ExplainOutcome.NotFound);
        result.Explanation.ShouldBeNull();
    }

    [Fact]
    public async Task Explain_CrossTenant_Uniform404()
    {
        // A caller of another tenant, even with a cluster role, cannot see tenant-a's object through its own tenant.
        var result = await Create(Meta("tenant-a")).ExplainAsync(Ctx("S-1-ADMIN", "tenant-b", "ClusterAdmin"), Req("S-1-ADMIN"));

        result.Outcome.ShouldBe(ExplainOutcome.NotFound);
    }

    [Fact]
    public async Task Explain_IsAudited_ForEveryOutcome_WithoutPredicateValues()
    {
        var service = Create(Meta());
        await service.ExplainAsync(Ctx("S-1-USER"), Req("S-1-OTHER"));
        await service.ExplainAsync(Ctx("S-1-USER"), Req("S-1-USER"));

        var entries = _audit.ReceivedCalls().Select(c => (AuditLogEntry)c.GetArguments()[0]!).ToList();
        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(e => e.EventType == AuditEventTypes.AuthzExplain && e.TargetTable == "default.dbo.orders");
        entries.Select(e => e.Decision).ShouldBe(["DENY", "ALLOW"]);
        entries[0].DetailsJson.ShouldContain("S-1-OTHER");
    }

    [Fact]
    public async Task Explain_MatchesDecideAsync_ForAllParityScenarios()
    {
        var bypass = new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true } };
        var scenarios = new (GatewayOptions Options, bool Active)[]
        {
            (bypass, true),
            (bypass, false),
            (new GatewayOptions(), true)
        };

        foreach (var (options, active) in scenarios)
        {
            var pdp = new AccessDecisionPoint(Options.Create(options));
            var meta = Meta() with { Table = new Table { SourceName = "default", SchemaName = "dbo", TableName = "orders", IsActive = active } };
            var request = new AccessRequest(Ctx("S-1-USER"), meta, AccessOperation.Read, AccessTransport.Rest);

            var decision = await pdp.DecideAsync(request);
            var explanation = await pdp.ExplainAsync(request);

            explanation.Allowed.ShouldBe(decision.IsAllowed);
            explanation.Trace.ShouldBe(decision.Reasons);
            explanation.PolicyEpoch.ShouldBe(decision.PolicyEpoch);
        }
    }

    [Fact]
    public async Task Grants_List_OmitsPredicateValues()
    {
        var consent = new Consent
        {
            TableIdentifier = Orders,
            GranteeSid = new Sid("S-1-OTHER"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }],
            RowFilters = [new ConsentRowFilter { ColumnName = "region", ValueJson = "[\"SECRET-EU\"]" }]
        };
        _consents.GetConsentPageAsync(Arg.Any<ConsentPageQuery>(), Arg.Any<CancellationToken>()).Returns(new ConsentPage([consent], null));
        var service = Create(Meta());

        var denied = await service.ListGrantsAsync(Ctx("S-1-USER"), "default.dbo.orders");
        var ok = await service.ListGrantsAsync(Ctx("S-1-ADMIN", clusterRoles: "GovernanceAdmin"), "default.dbo.orders");

        denied.Outcome.ShouldBe(ExplainOutcome.Forbidden);
        ok.Outcome.ShouldBe(ExplainOutcome.Ok);
        ok.Grants!.Single().RowFilterIds.ShouldBe([consent.RowFilters[0].Id]);
        JsonSerializer.Serialize(ok.Grants).ShouldNotContain("SECRET-EU");
        _audit.ReceivedCalls().Select(c => ((AuditLogEntry)c.GetArguments()[0]!).EventType).ShouldAllBe(t => t == AuditEventTypes.AuthzGrantsList);
    }

    [Fact]
    public void Request_binds_string_enums_and_missing_operation_is_null()
    {
        var ok = JsonSerializer.Deserialize<ExplainRequest>("{\"subject\":{\"sid\":\"S-1-A\"},\"object\":\"default.dbo.orders\",\"operation\":\"Update\",\"transport\":\"Mcp\"}", Web)!;
        ok.Operation.ShouldBe(AccessOperation.Update);
        ok.Transport.ShouldBe(AccessTransport.Mcp);
        ok.ObjectName.ShouldBe("default.dbo.orders");

        JsonSerializer.Deserialize<ExplainRequest>("{\"subject\":{\"sid\":\"S-1-A\"},\"object\":\"a.b.c\"}", Web)!.Operation.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(99, null)]
    [InlineData(0, 99)]
    public async Task Missing_or_undefined_enum_values_are_BadRequest_and_audited(int? operation, int? transport)
    {
        var service = Create(Meta());
        var request = new ExplainRequest(new ExplainSubjectRef("S-1-USER", null), "default.dbo.orders", (AccessOperation?)operation, (AccessTransport?)transport);

        var result = await service.ExplainAsync(Ctx("S-1-USER"), request);

        result.Outcome.ShouldBe(ExplainOutcome.BadRequest);
        _audit.ReceivedCalls().Count().ShouldBe(1);
        await _pdp.DidNotReceiveWithAnyArgs().ExplainAsync(default!, default);
    }

    [Fact]
    public async Task Foreign_subject_with_default_resolver_reports_SubjectResolved_false_and_Degraded()
    {
        var service = Create(Meta());
        _pdp.ExplainAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var subject = ci.Arg<AccessRequest>().Subject;
            return new ValueTask<AccessExplanation>(new AccessExplanation(false, [], [],
                new EffectiveGroupsExplanation([], "Token", subject.GroupResolutionStatus.ToString()), [], 1));
        });
        var resolver = new DegradedExplainSubjectResolver();
        service = new AccessExplanationService(_pdp, _tables, _visibility, _owners, _consents, resolver, _audit);

        var foreign = await service.ExplainAsync(Ctx("S-1-ADMIN", clusterRoles: "GovernanceAdmin"), Req("S-1-OTHER"));
        var self = await service.ExplainAsync(Ctx("S-1-USER"), Req("S-1-USER"));

        foreign.Explanation!.Groups.Status.ShouldBe("Degraded");
        foreign.Explanation.SubjectResolved.ShouldBeFalse();
        self.Explanation!.SubjectResolved.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("S-1-A B")]
    [InlineData("S-1-<script>")]
    public async Task Malformed_subject_sid_is_BadRequest_before_object_lookup(string sid)
    {
        var service = Create(Meta());

        (await service.ExplainAsync(Ctx("S-1-USER"), Req(sid))).Outcome.ShouldBe(ExplainOutcome.BadRequest);
        (await service.ExplainAsync(Ctx("S-1-USER"), Req(new string('S', 257)))).Outcome.ShouldBe(ExplainOutcome.BadRequest);
        (await service.ExplainAsync(Ctx("S-1-USER"), new ExplainRequest(new ExplainSubjectRef(null, new string('u', 257)), "default.dbo.orders", AccessOperation.Read)))
            .Outcome.ShouldBe(ExplainOutcome.BadRequest);
        await _tables.DidNotReceiveWithAnyArgs().GetTableMetadataAsync(default, default);
    }

    [Fact]
    public async Task Audit_Decision_reflects_explanation_Allowed()
    {
        var service = Create(Meta());
        _pdp.ExplainAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ValueTask<AccessExplanation>(new AccessExplanation(false, [], [], new EffectiveGroupsExplanation([], "Token", "Complete"), [], 1)));

        await service.ExplainAsync(Ctx("S-1-USER"), Req("S-1-USER"));

        ((AuditLogEntry)_audit.ReceivedCalls().Single().GetArguments()[0]!).Decision.ShouldBe("DENY");
    }

    [Fact]
    public async Task Grants_list_flags_truncation_when_page_cap_is_hit()
    {
        _consents.GetConsentPageAsync(Arg.Any<ConsentPageQuery>(), Arg.Any<CancellationToken>()).Returns(new ConsentPage([], Guid.NewGuid()));
        var service = Create(Meta());

        var result = await service.ListGrantsAsync(Ctx("S-1-ADMIN", clusterRoles: "GovernanceAdmin"), "default.dbo.orders");

        result.Outcome.ShouldBe(ExplainOutcome.Ok);
        result.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task NotFound_results_are_identical_for_foreign_hidden_unknown_and_failing_lookup()
    {
        var caller = Ctx("S-1-ADMIN", clusterRoles: "ClusterAdmin");
        Func<AccessExplanationService> failing = () =>
        {
            var created = Create(Meta());
            _tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns<TableMetadata?>(_ => throw new InvalidOperationException("db down"));
            return created;
        };
        var services = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.AddOptions(Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(new Microsoft.Extensions.DependencyInjection.ServiceCollection())));

        var bodies = new List<(int, string)>();
        foreach (var factory in new Func<AccessExplanationService>[] { () => Create(Meta("tenant-b")), () => Create(Meta(), visible: false), () => Create(null), failing })
        {
            var service = factory();
            var outcome = (await service.ExplainAsync(caller, Req("S-1-ADMIN"))).Outcome;
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = services, TraceIdentifier = "trace-fixed" };
            context.Response.Body = new System.IO.MemoryStream();
            await Autheris.Api.Endpoints.AuthorizationExplainEndpoints.ToResult(outcome, null, context).ExecuteAsync(context);
            context.Response.Body.Position = 0;
            bodies.Add((context.Response.StatusCode, await new System.IO.StreamReader(context.Response.Body).ReadToEndAsync()));
        }

        bodies.Select(b => b.Item1).Distinct().ShouldBe([404]);
        bodies.Select(b => b.Item2).Distinct().Count().ShouldBe(1);
    }
}
