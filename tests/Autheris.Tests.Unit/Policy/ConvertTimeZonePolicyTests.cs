using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Lint;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>ST-130-8 / BC-130-2: admin-authored policy predicates never admit <c>convert_timezone</c> (no time zone validator runs there).</summary>
[Trait("Category", "Security")]
public sealed class ConvertTimeZonePolicyTests
{
    private const string Predicate = "convert_timezone('UTC', 'Europe/Berlin', created_at) > TIMESTAMP '2026-01-01 00:00:00'";

    private static TableMetadata Events(string sourceType = "PostgreSQL") => new()
    {
        Table = new Table { SourceType = sourceType, SourceName = "erp", SchemaName = "main", TableName = "events" },
        Identifier = new TableIdentifier("erp", "main", "events"),
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "created_at", DataType = "timestamp" },
            new TableColumn { ColumnName = "region", DataType = "varchar" }
        ]
    };

    private static RowPolicyFactory Factory()
    {
        var services = new ServiceCollection();
        services.AddAutherisGovernance(new GatewayOptions());
        services.AddSingleton<ISqlEngine, FastSqlEngine>();
        return services.BuildServiceProvider().GetRequiredService<RowPolicyFactory>();
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    public void RowPolicyFactory_RejectsAPredicateWithTheFunction_AndStillParsesAnOrdinaryOne(DatabaseDialect dialect)
    {
        var factory = Factory();
        var table = new TableIdentity("main", "events");
        string[] columns = ["id", "created_at", "region"];

        factory.ParseAdminPredicate("lower(region) = 'eu'", table, columns, dialect).ShouldNotBeNull();
        Should.Throw<PolicyParseException>(() => factory.ParseAdminPredicate(Predicate, table, columns, dialect));
        Should.Throw<PolicyParseException>(() => factory.ParseAdminPredicate("CONVERT_TIMEZONE('UTC', 'Europe/Berlin', created_at) IS NOT NULL", table, columns, dialect));
    }

    [Fact]
    public void PolicyParserSetup_DoesNotAdmitTheFunction_EvenWhenConfiguredAsAnAdditionalFunction()
    {
        foreach (var additional in new IEnumerable<string>?[] { null, ["convert_timezone"], ["CONVERT_TIMEZONE"] })
        {
            PolicyParserSetup.TryCreateContext(Events(), new InMemoryTableCatalog([], "main"), additional, 0, out var context).ShouldBeTrue();
            context.AllowedFunctions.ShouldNotContain("convert_timezone");
            context.AllowedFunctions.ShouldContain("lower");
        }
    }

    [Fact]
    public void PolicyLint_ReportsAPredicateWithTheFunction()
    {
        var profile = new AccessProfile
        {
            ProfileId = "p-tz",
            Name = "p-tz",
            TenantId = TenantId.LegacySingleTenant,
            TargetTables = ["erp.main.events"],
            RowFilterPredicate = Predicate
        };
        var input = new PolicyLintInput([Events()], [profile], [], []);

        var report = new PolicyPredicateLinter().Lint(input);

        report.Findings.Single().PolicyId.ShouldBe("p-tz");
    }
}
