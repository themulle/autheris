namespace Autheris.Tests.Unit.Databricks.Provisioning;

using System.Collections.Immutable;
using Autheris.Application.Governance.Provisioning;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1122: only a consent that Unity Catalog can express exactly as a table-wide SELECT is provisionable; every other or
/// unknown construct stays gateway-only with a reason code (never widen).
/// </summary>
[Trait("Category", "Security")]
public sealed class UcProvisioningEligibilityTests
{
    private const string Source = "dbx";
    private static readonly Guid TableId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Plain_allow_data_grant_on_one_table_is_provisionable_as_lower_case_select()
    {
        var result = Evaluate(Consent(), Table(schema: "Sales", table: "Orders"));

        result.IsProvisionable.ShouldBeTrue(result.ReasonCode);
        result.Target!.FullName.ShouldBe("main.sales.orders");
        result.Target.SchemaFullName.ShouldBe("main.sales");
        result.Target.Privilege.ShouldBe(UcPrivileges.Select);
        result.ReasonCode.ShouldBeNull();
    }

    [Theory]
    [InlineData(GranteeType.User)]
    [InlineData(GranteeType.Group)]
    [InlineData(GranteeType.ServicePrincipal)]
    public void Every_known_grantee_type_is_provisionable(GranteeType type)
    {
        Evaluate(Consent(c => Copy(c, granteeType: type)), Table()).IsProvisionable.ShouldBeTrue();
    }

    public static TheoryData<string, Func<Consent, Consent>> ConsentConstructs() => new()
    {
        { UcReasonCodes.GrantKindNotSupported, c => Copy(c, grantKind: PermissionGrantKind.Unmask) },
        { UcReasonCodes.GrantKindNotSupported, c => Copy(c, grantKind: PermissionGrantKind.Catalog) },
        { UcReasonCodes.GrantKindNotSupported, c => Copy(c, grantKind: PermissionGrantKind.Unknown) },
        { UcReasonCodes.GrantKindNotSupported, c => Copy(c, grantKind: (PermissionGrantKind)99) },
        { UcReasonCodes.EffectNotSupported, c => Copy(c, effect: ConsentEffect.Deny) },
        { UcReasonCodes.EffectNotSupported, c => Copy(c, effect: (ConsentEffect)42) },
        { UcReasonCodes.GranteeNotSupported, c => Copy(c, granteeType: (GranteeType)8) },
        { UcReasonCodes.GranteeNotSupported, c => Copy(c, granteeSid: null) },
        { UcReasonCodes.PatternGrant, c => Copy(c, objectPattern: Source + ".sales.*") },
        { UcReasonCodes.ColumnSetNotSupported, c => Copy(c, columnSetMode: ColumnSetMode.Explicit) },
        { UcReasonCodes.ColumnSetNotSupported, c => Copy(c, columnSetMode: (ColumnSetMode)7) },
        { UcReasonCodes.ColumnSetNotSupported, c => Copy(c, targetColumns: ["amount"]) },
        { UcReasonCodes.ColumnRules, c => With(c, x => x.ColumnRules = [new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }]) },
        { UcReasonCodes.RowFilters, c => With(c, x => x.RowFilters = [new ConsentRowFilter { ColumnName = "region", ValueJson = "[\"EU\"]" }]) },
        { UcReasonCodes.RowScope, c => With(c, x => x.RowScope = [new GrantRowScopeEntry("region", new Dictionary<string, System.Text.Json.JsonElement>())]) },
        { UcReasonCodes.WriteRules, c => With(c, x => x.WriteRules = [new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows)]) },
        { UcReasonCodes.TenantNotProvisioned, c => Copy(c, tenant: new TenantId("other")) },
    };

    [Theory]
    [MemberData(nameof(ConsentConstructs))]
    public void Consent_constructs_that_uc_cannot_express_are_unsupported(string reason, Func<Consent, Consent> change)
    {
        var result = Evaluate(change(Consent()), Table());

        result.IsProvisionable.ShouldBeFalse();
        result.Target.ShouldBeNull();
        result.ReasonCode.ShouldBe(reason);
    }

    public static TheoryData<string, Func<TableMetadata, TableMetadata>> TableConstructs() => new()
    {
        { UcReasonCodes.TableRowPolicy, t => t with { Table = t.Table with { RlsExpression = "region = 'EU'" } } },
        { UcReasonCodes.TableRowPolicy, t => t with { Table = t.Table with { RequiredFilterColumns = ["region"] } } },
        { UcReasonCodes.TableRowPolicy, t => t with { Table = t.Table with { RequiredFilterColumnGroups = [["region"]] } } },
        { UcReasonCodes.TableRowPolicy, t => t with { Columns = [.. t.Columns, Column("tenant_id")] } },
        { UcReasonCodes.AggregationPolicy, t => t with { MinGroupSize = 5 } },
        { UcReasonCodes.AggregationPolicy, t => t with { Table = t.Table with { SmallGroupStrategy = SmallGroupStrategy.Suppress } } },
        { UcReasonCodes.HiddenColumns, t => t with { Columns = [.. t.Columns, new TableColumn { ColumnName = "secret_note", DiscoveryHidden = true }] } },
        { UcReasonCodes.ColumnProtection, t => t with { ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["amount"] = new() } } },
        { UcReasonCodes.ColumnProtection, t => t with { ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>> { ["amount"] = [] } } },
        { UcReasonCodes.ColumnProtection, t => t with { ColumnMaskOverrides = new Dictionary<string, IReadOnlyList<ColumnMaskOverride>> { ["amount"] = [] } } },
        { UcReasonCodes.ObjectKindNotSupported, t => t with { Table = t.Table with { ObjectKind = DiscoveryObjectKind.MetricView } } },
        { UcReasonCodes.ObjectKindNotSupported, t => t with { Table = t.Table with { ObjectKind = DiscoveryObjectKind.Procedure } } },
        { UcReasonCodes.CatalogMismatch, t => t with { Table = t.Table with { PhysicalDatabaseName = "other_catalog" } } },
        { UcReasonCodes.DataSourceMismatch, t => t with { Table = t.Table with { SourceType = "PostgreSQL" } } },
        { UcReasonCodes.DataSourceMismatch, t => t with { Table = t.Table with { DataSourceType = DataSourceType.HttpDeclarative } } },
        { UcReasonCodes.TableNotFound, t => t with { Table = t.Table with { IsActive = false } } },
        { UcReasonCodes.SchemaNotAllowed, t => Table(schema: "finance") },
        { UcReasonCodes.InvalidIdentifier, t => Table(table: "orders-2024") },
    };

    [Theory]
    [MemberData(nameof(TableConstructs))]
    public void Table_constructs_the_gateway_enforces_beyond_the_consent_are_unsupported(string reason, Func<TableMetadata, TableMetadata> change)
    {
        var table = change(Table());
        var consent = Consent(c => Copy(c, table: table.Identifier));

        Evaluate(consent, table).ReasonCode.ShouldBe(reason);
    }

    [Fact]
    public void Missing_table_metadata_is_unsupported()
    {
        Evaluate(Consent(), table: null).ReasonCode.ShouldBe(UcReasonCodes.TableNotFound);
    }

    [Fact]
    public void Table_of_another_data_source_is_unsupported()
    {
        var table = Table(domain: "other");
        Evaluate(Consent(c => Copy(c, table: table.Identifier)), table).ReasonCode.ShouldBe(UcReasonCodes.DataSourceMismatch);
    }

    [Fact]
    public void Consent_for_another_table_than_the_metadata_is_unsupported()
    {
        Evaluate(Consent(c => Copy(c, table: new TableIdentifier(Source, "sales", "customers"))), Table())
            .ReasonCode.ShouldBe(UcReasonCodes.DataSourceMismatch);
        Evaluate(Consent(c => Copy(c, tableId: Guid.NewGuid())), Table())
            .ReasonCode.ShouldBe(UcReasonCodes.DataSourceMismatch);
        Evaluate(Consent(c => Copy(c, dataSource: "other")), Table())
            .ReasonCode.ShouldBe(UcReasonCodes.DataSourceMismatch);
    }

    [Theory]
    [InlineData(ProtectionClass.Confidential)]
    [InlineData(ProtectionClass.Secret)]
    [InlineData((ProtectionClass)17)]
    public void Table_at_or_above_confidential_is_unsupported(ProtectionClass tableClass)
    {
        Evaluate(Consent(), Table(), Protection(tableClass: tableClass)).ReasonCode.ShouldBe(UcReasonCodes.ProtectionClass);
    }

    [Fact]
    public void Confidential_column_on_an_internal_table_is_unsupported()
    {
        var protection = Protection(columns: new() { ["id"] = new(ProtectionClass.Internal, null), ["amount"] = new(ProtectionClass.Confidential, null) });
        Evaluate(Consent(), Table(), protection).ReasonCode.ShouldBe(UcReasonCodes.ProtectionClass);
    }

    [Fact]
    public void Mask_category_on_table_or_column_is_unsupported()
    {
        Evaluate(Consent(), Table(), Protection(tableMask: "pii")).ReasonCode.ShouldBe(UcReasonCodes.ColumnProtection);
        var columns = new Dictionary<string, ColumnProtection> { ["id"] = new(ProtectionClass.Public, null), ["amount"] = new(ProtectionClass.Public, "finance") };
        Evaluate(Consent(), Table(), Protection(columns: columns)).ReasonCode.ShouldBe(UcReasonCodes.ColumnProtection);
    }

    [Fact]
    public void Unknown_protection_or_unknown_column_protection_is_unsupported()
    {
        Evaluate(Consent(), Table(), protection: null, useDefaultProtection: false).ReasonCode.ShouldBe(UcReasonCodes.ProtectionUnknown);
        var partial = new Dictionary<string, ColumnProtection> { ["id"] = new(ProtectionClass.Public, null) };
        Evaluate(Consent(), Table(), Protection(columns: partial)).ReasonCode.ShouldBe(UcReasonCodes.ProtectionUnknown);
    }

    [Fact]
    public void Data_source_without_provisioning_section_is_unsupported()
    {
        var databricks = new DatabricksDataSourceOptions { Catalog = "main" };
        UcProvisioningEligibility.Evaluate(Source, databricks, Consent(), Table(), Protection())
            .ReasonCode.ShouldBe(UcReasonCodes.DataSourceNotProvisioned);
        UcProvisioningEligibility.Evaluate(Source, null, Consent(), Table(), Protection())
            .ReasonCode.ShouldBe(UcReasonCodes.DataSourceNotProvisioned);
    }

    [Fact]
    public void Tenant_comparison_ignores_case_but_no_other_tenant_is_provisioned()
    {
        Evaluate(Consent(c => Copy(c, tenant: new TenantId("ACME"))), Table()).IsProvisionable.ShouldBeTrue();
        Evaluate(Consent(c => Copy(c, tenant: new TenantId("acme-2"))), Table()).ReasonCode.ShouldBe(UcReasonCodes.TenantNotProvisioned);
    }

    // ---- builders ----

    private static UcEligibility Evaluate(Consent consent, TableMetadata? table, EffectiveProtection? protection = null, bool useDefaultProtection = true) =>
        UcProvisioningEligibility.Evaluate(Source, Databricks(), consent, table, protection ?? (useDefaultProtection && table is not null ? Protection(table) : null));

    private static DatabricksDataSourceOptions Databricks() => new()
    {
        Host = "adb-123.azuredatabricks.net",
        Catalog = "Main",
        Provisioning = new DatabricksProvisioningOptions
        {
            Mode = DatabricksProvisioningMode.Enforce,
            Tenant = "acme",
            AllowedSchemas = ["main.sales"]
        }
    };

    private static TableColumn Column(string name) => new() { ColumnName = name, TableId = TableId };

    private static TableMetadata Table(string domain = Source, string schema = "sales", string table = "orders") => new()
    {
        Table = new Table { Id = TableId, SourceType = "Databricks", SourceName = domain, SchemaName = schema, TableName = table },
        Identifier = new TableIdentifier(domain, schema, table),
        Columns = [Column("id"), Column("amount")]
    };

    private static EffectiveProtection Protection(TableMetadata table) =>
        Protection(columns: table.Columns.ToDictionary(c => c.ColumnName, _ => new ColumnProtection(ProtectionClass.Internal, null), StringComparer.OrdinalIgnoreCase));

    private static EffectiveProtection Protection(
        ProtectionClass tableClass = ProtectionClass.Internal,
        string? tableMask = null,
        Dictionary<string, ColumnProtection>? columns = null) =>
        new(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            null,
            tableClass,
            tableClass,
            tableMask,
            columns ?? new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = new(ProtectionClass.Internal, null),
                ["amount"] = new(ProtectionClass.Internal, null)
            },
            CatalogVisibilityMode.OnRequest);

    private static Consent Consent(Func<Consent, Consent>? change = null)
    {
        var consent = new Consent
        {
            TableId = TableId,
            TableIdentifier = new TableIdentifier(Source, "sales", "orders"),
            DataSource = Source,
            ColumnSetMode = ColumnSetMode.AllNonHidden,
            Effect = ConsentEffect.Allow,
            TenantId = new TenantId("acme"),
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid("8b0c4b5e-6f0e-4a63-9a5e-1b2a4f9d7c11"),
            GrantKind = PermissionGrantKind.Data,
            ValidFrom = DateTimeOffset.UnixEpoch,
            ValidTo = DateTimeOffset.UnixEpoch.AddDays(30)
        };
        return change is null ? consent : change(consent);
    }

    private static Consent With(Consent consent, Action<Consent> mutate)
    {
        mutate(consent);
        return consent;
    }

    private static Consent Copy(
        Consent c,
        PermissionGrantKind? grantKind = null,
        ConsentEffect? effect = null,
        GranteeType? granteeType = null,
        Optional<Sid?> granteeSid = default,
        string? objectPattern = null,
        ColumnSetMode? columnSetMode = null,
        IReadOnlyList<string>? targetColumns = null,
        TenantId? tenant = null,
        TableIdentifier? table = null,
        Guid? tableId = null,
        string? dataSource = null) => new()
        {
            Id = c.Id,
            TableId = objectPattern is null ? tableId ?? c.TableId : Guid.Empty,
            TableIdentifier = table ?? c.TableIdentifier,
            ObjectPattern = objectPattern ?? c.ObjectPattern,
            DataSource = dataSource ?? c.DataSource,
            ColumnSetMode = columnSetMode ?? c.ColumnSetMode,
            Effect = effect ?? c.Effect,
            TenantId = tenant ?? c.TenantId,
            GranteeType = granteeType ?? c.GranteeType,
            GranteeSid = granteeSid.HasValue ? granteeSid.Value : c.GranteeSid,
            GrantKind = grantKind ?? c.GrantKind,
            TargetColumns = targetColumns ?? c.TargetColumns,
            ValidFrom = c.ValidFrom,
            ValidTo = c.ValidTo
        };

    /// <summary>Distinguishes "not passed" from an explicit null for <see cref="Copy"/>.</summary>
    private readonly record struct Optional<T>(T Value)
    {
        public bool HasValue { get; } = true;

        public static implicit operator Optional<T>(T value) => new(value);
    }
}
