namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static CallerAttributeTestSupport;

/// <summary>ST-129-16, test 33 (BC-129-13): the simulation returns hashes by default; clear values need role, reason and an audit record.</summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeSimulationServiceTests : IDisposable
{
    private const string Secret = "top-secret-cost-center-4711";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
    private readonly List<AuditLogEntry> _entries = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private sealed class ValueProvider(string kind) : ICallerAttributeProvider
    {
        public string Kind => kind;

        public ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct) =>
            ValueTask.FromResult(CallerAttributeResult.Resolved(new CallerAttributeValue([Secret, "second"], SqlParameterType.String)));
    }

    private CallerAttributeSimulationService Service(int perMinute = 30)
    {
        var def = Definition("cost_center", "attribute-table", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60);
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [def, def with { Tenant = new TenantId("tenant_b"), Id = Guid.NewGuid() }], []));
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = new CallerAttributeOptions { Enabled = true, SimulationRequestsPerMinute = perMinute, SimulationHashPepper = "pepper" } });
        var resolver = new CallerAttributeResolver(registry, [new ValueProvider("attribute-table")], options, _time);
        _disposables.Add(resolver);
        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(_entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return new CallerAttributeSimulationService(resolver, audit, options, _time);
    }

    private static CallerAttributeSimulationRequest Request(bool reveal = false, string? reason = null, TenantId? tenant = null, string attribute = "cost_center") =>
        new(tenant ?? Tenant, SubjectKeyKind.EntraObjectId, UserOid, null, null, [attribute], reveal, reason);

    private static Task<IReadOnlyList<CallerAttributeSimulationItem>> Run(CallerAttributeSimulationService s, CallerAttributeSimulationRequest r, string admin = "admin-1", bool inspector = false, bool cross = false) =>
        s.SimulateAsync(r, admin, Tenant, inspector, cross);

    [Fact]
    public async Task ST_129_16_DefaultOutput_HasNoClearValues_ButCountAndHashes()
    {
        var item = (await Run(Service(), Request())).ShouldHaveSingleItem();
        item.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        item.Count.ShouldBe(2);
        item.Values.ShouldBeNull();
        item.ValueHashes!.ShouldAllBe(h => h.Length == 8 && h.All(Uri.IsHexDigit));
        string.Join(' ', item.ValueHashes!).ShouldNotContain(Secret);
    }

    [Fact]
    public async Task Hashes_AreStable_AndDifferPerTenant()
    {
        var service = Service();
        var a1 = (await Run(service, Request())).Single().ValueHashes![0];
        var a2 = (await Run(service, Request())).Single().ValueHashes![0];
        var other = (await service.SimulateAsync(Request(tenant: new TenantId("tenant_b")), "admin-1", new TenantId("tenant_b"), false, false)).Single().ValueHashes![0];
        a1.ShouldBe(a2);
        a1.ShouldNotBe(other);
    }

    [Fact]
    public async Task ST_129_16_Reveal_NeedsTheRole_AndAReason()
    {
        var service = Service();
        (await Should.ThrowAsync<CallerAttributeSimulationException>(() => Run(service, Request(reveal: true, reason: "support case 1")))).Code.ShouldBe("RevealRoleRequired");
        (await Should.ThrowAsync<CallerAttributeSimulationException>(() => Run(service, Request(reveal: true), inspector: true))).Code.ShouldBe("ReasonRequired");
        (await Should.ThrowAsync<CallerAttributeSimulationException>(() => Run(service, Request(reveal: true, reason: new string('x', 257)), inspector: true))).Code.ShouldBe("ReasonRequired");

        var item = (await Run(service, Request(reveal: true, reason: "support case 1"), inspector: true)).Single();
        item.Values.ShouldBe([Secret, "second"]);
    }

    [Fact]
    public async Task EverySimulation_IsAudited_WithoutValues()
    {
        var service = Service();
        await Run(service, Request());
        await Run(service, Request(reveal: true, reason: "support case 1"), inspector: true);

        _entries.Count.ShouldBe(2);
        _entries.ShouldAllBe(e => e.EventType == AuditEventTypes.CallerAttributeSimulationRead);
        _entries.ShouldAllBe(e => !e.DetailsJson.Contains(Secret) && !e.DetailsJson.Contains("second") && !e.DetailsJson.Contains(UserOid));
        _entries[1].DetailsJson.ShouldContain("support case 1");
        _entries[0].DetailsJson.ShouldContain("\"count\":2");
    }

    [Fact]
    public async Task ST_129_16_ASubjectOfAnotherTenant_IsRefused_UnlessClusterWide()
    {
        var service = Service();
        var other = new TenantId("tenant_b");
        (await Should.ThrowAsync<CallerAttributeSimulationException>(() => Run(service, Request(tenant: other)))).Code.ShouldBe("TenantMismatch");
        (await Run(service, Request(tenant: other), cross: true)).Single().Outcome.ShouldBe(CallerAttributeOutcome.Resolved);   // a cluster administrator may
    }

    [Fact]
    public async Task RateLimit_IsPerAdministrator_AndRecovers()
    {
        var service = Service(perMinute: 2);
        await Run(service, Request());
        await Run(service, Request());
        (await Should.ThrowAsync<CallerAttributeSimulationException>(() => Run(service, Request()))).Code.ShouldBe("RateLimited");
        await Run(service, Request(), admin: "admin-2");
        _time.Advance(TimeSpan.FromSeconds(61));
        await Run(service, Request());
    }

    [Theory]
    [InlineData("Cost")]
    [InlineData("")]
    public async Task InvalidAttributeNames_AreRefused(string name) =>
        (await Should.ThrowAsync<CallerAttributeSimulationException>(() => Run(Service(), Request(attribute: name)))).Code.ShouldBe("InvalidAttributes");

    [Fact]
    public async Task ASubjectWithoutAKey_IsRefused() =>
        (await Should.ThrowAsync<CallerAttributeSimulationException>(() => Run(Service(), new CallerAttributeSimulationRequest(Tenant, null, null, null, null, ["cost_center"], false, null)))).Code.ShouldBe("InvalidSubject");

    [Fact]
    public async Task Disabled_IsRefused()
    {
        var resolver = Substitute.For<ICallerAttributeResolver>();
        var service = new CallerAttributeSimulationService(resolver, Substitute.For<IAuditLogRepository>(), Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), _time);
        (await Should.ThrowAsync<CallerAttributeSimulationException>(() => Run(service, Request()))).Code.ShouldBe("Disabled");
    }
}
