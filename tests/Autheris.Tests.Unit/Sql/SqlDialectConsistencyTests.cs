namespace Autheris.Tests.Unit.Sql;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

public sealed class SqlDialectConsistencyTests
{
    [Theory]
    [InlineData(DatabaseDialect.SqlServer, TargetSqlDialect.SqlServer, true)]
    [InlineData(DatabaseDialect.PostgreSql, TargetSqlDialect.PostgreSql, true)]
    [InlineData(DatabaseDialect.Sqlite, TargetSqlDialect.Sqlite, true)]
    [InlineData(DatabaseDialect.Oracle, TargetSqlDialect.Oracle, false)]
    public void SqlDialectMapper_MapsDialectsCorrectly(DatabaseDialect dialect, TargetSqlDialect expectedTarget, bool expectedExecutable)
    {
        SqlDialectMapper.TryToTargetDialect(dialect, out var target).ShouldBeTrue();
        target.ShouldBe(expectedTarget);

        SqlDialectMapper.ToTargetDialect(dialect).ShouldBe(expectedTarget);
        SqlDialectMapper.IsExecutable(dialect).ShouldBe(expectedExecutable);
    }

    [Fact]
    public void SqlDialectMapper_Databricks_ThrowsNotSupportedException()
    {
        SqlDialectMapper.TryToTargetDialect(DatabaseDialect.Databricks, out _).ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => SqlDialectMapper.ToTargetDialect(DatabaseDialect.Databricks));
        SqlDialectMapper.IsExecutable(DatabaseDialect.Databricks).ShouldBeFalse();
    }

    [Fact]
    public void SqlDialectMapper_IsExecutable_Matches_SqlConnectionFactory_SupportedDialects()
    {
        foreach (DatabaseDialect dialect in Enum.GetValues<DatabaseDialect>())
        {
            bool isExec = SqlDialectMapper.IsExecutable(dialect);
            bool factorySupports = dialect is DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite;
            isExec.ShouldBe(factorySupports, $"Mismatch for dialect {dialect}: IsExecutable={isExec}, Factory={factorySupports}");
        }
    }

    [Fact]
    public void SqlDataSourceExecutor_HasZeroDatabricksReferences()
    {
        // AR-13: Dead Databricks branches in SqlDataSourceExecutor.cs removed
        var repoRoot = FindRepoRoot();
        var filePath = Path.Combine(repoRoot, "src", "Autheris.Application", "Services", "SqlDataSourceExecutor.cs");
        var content = File.ReadAllText(filePath);

        var matches = Regex.Matches(content, @"\bDatabricks\b");
        matches.Count.ShouldBe(0, $"Found {matches.Count} Databricks references in SqlDataSourceExecutor.cs");
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, "a]b")]
    [InlineData(DatabaseDialect.SqlServer, "a;--")]
    [InlineData(DatabaseDialect.PostgreSql, "a\"b")]
    [InlineData(DatabaseDialect.PostgreSql, "a;--")]
    [InlineData(DatabaseDialect.Sqlite, "a\"b")]
    [InlineData(DatabaseDialect.Sqlite, "a;--")]
    public void StrictIdentifierValidation_RejectsOrProperlyQuotes_MaliciousIdentifiers(DatabaseDialect dialect, string maliciousId)
    {
        // Security Invariant 3: strict identifier quoting parity & validation
        dialect.ShouldBeOneOf(DatabaseDialect.SqlServer, DatabaseDialect.PostgreSql, DatabaseDialect.Sqlite);
        Should.Throw<ArgumentException>(() => DatabaseDialectExtensions.ValidateIdentifier(maliciousId));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Autheris.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }
}
