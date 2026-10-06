using System.Collections.Generic;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>
/// Client parameters (@name) are parsed as placeholder identifiers and restored to bound parameters after the rewrite.
/// </summary>
public sealed class WebSqlClientParameterTests
{
    private static readonly Dictionary<string, object?> CraneType = new() { ["crane_type"] = "Mobilkran", ["@crane_type"] = "Mobilkran" };

    [Fact]
    public void Normalize_ReplacesSuppliedParameter_AndRestoreTurnsItBack()
    {
        var sql = GovernedSqlExecutionService.NormalizeClientParameters(
            "SELECT a FROM t WHERE crane_type = @crane_type", CraneType, out var names);

        sql.ShouldBe("SELECT a FROM t WHERE crane_type = __param_crane_type");
        GovernedSqlExecutionService.RestoreClientParameters("SELECT a FROM t WHERE crane_type = \"__param_crane_type\" LIMIT 10", names)
            .ShouldBe("SELECT a FROM t WHERE crane_type = @crane_type LIMIT 10");
    }

    [Fact]
    public void Normalize_LeavesStringLiteralsAndQuotedIdentifiersUntouched()
    {
        var sql = GovernedSqlExecutionService.NormalizeClientParameters(
            "SELECT 'it''s @crane_type', \"@crane_type\" FROM t WHERE x = @crane_type", CraneType, out _);

        sql.ShouldBe("SELECT 'it''s @crane_type', \"@crane_type\" FROM t WHERE x = __param_crane_type");
    }

    [Fact]
    public void Normalize_IgnoresUnsuppliedNamesAndDoubleAt()
    {
        var sql = GovernedSqlExecutionService.NormalizeClientParameters(
            "SELECT @@version, @other FROM t WHERE x = @crane_type", CraneType, out var names);

        sql.ShouldBe("SELECT @@version, @other FROM t WHERE x = __param_crane_type");
        names.ShouldBe(new[] { "crane_type" });
    }

    [Fact]
    public void Normalize_RejectsReservedPlaceholderPrefixInClientSql()
    {
        Should.Throw<WebSqlPolicyException>(() => GovernedSqlExecutionService.NormalizeClientParameters(
            "SELECT __param_crane_type FROM t WHERE x = @crane_type", CraneType, out _));
    }

    [Fact]
    public void Normalize_WithoutParameters_ReturnsSqlUnchanged()
    {
        GovernedSqlExecutionService.NormalizeClientParameters("SELECT @x FROM t", null, out var names).ShouldBe("SELECT @x FROM t");
        names.ShouldBeEmpty();
    }
}
