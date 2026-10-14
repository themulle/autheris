namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>F-GOV-14 WI-9: the federation fields of a binding survive the governance store, legacy JSON still loads.</summary>
public sealed class SqliteVirtualFilterBindingFederationTests : IDisposable
{
    private readonly string _connectionString = $"Data Source=vfb_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteGovernanceRepository _repository;

    public SqliteVirtualFilterBindingFederationTests()
    {
        _repository = GovernanceStoreFactory.CreateSqlite(
            Substitute.For<IEpochValidationService>(),
            Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = _connectionString } }));
    }

    public void Dispose() => _repository.Dispose();

    private IVirtualFilterRepository Repository => _repository;

    [Theory]
    [InlineData(null)]
    [InlineData(PushdownParameterFormat.PostBatch)]
    [InlineData(PushdownParameterFormat.CommaSeparated)]
    public async Task Binding_FederationFields_RoundTrip(PushdownParameterFormat? format)
    {
        var profile = VirtualFilterModelTests.DavidProfile() with
        {
            Bindings =
            [
                new FilterBinding
                {
                    FilterName = "nicht_ausgelieferte_krane",
                    Strategy = VirtualFilterExecutionStrategy.PushdownOnly,
                    PushdownFormat = format,
                    MaxPushdownKeys = 250
                }
            ]
        };

        await Repository.ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [profile] });
        var loaded = (await Repository.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem().Bindings.ShouldHaveSingleItem();

        loaded.Strategy.ShouldBe(VirtualFilterExecutionStrategy.PushdownOnly);
        loaded.PushdownFormat.ShouldBe(format);
        loaded.MaxPushdownKeys.ShouldBe(250);
    }

    [Fact]
    public async Task LegacyBindingJsonWithCompositeKeys_StillLoads()
    {
        await Repository.ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [VirtualFilterModelTests.DavidProfile()] });
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE VIRTUAL_FILTER_ACCESS_PROFILES SET definition_json = replace(definition_json, '\"MaxPushdownKeys\"', '\"CompositeKeys\":[\"a\",\"b\"],\"MaxPushdownKeys\"')";
            (await update.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        var loaded = (await Repository.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem();

        loaded.Bindings.ShouldNotBeEmpty();
    }
}
