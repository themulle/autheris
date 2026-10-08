namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class SqlMaskingDialectTests
{
    [Fact]
    public void SQL2_15_BuildMaskedColumnProjection_Uses_N_Prefix_For_SqlServer()
    {
        var meta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "customers"),
            Table = new Table { TableName = "customers", SchemaName = "dbo", SourceName = "sales", SourceType = "sqlserver" },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["secret"] = new MaskingRule { RuleType = "REDACT", Replacement = "Confidential-Geheim" }
            }
        };

        var proj = SqlDataSourceExecutor.BuildMaskedColumnProjection("secret", "nvarchar", DatabaseDialect.SqlServer, meta);
        proj.ShouldBe("N'Confidential-Geheim' AS [secret]");
    }

    [Fact]
    public void SQL2_15_BuildMaskedColumnProjection_Escapes_Backslashes_For_Databricks()
    {
        var meta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "customers"),
            Table = new Table { TableName = "customers", SchemaName = "dbo", SourceName = "sales", SourceType = "databricks" },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["secret"] = new MaskingRule { RuleType = "REDACT", Replacement = @"path\to\mask" }
            }
        };

        var proj = SqlDataSourceExecutor.BuildMaskedColumnProjection("secret", "string", DatabaseDialect.Databricks, meta);
        proj.ShouldBe(@"'path\\to\\mask' AS `secret`");
    }

    [Fact]
    public void SQL2_15_BuildMaskedColumnProjection_Rejects_Null_Byte()
    {
        var meta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "customers"),
            Table = new Table { TableName = "customers", SchemaName = "dbo", SourceName = "sales", SourceType = "postgresql" },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["secret"] = new MaskingRule { RuleType = "REDACT", Replacement = "test\0injection" }
            }
        };

        Should.Throw<InvalidOperationException>(() =>
            SqlDataSourceExecutor.BuildMaskedColumnProjection("secret", "varchar", DatabaseDialect.PostgreSql, meta));
    }
}
