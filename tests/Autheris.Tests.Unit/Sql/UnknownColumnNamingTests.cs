namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// An unknown column is named only when the caller sees every table of the statement; otherwise the denial is the unchanged
/// <c>SQL_POLICY_DENIED</c>, byte-identical for an unknown and a hidden column. The provider's unknown-column error is simulated as PostgreSQL 42703.
/// </summary>
public sealed class UnknownColumnNamingTests
{
    private const string Tenant = "tenant_a";

    private sealed class UnknownColumnDbException(string message) : DbException(message)
    {
        public override string? SqlState => "42703";
    }

    private static TableMetadata Table(string name, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("default", "public", name),
        Table = new Table { TableName = name, SchemaName = "public", SourceType = "PostgreSQL", SourceName = "default" },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = c == "id" ? "int" : "varchar" }).Append(new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }).ToList()
    };

    /// <param name="levels">per table name: column levels other than Clear; a table in <paramref name="deniedTables"/> has no allow decision.</param>
    private static GovernedSqlRewriter Rewriter(
        IReadOnlyDictionary<string, Dictionary<string, ColumnAccessLevel>>? levels = null,
        ISet<string>? deniedTables = null)
    {
        var tables = new[]
        {
            Table("orders", "id", "amount", "secret"),
            Table("customers", "id", "name", "ssn")
        };
        var repo = new Autheris.Tests.Common.Fakes.InMemoryTableMetadataRepository(tables);
        var resolution = new Autheris.Tests.Common.Fakes.StubConsentResolutionService(table =>
        {
            if (deniedTables?.Contains(table.TableName) == true)
            {
                return TableAccessDecision.Denied(table, "no consent");
            }

            var access = levels is not null && levels.TryGetValue(table.TableName, out var l)
                ? new Dictionary<string, ColumnAccessLevel>(l, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
            return TableAccessDecision.Allowed(table, access, null, hasUnconstrainedColumnAllow: true);
        });
        var consents = new Autheris.Tests.Common.Fakes.InMemoryConsentRepository();
        var options = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" } });
        return new GovernedSqlRewriter(
            options, consentResolution: resolution, tableRepository: repo, consentRepository: consents,
            policyFactory: GovernedTestSupport.PolicyFactory(repo), typedPolicies: GovernedTestSupport.TypedPolicies());
    }

    private static Task<GovernedRewrite> RewriteAsync(GovernedSqlRewriter rewriter, string sql) =>
        rewriter.RewriteCoreAsync(sql, GovernedSqlExecutorX1Tests.Writer(), new TenantId(Tenant), dataSourceName: null, dmlContext: null, CancellationToken.None);

    /// <summary>Executes the rewrite against a provider that rejects the column and returns the sanitized failure and the audit of the attempt.</summary>
    private static async Task<(GovernedSqlException Error, GovernedSqlExecutorX1Tests.Harness Harness)> FailAsync(GovernedRewrite rewrite, string requestSql = "SELECT 1", string driverMessage = "column does not exist")
    {
        var h = GovernedSqlExecutorX1Tests.CreateHarness("PostgreSql", () => new FakeDbCommand { OnExecuteReader = (_, _) => throw new UnknownColumnDbException(driverMessage) });
        var ex = await Should.ThrowAsync<GovernedSqlException>(() => h.Executor.ExecuteRewrittenAsync(
            new GovernedSqlQueryRequest(requestSql), GovernedSqlExecutorX1Tests.Writer(), new TenantId(Tenant), rewrite, new DmlAuditContext(),
            new Dictionary<string, object?>(), (_, _) => Task.CompletedTask, CancellationToken.None));
        return (ex, h);
    }

    /// <summary>The response body of a failure without its trace id: what the caller sees.</summary>
    private static string Body(Exception ex)
    {
        SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        Dictionary<string, object?>? ext = null;
        Autheris.Api.Endpoints.SqlErrorResponse.AddMappedExtensions(ref ext, mapped);
        var problem = Autheris.Api.Errors.ApiProblems.Problem(mapped.Status, mapped.Code, mapped.Title, extraExtensions: ext)
            .ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>().ProblemDetails;
        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = problem.Status, ["title"] = problem.Title, ["detail"] = problem.Detail, ["type"] = problem.Type
        };
        foreach (var (key, value) in problem.Extensions.Where(e => e.Key != "traceId"))
        {
            fields[key] = value;
        }

        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public async Task FullyVisibleTable_UnknownColumn_Named400()
    {
        var rewrite = await RewriteAsync(Rewriter(), "SELECT custmer_id FROM orders");

        var (error, _) = await FailAsync(rewrite);

        var named = error.ShouldBeOfType<SqlUnknownColumnException>();
        named.ColumnName.ShouldBe("custmer_id");
        named.Line.ShouldBe(1);
        named.Column.ShouldBe(8);
        SqlCompilerErrorMapper.TryMap(error, out var mapped).ShouldBeTrue();
        mapped.Status.ShouldBe(400);
        mapped.Code.ShouldBe("UNKNOWN_COLUMN");
        error.Message.ShouldNotContain("custmer_id");
    }

    [Fact]
    public async Task MaskedColumnsOnly_CountAsVisible_Named()
    {
        var levels = new Dictionary<string, Dictionary<string, ColumnAccessLevel>> { ["orders"] = new() { ["secret"] = ColumnAccessLevel.Mask } };
        var rewrite = await RewriteAsync(Rewriter(levels), "SELECT custmer_id FROM orders");

        var (error, _) = await FailAsync(rewrite);

        error.ShouldBeOfType<SqlUnknownColumnException>().ColumnName.ShouldBe("custmer_id");
    }

    [Fact]
    public async Task TableWithDeniedColumn_UnknownColumn_StaysPolicyDenied()
    {
        var levels = new Dictionary<string, Dictionary<string, ColumnAccessLevel>> { ["orders"] = new() { ["secret"] = ColumnAccessLevel.Deny } };
        var rewrite = await RewriteAsync(Rewriter(levels), "SELECT custmer_id FROM orders");

        rewrite.ColumnProbe.ShouldBeNull();
        var (error, _) = await FailAsync(rewrite);

        error.Code.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TableWithDeniedColumn_UnknownAndDenied_ByteIdenticalBodies()
    {
        var levels = new Dictionary<string, Dictionary<string, ColumnAccessLevel>> { ["orders"] = new() { ["secret"] = ColumnAccessLevel.Deny } };
        var rewriter = Rewriter(levels);
        var unknown = await RewriteAsync(rewriter, "SELECT custmer_id FROM orders");
        var (unknownError, _) = await FailAsync(unknown);

        // the same statement shape with the hidden column: the provider answers the same way (the column is not in the compile catalog)
        var hiddenRewrite = await RewriteAsync(rewriter, "SELECT secret FROM orders").ContinueWith(t => t.IsFaulted ? null : t.Result);
        var hiddenBody = hiddenRewrite is null ? null : Body((await FailAsync(hiddenRewrite)).Error);
        var unknownBody = Body(unknownError);

        unknownBody.ShouldContain("SQL_POLICY_DENIED");
        if (hiddenBody is not null)
        {
            hiddenBody.ShouldBe(unknownBody);
        }

        foreach (var key in new[] { "\"line\"", "\"column\"", "\"hint\"", "\"columnName\"" })
        {
            unknownBody.ShouldNotContain(key);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PolicyDenied_Body_NeverCarriesAName_WhenAnyTableHidesAColumn()
    {
        var levels = new Dictionary<string, Dictionary<string, ColumnAccessLevel>> { ["customers"] = new() { ["ssn"] = ColumnAccessLevel.Deny } };
        var rewrite = await RewriteAsync(Rewriter(levels), "SELECT custmer_id FROM orders o JOIN customers c ON o.id = c.id");

        var (error, _) = await FailAsync(rewrite);

        Body(error).ShouldNotContain("custmer_id");
    }

    [Fact]
    public async Task JoinWithPartiallyVisibleTable_NotNamed()
    {
        var levels = new Dictionary<string, Dictionary<string, ColumnAccessLevel>> { ["customers"] = new() { ["ssn"] = ColumnAccessLevel.Deny } };

        var rewrite = await RewriteAsync(Rewriter(levels), "SELECT custmer_id FROM orders o JOIN customers c ON o.id = c.id");

        rewrite.ColumnProbe.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AllTablesVisibleRequiredAcrossJoinAndCte()
    {
        var levels = new Dictionary<string, Dictionary<string, ColumnAccessLevel>> { ["customers"] = new() { ["ssn"] = ColumnAccessLevel.Deny } };
        var hidden = Rewriter(levels);
        var visible = Rewriter();

        (await RewriteAsync(hidden, "SELECT custmer_id FROM orders o JOIN customers c ON o.id = c.id")).ColumnProbe.ShouldBeNull();
        (await RewriteAsync(hidden, "WITH c AS (SELECT id FROM customers) SELECT x FROM c")).ColumnProbe.ShouldBeNull();
        (await RewriteAsync(hidden, "SELECT x FROM orders WHERE id IN (SELECT id FROM customers)")).ColumnProbe.ShouldBeNull();

        var probe = (await RewriteAsync(visible, "SELECT x FROM orders o JOIN customers c ON o.id = c.id")).ColumnProbe.ShouldNotBeNull();
        probe.CatalogColumns.ShouldContain("ssn");      // every catalog column of every table, hidden ones included
        probe.CatalogColumns.ShouldContain("secret");
        probe.ClientSql.ShouldBe("SELECT x FROM orders o JOIN customers c ON o.id = c.id");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DecisionDeniedTable_StaysPolicyDenied_NoResolver()
    {
        var rewriter = Rewriter(deniedTables: new HashSet<string> { "customers" });

        var ex = await Should.ThrowAsync<WebSqlTableDeniedException>(() => RewriteAsync(rewriter, "SELECT custmer_id FROM customers"));

        SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        mapped.Code.ShouldBe("SQL_POLICY_DENIED");
        Body(ex).ShouldNotContain("custmer_id");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenColumnNameAsUnknown_IsNeverNamed()
    {
        // the catalog columns include the hidden one: a differently spelled hidden column is a catalog column, not an unknown one.
        var probe = new UnknownColumnProbe("SELECT SECRET FROM orders", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "secret" });
        UnknownColumnResolver.TryFind(probe, out _, out _, out _).ShouldBeFalse();

        var (error, _) = await FailAsync(new GovernedRewrite(await GovernedSqlExecutorX1Tests.CompileAsync("PostgreSQL", "SELECT id FROM orders"), [], "default", ColumnProbe: probe));
        error.Code.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }

    [Fact]
    public async Task SelectAliasAndCteColumn_NotCandidates()
    {
        var catalog = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "amount" };
        foreach (var sql in new[]
        {
            "SELECT amount AS total FROM orders ORDER BY total",
            "WITH c(x) AS (SELECT id FROM orders) SELECT x FROM c",
            "SELECT d.v FROM (SELECT amount AS v FROM orders) d",
            "SELECT AMOUNT FROM orders"
        })
        {
            UnknownColumnResolver.TryFind(new UnknownColumnProbe(sql, catalog), out _, out _, out _).ShouldBeFalse(sql);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public void FirstUnknownReference_IsChosen_WithPosition()
    {
        var catalog = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "amount" };

        UnknownColumnResolver.TryFind(new UnknownColumnProbe("SELECT id,\n  nope1, nope2 FROM orders", catalog), out var name, out var line, out var column).ShouldBeTrue();

        name.ShouldBe("nope1");
        line.ShouldBe(2);
        column.ShouldBe(3);
    }

    [Fact]
    public async Task NoCandidate_StaysPolicyDenied()
    {
        var rewrite = await RewriteAsync(Rewriter(), "SELECT id FROM orders");   // every referenced name is a catalog column

        var (error, _) = await FailAsync(rewrite);

        error.Code.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ResolverException_StaysPolicyDenied()
    {
        var probe = new UnknownColumnProbe("SELEC ??? FROM", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        UnknownColumnResolver.TryFind(probe, out _, out _, out _).ShouldBeFalse();

        var (error, _) = await FailAsync(new GovernedRewrite(await GovernedSqlExecutorX1Tests.CompileAsync("PostgreSQL", "SELECT id FROM orders"), [], "default", ColumnProbe: probe));

        error.Code.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DriverMessageContainingColumn_NeverEchoed()
    {
        var rewrite = await RewriteAsync(Rewriter(), "SELECT custmer_id FROM orders");

        var (error, h) = await FailAsync(rewrite, driverMessage: "column \"other_secret\" of relation \"orders\" does not exist");

        error.ShouldBeOfType<SqlUnknownColumnException>().ColumnName.ShouldBe("custmer_id");
        Body(error).ShouldNotContain("other_secret");
        error.Message.ShouldNotContain("other_secret");
        error.InnerException.ShouldBeNull();
        h.Audit.ShouldAllBe(e => !e.DetailsJson.Contains("other_secret"));
    }

    [Theory]
    [InlineData("SELECT \"my col\" FROM orders")]
    [InlineData("SELECT \"a\"\"b\" FROM orders")]
    [InlineData("SELECT \"x y\" FROM orders")]
    [Trait("Category", "Security")]
    public async Task QuotedSpecialName_NotNamed(string clientSql)
    {
        var probe = new UnknownColumnProbe(clientSql, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" });
        UnknownColumnResolver.TryFind(probe, out _, out _, out _).ShouldBeFalse();

        var (error, _) = await FailAsync(new GovernedRewrite(await GovernedSqlExecutorX1Tests.CompileAsync("PostgreSQL", "SELECT id FROM orders"), [], "default", ColumnProbe: probe));

        error.Code.ShouldBe(GovernedSqlErrorCodes.PolicyDenied);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UnknownColumn_Audit_StoresCodeNotName()
    {
        var rewrite = await RewriteAsync(Rewriter(), "SELECT custmer_id FROM orders");

        var (_, h) = await FailAsync(rewrite);   // the request text does not name the column, so any name in the audit would come from the resolver

        var entry = h.Audit.Single(e => e.EventType == Autheris.Domain.Audit.AuditEventTypes.QueryExecutionError);
        var details = JsonDocument.Parse(entry.DetailsJson).RootElement;
        details.GetProperty("errorCode").GetString().ShouldBe("UNKNOWN_COLUMN");
        entry.DetailsJson.ShouldNotContain("custmer_id");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenColumn_Audit_StoresPolicyDenied()
    {
        var levels = new Dictionary<string, Dictionary<string, ColumnAccessLevel>> { ["orders"] = new() { ["secret"] = ColumnAccessLevel.Deny } };
        var rewrite = await RewriteAsync(Rewriter(levels), "SELECT custmer_id FROM orders");

        var (_, h) = await FailAsync(rewrite);

        var entry = h.Audit.Single(e => e.EventType == Autheris.Domain.Audit.AuditEventTypes.QueryExecutionError);
        JsonDocument.Parse(entry.DetailsJson).RootElement.GetProperty("errorCode").GetString().ShouldBe("SQL_POLICY_DENIED");
        entry.DetailsJson.ShouldNotContain("UNKNOWN_COLUMN");
        entry.DetailsJson.ShouldNotContain("custmer_id");
    }
}
