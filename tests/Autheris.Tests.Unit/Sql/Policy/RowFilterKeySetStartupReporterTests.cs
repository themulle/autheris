namespace Autheris.Tests.Unit.Sql.Policy;

using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Issue #107 SEC-RF107-01 c, D-WPC-9 a: the opt-in is visible once at startup.</summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetStartupReporterTests
{
    private static async Task<List<(LogLevel Level, string Message)>> Run(params (string Name, bool Allowed)[] sources)
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, allowed) in sources) connections[name] = new DataSourceConnectionOptions { KeySetCacheAllowed = allowed };
        var options = Options.Create(new GatewayOptions { DataSources = new SqlDataSourceOptions { Connections = connections } });
        await new RowFilterKeySetStartupReporter(options, new ListLogger<RowFilterKeySetStartupReporter>(logs)).StartAsync(default);
        return logs;
    }

    [Fact]
    public async Task LogsAllowedSourcesOnce_SortedOrNone()
    {
        var logs = await Run(("zeta", true), ("alpha", true), ("beta", false));
        var none = await Run(("beta", false));

        logs.Count.ShouldBe(1);
        logs[0].Level.ShouldBe(LogLevel.Information);
        logs[0].Message.ShouldContain("alpha, zeta");
        logs[0].Message.ShouldNotContain("beta");
        none.Single().Message.ShouldContain("none");
    }
}
