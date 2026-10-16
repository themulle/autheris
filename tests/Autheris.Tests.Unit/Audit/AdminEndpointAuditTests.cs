using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>R-2: admin endpoints write their audit through <see cref="IAuditWriter"/>: fail-closed, non-cancelable after the commit.</summary>
[Trait("Category", "Security")]
public sealed class AdminEndpointAuditTests
{
    private static DefaultHttpContext AdminContext()
    {
        var claims = new List<Claim> { new("sub", "S-1-5-21-ADMIN1"), new("tenant_id", "home"), new(ClaimTypes.Role, "ClusterAdmin") };
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ADMIN1"),
            TenantId = new TenantId("home"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string> { "ClusterAdmin" },
            AuthenticationScheme = "Bearer"
        };
        return context;
    }

    private static IAuditLogRepository DownStore()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        repo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("store down")));
        return repo;
    }

    [Fact]
    public async Task TokenRevocation_AuditIsWrittenWithNonCancelableToken()
    {
        using var cts = new CancellationTokenSource();
        var revocation = Substitute.For<ITokenRevocationService>();
        revocation.RevokeAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(_ => { cts.Cancel(); return Task.CompletedTask; });
        var repo = Substitute.For<IAuditLogRepository>();
        repo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<CancellationToken>(1).IsCancellationRequested ? Task.FromCanceled(call.ArgAt<CancellationToken>(1)) : Task.CompletedTask);

        var result = await TokenRevocationEndpoints.RevokeAsync(
            new TokenRevocationEndpoints.RevokeTokenRequest("alice", null), AdminContext(), revocation, AuditWriterTestServices.Over(repo), cts.Token);

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status200OK);
        await repo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.TokenRevoked), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task TokenRevocation_AuditStoreDown_FailsClosed()
    {
        var revocation = Substitute.For<ITokenRevocationService>();

        await Should.ThrowAsync<AuditUnavailableException>(() => TokenRevocationEndpoints.RevokeAsync(
            new TokenRevocationEndpoints.RevokeTokenRequest("alice", null), AdminContext(), revocation, AuditWriterTestServices.Over(DownStore()), default));
    }

    private static RebacTuple Tuple() => new("home", "user:alice", "viewer", "table:sales.public.orders") { ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) };

    [Theory]
    [InlineData(null)]
    [InlineData(400)]
    public async Task SEC_SC_20_RebacTupleAdd_IndividualUserTuple_NeedsAnExpiryWithinTheMaximum(int? days)
    {
        var context = AdminContext();
        var store = Substitute.For<IRebacStore>();
        var tuple = new RebacTuple("home", "user:alice", "viewer", "table:sales.public.orders") { ExpiresAt = days is { } d ? DateTimeOffset.UtcNow.AddDays(d) : null };

        var result = await RebacEndpoints.AddTuplesAsync([tuple], context.Request, store, Substitute.For<IRebacEvaluator>(), AuditWriterTestServices.Over(CancellationAwareRepo()));

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        await store.DidNotReceiveWithAnyArgs().AddTupleAsync(default!, default);
    }

    [Fact]
    public async Task SEC_SC_20_RebacTupleAdd_StructuralAndUsersetTuples_AreNotIndividualGrants()
    {
        var context = AdminContext();
        var store = Substitute.For<IRebacStore>();
        var evaluator = Substitute.For<IRebacEvaluator>();
        evaluator.InvalidateTenantCacheAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        RebacTuple[] standing = [new("home", "group:eng#member", "viewer", "table:sales.public.orders"), new("home", "schema:sales.public", "parent", "table:sales.public.orders")];

        var result = await RebacEndpoints.AddTuplesAsync([.. standing], context.Request, store, evaluator, AuditWriterTestServices.Over(CancellationAwareRepo()));

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task RebacTupleAdd_AuditStoreDown_Returns503()
    {
        var context = AdminContext();
        var store = Substitute.For<IRebacStore>();
        var evaluator = Substitute.For<IRebacEvaluator>();

        // The endpoint lets the AuditUnavailableException escape; the exception handler maps it to 503.
        var ex = await Should.ThrowAsync<AuditUnavailableException>(() =>
            RebacEndpoints.AddTuplesAsync([Tuple()], context.Request, store, evaluator, AuditWriterTestServices.Over(DownStore())));

        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);
        var response = new DefaultHttpContext();
        (await handler.TryHandleAsync(response, ex, default)).ShouldBeTrue();
        response.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task RebacTupleAdd_AuditsWithNonCancelableToken()
    {
        var context = AdminContext();
        using var cts = new CancellationTokenSource();
        context.RequestAborted = cts.Token;
        var store = Substitute.For<IRebacStore>();
        var evaluator = Substitute.For<IRebacEvaluator>();
        evaluator.InvalidateTenantCacheAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => { cts.Cancel(); return Task.CompletedTask; });
        var repo = CancellationAwareRepo();

        var result = await RebacEndpoints.AddTuplesAsync([Tuple()], context.Request, store, evaluator, AuditWriterTestServices.Over(repo));

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status200OK);
        await repo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.RebacTupleAdded), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    private static IAuditLogRepository CancellationAwareRepo()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        repo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<CancellationToken>(1).IsCancellationRequested ? Task.FromCanceled(call.ArgAt<CancellationToken>(1)) : Task.CompletedTask);
        return repo;
    }

    [Fact]
    public async Task RebacTupleRemove_AuditsWithNonCancelableToken()
    {
        var context = AdminContext();
        using var cts = new CancellationTokenSource();
        context.RequestAborted = cts.Token;
        var store = Substitute.For<IRebacStore>();
        store.DeleteTupleAsync(Arg.Any<RebacTuple>(), Arg.Any<CancellationToken>()).Returns(_ => { cts.Cancel(); return new ValueTask<bool>(true); });
        var repo = CancellationAwareRepo();

        var result = await RebacEndpoints.RemoveTupleAsync(Tuple(), context.Request, store, Substitute.For<IRebacEvaluator>(), AuditWriterTestServices.Over(repo));

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status200OK);
        await repo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.RebacTupleRemoved), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task RebacTupleRemove_AuditStoreDown_FailsClosed()
    {
        var context = AdminContext();

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            RebacEndpoints.RemoveTupleAsync(Tuple(), context.Request, Substitute.For<IRebacStore>(), Substitute.For<IRebacEvaluator>(), AuditWriterTestServices.Over(DownStore())));
    }

    [Fact]
    public async Task RebacInvalidationFailure_IsAuditedAsFailClosedEvent_AndStays503()
    {
        var context = AdminContext();
        var evaluator = Substitute.For<IRebacEvaluator>();
        evaluator.InvalidateTenantCacheAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("cache down")));
        var repo = Substitute.For<IAuditLogRepository>();

        var result = await RebacEndpoints.AddTuplesAsync([Tuple()], context.Request, Substitute.For<IRebacStore>(), evaluator, AuditWriterTestServices.Over(repo));

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        await repo.Received(1).RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.RebacInvalidationFailed), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }
}
