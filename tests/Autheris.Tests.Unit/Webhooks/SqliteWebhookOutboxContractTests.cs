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
    public void OutboxPartial_AccessesConnectionAndLockOnlyThroughOneHelper()
    {
        // B-13 converts SQLite to pooled connections; the outbox partial must only need one helper swapped.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var file = Path.Combine(dir!.FullName, "src", "Autheris.Infrastructure", "Persistence", "SqliteGovernanceRepository.WebhookOutbox.cs");
        var text = File.ReadAllText(file);

        System.Text.RegularExpressions.Regex.Count(text, @"\b_connection\b").ShouldBe(1);
        System.Text.RegularExpressions.Regex.Count(text, @"\b_lock\b").ShouldBe(2); // WaitAsync + Release
        text.ShouldContain("WithOutboxConnectionAsync");
    }
}
