namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.KeySourceStubFixture;

/// <summary>
/// F-GOV-14 WI-5 / WI-19 (M4, C-3, C-4; SEC-4, SEC-5, SEC-6, SEC-10, SEC-11): unit contract of structured key-set
/// validation, guardrails, limits, bounded dedicated cache with single-flight loading, and fail-closed error handling.
/// </summary>
public sealed class SqlVirtualFilterKeySetProviderTests
{
    private static readonly IReadOnlyList<VirtualFilterKeyType> IntegerKey = [VirtualFilterKeyType.IntegerNumber];

    private readonly KeySourceStubFixture _db = new();

    private static long[] Values(VirtualFilterKeySet set) => set.Keys.Select(k => (long)k[0]).OrderBy(v => v).ToArray();

    [Fact]
    public async Task Get_RequireTenantColumn_TableWithoutTenantColumn_ThrowsKeySourceUnsupported()
    {
        // ST-16
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            _db.Provider(_db.Options(requireTenantColumn: true)).GetAsync(David(), IntegerKey, TenantA).AsTask());

        ex.ErrorCode.ShouldBe("VF_KEY_SOURCE_UNSUPPORTED");
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public async Task Get_MaxAuthorizedKeys_OverflowDeniesExactLimitSucceeds(int maxKeys, bool overflows)
    {
        // ST-17: 5 distinct keys; never truncated, no off-by-one.
        var filter = Filter("many_keys", "auth", "many", "m", "k");
        var provider = _db.Provider(_db.Options(maxKeys: maxKeys));

        if (overflows)
        {
            (await Should.ThrowAsync<GatewaySecurityException>(() => provider.GetAsync(filter, IntegerKey, TenantA).AsTask()))
                .ErrorCode.ShouldBe("VF_KEY_SET_TOO_LARGE");
        }
        else
        {
            Values(await provider.GetAsync(filter, IntegerKey, TenantA)).ShouldBe([1L, 2L, 3L, 4L, 5L]);
        }
    }

    [Fact]
    public async Task Get_SqlDefinition_ThrowsKeySourceUnsupported()
    {
        var sqlFilter = new VirtualFilter { TenantId = TenantA, Name = "vf_sql", Source = Domain, Sql = "from client c where c.client_id = target.client_id", SqlTargetColumns = ["client_id"] };

        (await Should.ThrowAsync<GatewaySecurityException>(() => _db.Provider().GetAsync(sqlFilter, IntegerKey, TenantA).AsTask()))
            .ErrorCode.ShouldBe("VF_KEY_SOURCE_UNSUPPORTED");
    }

    [Fact]
    public async Task Get_ValidityWindow_ThrowsKeySourceUnsupported()
    {
        var windowed = Filter("windowed", "auth", "assignment", "a", "client_id") with { ValidToColumn = "a.revoked_at" };

        (await Should.ThrowAsync<GatewaySecurityException>(() => _db.Provider().GetAsync(windowed, IntegerKey, TenantA).AsTask()))
            .ErrorCode.ShouldBe("VF_KEY_SOURCE_UNSUPPORTED");
    }

    [Fact]
    public async Task Get_FromOrJoinTableNotSqlOrNotInCatalog_ThrowsKeySourceUnsupported()
    {
        var httpClient = Tables[0] with { Table = new Table { SourceName = Domain, SchemaName = "conf", TableName = "client", DataSourceType = DataSourceType.HttpDeclarative } };
        _db.Catalog.GetTableMetadataAsync(httpClient.Identifier, Arg.Any<CancellationToken>()).Returns(httpClient);
        var unknownJoin = Parity() with
        {
            Structured = Parity().Structured! with { Joins = [new FilterJoin(new TableIdentifier(Domain, "md", "unknown"), "crane", "crane.serial_number", "a.crane_serial_number")] }
        };

        (await Should.ThrowAsync<GatewaySecurityException>(() => _db.Provider().GetAsync(David(), IntegerKey, TenantA).AsTask()))
            .ErrorCode.ShouldBe("VF_KEY_SOURCE_UNSUPPORTED");
        (await Should.ThrowAsync<GatewaySecurityException>(() => _db.Provider().GetAsync(unknownJoin, IntegerKey, TenantA).AsTask()))
            .ErrorCode.ShouldBe("VF_KEY_SOURCE_UNSUPPORTED");
    }

    [Fact]
    public async Task Get_FilterOfAnotherTenant_ThrowsKeySourceUnsupported()
    {
        (await Should.ThrowAsync<GatewaySecurityException>(() => _db.Provider().GetAsync(David(), IntegerKey, TenantB).AsTask()))
            .ErrorCode.ShouldBe("VF_KEY_SOURCE_UNSUPPORTED");
    }

    [Fact]
    public async Task Get_KeyTypeCountDiffers_ThrowsKeyTypeMismatch()
    {
        (await Should.ThrowAsync<GatewaySecurityException>(() =>
                _db.Provider().GetAsync(David(), [VirtualFilterKeyType.IntegerNumber, VirtualFilterKeyType.Text], TenantA).AsTask()))
            .ErrorCode.ShouldBe("VF_KEY_TYPE_MISMATCH");
    }

    [Fact]
    public async Task Get_SourceUnreadable_ThrowsSourceUnavailable_WithoutInnerMessage()
    {
        // ST-18: non-existent directory, read-only mode.
        var options = _db.Options(connectionString: $"Data Source=/nonexistent_{Guid.NewGuid():N}/keys.db;Mode=ReadOnly");

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => _db.Provider(options).GetAsync(David(), IntegerKey, TenantA).AsTask());

        ex.ErrorCode.ShouldBe("VIRTUAL_FILTER_SOURCE_UNAVAILABLE");
        ex.InnerException.ShouldNotBeNull();
        ex.Message.ShouldNotContain(ex.InnerException.Message);
    }

    [Fact]
    public async Task Get_SecondCall_CacheHit_NoQuery()
    {
        var factory = new CountingConnectionFactory(_db.CreateFactory());
        var provider = _db.Provider(factory: factory);
        await provider.GetAsync(David(), IntegerKey, TenantA);
        _db.Cleared = true;

        var second = await provider.GetAsync(David(), IntegerKey, TenantA);

        Values(second).ShouldBe([3L, 4L, 5L]);
        second.CacheHit.ShouldBeTrue();
        factory.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Get_DefinitionChanged_CacheMiss()
    {
        var factory = new CountingConnectionFactory(_db.CreateFactory());
        var provider = _db.Provider(factory: factory);
        var david = David();
        await provider.GetAsync(david, IntegerKey, TenantA);

        var changed = david with { Structured = david.Structured! with { Where = [new FilterCondition("crane.is_delivered", FilterConditionOperator.IsNotNull)] } };
        var set = await provider.GetAsync(changed, IntegerKey, TenantA);

        Values(set).ShouldBe([1L, 2L]);
        set.CacheHit.ShouldBeFalse();
        factory.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Get_ConcurrentColdCalls_LoadOnce_FailureNotCached()
    {
        // ST-20
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new CountingConnectionFactory(_db.CreateFactory()) { Entered = entered, Gate = gate };
        var provider = _db.Provider(factory: factory);

        var tasks = Enumerable.Range(0, 20).Select(_ => provider.GetAsync(David(), IntegerKey, TenantA).AsTask()).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        factory.Calls.ShouldBe(1);
        results.ShouldAllBe(r => r.Count == 3);

        var failing = new CountingConnectionFactory(_db.CreateFactory()) { FailNext = 1 };
        var second = _db.Provider(factory: failing);
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => second.GetAsync(David(), IntegerKey, TenantA).AsTask());
        ex.ErrorCode.ShouldBe("VIRTUAL_FILTER_SOURCE_UNAVAILABLE");
        ex.Message.ShouldNotContain("secret-value-42");
        Values(await second.GetAsync(David(), IntegerKey, TenantA)).ShouldBe([3L, 4L, 5L]);
        failing.Calls.ShouldBe(2);
    }

    [Fact]
    public void Provider_ExposesNoMutator()
    {
        // ST-21: no public method other than the interface members (no SetInMemoryTuples-like override).
        var allowed = typeof(IVirtualFilterKeySetProvider).GetMethods().Select(m => m.Name).ToHashSet();
        var declared = typeof(SqlVirtualFilterKeySetProvider)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name);

        declared.ShouldAllBe(name => allowed.Contains(name));
        typeof(SqlVirtualFilterKeySetProvider).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Options_TtlOutOfRange_Rejected(int ttl)
    {
        var options = _db.Options();
        var bad = new GatewayOptions
        {
            DataSources = options.DataSources,
            VirtualFilters = new VirtualFilterOptions { Federation = new VirtualFilterFederationOptions { KeyCacheTtlSeconds = ttl } }
        };

        Should.Throw<ArgumentOutOfRangeException>(() => bad.VirtualFilters.Federation.Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => _db.Provider(bad));
    }

    [Fact]
    public void Options_Defaults()
    {
        var defaults = new VirtualFilterFederationOptions();

        defaults.KeyCacheTtlSeconds.ShouldBe(30);
        defaults.MaxAuthorizedKeys.ShouldBe(50_000);
        defaults.KeyQueryTimeoutSeconds.ShouldBe(10);
        defaults.KeyCacheSizeLimitBytes.ShouldBe(64L * 1024 * 1024);
        Should.NotThrow(defaults.Validate);
    }
}
