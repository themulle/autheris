namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static CallerAttributeTestSupport;

/// <summary>PLAN-GOV-129 test 17, ST-129-24 (A-9, BC-129-11, SEC-129-16): save-time validation through one seam.</summary>
[Trait("Category", "Security")]
public sealed class ConsentRowFilterAttributeValidatorTests
{
    private static readonly TableIdentifier Orders = new("hr_db", "main", "orders");

    private static ConsentRowFilter Attr(string op = "EQ", string name = "cost_center", string json = "[]", string source = "USER_ATTRIBUTE", RowFilterType type = RowFilterType.SimpleColumnPredicate) => new()
    {
        ColumnName = "cost_center", Operator = op, UserAttribute = name, ValueJson = json, ValueSource = source, FilterType = type
    };

    private static Consent ConsentOf(params ConsentRowFilter[] filters) => ConsentOn(Orders, Tenant, filters);

    private static Consent ConsentOn(TableIdentifier table, TenantId tenant, params ConsentRowFilter[] filters) => new()
    {
        TableIdentifier = table, TenantId = tenant, GranteeType = GranteeType.Role, RoleName = "analyst", Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1), RowFilters = filters
    };

    private static CallerAttributeSnapshot Snapshot(CallerAttributeCardinality cardinality = CallerAttributeCardinality.List) =>
        new(1, [Definition("cost_center", "claims", CallerAttributeType.Text, cardinality, 50, 60)], []);

    private static TableMetadata Meta(DataSourceType type = DataSourceType.Sql) => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "hr_db", SchemaName = "main", TableName = "orders", SourceType = "SQLite", DataSourceType = type },
        Columns = [new TableColumn { ColumnName = "cost_center", DataType = "TEXT" }]
    };

    private static void Registry(Consent c, CallerAttributeSnapshot? snapshot = null, bool enabled = true, TableMetadata? meta = null) =>
        ConsentRowFilterAttributeValidator.ValidateAgainstRegistry(c, snapshot ?? Snapshot(), Options(enabled), meta ?? Meta());

    [Fact]
    public void ValidFilters_Pass()
    {
        ConsentRowFilterAttributeValidator.ValidateShape([Attr(), Attr("IN"), Attr("NEQ")]);
        Registry(ConsentOf(Attr("IN")));
    }

    [Theory]
    [InlineData("STATIC")]
    [InlineData("")]
    [InlineData("user_attribute ")]
    public void ST_129_24_ValueSourceOutsideTheTwoKnown_IsRefused(string source) =>
        Should.Throw<InvalidOperationException>(() => ConsentRowFilterAttributeValidator.ValidateShape([Attr(source: source)]));

    [Theory]
    [InlineData("LT")]
    [InlineData("LIKE")]
    [InlineData("GTE")]
    public void ST_129_24_WrongOperator_IsRefused(string op) =>
        Should.Throw<InvalidOperationException>(() => ConsentRowFilterAttributeValidator.ValidateShape([Attr(op)]));

    [Theory]
    [InlineData("Cost")]
    [InlineData("cost__center")]
    [InlineData("")]
    [InlineData(null)]
    public void BadAttributeName_IsRefused(string? name) =>
        Should.Throw<InvalidOperationException>(() => ConsentRowFilterAttributeValidator.ValidateShape([Attr(name: name!)]));

    [Fact]
    public void ST_129_24_NonEmptyValueJson_IsRefused_ForAnAttributeFilter()
    {
        Should.Throw<InvalidOperationException>(() => ConsentRowFilterAttributeValidator.ValidateShape([Attr(json: "\"x\"")]));
        ConsentRowFilterAttributeValidator.ValidateShape([Attr(json: "")]);
        ConsentRowFilterAttributeValidator.ValidateShape([Attr(json: " [] ")]);
    }

    [Theory]
    [InlineData(RowFilterType.SubqueryCorrelated)]
    [InlineData(RowFilterType.CrossSourceSetFilter)]
    public void CorrelatedAndCrossSourceFilters_CannotCarryAnAttribute(RowFilterType type) =>
        Should.Throw<InvalidOperationException>(() => ConsentRowFilterAttributeValidator.ValidateShape([Attr(type: type)]));

    [Fact]
    public void Message_NamesTheRule_NeverAValue()
    {
        var ex = Should.Throw<InvalidOperationException>(() => ConsentRowFilterAttributeValidator.ValidateShape([Attr(json: "\"secret-value\"")]));
        ex.Message.ShouldNotContain("secret-value");
        ex.Message.ShouldContain("cost_center");
    }

    [Fact]
    public void Registry_UnknownAttribute_IsRefused() =>
        Should.Throw<InvalidOperationException>(() => Registry(ConsentOf(Attr(name: "other")))).Message.ShouldContain("not registered");

    [Fact]
    public void Registry_AttributeOfAnotherTenant_IsRefused()
    {
        var consent = ConsentOn(Orders, new TenantId("tenant_b"), Attr());
        Should.Throw<InvalidOperationException>(() => Registry(consent));
    }

    [Fact]
    public void Registry_ListAttribute_WithEq_IsRefused_ScalarWithEqPasses()
    {
        Should.Throw<InvalidOperationException>(() => Registry(ConsentOf(Attr("EQ"))));
        Registry(ConsentOf(Attr("EQ")), Snapshot(CallerAttributeCardinality.Scalar));
    }

    [Fact]
    public void Registry_NonSqlOrUnknownTable_IsRefused()
    {
        Should.Throw<InvalidOperationException>(() => Registry(ConsentOf(Attr("IN")), meta: Meta(DataSourceType.HttpDeclarative)));
        Should.Throw<InvalidOperationException>(() => ConsentRowFilterAttributeValidator.ValidateAgainstRegistry(ConsentOf(Attr("IN")), Snapshot(), Options(), null));
    }

    [Fact]
    public void Registry_Disabled_RefusesAttributeFilters() =>
        Should.Throw<InvalidOperationException>(() => Registry(ConsentOf(Attr("IN")), enabled: false));

    [Fact]
    public void LiteralFilters_NeedNoRegistry()
    {
        var literal = new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueJson = "\"EU\"" };
        ConsentRowFilterAttributeValidator.ValidateAgainstRegistry(ConsentOf(literal), CallerAttributeSnapshot.Empty, Options(false), null);
    }

    private static CallerAttributeSnapshot SnapshotWithSource(string table = "person_attributes") => new(1,
    [
        Definition("dept", "attribute-table", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60,
            ("dataSource", "hr_db"), ("schema", "main"), ("table", table), ("subjectKeyKind", "EntraObjectId"), ("subjectKeyColumn", "person_oid"), ("valueColumn", "cost_center"), ("tenantColumn", "tenant_id"))
    ], []);

    [Fact]
    public void A10_AConsentOnTheAttributeSourceTable_IsRefused_UnlessAllowListed()
    {
        var consent = ConsentOn(new TableIdentifier("hr_db", "main", "person_attributes"), Tenant);
        ConsentRowFilterAttributeValidator.IsAttributeSource(consent, SnapshotWithSource(), Options()).ShouldBeTrue();
        ConsentRowFilterAttributeValidator.IsAttributeSource(consent, SnapshotWithSource(), new CallerAttributeOptions { Enabled = true, AllowAttributeSourceInCatalog = ["MAIN.PERSON_ATTRIBUTES"] }).ShouldBeFalse();
        ConsentRowFilterAttributeValidator.IsAttributeSource(ConsentOf(), SnapshotWithSource(), Options()).ShouldBeFalse();
    }

    // ---- the decorator ------------------------------------------------------------------------------------------------------------

    private static (AttributeValidatingConsentRepository Repo, IConsentRepository Inner) Decorator(CallerAttributeSnapshot? snapshot = null, bool enabled = true, TableMetadata? meta = null)
    {
        var inner = Substitute.For<IConsentRepository>();
        inner.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Consent>()));
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(snapshot ?? Snapshot());
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(meta ?? Meta());
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = Options(enabled) });
        return (new AttributeValidatingConsentRepository(inner, registry, tables, options), inner);
    }

    [Fact]
    public async Task Decorator_Create_ValidatesBeforeTheInnerStores()
    {
        var (repo, inner) = Decorator();
        await Should.ThrowAsync<InvalidOperationException>(() => repo.CreateConsentAsync(ConsentOf(Attr(name: "unknown")), default));
        await inner.DidNotReceive().CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>());

        await repo.CreateConsentAsync(ConsentOf(Attr("IN")), default);
        await inner.Received(1).CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decorator_Replace_ValidatesEveryNewConsent()
    {
        var (repo, inner) = Decorator();
        await Should.ThrowAsync<InvalidOperationException>(() => repo.ReplaceConsentsAsync([], [ConsentOf(Attr("IN")), ConsentOf(Attr("LT"))], new Sid("S-1"), "r"));
        await inner.DidNotReceive().ReplaceConsentsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<Sid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<AuditLogEntry>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decorator_ConsentOnAnAttributeSourceTable_IsRefused()
    {
        var (repo, inner) = Decorator(SnapshotWithSource());
        var consent = ConsentOn(new TableIdentifier("hr_db", "main", "person_attributes"), Tenant);
        await Should.ThrowAsync<InvalidOperationException>(() => repo.CreateConsentAsync(consent, default));
        await inner.DidNotReceive().CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decorator_Disabled_AllowsLiteralConsents_AndRefusesAttributes()
    {
        var (repo, inner) = Decorator(enabled: false);
        await repo.CreateConsentAsync(ConsentOf(new ConsentRowFilter { ColumnName = "r", ValueJson = "\"x\"" }), default);
        await Should.ThrowAsync<InvalidOperationException>(() => repo.CreateConsentAsync(ConsentOf(Attr("IN")), default));
        await inner.Received(1).CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decorator_ReadsPassThrough()
    {
        var (repo, inner) = Decorator();
        await repo.GetConsentByIdAsync(Guid.NewGuid());
        await inner.Received(1).GetConsentByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ---- the repositories (shape rules in Consent.Validate: every engine, every writer) -----------------------------------------------------

    [Fact]
    public async Task ST_129_24_DirectRepository_Create_RefusesTheShapeViolations_OnSqlite()
    {
        using var store = GovernanceStoreFactory.CreateSqlite(
            Substitute.For<IEpochValidationService>(),
            Microsoft.Extensions.Options.Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=cv_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));
        IConsentRepository repo = store;
        await Should.ThrowAsync<InvalidOperationException>(() => repo.CreateConsentAsync(ConsentOf(Attr(source: "STATIC")), default));
        await Should.ThrowAsync<InvalidOperationException>(() => repo.CreateConsentAsync(ConsentOf(Attr(json: "\"x\"")), default));
        await Should.ThrowAsync<InvalidOperationException>(() => repo.ReplaceConsentsAsync([], [ConsentOf(Attr("LIKE"))], new Sid("S-1"), "r"));
    }
}
