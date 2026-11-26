using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Domain.Common;
using Shouldly;

namespace Autheris.Tests.Unit.Governance.Requests;

/// <summary>Records consent events appended by the permission request engine, with the transaction each one was appended in.</summary>
internal sealed class RecordingConsentEventOutbox : IConsentEventOutbox
{
    private static readonly string[] ThinRequestPayloadProperties =
        ["tenantId", "requestId", "objectId", "status", "version", "workflowMode", "occurredAt", "links"];

    public List<(ConsentEventWrite Entry, DbTransaction? Transaction)> Appended { get; } = [];

    public IReadOnlyList<ConsentEventWrite> Entries => Appended.Select(a => a.Entry).ToList();

    public Task<long> AppendAsync(ConsentEventWrite entry, DbTransaction? transaction = null, CancellationToken ct = default)
    {
        Appended.Add((entry, transaction));
        return Task.FromResult((long)Appended.Count);
    }

    /// <summary>Asserts the thin request payload (no decider, reason, justification or scope) and returns its status and version.</summary>
    public static (string Status, int Version, string WorkflowMode) ReadThinRequestPayload(ConsentEventWrite entry)
    {
        using var doc = JsonDocument.Parse(entry.PayloadJson);
        var root = doc.RootElement;
        root.EnumerateObject().Select(p => p.Name).ShouldBe(ThinRequestPayloadProperties, ignoreOrder: true);
        root.GetProperty("requestId").GetGuid().ToString("D").ShouldBe(entry.SubjectId);
        root.GetProperty("tenantId").GetString().ShouldBe(entry.TenantId);
        return (root.GetProperty("status").GetString()!, root.GetProperty("version").GetInt32(), root.GetProperty("workflowMode").GetString()!);
    }

    public Task<IReadOnlyList<PendingConsentEventDelivery>> ClaimDeliveriesAsync(
        int limit, DateTimeOffset now, TimeSpan leaseDuration, string leaseOwner, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task MarkDeliveredAsync(Guid eventId, Guid externalSystemId, string leaseOwner, DateTimeOffset deliveredAt, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task RescheduleDeliveryAsync(Guid eventId, Guid externalSystemId, string leaseOwner, DateTimeOffset nextAttemptAt, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task MarkDeadLetteredAsync(Guid eventId, Guid externalSystemId, string leaseOwner, DateTimeOffset deadLetteredAt, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ConsentEventFeedPage> ReadFeedAsync(
        TenantId tenantId, long sinceSeq, int pageSize, Guid? externalSystemId = null, DateTimeOffset? retentionCutoff = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<int> PurgeExpiredEventsAsync(DateTimeOffset olderThan, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

/// <summary>A decision transaction stand-in whose identity the tests compare with the transaction the event was appended in.</summary>
internal sealed class StubDbTransaction : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection? DbConnection => null;

    public override void Commit()
    {
    }

    public override void Rollback()
    {
    }
}
