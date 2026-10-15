namespace Autheris.Tests.Unit.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class SqliteCloudEventSubscriptionStoreContractTests : CloudEventSubscriptionStoreContractTests
{
    private readonly string _connectionString = $"Data Source=ces_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private static SqliteGovernanceRepository NewRepository(string connectionString) =>
        GovernanceStoreFactory.CreateSqlite(
            Substitute.For<IEpochValidationService>(),
            Options.Create(new GatewayOptions
            {
                GovernanceDb = new GovernanceDbOptions { ConnectionString = connectionString, SeedDemoData = false }
            }));

    protected override Task<ICloudEventSubscriptionStore?> OpenAsync() =>
        Task.FromResult<ICloudEventSubscriptionStore?>(Track(NewRepository(_connectionString)));

    [Fact]
    public void SubscriptionsPartial_AccessesConnectionAndLockOnlyThroughOneHelper()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Autheris.Infrastructure", "Persistence", "SqliteGovernanceRepository.CloudEventSubscriptions.cs"));

        System.Text.RegularExpressions.Regex.Count(text, @"\b_connection\b").ShouldBe(1);
        System.Text.RegularExpressions.Regex.Count(text, @"\b_lock\b").ShouldBe(2);
        text.ShouldContain("WithSubscriptionsConnectionAsync");
    }

    [Fact]
    public async Task MalformedJsonRow_IsSkipped_OtherRowsStillLoad()
    {
        var repo = NewRepository(_connectionString);
        Track(repo);
        await ((ICloudEventSubscriptionStore)repo).RegisterSubscriptionAsync(Sub("good"), Quota);
        await using (var cmd = repo.Connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO CLOUDEVENT_SUBSCRIPTIONS (id, tenant_id, target_url, filter_table, filter_operations, is_enabled, created_ms) VALUES ('bad','tenant-a','https://e.test','*','{not json',1,1)";
            await cmd.ExecuteNonQueryAsync();
        }

        var list = await ((ICloudEventSubscriptionStore)repo).ListSubscriptionsAsync("tenant-a");

        list.Select(x => x.Id).ShouldBe(["good"]);
    }

    [Fact]
    public async Task OutOfBoundsRolesOrHeaders_AreRejected()
    {
        var store = (await OpenAsync())!;

        await Should.ThrowAsync<ArgumentException>(async () => await store.RegisterSubscriptionAsync(
            Sub("r") with { CreatedByRoles = Enumerable.Range(0, 300).Select(i => "r" + i).ToList() }, Quota));
        await Should.ThrowAsync<ArgumentException>(async () => await store.RegisterSubscriptionAsync(
            Sub("h") with { CustomHeaders = new Dictionary<string, string> { ["X"] = new string('v', 40000) } }, Quota));
    }
}
