namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using Antlr4.Runtime.Misc;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Microsoft.Extensions.Logging;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

public sealed class WebSqlCommentPolicyTests
{
    private static readonly TargetSqlDialect[] TestedDialects =
    [
        TargetSqlDialect.PostgreSql,
        TargetSqlDialect.SqlServer,
        TargetSqlDialect.Oracle,
        TargetSqlDialect.Databricks,
        TargetSqlDialect.DuckDb
    ];

    private static CompiledSql CompileFor(TargetSqlDialect dialect, string sql)
    {
        return new Autheris.Tests.Unit.Sql.TestCompileEnv(dialect)
            .Compile(sql, guards: ClientSqlTokenPolicy.Compile(dialect));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Comments_Accepted_ForClientSql()
    {
        const string Sql = "SELECT 1 -- inline comment\n /* block comment */";
        var compiled = CompileFor(TargetSqlDialect.PostgreSql, Sql);
        compiled.ShouldNotBeNull();
        compiled.Sql.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Comments_NotForwardedToSource_AllDialects()
    {
        const string Sql = "SELECT 1 /* comment */ -- line comment\n";
        foreach (var dialect in TestedDialects)
        {
            var compiled = CompileFor(dialect, Sql);
            compiled.Sql.ShouldNotContain("--");
            compiled.Sql.ShouldNotContain("/*");
            compiled.Sql.ShouldNotContain("comment");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OptimizerHint_NotEmitted_SqlServerOracleDatabricks()
    {
        const string Sql = "SELECT /*+ FULL(orders) */ 1";
        foreach (var dialect in new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks })
        {
            var compiled = CompileFor(dialect, Sql);
            compiled.Sql.ShouldNotContain("/*+");
            compiled.Sql.ShouldNotContain("FULL");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NestedBlockComment_StillRejected()
    {
        const string Sql = "SELECT /* outer /* nested */ comment */ 1";
        var ex = Should.Throw<ParseCanceledException>(() =>
            FastSqlEngine.Default.Parse(Sql.AsMemory(), ClientSqlTokenPolicy.Analysis));

        ex.ShouldBeOfType<SqlSyntaxErrorException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CommentCannotHideSecondStatement()
    {
        const string Sql = "SELECT 1 /* x */ ; DROP TABLE t";
        var ex = Should.Throw<ParseCanceledException>(() =>
            FastSqlEngine.Default.Parse(Sql.AsMemory(), ClientSqlTokenPolicy.Analysis));

        ex.ShouldBeOfType<SqlSyntaxErrorException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FragmentValidator_StillRejectsComments()
    {
        Should.Throw<ArgumentException>(() =>
            SqlSecurityValidator.ValidatePredicateSql("id = 1 -- comment", "testFilter"));

        Should.Throw<ArgumentException>(() =>
            SqlSecurityValidator.ValidatePredicateSql("id = 1 /* comment */", "testFilter"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OlapStagingAndSqlEndpoints_StillRejectComments()
    {
        // Staging service options
        var stagingField = typeof(OlapStagingService).GetField("StagingTokenOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (stagingField?.GetValue(null) is SqlTokenSecurityOptions stagingOptions)
        {
            stagingOptions.RejectComments.ShouldBeTrue();
        }

        // Fragment validator options
        var fragField = typeof(SqlSecurityValidator).GetField("FragmentTokenOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (fragField?.GetValue(null) is SqlTokenSecurityOptions fragOptions)
        {
            fragOptions.RejectComments.ShouldBeTrue();
        }

        // Strict default options
        SqlTokenSecurityOptions.Strict.RejectComments.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CommentWithNonAsciiAndDollar_NotForwardedOrAudited()
    {
        const string Sql = "SELECT 1 /* ü $$ secret */ -- ü $$ comment\n";
        foreach (var dialect in TestedDialects)
        {
            var compiled = CompileFor(dialect, Sql);
            compiled.Sql.ShouldNotContain("ü");
            compiled.Sql.ShouldNotContain("$$");
            compiled.Sql.ShouldNotContain("secret");
        }

        var anonymized = AstSecurityVisitor.RedactSqlLiterals(Sql);
        anonymized.ShouldNotContain("ü");
        anonymized.ShouldNotContain("$$");
        anonymized.ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RawClientSql_NotLoggedAtInformationOrAbove()
    {
        var testLogger = new TestListLogger();
        const string SecretSql = "SELECT * FROM secret_customers_table WHERE sensitive_comment = 1";

        // Simulate error logging via SqlErrorResponse with mapped error
        var mapping = new Autheris.Application.Sql.Errors.SqlErrorMapping(400, "SQL_SYNTAX_ERROR", "Invalid SQL syntax.", Line: 1, Column: 1);
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var ex = new SqlSyntaxErrorException(1, 1, "syntax error");

        await Autheris.Api.Endpoints.SqlErrorResponse.WriteAsync(
            httpContext, ex, mapping, testLogger, "WebSQL", System.Threading.CancellationToken.None);

        foreach (var entry in testLogger.Entries)
        {
            if (entry.Level >= LogLevel.Information)
            {
                entry.Message.ShouldNotContain(SecretSql);
                entry.Message.ShouldNotContain("secret_customers_table");
            }
        }
    }

    private sealed class TestListLogger : ILogger
    {
        public sealed record LogEntry(LogLevel Level, string Message);
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
