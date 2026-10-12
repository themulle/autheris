namespace Autheris.Tests.Unit.Sql;

using System;
using System.Threading.Tasks;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Pipeline;
using Autheris.Application.Sql.Services;
using Autheris.Application.Sql.Synthetic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class SqlRewritePipelineTests
{
    [Fact]
    public void Pipeline_StageOrder_IsFixed()
    {
        var options = Options.Create(new GatewayOptions());
        var rewriter = new GovernedSqlRewriter(options);

        var stages = rewriter.StageOrder;

        stages.Count.ShouldBe(5);
        stages[0].ShouldBe(SqlRewriteStageOrder.AnalyseAndStatementPolicy);
        stages[1].ShouldBe(SqlRewriteStageOrder.IdentityResolution);
        stages[2].ShouldBe(SqlRewriteStageOrder.GovernanceResolution);
        stages[3].ShouldBe(SqlRewriteStageOrder.RewriteOptions);
        stages[4].ShouldBe(SqlRewriteStageOrder.SecureRewrite);

        var service = new GovernedSqlExecutionService(options);
        service.StageOrder.ShouldBe(stages);
    }

    [Fact]
    public async Task SyntheticDataTableReader_ReadsRewrittenSqlSuccessfully()
    {
        const string sql = "SELECT a, b FROM t WHERE tenant_id = 42";
        using var reader = new SyntheticDataTableReader(sql);

        reader.FieldCount.ShouldBe(3);
        reader.GetName(0).ShouldBe("status");
        reader.GetName(1).ShouldBe("query");
        reader.GetName(2).ShouldBe("governed");

        var canRead = await reader.ReadAsync();
        canRead.ShouldBeTrue();

        reader.GetString(0).ShouldBe("ok");
        reader.GetString(1).ShouldBe(sql);
        reader.GetBoolean(2).ShouldBeTrue();

        var readAgain = await reader.ReadAsync();
        readAgain.ShouldBeFalse();
    }

    [Fact]
    public void SqlDataMaskingProvider_GeneratesTypeSafeDefaults()
    {
        var options = Options.Create(new GatewayOptions());
        var meta = new TableMetadata
        {
            Table = new Table
            {
                TableName = "employees",
                SchemaName = "dbo",
                SourceName = "Lakehouse",
                SourceType = "SqlServer"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "salary", DataType = "decimal(18,2)" },
                new TableColumn { ColumnName = "name", DataType = "varchar(100)" }
            ]
        };

        SqlDataMaskingProvider.BuildDefaultTypeSafeMask(meta, "id").ShouldBe("NULL");
        SqlDataMaskingProvider.BuildDefaultTypeSafeMask(meta, "salary").ShouldBe("NULL");
        SqlDataMaskingProvider.BuildDefaultTypeSafeMask(meta, "name").ShouldBe("'***'");
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "[email]", "NVARCHAR(MAX)")]
    [InlineData(DatabaseDialect.PostgreSql, "\"email\"", "TEXT")]
    [InlineData(DatabaseDialect.Sqlite, "\"email\"", "TEXT")]
    public void SqlDataMaskingProvider_GeneratesEmailMasksPerDialect(DatabaseDialect dialect, string expectedColumn, string expectedCast)
    {
        var expr = SqlDataMaskingProvider.BuildEmailMaskExpression("email", dialect);
        expr.ShouldContain(expectedColumn);
        expr.ShouldContain(expectedCast);
    }
}
