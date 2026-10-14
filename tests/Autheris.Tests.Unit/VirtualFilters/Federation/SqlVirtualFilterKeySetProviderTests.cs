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
using static Autheris.Tests.Unit.VirtualFilters.Federation.KeySourceSqliteFixture;

/// <summary>
/// F-GOV-14 WI-5 / WI-19 (M4, C-3, C-4; SEC-4, SEC-5, SEC-6, SEC-10, SEC-11): authorized keys come from the complete
/// structured definition executed in the SQL key source, bounded, tenant isolated, cached in a dedicated size-limited
/// cache with single-flight loading. Every failure denies with a stable code and without database messages.
/// </summary>
public sealed class SqlVirtualFilterKeySetProviderTests : IDisposable
{
    private static readonly IReadOnlyList<VirtualFilterKeyType> IntegerKey = [VirtualFilterKeyType.IntegerNumber];

    private readonly KeySourceSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    private static long[] Values(VirtualFilterKeySet set) => set.Keys.Select(k => (long)k[0]).OrderBy(v => v).ToArray();

    [Fact]
    public async Task Get_DavidCaseJoinAndIsNull_ReturnsExactKeys()
    {
        var set = await _db.Provider().GetAsync(David(), IntegerKey, TenantA);

        Values(set).ShouldBe([3L, 4L, 5L]);
        set.CacheHit.ShouldBeFalse();
    }

    [Fact]
    public async Task Get_KeySetParity_WithSqlChannelPredicate()
    {
        // ST-14: same keys as the SQL channel's predicate (join + IsNull + NotEq + duplicates + NULL keys).
        var filter = Parity();
        var binding = new FilterBinding { FilterName = "parity" };
        var air1 = Tables.Single(t => t.Identifier.TableName == "air1");
        var predicate = new StructuredFilterSqlBuilder().Build(filter, binding, air1, Autheris.Domain.Common.DatabaseDialect.Sqlite);
        var sqlChannel = _db.Query($"SELECT DISTINCT \"client_id\" FROM \"air1\" AS \"autheris_target\" WHERE {predicate}")
            .Select(v => Convert.ToInt64(v)).OrderBy(v => v).ToArray();

        var set = await _db.Provider().GetAsync(filter, IntegerKey, TenantA);

        var authorizedAir1Keys = _db.Query("SELECT DISTINCT client_id FROM air1 WHERE client_id IS NOT NULL")
            .Select(v => Convert.ToInt64(v)).Where(v => Values(set).Contains(v)).OrderBy(v => v).ToArray();
        sqlChannel.ShouldBe([3L, 7L]);
        authorizedAir1Keys.ShouldBe(sqlChannel);
        Values(set).ShouldBe([3L, 7L]);          // revoked (5), inactive (4), delivered (1), unjoined (8), NULL excluded
        set.DroppedKeyCount.ShouldBe(1);        // 'x' is not an integer: dropped and counted, never authorizes
    }

    [Fact]
    public async Task Get_NotEqCondition_Excludes()
    {
        var filter = Filter("not_inactive", "auth", "assignment", "a", "client_id",
            new FilterCondition("a.status", FilterConditionOperator.NotEq, "INACTIVE"));

        var set = await _db.Provider().GetAsync(filter, [VirtualFilterKeyType.Text], TenantA);

        set.Keys.Select(k => (string)k[0]).ShouldNotContain("4");
        set.Keys.Select(k => (string)k[0]).ShouldContain("3");
    }

    [Fact]
    public async Task Get_NullKeysExcluded_DuplicatesDeduplicated()
    {
        var filter = Filter("all_assignments", "auth", "assignment", "a", "client_id");

        var set = await _db.Provider().GetAsync(filter, [VirtualFilterKeyType.Text], TenantA);

        set.Keys.Select(k => (string)k[0]).OrderBy(v => v, StringComparer.Ordinal).ShouldBe(["1", "3", "4", "5", "7", "8", "x"]);
        set.DroppedKeyCount.ShouldBe(0);
    }

    [Fact]
    public async Task Get_CompositeKey()
    {
        var filter = Filter("composite", "auth", "assignment", "a", "client_id",
            new FilterCondition("a.status", FilterConditionOperator.Eq, "ACTIVE")) with
        { KeyColumns = ["a.client_id", "a.crane_serial_number"] };

        var set = await _db.Provider().GetAsync(filter, [VirtualFilterKeyType.Text, VirtualFilterKeyType.Text], TenantA);

        set.Keys.Select(k => $"{k[0]}/{k[1]}").OrderBy(v => v, StringComparer.Ordinal)
            .ShouldBe(["1/C1", "3/C3", "5/C5", "7/C3", "8/NO-CRANE", "x/C3"]);
    }

    [Fact]
    public async Task Get_TenantColumn_ReturnsOnlyTheCallersKeys()
    {
        // ST-15: rows of tenant B never authorize keys for tenant A.
        var filter = Filter("granted", "auth", "grant_t", "g", "client_id");
        var withTenantColumn = Tables.Single(t => t.Identifier.TableName == "grant_t");
        withTenantColumn.TenantColumnName.ShouldBe("tenant_id");

        var forA = await _db.Provider().GetAsync(filter, IntegerKey, TenantA);
        var forB = await _db.Provider().GetAsync(filter with { TenantId = TenantB }, IntegerKey, TenantB);

        Values(forA).ShouldBe([1L, 2L, 3L]);
        Values(forB).ShouldBe([3L]);
    }

    [Fact]
    public async Task Get_RequireTenantColumn_TableWithoutTenantColumn_ThrowsKeySourceUnsupported()
    {
        // ST-16
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            _db.Provider(_db.Options(requireTenantColumn: true)).GetAsync(David(), IntegerKey, TenantA).AsTask());

        ex.ErrorCode.ShouldBe("VF_KEY_SOURCE_UNSUPPORTED");
    }

    [Fact]
    public async Task Get_RequireTenantColumn_ExemptTables_Succeeds()
    {
        var set = await _db.Provider(_db.Options(requireTenantColumn: true, exempt: ["conf.client", "crane"])).GetAsync(David(), IntegerKey, TenantA);

        Values(set).ShouldBe([3L, 4L, 5L]);
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
        var factory = new CountingConnectionFactory(new SqlConnectionFactory());
        var provider = _db.Provider(factory: factory);
        await provider.GetAsync(David(), IntegerKey, TenantA);
        _db.Execute("DELETE FROM client");

        var second = await provider.GetAsync(David(), IntegerKey, TenantA);

        Values(second).ShouldBe([3L, 4L, 5L]);
        second.CacheHit.ShouldBeTrue();
        factory.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Get_DefinitionChanged_CacheMiss()
    {
        var factory = new CountingConnectionFactory(new SqlConnectionFactory());
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
        var factory = new CountingConnectionFactory(new SqlConnectionFactory()) { Delay = TimeSpan.FromMilliseconds(150) };
        var provider = _db.Provider(factory: factory);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => provider.GetAsync(David(), IntegerKey, TenantA).AsTask()));

        factory.Calls.ShouldBe(1);
        results.ShouldAllBe(r => r.Count == 3);

        var failing = new CountingConnectionFactory(new SqlConnectionFactory()) { FailNext = 1 };
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
