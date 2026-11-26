namespace Autheris.Tests.Unit.Sql.ResultCache;

using System.Collections.Generic;
using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public sealed class WebSqlResultCacheOptionsValidatorTests
{
    private static GatewayOptions Options(WebSqlOptions? webSql = null, params (string Key, DataSourceConnectionOptions Connection)[] connections)
    {
        var map = new Dictionary<string, DataSourceConnectionOptions>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (key, connection) in connections)
        {
            map[key] = connection;
        }

        return new GatewayOptions
        {
            WebSql = webSql ?? new WebSqlOptions(),
            DataSources = new SqlDataSourceOptions { Connections = map }
        };
    }

    private static ValidateResult Run(GatewayOptions options)
    {
        var result = new WebSqlResultCacheOptionsValidator().Validate(null, options);
        return new ValidateResult(result.Succeeded, result.Failures?.ToArray() ?? []);
    }

    private sealed record ValidateResult(bool Succeeded, string[] Failures);

    [Fact]
    public void Defaults_are_valid()
    {
        Run(Options()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Enabled_with_valid_values_passes()
    {
        var webSql = new WebSqlOptions { ResultCacheMaxMegabytes = 256, ResultCacheMaxEntryMegabytes = 16, ResultCacheMaxUserMegabytes = 64 };
        var connection = new DataSourceConnectionOptions { ResultCacheTtlSeconds = 300, ResultCacheTables = ["dbo.Orders"] };

        Run(Options(webSql, ("default", connection))).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public void Total_megabytes_out_of_range_fails(int total)
    {
        Run(Options(new WebSqlOptions { ResultCacheMaxMegabytes = total })).Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(17, 8)]
    [InlineData(8, 0)]
    [InlineData(8, 17)]
    public void Entry_and_user_caps_must_fit_the_total_when_on(int entry, int user)
    {
        var webSql = new WebSqlOptions { ResultCacheMaxMegabytes = 16, ResultCacheMaxEntryMegabytes = entry, ResultCacheMaxUserMegabytes = user };

        Run(Options(webSql)).Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(100_001, 10)]
    [InlineData(10, 0)]
    [InlineData(10, 10_001)]
    public void Row_and_entry_counts_out_of_range_fail(int rows, int entries)
    {
        var webSql = new WebSqlOptions { ResultCacheMaxRowsPerEntry = rows, ResultCacheMaxEntriesPerUser = entries };

        Run(Options(webSql)).Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Connection_ttl_out_of_range_fails(int ttl)
    {
        var result = Run(Options(null, ("default", new DataSourceConnectionOptions { ResultCacheTtlSeconds = ttl })));

        result.Succeeded.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("DataSources:Connections:default:ResultCacheTtlSeconds"));
    }

    [Theory]
    [InlineData(EnforcementMode.Delegated)]
    [InlineData(EnforcementMode.DelegatedDefenceInDepth)]
    public void Ttl_on_non_enforced_source_fails(EnforcementMode mode)
    {
        var connection = new DataSourceConnectionOptions { EnforcementMode = mode, ResultCacheTtlSeconds = 60 };

        Run(Options(null, ("src", connection))).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void Non_enforced_source_without_ttl_passes()
    {
        var connection = new DataSourceConnectionOptions { EnforcementMode = EnforcementMode.Delegated };

        Run(Options(null, ("src", connection))).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Ttl_on_databricks_obo_source_fails()
    {
        var connection = new DataSourceConnectionOptions
        {
            ResultCacheTtlSeconds = 60,
            Databricks = new DatabricksDataSourceOptions { Auth = new DatabricksAuthOptions { Mode = DatabricksAuthMode.OAuthObo } }
        };

        Run(Options(null, ("dbx", connection))).Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_table_entry_fails(string entry)
    {
        var connection = new DataSourceConnectionOptions { ResultCacheTables = ["dbo.Orders", entry] };

        Run(Options(null, ("default", connection))).Succeeded.ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Rejects_invalid_ttl_and_caps_even_when_globally_off_SECRC23()
    {
        var webSql = new WebSqlOptions { ResultCacheMaxMegabytes = 0, ResultCacheMaxRowsPerEntry = 0, ResultCacheMaxEntriesPerUser = 0 };
        var bad = new DataSourceConnectionOptions { ResultCacheTtlSeconds = 7200, ResultCacheTables = [" "] };
        var delegated = new DataSourceConnectionOptions { EnforcementMode = EnforcementMode.Delegated, ResultCacheTtlSeconds = 10 };

        var result = Run(Options(webSql, ("a", bad), ("b", delegated)));

        result.Succeeded.ShouldBeFalse();
        result.Failures.Length.ShouldBeGreaterThanOrEqualTo(5);
        Run(Options(new WebSqlOptions { ResultCacheMaxMegabytes = -1 })).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void Messages_name_option_paths_without_echoing_values()
    {
        var connection = new DataSourceConnectionOptions { ResultCacheTtlSeconds = 987_654, ResultCacheTables = ["  "] };

        var result = Run(Options(null, ("default", connection)));

        result.Failures.ShouldNotContain(f => f.Contains("987654") || f.Contains("987_654"));
    }
}
