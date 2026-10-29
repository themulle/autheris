using Autheris.Domain.Security;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.CdcStream;

/// <summary>REQ-CDC-99 I1-C: stream admin endpoints (D-A14). SEC-CDC99-11, SEC-CDC99-21 (ST-13).</summary>
[Trait("Category", "Security")]
public sealed class CdcStreamAdminEndpointsTests : IDisposable
{
    private static readonly CdcStreamId Id = new("0123456789abcdef0123456789abcdef");
    private static readonly TableIdentifier Table = new("finance", "dbo", "invoices");

    private readonly FakeStreamStore _store = new(Id, Table);
    private readonly FakeKeyStore _keys = new();
    private readonly FakeKekProvider _keks = new FakeKekProvider().Add("k1");
    private readonly FakeTimeProvider _time = new();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly ICdcStreamRestarter _restarter = Substitute.For<ICdcStreamRestarter>();
    private readonly CdcStreamKeyring _keyring;

    public CdcStreamAdminEndpointsTests()
    {
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(new GatewayOptions());
        _keyring = new CdcStreamKeyring(_keys, _keks, monitor, _time);
        _restarter.RestartAsync(Arg.Any<CdcStreamId>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    public void Dispose() => _keyring.Dispose();

    private CdcStreamAdminService Service(ICdcStreamRestarter? restarter = null) => new(_store, _keys, _keyring, restarter ?? _restarter);

    private IAuditWriter Audit() => AuditWriterTestServices.Over(_auditRepo);

    private static DefaultHttpContext Ctx(params string[] roles)
    {
        var claims = new List<Claim> { new("sub", "S-1-5-21-ADMIN1"), new("tenant_id", "home") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ADMIN1"),
            TenantId = new TenantId("home"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(roles.Where(r => r == "TenantAdmin")),
            ClusterRoles = new HashSet<string>(roles.Where(r => r is "GovernanceAdmin" or "ClusterAdmin")),
            AuthenticationScheme = "Bearer"
        };
        return context;
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode!.Value;

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("DataSteward")]
    public async Task NonGlobalAdmin_Gets403_OnEveryStreamEndpoint(string role)
    {
        Status(await CdcStreamAdminEndpoints.ListAsync(Ctx(role), Service(), default)).ShouldBe(403);
        Status(await CdcStreamAdminEndpoints.ResyncAsync(Id.Value, false, null, Ctx(role), Service(), Audit(), default)).ShouldBe(403);
        Status(await CdcStreamAdminEndpoints.RotateImageKeyAsync(Id.Value, Ctx(role), Service(), Audit(), default)).ShouldBe(403);
        await _restarter.DidNotReceiveWithAnyArgs().RestartAsync(default, default);
        await _auditRepo.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(default!, default);
    }

    [Fact]
    public async Task List_ReturnsStreamsAndKekCounts_WithoutPayloadOrKeys()
    {
        _keys.Keys.Add(new CdcWrappedKey(Id, "d1", CdcKeyPurpose.Payload, "k1", [1, 2, 3], DateTimeOffset.UnixEpoch, null));
        var result = await CdcStreamAdminEndpoints.ListAsync(Ctx("GovernanceAdmin"), Service(), default);
        Status(result).ShouldBe(200);
        var json = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        json.ShouldContain(Id.Value);
        json.ShouldContain("finance.dbo.invoices");
        json.ShouldContain("\"k1\":1");
        json.ToLowerInvariant().ShouldNotContain("wrappedkey");
        json.ToLowerInvariant().ShouldNotContain("fingerprint");
        json.ToLowerInvariant().ShouldNotContain("payload");
    }

    [Fact]
    public async Task Resync_RestartsAndAudits_WithAllowListedDetails()
    {
        var result = await CdcStreamAdminEndpoints.ResyncAsync(Id.Value, false, null, Ctx("GovernanceAdmin"), Service(), Audit(), default);
        Status(result).ShouldBe(200);
        await _restarter.Received(1).RestartAsync(Id, Arg.Any<CancellationToken>());
        await _auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.CdcStreamResync && e.DetailsJson!.Contains(Id.Value) && e.DetailsJson.Contains("discardedEvents")),
            Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fedcba9876543210fedcba9876543210")]
    public async Task DiscardUnreadable_WithoutMatchingConfirmation_Is400_AndDestroysNothing(string? confirm)
    {
        var store = Substitute.For<ICdcStreamStore>();
        var service = new CdcStreamAdminService(store, _keys, _keyring, _restarter);
        var result = await CdcStreamAdminEndpoints.ResyncAsync(Id.Value, true, confirm, Ctx("GovernanceAdmin"), service, Audit(), default);
        Status(result).ShouldBe(400);
        await store.DidNotReceiveWithAnyArgs().DiscardUnreadableAsync(default, default!, default);
        await _restarter.DidNotReceiveWithAnyArgs().RestartAsync(default, default);
    }

    [Fact]
    public async Task DiscardUnreadable_RemovesOnlyUnreadableKeysEvents_ThenRestarts_AndAuditsCount()
    {
        // k1 wraps "good"; the KEK of "lost" is gone (KEK loss).
        var crypto = new CdcStreamCrypto(_keyring, Microsoft.Extensions.Logging.Abstractions.NullLogger<CdcStreamCrypto>.Instance);
        var good = await crypto.ProtectEventAsync(Id, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, new byte[] { 1 });
        _keys.Keys.Add(new CdcWrappedKey(Id, "lost", CdcKeyPurpose.Payload, "k-gone", new byte[60], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        var store = Substitute.For<ICdcStreamStore>();
        store.GetStreamAsync(Id, Arg.Any<CancellationToken>()).Returns(_store.Descriptor);
        store.DiscardUnreadableAsync(Id, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(5);
        var service = new CdcStreamAdminService(store, _keys, _keyring, _restarter);

        var result = await CdcStreamAdminEndpoints.ResyncAsync(Id.Value, true, Id.Value, Ctx("GovernanceAdmin"), service, Audit(), default);

        Status(result).ShouldBe(200);
        await store.Received(1).DiscardUnreadableAsync(Id, Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 1 && ids.Contains("lost")), Arg.Any<CancellationToken>());
        await _restarter.Received(1).RestartAsync(Id, Arg.Any<CancellationToken>());
        await _auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.DetailsJson!.Contains("\"discardedEvents\":5") && e.DetailsJson.Contains("\"discardUnreadable\":true")), Arg.Any<CancellationToken>());
        good.DekId.ShouldNotBe("lost");
    }

    [Theory]
    [InlineData("fedcba9876543210fedcba9876543210")]
    [InlineData("not-an-id")]
    [InlineData("")]
    public async Task UnknownOrMalformedStream_Is404_AndSameForRotate(string id)
    {
        Status(await CdcStreamAdminEndpoints.ResyncAsync(id, false, null, Ctx("GovernanceAdmin"), Service(), Audit(), default)).ShouldBe(404);
        Status(await CdcStreamAdminEndpoints.RotateImageKeyAsync(id, Ctx("GovernanceAdmin"), Service(), Audit(), default)).ShouldBe(404);
    }

    [Fact]
    public async Task Resync_WithoutRestarterOrWhenRestartFails_Is503()
    {
        var noRestarter = new CdcStreamAdminService(_store, _keys, _keyring, null);
        Status(await CdcStreamAdminEndpoints.ResyncAsync(Id.Value, false, null, Ctx("GovernanceAdmin"), noRestarter, Audit(), default)).ShouldBe(503);

        _restarter.RestartAsync(Id, Arg.Any<CancellationToken>()).Returns(false);
        Status(await CdcStreamAdminEndpoints.ResyncAsync(Id.Value, false, null, Ctx("GovernanceAdmin"), Service(), Audit(), default)).ShouldBe(503);
        await _auditRepo.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(default!, default);
    }

    private CdcStreamAdminService DiscardService(ICdcStreamStore store) => new(store, _keys, _keyring, _restarter);

    [Fact]
    public async Task Resync_KeyUnavailable_Returns503StableCode_AuditsRefusal()
    {
        _keks.Add("k2", active: false).MakeUnavailable("k2");
        _keys.Keys.Add(new CdcWrappedKey(Id, "transient", CdcKeyPurpose.Payload, "k2", new byte[60], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        var store = Substitute.For<ICdcStreamStore>();
        store.GetStreamAsync(Id, Arg.Any<CancellationToken>()).Returns(_store.Descriptor);

        var result = await CdcStreamAdminEndpoints.ResyncAsync(Id.Value, true, Id.Value, Ctx("GovernanceAdmin"), DiscardService(store), Audit(), default);

        Status(result).ShouldBe(503);
        JsonSerializer.Serialize(((IValueHttpResult)result).Value).ShouldContain("CDC_KEY_UNAVAILABLE");
        await store.DidNotReceiveWithAnyArgs().DiscardUnreadableAsync(default, default!, default);
        await _restarter.DidNotReceiveWithAnyArgs().RestartAsync(default, default);
        await _auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.CdcStreamResync
                && e.DetailsJson!.Contains("\"refused\":\"CDC_KEY_UNAVAILABLE\"")
                && e.DetailsJson.Contains("\"discardedEvents\":0")
                && e.DetailsJson.Contains("\"restarted\":false")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resync_Discard_ResponseAndAudit_ContainNoKekIdOrExceptionText()
    {
        _keks.Add("canary-kek-id", active: false).ThrowFor("canary-kek-id", "canary-exception-text");
        _keys.Keys.Add(new CdcWrappedKey(Id, "canary-dek-id", CdcKeyPurpose.Payload, "canary-kek-id", new byte[60], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        var store = Substitute.For<ICdcStreamStore>();
        store.GetStreamAsync(Id, Arg.Any<CancellationToken>()).Returns(_store.Descriptor);

        var result = await CdcStreamAdminEndpoints.ResyncAsync(Id.Value, true, Id.Value, Ctx("GovernanceAdmin"), DiscardService(store), Audit(), default);

        var body = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        var audit = (string)_auditRepo.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<AuditLogEntry>().Single().DetailsJson!;
        foreach (var canary in new[] { "canary-kek-id", "canary-exception-text", "canary-dek-id" })
        {
            body.ShouldNotContain(canary);
            audit.ShouldNotContain(canary);
        }
    }

    [Fact]
    public async Task Resync_Discard_KeyUnavailable_WrongConfirmStreamId_RejectedBeforeClassification()
    {
        var keyStore = Substitute.For<ICdcStreamKeyStore>();
        var store = Substitute.For<ICdcStreamStore>();
        var service = new CdcStreamAdminService(store, keyStore, _keyring, _restarter);

        var result = await CdcStreamAdminEndpoints.ResyncAsync(Id.Value, true, "fedcba9876543210fedcba9876543210", Ctx("GovernanceAdmin"), service, Audit(), default);

        Status(result).ShouldBe(400);
        await keyStore.DidNotReceiveWithAnyArgs().GetKeysAsync(default, default);
        await _auditRepo.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(default!, default);
    }

    [Fact]
    public async Task RotateImageKey_CreatesNewImageKey_AndAudits()
    {
        var crypto = new CdcStreamCrypto(_keyring, Microsoft.Extensions.Logging.Abstractions.NullLogger<CdcStreamCrypto>.Instance);
        var fp = await crypto.FingerprintAsync(Id, [1]);
        var before = (await crypto.ProtectImageAsync(Id, fp, new byte[] { 1 })).DekId;

        Status(await CdcStreamAdminEndpoints.RotateImageKeyAsync(Id.Value, Ctx("ClusterAdmin"), Service(), Audit(), default)).ShouldBe(200);

        (await crypto.ProtectImageAsync(Id, fp, new byte[] { 1 })).DekId.ShouldNotBe(before);
        await _auditRepo.Received(1).RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.CdcStreamKeyRotated), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditStoreDown_FailsClosed()
    {
        _auditRepo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("down")));
        await Should.ThrowAsync<AuditUnavailableException>(() =>
            CdcStreamAdminEndpoints.ResyncAsync(Id.Value, false, null, Ctx("GovernanceAdmin"), Service(), Audit(), default));
    }

    [Theory]
    [InlineData("GET", "/api/v1/cdc/streams")]
    [InlineData("POST", "/api/v1/cdc/streams/{id}/resync")]
    [InlineData("POST", "/api/v1/cdc/streams/{id}/rotate-image-key")]
    public void Routes_RequireGovernanceAdmin_NeverAnonymous(string method, string pattern)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Service());
        builder.Services.AddSingleton(Substitute.For<IAuditWriter>());
        var app = builder.Build();
        app.MapCdcStreamAdminEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));

        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldContain(a => a.Policy == GatewayPolicies.GovernanceAdmin);
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
    }
}
