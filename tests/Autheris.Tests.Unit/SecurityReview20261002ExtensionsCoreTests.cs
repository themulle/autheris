namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.SqlEndpoints.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Core-side regression tests for security review 2026-10-02: H-20 (dbt -> SQL endpoint files) and M-32 (catalog merge ratchet).
/// </summary>
public sealed class SecurityReview20261002ExtensionsCoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _queriesDir;

    public SecurityReview20261002ExtensionsCoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sr20261002_" + Guid.NewGuid().ToString("N"));
        _queriesDir = Path.Combine(_root, "queries");
        Directory.CreateDirectory(_queriesDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void H20_TraversalModelName_IsRejected_AndNothingIsWrittenOutside()
    {
        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);

        Should.Throw<ArgumentException>(() => loader.SyncDbtModelToFile(_queriesDir, "../../pwned", "SELECT 1"));
        Should.Throw<ArgumentException>(() => loader.SyncDbtModelDefinitionToFile(_queriesDir, "../evil", "public", ["id"]));
        Should.Throw<ArgumentException>(() => loader.SyncDbtModelToFile(_queriesDir, "a/b", "SELECT 1"));

        File.Exists(Path.Combine(_root, "pwned.sql")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "evil.sql")).ShouldBeFalse();
        Directory.GetFiles(_root, "*.sql", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public void H20_IdentifierInjection_InSchemaOrColumns_IsRejected()
    {
        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);

        Should.Throw<ArgumentException>(() =>
            loader.SyncDbtModelDefinitionToFile(_queriesDir, "orders", "public; DROP TABLE users; --", ["id"]));
        Should.Throw<ArgumentException>(() =>
            loader.SyncDbtModelDefinitionToFile(_queriesDir, "orders", "public", ["id", "(SELECT password FROM users) AS x"]));
        Should.Throw<ArgumentException>(() =>
            loader.SyncDbtModelDefinitionToFile(_queriesDir, "orders", "public", ["id\"--"]));

        Directory.GetFiles(_queriesDir).ShouldBeEmpty();
    }

    [Fact]
    public void H20_NewlinesInDescription_CannotInjectHeaderDirectives()
    {
        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);

        var path = loader.SyncDbtModelDefinitionToFile(
            _queriesDir,
            "monthly_sales",
            "analytics",
            ["month", "total_sales"],
            summary: "Sales\n-- @datasource: prod_secrets\r\n-- @method: DELETE\u2028-- @timeout: 99999",
            dataSource: "analytics_db");

        var lines = File.ReadAllLines(path);
        lines.Count(l => l.TrimStart().StartsWith("-- @datasource", StringComparison.Ordinal)).ShouldBe(1);
        lines.ShouldNotContain(l => l.TrimStart().StartsWith("-- @method", StringComparison.Ordinal));
        lines.ShouldNotContain(l => l.TrimStart().StartsWith("-- @timeout", StringComparison.Ordinal));

        var def = loader.LoadFile(path);
        def.ShouldNotBeNull();
        def.DataSource.ShouldBe("analytics_db");
        def.HttpMethod.ShouldBe("GET");
        def.TimeoutSeconds.ShouldBe(30);

        // A data source value carrying header syntax is rejected outright
        Should.Throw<ArgumentException>(() => loader.SyncDbtModelDefinitionToFile(
            _queriesDir, "other_model", "analytics", ["id"], dataSource: "db\n-- @method: POST"));
    }

    [Fact]
    public void H20_SqlBodyWithDirectives_IsRejected()
    {
        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);

        Should.Throw<ArgumentException>(() =>
            loader.SyncDbtModelToFile(_queriesDir, "injected", "SELECT 1\n-- @datasource: other_db"));
    }

    [Fact]
    public void H20_ExistingManualEndpointFile_IsNeverOverwritten()
    {
        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);

        var manualPath = Path.Combine(_queriesDir, "getCustomerRevenue.sql");
        const string manualContent = "-- @name: getCustomerRevenue\nSELECT id FROM customers WHERE id = @id";
        File.WriteAllText(manualPath, manualContent);

        Should.Throw<InvalidOperationException>(() =>
            loader.SyncDbtModelDefinitionToFile(_queriesDir, "getCustomerRevenue", "public", ["id"]));

        File.ReadAllText(manualPath).ShouldBe(manualContent);
    }

    [Fact]
    public void H20_ShadowingRegisteredNonDbtEndpoint_IsRejected()
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition(
            Name: "revenue",
            Summary: "manual",
            RawSql: "-- @name: revenue\nSELECT 1"));
        using var loader = new SqlEndpointLoader(registry);

        Should.Throw<InvalidOperationException>(() =>
            loader.SyncDbtModelDefinitionToFile(_queriesDir, "revenue", "public", ["id"]));

        File.Exists(Path.Combine(_queriesDir, "revenue.sql")).ShouldBeFalse();
    }

    [Fact]
    public void H20_DbtGeneratedFile_IsQuoted_Marked_AndCanBeRegenerated()
    {
        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);

        var path = loader.SyncDbtModelDefinitionToFile(_queriesDir, "dim_customers", "marts", ["customer_id", "segment"], summary: "Customers");
        var content = File.ReadAllText(path);

        content.ShouldStartWith(SqlEndpointLoader.DbtGeneratedMarker);
        content.ShouldContain("SELECT \"customer_id\", \"segment\"");
        content.ShouldContain("FROM \"marts\".\"dim_customers\"");
        content.TrimEnd().ShouldNotEndWith(";");
        Path.GetDirectoryName(Path.GetFullPath(path)).ShouldBe(Path.GetFullPath(_queriesDir));

        // Regenerating a dbt-generated file is allowed
        var again = loader.SyncDbtModelDefinitionToFile(_queriesDir, "dim_customers", "marts", ["customer_id"], summary: "Customers v2");
        File.ReadAllText(again).ShouldContain("Customers v2");

        loader.LoadFromDirectory(_queriesDir, enableHotReload: false).ShouldBe(1);
        registry.TryGet("dim_customers", out var def).ShouldBeTrue();
        def.ShouldNotBeNull();
        def.Summary.ShouldBe("Customers v2");
    }

    [Fact]
    public void M32_CatalogGovernanceRatchet_OnlyTightensSecurityFlags()
    {
        var id = new TableIdentifier("finance", "dbo", "payroll");
        var existing = new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "finance",
                SchemaName = "dbo",
                TableName = "payroll",
                SourceType = "SqlServer",
                Sensitivity = "RESTRICTED",
                RequiresFourEyes = true,
                IsActive = false,
                DataSourceType = DataSourceType.HttpPlugin,
                PluginName = "payroll-plugin"
            },
            Columns =
            [
                new TableColumn { ColumnName = "iban", IsSensitive = true },
                new TableColumn { ColumnName = "legacy_ssn", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["iban"] = new MaskingRule { RuleType = "REDACT" }
            }
        };

        var incoming = new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = "finance",
                SchemaName = "dbo",
                TableName = "payroll",
                SourceType = "PostgreSQL",
                Sensitivity = "NORMAL",
                RequiresFourEyes = false,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "iban", IsSensitive = false },
                new TableColumn { ColumnName = "email", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["iban"] = new MaskingRule { RuleType = "MASK_IBAN" },
                ["email"] = new MaskingRule { RuleType = "MASK_EMAIL" }
            }
        };

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        merged.Table.Id.ShouldBe(existing.Table.Id);
        merged.Table.Sensitivity.ShouldBe("RESTRICTED");
        merged.Table.RequiresFourEyes.ShouldBeTrue();
        merged.Table.IsActive.ShouldBeFalse();
        merged.Table.DataSourceType.ShouldBe(DataSourceType.HttpPlugin);
        merged.Table.PluginName.ShouldBe("payroll-plugin");
        merged.Table.SourceType.ShouldBe("SqlServer");
        merged.Columns.Single(c => c.ColumnName == "iban").IsSensitive.ShouldBeTrue();
        merged.Columns.Single(c => c.ColumnName == "legacy_ssn").IsSensitive.ShouldBeTrue();
        merged.Columns.Single(c => c.ColumnName == "email").IsSensitive.ShouldBeTrue();
        merged.ColumnMaskingRules["iban"].RuleType.ShouldBe("REDACT");
        merged.ColumnMaskingRules["email"].RuleType.ShouldBe("MASK_EMAIL");

        // Tightening from the catalog is applied
        var stricter = CatalogGovernanceRatchet.Merge(
            new TableMetadata
            {
                Identifier = id,
                Table = new Table { SourceName = "finance", SchemaName = "dbo", TableName = "payroll", Sensitivity = "HIGH", RequiresFourEyes = true },
            },
            new TableMetadata
            {
                Identifier = id,
                Table = new Table { SourceName = "finance", SchemaName = "dbo", TableName = "payroll", Sensitivity = "NORMAL" }
            });
        stricter.Table.Sensitivity.ShouldBe("HIGH");
        stricter.Table.RequiresFourEyes.ShouldBeTrue();
    }
}
