namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.IO;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class SqliteWebhookOutboxContractTests : WebhookOutboxContractTests
{
    private static SqliteGovernanceRepository NewRepository(string connectionString) =>
        GovernanceStoreFactory.CreateSqlite(
            Substitute.For<IEpochValidationService>(),
            Options.Create(new GatewayOptions
            {
                GovernanceDb = new GovernanceDbOptions { ConnectionString = connectionString, SeedDemoData = false }
            }));

    protected override Task<IWebhookOutbox?> OpenAsync()
    {
        var repo = Track(NewRepository($"Data Source=wh_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"));
        return Task.FromResult<IWebhookOutbox?>(repo);
    }

    [Fact]
    public async Task EventSurvivesRepositoryRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wh_outbox_{Guid.NewGuid():N}.db");
        try
        {
            var cs = $"Data Source={path}";
            using (var first = NewRepository(cs))
            {
                (await ((IWebhookOutbox)first).EnqueueAsync(Msg(eventId: "persisted"))).ShouldBeTrue();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var second = NewRepository(cs);
            var claimed = await ((IWebhookOutbox)second).ClaimBatchAsync(5, Now, Lease, "w1");

            claimed.ShouldHaveSingleItem().EventId.ShouldBe("persisted");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void OutboxPartial_RunsEveryStatementInAWriteOrReadScope_NeverOnASharedConnection()
    {
        // WP-ARC-B-13: claim, complete, reschedule, dead-letter, defer, enqueue and purge are writes under the single write gate;
        // only list and count are reads. The partial never touches the gate or a connection directly.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var file = Path.Combine(dir!.FullName, "src", "Autheris.Infrastructure", "Persistence", "SqliteGovernanceRepository.WebhookOutbox.cs");
        var text = File.ReadAllText(file);

        foreach (var forbidden in new[] { "_connection", "_lock", "_writeGate", "Connections." })
        {
            text.ShouldNotContain(forbidden);
        }

        System.Text.RegularExpressions.Regex.Count(text, @"(?<!reader\.)\bWriteAsync\b").ShouldBe(5);
        System.Text.RegularExpressions.Regex.Count(text, @"(?<!reader\.)(?<!\.)\bReadAsync[<(]").ShouldBe(2);
    }
}
