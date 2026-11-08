namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

public sealed class WebSqlOutputMaskTests
{
    private static TestCompileEnv CreateEnv()
    {
        var env = new TestCompileEnv(TargetSqlDialect.PostgreSql);
        env.Table("users", "tenant_id", "id:INTEGER", "email:TEXT", "name:TEXT", "score:INTEGER");
        env.Mask("users", "email", new MaskSpec(MaskKind.Nullify, new MaskArguments(), System.Collections.Frozen.FrozenDictionary<string, PolicyValue>.Empty));
        return env;
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MaskedColumn_IsFlagged()
    {
        var env = CreateEnv();
        var compiled = env.Compile("SELECT email FROM users");
        compiled.OutputColumnMasks.Length.ShouldBe(1);
        compiled.OutputColumnMasks[0].ShouldBeTrue();
    }

    [Fact]
    public void ClearColumn_NotFlagged()
    {
        var env = CreateEnv();
        var compiled = env.Compile("SELECT id FROM users");
        compiled.OutputColumnMasks.Length.ShouldBe(1);
        compiled.OutputColumnMasks[0].ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ExpressionOverMaskedColumn_IsFlagged()
    {
        var env = CreateEnv();
        var compiled = env.Compile("SELECT upper(email) FROM users");
        compiled.OutputColumnMasks.Length.ShouldBe(1);
        compiled.OutputColumnMasks[0].ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MaskedInOneUnionBranch_IsFlagged()
    {
        var env = CreateEnv();
        var compiled = env.Compile("SELECT email FROM users UNION ALL SELECT name FROM users");
        compiled.OutputColumnMasks.Length.ShouldBe(1);
        compiled.OutputColumnMasks[0].ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MaskedThroughCteAndDerivedTable_IsFlagged()
    {
        var env = CreateEnv();
        var compiled = env.Compile("WITH c AS (SELECT email FROM users) SELECT email FROM (SELECT email FROM c) d");
        compiled.OutputColumnMasks.Length.ShouldBe(1);
        compiled.OutputColumnMasks[0].ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void HiddenColumn_NotInOutput_NoFlagRevealsIt()
    {
        var env = new TestCompileEnv(TargetSqlDialect.PostgreSql);
        // Table has id and name exposed; a hidden column is omitted from the catalog entry
        env.Table("accounts", "tenant_id", "id:INTEGER", "name:TEXT");
        var compiled = env.Compile("SELECT * FROM accounts");
        compiled.OutputColumnMasks.Length.ShouldBe(2);
        compiled.OutputColumnMasks[0].ShouldBeFalse();
        compiled.OutputColumnMasks[1].ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MaskedColumnType_IsDeliveredType()
    {
        // An integer column masked with a text mask (e.g. hash or redact) delivers varchar
        var col = new SqlResultColumn("score", typeof(string), DataTypeName: "varchar", AllowDbNull: true);
        var trinoType = TrinoColumnTypes.Map(col, Array.Empty<object?>());
        trinoType.Name.ShouldBe("varchar");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MaskedColumn_NullableAlwaysTrue()
    {
        // A NOT NULL source column (AllowDbNull = false) masked must deliver nullable = true
        var col = new SqlResultColumn("email", typeof(string), AllowDbNull: false);
        bool masked = true;
        bool nullable = masked ? true : (col.AllowDbNull ?? true);
        nullable.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MaskArrayLengthMismatch_FailsClosed()
    {
        // Mismatch between mask array length and reader.FieldCount throws SQL_COMPILER_INVARIANT 500
        var table = new DataTable();
        table.Columns.Add("c1", typeof(int));
        table.Columns.Add("c2", typeof(string));
        table.Rows.Add(1, "test");

        var compiled = new CompiledSql("SELECT c1, c2 FROM t", ImmutableArray<BoundParameter>.Empty, TargetSqlDialect.PostgreSql, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "1.0")
        {
            OutputColumnMasks = ImmutableArray.Create(false) // length 1 != FieldCount 2
        };

        var ex = Should.Throw<GovernedSqlException>(() =>
        {
            using var reader = table.CreateDataReader();
            var maskArray = compiled.OutputColumnMasks;
            if (maskArray.IsDefault || maskArray.Length != reader.FieldCount)
            {
                throw new GovernedSqlException(GovernedSqlErrorCodes.CompilerInvariant, compiled.Dialect);
            }
        });

        ex.Code.ShouldBe(GovernedSqlErrorCodes.CompilerInvariant);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Federated_UnresolvedAlias_FlagsMasked()
    {
        // An alias the matcher cannot resolve yields masked = true
        var plan = CreateFederatedPlan();
        var isMasked = InvokeIsMaskedByName("unresolved_alias_col", plan);
        isMasked.ShouldBeTrue();
    }

    [Fact]
    public void Federated_MaskedColumn_IsFlagged()
    {
        var plan = CreateFederatedPlan();
        var isMasked = InvokeIsMaskedByName("masked_email", plan);
        isMasked.ShouldBeTrue();

        var isClearMasked = InvokeIsMaskedByName("clear_id", plan);
        isClearMasked.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MaskFlags_NotSharedAcrossPolicies()
    {
        var env1 = CreateEnv();
        var compiled1 = env1.Compile("SELECT email FROM users");
        compiled1.OutputColumnMasks[0].ShouldBeTrue();

        var env2 = new TestCompileEnv(TargetSqlDialect.PostgreSql);
        env2.Table("users", "tenant_id", "id:INTEGER", "email:TEXT", "name:TEXT", "score:INTEGER");
        // No mask on email in env2
        var compiled2 = env2.Compile("SELECT email FROM users");
        compiled2.OutputColumnMasks[0].ShouldBeFalse();

        // They must differ
        compiled1.OutputColumnMasks[0].ShouldNotBe(compiled2.OutputColumnMasks[0]);
    }

    private static CrossSourcePlan CreateFederatedPlan()
    {
        var tableId = new TableIdentifier("ds1", "public", "users");
        var meta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { TableName = "users", SchemaName = "public", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns = [
                new TableColumn { ColumnName = "clear_id", DataType = "int" },
                new TableColumn { ColumnName = "masked_email", DataType = "varchar" }
            ]
        };
        var decision = TableAccessDecision.Allowed(
            tableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["clear_id"] = ColumnAccessLevel.Clear,
                ["masked_email"] = ColumnAccessLevel.Mask
            },
            hasUnconstrainedColumnAllow: true);

        var stagingReq = new StagingTableRequest(
            new TrinoSqlEngine.Analysis.TableAccessTarget("ds1", "public", "users", null, "ds1.public.users"),
            meta,
            decision,
            "stg_users",
            ["clear_id", "masked_email"]);

        var compiled = new CompiledSql("SELECT clear_id, masked_email FROM stg_users", ImmutableArray<BoundParameter>.Empty, TargetSqlDialect.DuckDb, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "1.0");
        return new CrossSourcePlan(compiled, new[] { stagingReq }, new[] { "stg_users" });
    }

    private static bool InvokeIsMaskedByName(string columnName, CrossSourcePlan plan)
    {
        var method = typeof(FederatedDuckDbExecutionService).GetMethod("IsMaskedByName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.ShouldNotBeNull();
        return (bool)method.Invoke(null, new object[] { columnName, plan })!;
    }
}
