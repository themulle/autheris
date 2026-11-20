using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Unit.Persistence.DialectSql;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Cdc;

public sealed class CdcStreamSqlDialectTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task Cdc_sql_for_unknown_dialect_throws()
    {
        var dialect = Substitute.For<IGovernanceSqlDialect>();
        dialect.CreateConnection().Returns(_ => DialectKit.PostgreSql.NewConnection());
        var repo = new GovernanceRepository(dialect);

        await Should.ThrowAsync<NotSupportedException>(
            () => repo.EnsureStreamAsync("src", new TableIdentifier("SALES", "DBO", "Orders")));
    }

    [Fact]
    public async Task Resolve_IsCachedPerDialect()
    {
        var id = CdcStreamId.New();

        static async Task<(string EventsSql, string BoundsSql)> ReadCommandsAsync(CdcStreamId streamId, bool sqlServer)
        {
            using var conn = (sqlServer ? DialectKit.SqlServer : DialectKit.PostgreSql).NewConnection();
            conn.ScalarResult = 1;
            IGovernanceSqlDialect dialect = sqlServer
                ? new SqlServerGovernanceDialect(() => conn)
                : new PostgreSqlGovernanceDialect(() => conn);
            await using var repo = new GovernanceRepository(dialect);
            await repo.ReadAsync(streamId, 0, 10);
            var eventsSql = conn.Commands.Single(c => c.CommandText.Contains("autheris_cdc_stream_events", StringComparison.Ordinal)).CommandText;
            var boundsSql = conn.Commands.Single(c => c.CommandText.Contains("autheris_cdc_streams", StringComparison.Ordinal)).CommandText;
            return (eventsSql, boundsSql);
        }

        var (pgEvents1, pgBounds1) = await ReadCommandsAsync(id, sqlServer: false);
        var (pgEvents2, pgBounds2) = await ReadCommandsAsync(id, sqlServer: false);
        var (msEvents1, msBounds1) = await ReadCommandsAsync(id, sqlServer: true);
        var (msEvents2, msBounds2) = await ReadCommandsAsync(id, sqlServer: true);

        Assert.Same(pgEvents1, pgEvents2);
        Assert.Same(pgBounds1, pgBounds2);
        Assert.Same(msEvents1, msEvents2);
        Assert.Same(msBounds1, msBounds2);
        Assert.NotSame(pgEvents1, msEvents1);
        Assert.NotSame(pgBounds1, msBounds1);
    }
}
