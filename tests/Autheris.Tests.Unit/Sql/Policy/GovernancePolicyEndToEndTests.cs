using System.Collections.Immutable;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>
/// WP-X0l end to end on SQLite: the live decision producer (consent resolution, typed row policy, typed masks) feeds the assembly, and
/// <c>ISqlEngine.Compile</c> consumes the result. These tests were written after the code (guards, not a Red step).
/// </summary>
public sealed class GovernancePolicyEndToEndTests
{
    private static readonly TenantId Tenant = new("acme");
    private static readonly Sid User = new("S-1");
    private static readonly TableIdentifier OrdersId = new("d", "main", "orders");
    private static readonly TableIdentity Orders = new("main", "orders");

    private static readonly TableMetadata Meta = new()
    {
        Identifier = OrdersId,
        Table = new Table { SourceName = "ds", SchemaName = "main", TableName = "orders", SourceType = "SQLite", IsActive = true },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "INTEGER" }, new TableColumn { ColumnName = "tenant_id", DataType = "TEXT" },
            new TableColumn { ColumnName = "region", DataType = "TEXT" }, new TableColumn { ColumnName = "email", DataType = "TEXT" },
            new TableColumn { ColumnName = "ssn", DataType = "TEXT" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["email"] = new() { RuleType = "PARTIAL_MASK", KeepPrefix = 1, KeepSuffix = 0 } }
    };

    private static Consent EuConsent() => new()
    {
        TableIdentifier = OrdersId,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = User,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = [new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EU\"" }],
        ColumnRules =
        [
            new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "tenant_id", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask },
            new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Deny }
        ]
    };

    private static async Task<List<object?[]>> RunAsync(string sql, params Consent[] consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents));
        var accessPolicy = new TableAccessPolicy(repo, new ConsentResolutionService(),
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
            Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null, new GatewayOptions(), NullMandatoryRowFilterResolver.Instance,
            rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer()),
            maskPolicyFactory: new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(Options.Create(new GatewayOptions()))));
        var decision = await accessPolicy.DecideAsync(
            new TableAccessQuery(User, Tenant, new HashSet<Sid>(), new HashSet<string>(), Meta), CancellationToken.None);

        var catalog = new InMemoryTableCatalog([new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("id", "INTEGER"), new CatalogColumn("tenant_id", "TEXT"), new CatalogColumn("region", "TEXT"),
            new CatalogColumn("email", "TEXT"), new CatalogColumn("ssn", "TEXT")), "tenant_id", 1)], "main");
        var provider = Substitute.For<IGovernedCatalogProvider>();
        provider.GetCatalogAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ITableCatalog>(catalog));

        var policy = await new GovernancePolicyFactory(provider, Substitute.For<Autheris.Application.Interfaces.ITableMetadataRepository>(), Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions())).CreateAsync(
            new GovernedPolicyInput("ds", Tenant, new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = decision },
                new HashSet<TableIdentity>(), GovernedSubqueryStrategy.Correlated)
            { Metadata = new Dictionary<TableIdentity, TableMetadata> { [Orders] = Meta } },
            CancellationToken.None);
        var request = CompileRequestFactory.Create(policy, TargetSqlDialect.Sqlite, new WebSqlOptions(), isDml: false, 100, new HashSet<string>());

        var compiled = new FastSqlEngine().Compile(sql.AsMemory(), request, CancellationToken.None);
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE orders (id INTEGER, tenant_id TEXT COLLATE NOCASE, region TEXT, email TEXT, ssn TEXT);
                INSERT INTO orders VALUES (1, 'acme', 'EU', 'alice@x.example', '111'), (2, 'acme', 'US', 'bob@x.example', '222'),
                    (3, 'other', 'EU', 'carol@x.example', '333'), (4, 'ACME', 'EU', 'dave@x.example', '444');
                """;
            setup.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        new SqliteCompiledSqlBinder().Bind(command, compiled, new Dictionary<string, object?>());
        var rows = new List<object?[]>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    [Fact]
    public async Task ConsentRowFilter_TenantIsolation_MaskAndDeny_AreAllEnforcedFromOneAssembledPolicy()
    {
        var rows = await RunAsync("SELECT id, email, ssn FROM orders ORDER BY id", EuConsent());

        // Only the acme/EU row: the US row by the consent filter, 'other' and 'ACME' by the exact tenant comparison.
        rows.Count.ShouldBe(1);
        Convert.ToInt32(rows[0][0]).ShouldBe(1);
        rows[0][1].ShouldBe("*****");   // typed PARTIAL_MASK on SQLite: the constant-length mask of the legacy SQLite output
        rows[0][2].ShouldBeNull();                // Deny becomes a typed NULL
    }

    [Fact]
    public async Task NoConsent_DeniesTheTable()
    {
        await Should.ThrowAsync<System.Security.SecurityException>(async () => await RunAsync("SELECT id FROM orders"));
    }
}
