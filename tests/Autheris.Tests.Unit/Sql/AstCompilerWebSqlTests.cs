namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Wunsch 4: WebSQL through the governed service (the AST compiler is the only path since X1), per target dialect.
/// </summary>
public sealed class AstCompilerWebSqlTests(ITestOutputHelper output)
{
    private const string Tenant = "tenant_a";

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-AST"), new Claim("tenant_id", Tenant)], "Test"));

    internal static GovernedSqlExecutionService CreateService(
        string sourceType,
        ConsentRowFilter? rowFilter = null)
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "public", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "public", SourceType = sourceType },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "dept", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "created_at", DataType = "timestamp" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(table.Identifier.TableName == ci.Arg<TableIdentifier>().TableName ? table : null));

        // A consent row filter is a real consent: the production resolution turns it into a typed row policy (a text-only filter is denied).
        IReadOnlyList<Consent> consentList = rowFilter is null ? Array.Empty<Consent>() : [AllowConsent(table, rowFilter)];
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(consentList));
        IConsentResolutionService resolution;
        if (rowFilter is null)
        {
            resolution = Substitute.For<IConsentResolutionService>();
            resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
                .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true));
        }
        else
        {
            resolution = new ConsentResolutionService();
        }

        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        return new GovernedSqlExecutionService(
            Options.Create(new GatewayOptions
            {
                WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 500 }
            }),
            consentResolution: resolution,
            tableRepository: repo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(repo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents);
    }

    private static Consent AllowConsent(TableMetadata table, ConsentRowFilter filter) => new()
    {
        TableIdentifier = table.Identifier,
        TenantId = new TenantId(Tenant),
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("S-1-5-21-AST"),
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = [filter],
        ColumnRules = table.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList()
    };

    private async Task<TrinoSqlEngine.Ast.Emit.CompiledSql> CompileAsync(string sourceType, string sql)
    {
        var compiled = await CreateService(sourceType).CompileAsync(sql, User(), new TenantId(Tenant));
        output.WriteLine(compiled.Sql);
        return compiled;
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SqlServer")]
    [InlineData("Sqlite")]
    public async Task UnsupportedConstruct_IsAClientError_NotServerError(string sourceType)
    {
        // X1: the engine's typed exception propagates and every error surface maps it (422 SQL_CONSTRUCT_NOT_SUPPORTED, fixed message).
        var ex = await Should.ThrowAsync<TrinoSqlEngine.Ast.Builder.AstBuildException>(() =>
            CompileAsync(sourceType, "SELECT dept, SUM(amount) FROM orders GROUP BY AUTO"));

        SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        mapped.Status.ShouldBe(422);
        mapped.Code.ShouldBe("SQL_CONSTRUCT_NOT_SUPPORTED");
    }

    [Theory]
    [InlineData("PostgreSQL", "COALESCE(\"amount\", ")]
    [InlineData("SqlServer", "COALESCE([amount], ")]
    [InlineData("Sqlite", "COALESCE(\"amount\", ")]
    public async Task Aggregates_AndBuiltins_AreEmittedUnquoted(string sourceType, string coalesce)
    {
        var compiled = await CompileAsync(sourceType, "SELECT dept, COUNT(*) AS n, SUM(COALESCE(amount, 0)) FROM orders GROUP BY dept HAVING COUNT(*) > 1");

        // The literals 0 and 1 are bound (X1); the function names stay unquoted.
        compiled.Sql.ShouldContain("COUNT(*)");
        compiled.Sql.ShouldContain("HAVING COUNT(*) >");
        compiled.Sql.ShouldContain($"SUM({coalesce}");
        compiled.ValuesOf(TrinoSqlEngine.Ast.Emit.ParameterOrigin.QueryLiteral).Select(Convert.ToInt64).ShouldBeSubsetOf([0L, 1L, 100L]);   // SQLite also binds the enforced row limit
        compiled.ValuesOf(TrinoSqlEngine.Ast.Emit.ParameterOrigin.QueryLiteral).Select(Convert.ToInt64).ShouldContain(0L);
        compiled.ValuesOf(TrinoSqlEngine.Ast.Emit.ParameterOrigin.QueryLiteral).Select(Convert.ToInt64).ShouldContain(1L);
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SqlServer")]
    [InlineData("Sqlite")]
    public async Task ClientParameters_AreCompiledAsNamedClientBinds(string sourceType)
    {
        // Wunsch 4, Phase 2: @name → __param_name → AST → a ClientNamed bind that the binder fills by its own source name per request.
        var parameters = new Dictionary<string, object?> { ["d"] = "Sales", ["m"] = 10 };
        string normalized = GovernedSqlExecutionService.NormalizeClientParameters(
            "SELECT id FROM orders WHERE dept = @d AND amount > @m", parameters, out _);

        var compiled = await CompileAsync(sourceType, normalized);

        compiled.Sql.ShouldNotContain("__param_");
        compiled.Sql.ShouldNotContain("Sales");
        compiled.Parameters.Where(p => p.Origin == TrinoSqlEngine.Ast.Emit.ParameterOrigin.ClientNamed).Select(p => p.SourceName)
            .ShouldBe(["d", "m"], ignoreOrder: true);
        compiled.Parameters.Where(p => p.Origin == TrinoSqlEngine.Ast.Emit.ParameterOrigin.ClientNamed).ShouldAllBe(p => p.Value == null);
    }

    [Fact]
    public async Task SqlServer_ConsentRowFilter_InTargetDialect_IsApplied()
    {
        // Befund: "Gegen SQL Server: Abfrage mit Zeilenfilter als ungültig abgelehnt (400)".
        var service = CreateService("SqlServer", new ConsentRowFilter { ColumnName = "dept", Operator = "EQ", ValueType = "string", ValueJson = "\"Sales\"" });

        var compiled = await service.CompileAsync("SELECT id, amount FROM orders", User(), new TenantId(Tenant));
        output.WriteLine(compiled.Sql);

        compiled.Sql.ShouldContain("[dept] =");
        compiled.CountOfValue(TrinoSqlEngine.Ast.Emit.ParameterOrigin.Policy, "Sales").ShouldBe(1);
        compiled.Sql.ShouldNotContain("Sales");
        compiled.ShouldBindTenant(Tenant);
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SqlServer")]
    [InlineData("Sqlite")]
    public async Task ParameterizedConsentRowFilter_IsApplied(string sourceType)
    {
        var service = CreateService(sourceType, new ConsentRowFilter { ColumnName = "dept", Operator = "EQ", ValueType = "string", ValueJson = "\"Sales\"" });

        var compiled = await service.CompileAsync("SELECT id FROM orders", User(), new TenantId(Tenant));
        output.WriteLine(compiled.Sql);

        // The consent value is a bound Policy parameter, never part of the text.
        compiled.CountOfValue(TrinoSqlEngine.Ast.Emit.ParameterOrigin.Policy, "Sales").ShouldBe(1);
        compiled.Sql.ShouldNotContain("Sales");
    }
}
