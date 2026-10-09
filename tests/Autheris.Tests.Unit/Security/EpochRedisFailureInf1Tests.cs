namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Cache;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>
/// INF-1: a Redis error on an established connection must not silently fall back to the node-local epoch. Otherwise a
/// revocation published by another node is ignored until the L1 TTL expires; the cached decision must be re-evaluated.
/// </summary>
public sealed class EpochRedisFailureInf1Tests
{
    private static readonly TableIdentifier Table = new("corp", "public", "orders");

    private static (EpochValidationService Service, IDatabase Db) Create()
    {
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return (new EpochValidationService(multiplexer: multiplexer), db);
    }

    [Fact]
    public async Task RedisReadFailure_OnConnectedMultiplexer_InvalidatesCachedEpoch()
    {
        var (service, db) = Create();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"7"));
        var epoch = await service.GetCurrentEpochAsync(Table);
        epoch.ShouldBe(7);

        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new TimeoutException("redis timeout"));

        (await service.IsEpochValidAsync(Table, epoch)).ShouldBeFalse();
    }

    [Fact]
    public async Task RedisRecovers_CachedEpochIsValidAgain()
    {
        var (service, db) = Create();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new InvalidOperationException("redis down"));
        (await service.IsEpochValidAsync(Table, 1)).ShouldBeFalse();

        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"3"));

        (await service.IsEpochValidAsync(Table, 3)).ShouldBeTrue();
    }

    [Fact]
    public async Task DegradedMode_TableWithSensitiveColumn_FailsClosed()
    {
        // SR15-41: In degraded mode (Redis disconnected), FailClosedOnSensitiveTables must fail closed
        // not only on IsHighlySensitive, but also when any column is marked IsSensitive.
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(false); // degraded

        var tableMetaRepo = Substitute.For<Autheris.Application.Interfaces.ITableMetadataRepository>();
        var metadata = new Autheris.Domain.Model.TableMetadata
        {
            Identifier = Table,
            Table = new Autheris.Domain.Model.Table
            {
                TableName = Table.TableName,
                SchemaName = Table.Schema,
                SourceName = Table.Domain,
                Sensitivity = "NORMAL", // IsHighlySensitive will be false
                RequiresFourEyes = false
            },
            Columns =
            [
                new Autheris.Domain.Model.TableColumn { ColumnName = "id", IsSensitive = false },
                new Autheris.Domain.Model.TableColumn { ColumnName = "salary", IsSensitive = true }
            ]
        };

        tableMetaRepo.GetTableMetadataAsync(Table, Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult<Autheris.Domain.Model.TableMetadata?>(metadata));

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(Autheris.Application.Interfaces.ITableMetadataRepository))
            .Returns(tableMetaRepo);

        var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
        {
            Caching = new Autheris.Domain.Options.CachingOptions
            {
                EpochValidation = new Autheris.Domain.Options.EpochValidationOptions
                {
                    FailClosedOnSensitiveTables = true
                }
            }
        });

        var service = new EpochValidationService(options: options, multiplexer: multiplexer, serviceProvider: sp);

        var isValid = await service.IsEpochValidAsync(Table, 1);
        isValid.ShouldBeFalse();
    }
}
