using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Autheris.Tests.Common.Fakes;

namespace Autheris.Tests.Unit.Audit;

/// <summary>The audit archive export is removed: its keys fail startup, retention keeps signed checkpoints and a valid chain.</summary>
[Trait("Category", "Security")]
public sealed class AuditRetentionSecurityTests
{
    private static string Hash(string prev, long seq) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{prev}|{seq}")));

    private static bool ChainValid(IReadOnlyList<Row> rows, string startPrev)
    {
        var prev = startPrev;
        foreach (var r in rows)
        {
            if (r.PrevHash != prev || r.Hash != Hash(prev, r.Seq))
            {
                return false;
            }

            prev = r.Hash;
        }

        return true;
    }

    [Theory]
    [InlineData("Gateway:Audit:Worm:Enabled", "true")]
    [InlineData("Gateway:Audit:Worm:Enabled", "false")]
    [InlineData("Gateway:Audit:Worm:S3Bucket", "")]
    [InlineData("Gateway:Audit:Worm:ExportPath", "/tmp/x")]
    public async Task Archive_key_fails_startup_and_purge_keeps_checkpoints_and_chain_valid(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(configuration))
            .Message.ShouldContain("Gateway:Audit:Worm was removed");

        // 100 chained rows, the first 60 are eligible for purge.
        var rows = new List<Row>();
        var prev = "GENESIS";
        for (long i = 1; i <= 100; i++)
        {
            var h = Hash(prev, i);
            rows.Add(new Row(i, prev, h));
            prev = h;
        }

        var repository = Substitute.For<IAuditLogRepository>();
        repository.PlanPurgePrefixAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((60L, 61L, rows[60].PrevHash, 60L));
        repository.ExecutePurgeBatchAsync(60L, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                rows.RemoveAll(r => r.Seq <= 60);
                return 60;
            });

        AuditPurgeCheckpoint? anchored = null;
        var anchorStore = Substitute.For<IAuditChainAnchorStore>();
        anchorStore.When(a => a.SavePurgeCheckpoint(Arg.Any<AuditPurgeCheckpoint>()))
            .Do(c => anchored = c.Arg<AuditPurgeCheckpoint>());
        AuditPurgeCheckpoint? persisted = null;
        repository.When(r => r.SavePurgeCheckpointAsync(Arg.Any<AuditPurgeCheckpoint>(), Arg.Any<CancellationToken>()))
            .Do(c => persisted = c.Arg<AuditPurgeCheckpoint>());

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IAuditLogRepository)).Returns(repository);
        sp.GetService(typeof(IAuditPurgeKeyProvider)).Returns(null);

        var keyProvider = Substitute.For<IAuditPurgeKeyProvider>();
        keyProvider.GetAuditHmacKey().Returns(RandomNumberGenerator.GetBytes(32));
        var options = new AuditRetentionOptions { Enabled = true, RetentionDays = 90, BatchSize = 100, InterBatchDelay = TimeSpan.Zero };
        var optionsMonitor = new TestOptionsMonitor<AuditRetentionOptions>(options);

        var worker = new AuditRetentionPurgeWorker(
            scopeFactory,
            Substitute.For<IDistributedClusterStateProvider>(),
            optionsMonitor,
            Substitute.For<IAuditWriter>(),
            anchorStore,
            NullLogger<AuditRetentionPurgeWorker>.Instance,
            keyProvider,
            null,
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.ExecutePurgeCycleAsync(options, CancellationToken.None);

        anchored.ShouldNotBeNull();
        anchored.Signature.ShouldNotBeNullOrEmpty();
        anchored.FirstSurvivingSequence.ShouldBe(61);
        persisted.ShouldNotBeNull();
        persisted.Signature.ShouldBe(anchored.Signature);

        rows.Count.ShouldBe(40);
        ChainValid(rows, anchored.SurvivingPrevHash).ShouldBeTrue();
    }

    private sealed record Row(long Seq, string PrevHash, string Hash);
}
