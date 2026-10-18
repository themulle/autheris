namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;
using static CallerAttributeTestSupport;

/// <summary>ST-129-02 (BC-129-02): service sets are read by application subjects only; the application id is canonical and compared ordinally.</summary>
[Trait("Category", "Security")]
public sealed class ServiceAttributeProviderTests
{
    private const string AppX = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string AppY = "bbbbbbbb-0000-0000-0000-000000000002";

    private static ServiceAttributeProvider Provider(params ServiceAttributeSet[] sets)
    {
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<System.Threading.CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [], sets));
        return new ServiceAttributeProvider(registry);
    }

    private static ServiceAttributeSet Set(string app, FilterApprovalStatus status = FilterApprovalStatus.Active, params string[] values) =>
        new(Guid.NewGuid(), Tenant, app, "cost_center", values, status);

    private static CallerSubject AppSubject(params (string Type, string Value)[] claims) =>
        CallerSubjectFactory.Create(Principal(Bearer, new[] { ("idtyp", "app") }.Concat(claims)), Tenant, Options());

    [Fact]
    public async Task ApplicationSubject_GetsItsOwnSet_AsRawStrings()
    {
        var result = await Provider(Set(AppX, values: ["100", "200"])).ResolveAsync(AppSubject(("appid", AppX)), Definition(providerKind: "service-set"), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        result.Value!.Count.ShouldBe(2);
        result.Value[0].ShouldBe("100");
    }

    [Fact]
    public async Task ST_129_02_ApplicationX_NeverGetsApplicationYsSet()
    {
        var result = await Provider(Set(AppY, values: ["999"])).ResolveAsync(AppSubject(("appid", AppX)), Definition(providerKind: "service-set"), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_02_GuidCaseVariants_AreOneApplication_WhitespaceVariantsAreNot()
    {
        var provider = Provider(Set(AppX, values: ["100"]));
        (await provider.ResolveAsync(AppSubject(("appid", AppX.ToUpperInvariant())), Definition(providerKind: "service-set"), default)).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);

        var withSpace = new CallerSubject(Tenant, CallerSubjectKind.Application, " " + AppX, [], null, Bearer);
        (await provider.ResolveAsync(withSpace, Definition(providerKind: "service-set"), default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_02_AppIdDisagreement_AppidVersusAzp_IsMissing()
    {
        var subject = AppSubject(("appid", AppX), ("azp", AppY));
        subject.ApplicationId.ShouldBeNull();
        var result = await Provider(Set(AppX, values: ["100"]), Set(AppY, values: ["200"])).ResolveAsync(subject, Definition(providerKind: "service-set"), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_02_PendingSet_IsNotApplied()
    {
        var result = await Provider(Set(AppX, FilterApprovalStatus.PendingApproval, "100")).ResolveAsync(AppSubject(("appid", AppX)), Definition(providerKind: "service-set"), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_02_UserSubject_NeverReadsASet()
    {
        var result = await Provider(Set(UserOid, values: ["100"]), Set(AppX, values: ["100"])).ResolveAsync(UserSubject(), Definition(providerKind: "service-set"), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task OtherTenant_IsNotServed()
    {
        var otherTenantSet = new ServiceAttributeSet(Guid.NewGuid(), new Autheris.Domain.Common.TenantId("tenant_b"), AppX, "cost_center", ["1"], FilterApprovalStatus.Active);
        var result = await Provider(otherTenantSet).ResolveAsync(AppSubject(("appid", AppX)), Definition(providerKind: "service-set"), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task EmptySet_IsMissing()
    {
        var result = await Provider(Set(AppX)).ResolveAsync(AppSubject(("appid", AppX)), Definition(providerKind: "service-set"), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }
}
