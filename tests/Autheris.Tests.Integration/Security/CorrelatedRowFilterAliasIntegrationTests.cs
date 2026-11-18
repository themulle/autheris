using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.TestDoubles;
using Autheris.Tests.Unit;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Correlated row filter execution against real PostgreSQL (<see cref="SqlTableReadExecutor"/>).
/// </summary>
[Trait("Category", "Security")]
public sealed class CorrelatedRowFilterAliasIntegrationTests
{
    [RequiresContainerRuntimeFact]
    public async Task SqlTableReadExecutor_AppliesCorrelatedPolicy_AgainstPostgreSql()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE public.customers (id integer PRIMARY KEY, country text NOT NULL);
                CREATE TABLE public.invoices (id integer PRIMARY KEY, customer_id integer NOT NULL, amount integer NOT NULL);
                INSERT INTO public.customers VALUES (1, 'CH'), (2, 'DE');
                INSERT INTO public.invoices VALUES (10, 1, 100), (11, 2, 200), (12, 1, 300);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier("erp", "public", "invoices"),
            Table = new Table
            {
                SourceName = "erp",
                SchemaName = "public",
                TableName = "invoices",
                DataSourceType = DataSourceType.Sql,
                SourceType = "PostgreSql"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "customer_id", DataType = "integer" },
                new TableColumn { ColumnName = "amount", DataType = "integer" }
            ]
        };

        var columnAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["customer_id"] = ColumnAccessLevel.Clear,
            ["amount"] = ColumnAccessLevel.Clear
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST")], "Test"));
        var context = new DataSourceExecutionContext(
            SourceName: "erp",
            Metadata: metadata,
            Principal: principal,
            AccessDecision: TableAccessDecision.Allowed(metadata.Identifier, columnAccess),
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id", "amount"]);

        var env = new PgCompileEnv()
            .Table("invoices", null, "id:integer", "customer_id:integer", "amount:integer")
            .Table("customers", null, "id:integer", "country")
            .Policy("invoices", CustomerCountryPolicy());

        var executor = new PgGovernedSqlService(db.ConnectionString, env).Executor();
        var rows = await executor.ExecuteAsync(context);

        rows.Select(r => Convert.ToInt64(r["id"], CultureInfo.InvariantCulture))
            .OrderBy(x => x)
            .ShouldBe([10L, 12L]);
    }

    private static ColumnReference Col(params string[] parts) =>
        new(new SqlQualifiedName(parts.Select(p => new SqlIdentifier(p, false)).ToList()));

    /// <summary>EXISTS (SELECT 1 FROM customers c WHERE c.id = autheris_target.customer_id AND c.country = 'CH').</summary>
    private static PolicyPredicate CustomerCountryPolicy() => PolicyPredicate.Create(
        new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("customers", false) }), new SqlIdentifier("c", false)),
                new BinaryExpression(
                    new BinaryExpression(Col("c", "id"), BinaryOperator.Equal, Col(RowFilterAliases.Target, "customer_id")),
                    BinaryOperator.And,
                    new BinaryExpression(Col("c", "country"), BinaryOperator.Equal, new PolicyParameterExpression("__pol_country", SqlParameterType.String))),
                null, null), null, null)),
        new Dictionary<string, PolicyValue> { ["__pol_country"] = new("CH", SqlParameterType.String) });
}
