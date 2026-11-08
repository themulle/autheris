namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Xunit;
using Shouldly;
using Npgsql;
using System.Linq;
using System.Collections.Generic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>WP-ARC-A-03: PostgreSQL contract tests of the CloudEvent subscription store. Need Docker; without it they return early.</summary>
public sealed class PostgreSqlCloudEventSubscriptionStoreContractTests : CloudEventSubscriptionStoreContractTests
{
    private PostgreSqlTestDatabase? _db;

    public override async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
        }
        catch (Exception ex)
        {
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
        }
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    protected override Task<ICloudEventSubscriptionStore?> OpenAsync()
    {
        if (_db is not { IsAvailable: true })
        {
            return Task.FromResult<ICloudEventSubscriptionStore?>(null);
        }

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = _db.ConnectionString,
                SeedDemoData = false
            }
        });
        var repo = Track(GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env));
        return Task.FromResult<ICloudEventSubscriptionStore?>(repo);
    }

    [Fact]
    public async Task MalformedJsonRow_IsSkipped_OtherRowsStillLoad()
    {
        var store = await OpenAsync();
        if (store is null)
        {
            return;
        }

        await store.RegisterSubscriptionAsync(Sub("good"), Quota);
        await using (var conn = new NpgsqlConnection(_db!.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO CLOUDEVENT_SUBSCRIPTIONS (id, tenant_id, target_url, filter_table, filter_operations, is_enabled, created_ms) VALUES ('bad','tenant-a','https://e.test','*','{not json',TRUE,1)";
            await cmd.ExecuteNonQueryAsync();
        }

        var list = await store.ListSubscriptionsAsync("tenant-a");

        list.Select(x => x.Id).ShouldBe(["good"]);
    }

    [Fact]
    public async Task OutOfBoundsRolesOrHeaders_AreRejected()
    {
        var store = await OpenAsync();
        if (store is null)
        {
            return;
        }

        await Should.ThrowAsync<ArgumentException>(async () => await store.RegisterSubscriptionAsync(
            Sub("r") with { CreatedByRoles = Enumerable.Range(0, 300).Select(i => "r" + i).ToList() }, Quota));
        await Should.ThrowAsync<ArgumentException>(async () => await store.RegisterSubscriptionAsync(
            Sub("h") with { CustomHeaders = new Dictionary<string, string> { ["X"] = new string('v', 40000) } }, Quota));
    }
}
