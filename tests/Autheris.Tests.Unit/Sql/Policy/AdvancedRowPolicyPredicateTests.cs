using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.Json;
using Autheris.Application.Governance;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>WP-X0d (Phase 3 amendment, SEC-X1-02): correlated, cross-source and Casbin producers build typed predicates.</summary>
public sealed class AdvancedRowPolicyPredicateTests
{
    private readonly PolicyPredicateRenderer _renderer = new();

    private static ConsentRowFilter Correlated(string predicateJson, IReadOnlyList<SubqueryJoinHop>? hops = null,
        string? temporal = null, string? validFrom = null, string? validTo = null) => new()
    {
        FilterType = RowFilterType.SubqueryCorrelated,
        DependentTable = new TableIdentifier("erp", "main", "customers"),
        DependentTableAlias = "c",
        ForeignKeyColumn = "customer_id",
        PrimaryKeyColumn = "id",
        SubqueryFilterPredicateJson = predicateJson,
        AdditionalHops = hops,
        TargetTemporalColumn = temporal,
        DependentValidFromColumn = validFrom,
        DependentValidToColumn = validTo
    };

    [Fact]
    public void CorrelatedSubquery_ValuesAreParameters()
    {
        var filter = Correlated("{\"c.country\": \"CH' OR '1'='1\", \"c.tier\": null}");

        var predicate = AdvancedRlsFilterGenerator.BuildCorrelatedPredicate(filter);
        var rendered = _renderer.Render(predicate, TargetSqlDialect.SqlServer, RowFilterAliases.Target);

        rendered.Sql.ShouldBe("EXISTS (SELECT 1 FROM [main].[customers] AS [c] WHERE [c].[id] = [autheris_target].[customer_id] AND [c].[country] = @__gql_rls_0 AND [c].[tier] IS NULL)");
        rendered.Sql.ShouldNotContain("CH");
        rendered.Sql.ShouldNotContain("'");
        rendered.Parameters.Single().Value.ShouldBe("CH' OR '1'='1");
        predicate.Parameters.Count.ShouldBe(1);
    }

    [Fact]
    public void CorrelatedSubquery_ArrayPredicates_UseOperatorsAndParameters()
    {
        var filter = Correlated("[{\"column\":\"c.score\",\"op\":\"GTE\",\"value\":10},{\"column\":\"c.name\",\"op\":\"LIKE\",\"value\":\"A%\"},{\"column\":\"c.x\",\"op\":\"NEQ\",\"value\":null}]");

        var rendered = _renderer.Render(AdvancedRlsFilterGenerator.BuildCorrelatedPredicate(filter), TargetSqlDialect.Sqlite, RowFilterAliases.Target);

        rendered.Sql.ShouldContain("\"c\".\"score\" >= @__gql_rls_0");
        rendered.Sql.ShouldContain("\"c\".\"name\" LIKE @__gql_rls_1");
        rendered.Sql.ShouldContain("\"c\".\"x\" IS NOT NULL");
        rendered.Parameters["@__gql_rls_0"].ShouldBe(10L);
    }

    [Fact]
    public void CorrelatedSubquery_MultiHopAndTemporal_BindsToTheTargetAlias()
    {
        var hops = new List<SubqueryJoinHop>
        {
            new() { Table = new TableIdentifier("erp", "main", "asset_ownership"), TableAlias = "ao", LeftJoinColumn = "c.id", RightJoinColumn = "ao.customer_id" }
        };
        var filter = Correlated("{\"c.country\": \"CH\"}", hops, temporal: "i.invoice_date", validFrom: "c.valid_from", validTo: "c.valid_to");
        filter = new ConsentRowFilter
        {
            FilterType = filter.FilterType, DependentTable = filter.DependentTable, DependentTableAlias = "c", ForeignKeyColumn = "customer_id",
            PrimaryKeyColumn = "id", SubqueryFilterPredicateJson = filter.SubqueryFilterPredicateJson, AdditionalHops = hops,
            TargetTableAlias = "i", TargetTemporalColumn = "i.invoice_date", DependentValidFromColumn = "c.valid_from", DependentValidToColumn = "c.valid_to"
        };

        var predicate = AdvancedRlsFilterGenerator.BuildCorrelatedPredicate(filter);
        var sql = _renderer.Render(predicate, TargetSqlDialect.Sqlite, RowFilterAliases.Target).Sql;

        sql.ShouldContain("INNER JOIN \"main\".\"asset_ownership\" AS \"ao\" ON \"c\".\"id\" = \"ao\".\"customer_id\"");
        sql.ShouldContain("\"autheris_target\".\"invoice_date\" >= \"c\".\"valid_from\"");
        sql.ShouldContain("(\"c\".\"valid_to\" IS NULL OR \"autheris_target\".\"invoice_date\" < \"c\".\"valid_to\")");
        sql.ShouldNotContain("\"i\".");
    }

    [Fact]
    public void CorrelatedSubquery_RejectsInjectionInIdentifiers_AndAliasCollisions()
    {
        var bad = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated, DependentTable = new TableIdentifier("erp", "main", "customers"),
            DependentTableAlias = "c", ForeignKeyColumn = "customer_id\"; DROP", PrimaryKeyColumn = "id"
        };
        Should.Throw<InvalidOperationException>(() => AdvancedRlsFilterGenerator.BuildCorrelatedPredicate(bad));

        var collide = Correlated("{}") ;
        collide = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated, DependentTable = collide.DependentTable, DependentTableAlias = RowFilterAliases.Target,
            ForeignKeyColumn = "customer_id", PrimaryKeyColumn = "id"
        };
        Should.Throw<InvalidOperationException>(() => AdvancedRlsFilterGenerator.BuildCorrelatedPredicate(collide));
    }

    [Fact]
    public void CorrelatedSubquery_RunsAgainstSqlite_WithHostileValue()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE customers (id INTEGER PRIMARY KEY, country TEXT NOT NULL);
CREATE TABLE invoices (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL);
INSERT INTO customers VALUES (1, 'CH'), (2, 'DE'), (3, 'CH'' OR ''1''=''1');
INSERT INTO invoices VALUES (10, 1), (11, 2), (12, 3);";
            cmd.ExecuteNonQuery();
        }

        var hostile = Correlated("{\"c.country\": \"CH' OR '1'='1\"}");
        var benign = Correlated("{\"c.country\": \"CH\"}");
        Run(connection, hostile).ShouldBe([12L]);
        Run(connection, benign).ShouldBe([10L]);

        List<long> Run(SqliteConnection c, ConsentRowFilter f)
        {
            var rendered = _renderer.Render(AdvancedRlsFilterGenerator.BuildCorrelatedPredicate(f), TargetSqlDialect.Sqlite, RowFilterAliases.Target);
            using var q = c.CreateCommand();
            q.CommandText = $"SELECT id FROM invoices AS autheris_target WHERE {rendered.Sql} ORDER BY id";
            foreach (var (name, value) in rendered.Parameters) q.Parameters.AddWithValue(name, value ?? DBNull.Value);
            var ids = new List<long>();
            using var r = q.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
            return ids;
        }
    }

    [Fact]
    public void CrossSourceSetFilter_ValuesAreParameters()
    {
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter,
            ColumnName = "customer_id",
            ValueJson = "[\"a'b\", 7, null, 2.5]"
        };

        var predicate = AdvancedRlsFilterGenerator.BuildCrossSourceSetPredicate(filter, DatabaseDialect.PostgreSql);
        var rendered = _renderer.Render(predicate, TargetSqlDialect.PostgreSql, RowFilterAliases.Target);

        rendered.Sql.ShouldBe("\"customer_id\" IN (@__gql_rls_0, @__gql_rls_1, @__gql_rls_2)");
        rendered.Sql.ShouldNotContain("a'b");
        rendered.Parameters.Values.ShouldBe(new object?[] { "a'b", 7L, 2.5m });
    }

    [Fact]
    public void CrossSourceSetFilter_EmptySet_DeniesAll_AndOracleChunksAt1000()
    {
        var empty = new ConsentRowFilter { FilterType = RowFilterType.CrossSourceSetFilter, ColumnName = "customer_id", ValueJson = "[]" };
        _renderer.Render(AdvancedRlsFilterGenerator.BuildCrossSourceSetPredicate(empty, DatabaseDialect.SqlServer), TargetSqlDialect.SqlServer, RowFilterAliases.Target)
            .Sql.ShouldBe("1 = 0");

        var many = new ConsentRowFilter
        {
            FilterType = RowFilterType.CrossSourceSetFilter, ColumnName = "customer_id",
            ValueJson = JsonSerializer.Serialize(Enumerable.Range(0, 1500))
        };
        var predicate = AdvancedRlsFilterGenerator.BuildCrossSourceSetPredicate(many, DatabaseDialect.Oracle);
        var rendered = _renderer.Render(predicate, TargetSqlDialect.Oracle, RowFilterAliases.Target);
        predicate.Parameters.Count.ShouldBe(1500);
        System.Text.RegularExpressions.Regex.Count(rendered.Sql, " IN \\(").ShouldBe(2);
    }

    [Fact]
    public void RlsFilterGenerator_ExposesTheTypedProducers()
    {
        var generator = RlsFilterGenerator.Instance;
        var filter = Correlated("{\"c.country\": \"CH\"}");

        generator.BuildCorrelatedPredicate(filter).Parameters.Count.ShouldBe(1);
        generator.BuildCrossSourceSetPredicate(
            new ConsentRowFilter { ColumnName = "k", ValueJson = "[1,2]" }, DatabaseDialect.Sqlite).Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Casbin_CorrelatedFilter_BuildsATypedPredicate_WithPerRuleParameterNames()
    {
        using var service = new CasbinEnforcementService();
        var filter = Correlated("{\"c.country\": \"CH\"}");

        var first = service.BuildCorrelatedRowFilterPredicate(filter, DatabaseDialect.PostgreSql, 0);
        var second = service.BuildCorrelatedRowFilterPredicate(Correlated("{\"c.country\": \"DE\"}"), DatabaseDialect.PostgreSql, 1);

        first.Parameters.Keys.ShouldNotBe(second.Parameters.Keys);
        Should.NotThrow(() => first.And(second));
    }

    [Fact]
    public void Casbin_UnknownTargetDialect_Throws()
    {
        using var service = new CasbinEnforcementService();
        var filter = Correlated("{\"c.country\": \"CH\"}");

        Should.Throw<SecurityException>(() => service.BuildCorrelatedRowFilterPredicate(filter, null, 0));
        Should.Throw<SecurityException>(() => service.BuildCorrelatedRowFilterPredicate(filter, (DatabaseDialect)99, 0));
    }

    [Fact]
    public async System.Threading.Tasks.Task Casbin_StringPath_WithoutTargetDialect_FailsClosed()
    {
        using var service = new CasbinEnforcementService();
        var tenant = new TenantId("t1");
        var table = new TableIdentifier("erp", "main", "invoices");
        service.AddPolicies(tenant, [new CasbinRuleInput("S-1-5-21-U", table.ToString(), "read", CorrelatedRowFilter: Correlated("{\"c.country\": \"CH\"}"))]);

        var context = new SecurityEvaluationContext(
            UserSid: new Sid("S-1-5-21-U"), GroupSids: [], Tenant: tenant, TargetTable: table, RequestedColumns: ["id"],
            ClientIp: System.Net.IPAddress.Loopback, Timestamp: DateTimeOffset.UtcNow, PurposeId: null,
            Attributes: new Dictionary<string, object?> { ["action"] = "read" }, TargetDialect: null);

        // Fail closed: the evaluation throws; it never renders SQL Server text for an unknown source dialect.
        await Should.ThrowAsync<SecurityException>(async () => await service.EvaluatePolicyAsync(context));
    }
}
