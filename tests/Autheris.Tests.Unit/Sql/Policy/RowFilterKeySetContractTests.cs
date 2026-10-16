using Autheris.Domain.Common;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>Issue #107 contract-level checks (defaults, serialization, confidentiality of key-holding types).</summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetContractTests
{
    [Fact]
    public void Options_DefaultsMatchThePlan_AndStrategyIsExists()
    {
        var o = new GatewayOptions().RowFilters.KeySetCache;

        o.DefaultStrategy.ShouldBe(RowFilterEvaluationStrategy.Exists);
        o.DefaultDuration.ShouldBe(TimeSpan.FromMinutes(5));
        o.MaxDuration.ShouldBe(TimeSpan.FromHours(1));
        o.MaxKeys.ShouldBe(2000);
        o.SizeLimitBytes.ShouldBe(64L * 1024 * 1024);
        o.KeyQueryTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        o.BindParameterReserve.ShouldBe(256);
        o.MaxConcurrentKeyQueries.ShouldBe(4);
        o.MaxKeys.ShouldBeLessThanOrEqualTo(RowFilterKeySetCacheOptions.MaxKeysCeiling);
        o.MaxDuration.ShouldBeLessThanOrEqualTo(RowFilterKeySetCacheOptions.DurationCeiling);
    }

    [Fact]
    public void KeySetCacheAllowed_DefaultsToFalse_SEC_RF107_01()
        => new DataSourceConnectionOptions().KeySetCacheAllowed.ShouldBeFalse();

    [Fact]
    public void ConsentRowFilterRecipe_RoundTripsKeySetSettings()
    {
        var filter = new ConsentRowFilter
        {
            KeySetStrategy = RowFilterEvaluationStrategy.KeySetCache,
            KeySetDurationSeconds = 90,
            KeySetMaxKeys = 500
        };
        var recipe = new ConsentRowFilterRecipe([[filter]], []);

        ConsentRowFilterRecipe.TryParse(recipe.ToJson(), out var parsed).ShouldBeTrue();
        var back = parsed!.Allow[0][0];
        back.KeySetStrategy.ShouldBe(RowFilterEvaluationStrategy.KeySetCache);
        back.KeySetDurationSeconds.ShouldBe(90);
        back.KeySetMaxKeys.ShouldBe(500);
    }

    [Fact]
    public void Inherit_HasNoValues()
        => RowFilterEvaluationSettings.Inherit.ShouldBe(new RowFilterEvaluationSettings(null, null, null));

    [Fact]
    public async Task EpochService_DefaultAuthoritativeRead_IsNull_SEC_RF107_05()
    {
        IEpochValidationService svc = new DefaultEpochService();
        (await svc.TryGetAuthoritativeEpochsAsync([], CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public void KeyHoldingTypes_ToStringPrintsCountsOnly_SEC_RF107_09()
    {
        var entry = new RowFilterKeySetEntry(["secret-key-4711"], false, DateTimeOffset.UnixEpoch, "t", "ds");
        var repl = new KeySetReplacement("fk", [new PolicyValue("secret-key-4711", TrinoSqlEngine.Ast.Emit.SqlParameterType.String)], 1, 0);

        entry.ToString().ShouldNotContain("secret-key-4711");
        repl.ToString().ShouldNotContain("secret-key-4711");
    }

    private sealed class DefaultEpochService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }
}
