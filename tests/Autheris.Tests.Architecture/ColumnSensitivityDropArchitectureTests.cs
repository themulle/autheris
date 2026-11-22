using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-SIMPL-52 step 3: column sensitivity is derived from column classifications and masks from column mask overrides. The legacy
/// <c>is_sensitive</c> flag and <c>COLUMN_MASKING_RULES</c> table are gone; only migrations (frozen history) may name them.
/// </summary>
[Trait("Category", "Security")]
public sealed class ColumnSensitivityDropArchitectureTests
{
    private static readonly Regex Legacy = new("is_sensitive|IsSensitive|COLUMN_MASKING_RULES", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TableSensitivityLegacy = new(
        @"\bTableSensitivity\b|\b(?:Table|table|Meta|Metadata|BaseTable|existing|stored|candidate|row|item|entry|t|x)\s*\.\s*(?:Sensitivity|RequiresFourEyes|IsHighlySensitive|SensitivityRank|IsSensitivityHigh)\b|(?:FROM|JOIN|UPDATE|ALTER\s+TABLE)\s+(?:dbo\.)?TABLES\b[^\n;]*\b(?:sensitivity|requires_four_eyes)\b",
        RegexOptions.CultureInvariant);

    /// <summary>Derived, read-only external contract fields (JSON <c>isSensitive</c>, documented for API consumers); computed from classifications, never stored.</summary>
    private static readonly string[] DerivedContractFiles =
    [
        "src/Autheris.Api/Endpoints/CatalogDiscoveryApiHelpers.cs",
        "src/Autheris.Application/Mcp/Services/McpToolExecutionHandler.cs",
        "src/Autheris.Domain/Model/McpDatasetModels.cs"
    ];

    /// <summary>Columns that contain "key" but hold no key material: identifiers, ordinals, fingerprints, wrapped (KMS-encrypted) keys and key-set sizing.</summary>
    private static readonly HashSet<string> NonSecretKeyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "key_ref", "key_id", "key_fingerprint", "wrapped_key", "key_ordinal", "user_key", "asset_key", "normalized_key", "max_keys",
        "hmac_secret_protected", "key_set_strategy", "key_set_duration_seconds", "key_set_max_keys", "key_set_max_age_seconds", "source_key"
    };

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }

    [Fact]
    public void No_IsSensitive_reference_outside_migrations()
    {
        var src = Path.Combine(Root(), "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root(), f).Replace('\\', '/'))
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))
            .Where(f => !f.Contains("/Persistence/Migrations/", StringComparison.Ordinal))
            .Where(f => !DerivedContractFiles.Contains(f, StringComparer.Ordinal))
            .Where(f => Legacy.IsMatch(File.ReadAllText(Path.Combine(Root(), f))))
            .ToList();

        offenders.ShouldBeEmpty("the legacy column sensitivity flag and masking-rule table are dropped; derive from classifications");
    }

    [Fact]
    public void No_table_sensitivity_or_four_eyes_properties_outside_migrations()
    {
        var tableModels = File.ReadAllText(Path.Combine(Root(), "src", "Autheris.Domain", "Model", "TableModels.cs"));
        tableModels.ShouldNotContain("public TableSensitivity", Case.Sensitive);
        tableModels.ShouldNotContain("RequiresFourEyes", Case.Sensitive);
        tableModels.ShouldNotContain("IsHighlySensitive", Case.Sensitive);
        tableModels.ShouldNotContain("SensitivityRank", Case.Sensitive);
        tableModels.ShouldNotContain("IsSensitivityHigh", Case.Sensitive);

        var tableType = typeof(Autheris.Domain.Model.Table);
        var retiredMembers = new[] { "Sensitivity", "RequiresFourEyes", "IsHighlySensitive", "SensitivityRank", "IsSensitivityHigh" };
        foreach (var member in retiredMembers)
        {
            tableType.GetMember(member).ShouldBeEmpty($"Table must not declare retired member '{member}'.");
        }

        var src = Path.Combine(Root(), "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root(), f).Replace('\\', '/'))
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))
            .Where(f => !f.Contains("/Persistence/Migrations/", StringComparison.Ordinal))
            .Where(f => TableSensitivityLegacy.IsMatch(File.ReadAllText(Path.Combine(Root(), f))))
            .ToList();

        offenders.ShouldBeEmpty("table sensitivity and four-eyes flags are retired in favor of object protection classes");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Table_sensitivity_columns_dropped(string engine)
    {
        var tables = FoldShape(engine);
        tables["tables"].ShouldNotContain("sensitivity");
        tables["tables"].ShouldNotContain("requires_four_eyes");
    }

    [Fact]
    public void Derived_contract_files_only_expose_a_computed_value()
    {
        foreach (var file in DerivedContractFiles)
        {
            var text = File.ReadAllText(Path.Combine(Root(), file));
            text.ShouldNotContain("is_sensitive", Case.Insensitive);
            text.ShouldNotContain("COLUMN_MASKING_RULES", Case.Insensitive);
        }
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Mask_keys_never_persisted(string engine)
    {
        var tables = FoldShape(engine);
        tables.ShouldNotContainKey("column_masking_rules");
        tables["table_columns"].ShouldNotContain("is_sensitive");

        var offenders = tables
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(x => x.Column.Contains("key", StringComparison.OrdinalIgnoreCase) || x.Column.Contains("hmac", StringComparison.OrdinalIgnoreCase))
            .Where(x => !NonSecretKeyColumns.Contains(x.Column))
            .Select(x => $"{x.Table}.{x.Column}")
            .ToList();

        offenders.ShouldBeEmpty("a mask references its key by handle (key_ref / key_id) only; key material is never a column");
    }

    private static Dictionary<string, List<string>> FoldShape(string engine)
    {
        var migrations = Path.Combine(Root(), "src", "Autheris.Infrastructure", "Persistence", "Migrations");
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(migrations, "Shape", $"governance-shape.{engine}.v0001.json")));
        var tables = baseline.RootElement.GetProperty("tables").EnumerateArray().ToDictionary(
            t => t.GetProperty("name").GetString()!.ToLowerInvariant(),
            t => t.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!.ToLowerInvariant()).ToList(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(migrations, "Steps", engine), "*.shape.json").Order(StringComparer.Ordinal))
        {
            using var delta = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var op in delta.RootElement.GetProperty("ops").EnumerateArray())
            {
                var table = op.GetProperty("table").GetString()!.ToLowerInvariant();
                switch (op.GetProperty("op").GetString())
                {
                    case "addTable":
                        tables[table] = op.GetProperty("newTable").GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!.ToLowerInvariant()).ToList();
                        break;
                    case "dropTable":
                        tables.Remove(table);
                        break;
                    case "addColumn":
                        tables[table].Add(op.GetProperty("column").GetProperty("name").GetString()!.ToLowerInvariant());
                        break;
                    case "dropColumn":
                        tables[table].Remove(op.GetProperty("name").GetString()!.ToLowerInvariant());
                        break;
                }
            }
        }

        return tables;
    }
}
