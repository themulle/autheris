using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Unit.Persistence.DialectSql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Cdc;

public sealed class CdcStreamResetWatermarkSqlTests
{
    private static readonly CdcStreamId Id = CdcStreamId.New();

    [Fact]
    public async Task SqlServer_reset_resolves_the_watermark_key_before_it_updates_the_watermark()
    {
        using var conn = DialectKit.SqlServer.NewConnection();
        conn.ScalarResult = 1;
        await using var repo = new GovernanceRepository(new SqlServerGovernanceDialect(() => conn));

        await repo.ResetWatermarkAsync(Id, "{}");

        var sql = conn.Commands.Select(c => c.CommandText).Single(t => t.Contains("dbo.autheris_cdc_watermarks", StringComparison.Ordinal));
        sql.ShouldNotContain("JOIN", Case.Sensitive, "a join lets the plan lock the watermark and the stream row in either order");
        var read = sql.IndexOf("FROM dbo.autheris_cdc_streams", StringComparison.Ordinal);
        var update = sql.IndexOf("UPDATE dbo.autheris_cdc_watermarks", StringComparison.Ordinal);
        read.ShouldBeGreaterThan(-1);
        update.ShouldBeGreaterThan(read);
    }
}
