using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

public sealed class CatalogSqlCoreGoldenTests
{
    public static TheoryData<string> Dialects() => DialectKit.Names();

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetActiveTableIdentifierByIdAsync_BindsTableId(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var tableId = Guid.NewGuid();

        _ = await GovernanceRepository.GetActiveTableIdentifierByIdAsync(conn, tableId, CancellationToken.None);

        conn.Commands.Count.ShouldBe(1);
        var cmd = conn.Commands[0];
        cmd.CommandText.ShouldBe(GovernanceRepository.ActiveTableByIdSql);
        cmd.Parameters.Select(p => p.Name).ShouldBe(["@id"]);
        cmd.Parameters[0].Value.ShouldBe(tableId.ToString());
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ReadTableEpochAsync_BindsTableId(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var tableId = Guid.NewGuid();

        _ = await GovernanceRepository.ReadTableEpochAsync(conn, tableId, CancellationToken.None);

        conn.Commands.Count.ShouldBe(1);
        var cmd = conn.Commands[0];
        cmd.CommandText.ShouldBe(GovernanceRepository.EpochByTableIdSql);
        cmd.Parameters.Select(p => p.Name).ShouldBe(["@tableId"]);
        cmd.Parameters[0].Value.ShouldBe(tableId.ToString());
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task UpdateRelationCrossDataSourceAsync_BindsParameters(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var relationId = Guid.NewGuid();

        _ = await GovernanceRepository.UpdateRelationCrossDataSourceAsync(conn, null!, relationId, true, CancellationToken.None);

        conn.Commands.Count.ShouldBe(1);
        var cmd = conn.Commands[0];
        cmd.CommandText.ShouldBe("UPDATE TABLE_RELATIONS SET allow_cross_data_source = @allowed WHERE id = @id");
        cmd.Parameters.Select(p => p.Name).ShouldBe(["@id", "@allowed"]);
        cmd.Parameters[0].Value.ShouldBe(relationId.ToString());
        cmd.Parameters[1].Value.ShouldBe(true);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task LoadColumnNamesAsync_BindsTableId(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var tableId = Guid.NewGuid().ToString();

        _ = await GovernanceRepository.LoadColumnNamesAsync(conn, null, tableId, CancellationToken.None);

        conn.Commands.Count.ShouldBe(1);
        var cmd = conn.Commands[0];
        cmd.CommandText.ShouldBe(GovernanceRepository.ActiveTableColumnNamesSql);
        cmd.Parameters.Select(p => p.Name).ShouldBe(["@t"]);
        cmd.Parameters[0].Value.ShouldBe(tableId);
    }

    [Fact]
    public void ParseColumnMetaJson_ParsesValidAndInvalidJson()
    {
        GovernanceRepository.ParseColumnMetaJson(null).Count.ShouldBe(0);
        GovernanceRepository.ParseColumnMetaJson("").Count.ShouldBe(0);
        GovernanceRepository.ParseColumnMetaJson("invalid-json").Count.ShouldBe(0);
        GovernanceRepository.ParseColumnMetaJson("[]").Count.ShouldBe(0);

        var parsed = GovernanceRepository.ParseColumnMetaJson("{\"key1\":\"val1\",\"key2\":42}");
        parsed.Count.ShouldBe(2);
        parsed["key1"].ShouldBe("val1");
        parsed["key2"].ShouldBe("42");
    }

    [Fact]
    public void HasMaskingRulesChanged_DetectsChangesAccurately()
    {
        var rule1 = new MaskingRule { Id = Guid.NewGuid(), RuleType = "Hash", PatternOrFormat = "sha256" };
        var rule2 = new MaskingRule { Id = Guid.NewGuid(), RuleType = "Hash", PatternOrFormat = "sha256" };
        var ruleDifferentType = new MaskingRule { Id = Guid.NewGuid(), RuleType = "Redact", PatternOrFormat = "sha256" };

        var dict1 = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["colA"] = rule1 };
        var dict2 = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["colA"] = rule2 };
        var dictDiff = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["colA"] = ruleDifferentType };

        GovernanceRepository.HasMaskingRulesChanged(dict1, dict1).ShouldBeFalse();
        GovernanceRepository.HasMaskingRulesChanged(dict1, dict2).ShouldBeFalse();
        GovernanceRepository.HasMaskingRulesChanged(dict1, dictDiff).ShouldBeTrue();
        GovernanceRepository.HasMaskingRulesChanged(dict1, null).ShouldBeTrue();
        GovernanceRepository.HasMaskingRulesChanged(dict1, new Dictionary<string, MaskingRule>()).ShouldBeTrue();
    }

    [Fact]
    public void ValidateVersionDeltas_ThrowsOnInvalidInputs()
    {
        Should.Throw<ArgumentException>(() => GovernanceRepository.ValidateVersionDeltas(0, 0));
        Should.Throw<ArgumentException>(() => GovernanceRepository.ValidateVersionDeltas(-1, 0));
        Should.Throw<ArgumentException>(() => GovernanceRepository.ValidateVersionDeltas(0, -1));

        Should.NotThrow(() => GovernanceRepository.ValidateVersionDeltas(1, 0));
        Should.NotThrow(() => GovernanceRepository.ValidateVersionDeltas(0, 1));
        Should.NotThrow(() => GovernanceRepository.ValidateVersionDeltas(1, 1));
    }
}
