namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// GQL-08: reloadSchema has a cluster-wide effect and is reserved for the canonical ClusterAdmin.
/// </summary>
public sealed class Gql08ReloadSchemaTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task GQL_08_TenantGovernanceAdmin_CannotReloadSchema()
    {
        var bus = Substitute.For<IEventBus>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-GOVADMIN"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ], "TestAuth"));
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = principal });

        var ex = await Should.ThrowAsync<GraphQLException>(() => new Mutation().ReloadSchemaAsync(bus, accessor));

        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        await bus.DidNotReceiveWithAnyArgs().PublishAsync<object>(default!, default!, default);
    }

    [Fact]
    public async Task ReloadSchemaAsync_ClusterAdmin_AuditsBeforePublishingSchemaReloadEvent()
    {
        var order = new List<string>();
        var bus = new RecordingEventBus(order);
        var auditWriter = new RecordingAuditWriter(order);
        var accessor = ClusterAdminAccessor();

        var result = await new Mutation().ReloadSchemaAsync(bus, accessor, auditWriter: auditWriter);

        result.ShouldBeTrue();
        order.ShouldBe(["AUDIT:SCHEMA_RELOAD", "PUBLISH:catalog:changed"]);
    }

    [Fact]
    public async Task ReloadSchemaAsync_WhenAuditWriterFails_FailsClosedAndDoesNotPublishEvent()
    {
        var order = new List<string>();
        var bus = new RecordingEventBus(order);
        var auditWriter = new RecordingAuditWriter(order, fail: true);
        var accessor = ClusterAdminAccessor();

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            new Mutation().ReloadSchemaAsync(bus, accessor, auditWriter: auditWriter));

        order.ShouldBeEmpty();
        bus.PublishCount.ShouldBe(0);
    }

    [Fact]
    public async Task ReloadSchemaAsync_WhenAuditLogRepositoryFails_FailsClosedAndDoesNotPublishEvent()
    {
        var order = new List<string>();
        var bus = new RecordingEventBus(order);
        var auditRepo = new RecordingAuditLogRepository(order, fail: true);
        var accessor = ClusterAdminAccessor();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new Mutation().ReloadSchemaAsync(bus, accessor, auditLog: auditRepo, auditWriter: null));

        order.ShouldBeEmpty();
        bus.PublishCount.ShouldBe(0);
    }

    [Fact]
    public async Task ReloadSchemaAsync_WhenNoAuditSinkAvailable_FailsClosedAndDoesNotPublishEvent()
    {
        var order = new List<string>();
        var bus = new RecordingEventBus(order);
        var accessor = ClusterAdminAccessor();

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            new Mutation().ReloadSchemaAsync(bus, accessor, auditLog: null!, auditWriter: null));

        bus.PublishCount.ShouldBe(0);
    }

    private static IHttpContextAccessor ClusterAdminAccessor()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-CLUSTERADMIN"),
            new Claim(ClaimTypes.Role, "ClusterAdmin")
        ], "TestAuth"));
        return new FixedHttpContextAccessor(new DefaultHttpContext { User = principal });
    }

    private sealed class FixedHttpContextAccessor(HttpContext? ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = ctx;
    }

    private sealed class RecordingEventBus(List<string> order) : IEventBus
    {
        public event Action? ConnectionRestored { add { } remove { } }
        public int PublishCount { get; private set; }

        public Task PublishAsync<T>(string channel, T message, CancellationToken ct = default)
        {
            PublishCount++;
            order.Add($"PUBLISH:{channel}");
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<T>(string channel, Func<T, Task> handler) => new NoopDisposable();
        public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> GetCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class RecordingAuditWriter(List<string> order, bool fail = false) : IAuditWriter
    {
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (fail) throw new AuditUnavailableException("Audit offline");
            order.Add($"AUDIT:{entry.EventType}");
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingAuditLogRepository(List<string> order, bool fail = false) : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (fail) throw new InvalidOperationException("Audit DB offline");
            order.Add($"AUDIT:{entry.EventType}");
            return Task.CompletedTask;
        }

        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default) => RecordAuditEventAsync(entry, ct);
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, Autheris.Domain.Common.TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Autheris.Domain.Common.Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, Autheris.Domain.Common.TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
