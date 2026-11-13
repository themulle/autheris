using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
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
}
