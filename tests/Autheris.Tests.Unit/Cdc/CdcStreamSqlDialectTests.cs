using Autheris.Domain.Common;
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
}
