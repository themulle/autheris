namespace Autheris.Tests.Unit.GraphQL;

using System.Security;
using System.Security.Claims;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #341 (C341-6, C341-7): a failing catalog sync answers a fixed governed code, logs the cause server side and closes the audit entry.</summary>
[Trait("Category", "Security")]
public sealed class Issue341SyncCatalogErrorTests
{
    private static (IHttpContextAccessor Accessor, ILogger<Mutation> Logger) Accessor()
    {
        var logger = Substitute.For<ILogger<Mutation>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var services = new ServiceCollection().AddSingleton(logger).BuildServiceProvider();
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-SYNC"), new("tenant_id", "tenant-a"), new(ClaimTypes.Role, "GovernanceAdmin") };
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")),
            RequestServices = services
        };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);
        return (accessor, logger);
    }

    [Fact]
    public async Task SyncDataCatalog_SecurityException_SyncFailedFixed_CauseLogged_AuthorizationBeforeTry()
    {
        var sync = Substitute.For<IDataCatalogSyncService>();
        sync.SyncCatalogAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Autheris.Application.DataCatalog.Models.CatalogSyncResult>>(_ => throw new SecurityException("egress to host internal.example refused"));
        var audit = Substitute.For<IAuditWriter>();
        var (accessor, logger) = Accessor();

        var ex = await Should.ThrowAsync<GraphQLException>(() => new Mutation().SyncDataCatalogAsync(false, sync, accessor, audit, logger: logger));

        ex.Errors[0].Code.ShouldBe("SYNC_FAILED");
        ex.Errors[0].Message.ShouldNotContain("internal.example");
        logger.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
            && c.GetArguments()[0] is LogLevel.Error
            && c.GetArguments()[3] is SecurityException).ShouldBeTrue();
        await audit.Received().RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.DataCatalogSync && e.Decision == "ERROR"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncDataCatalog_GatewayForbidden_AnswersAccessDenied()
    {
        var sync = Substitute.For<IDataCatalogSyncService>();
        sync.SyncCatalogAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Autheris.Application.DataCatalog.Models.CatalogSyncResult>>(_ => throw new GatewayForbiddenException("detail with table hr.salaries"));
        var (accessor, _) = Accessor();

        var ex = await Should.ThrowAsync<GraphQLException>(() => new Mutation().SyncDataCatalogAsync(false, sync, accessor, Substitute.For<IAuditWriter>()));

        ex.Errors[0].Code.ShouldBe("ACCESS_DENIED");
        ex.Errors[0].Message.ShouldBe("Access denied.");
    }

    [Fact]
    public async Task SyncDataCatalog_FailureAuditUnavailable_StillAnswersSyncFailed()
    {
        var sync = Substitute.For<IDataCatalogSyncService>();
        sync.SyncCatalogAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Autheris.Application.DataCatalog.Models.CatalogSyncResult>>(_ => throw new InvalidOperationException("boom"));
        var audit = Substitute.For<IAuditWriter>();
        var calls = 0;
        audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? Task.CompletedTask : throw new InvalidOperationException("audit down"));
        var (accessor, _) = Accessor();

        var ex = await Should.ThrowAsync<GraphQLException>(() => new Mutation().SyncDataCatalogAsync(false, sync, accessor, audit));

        ex.Errors[0].Code.ShouldBe("SYNC_FAILED");
    }
}
