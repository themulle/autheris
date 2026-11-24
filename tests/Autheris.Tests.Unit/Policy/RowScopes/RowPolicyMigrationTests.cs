namespace Autheris.Tests.Unit.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class RowPolicyMigrationTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    [Fact]
    public void Subquery_consent_filter_converted_to_generated_policy()
    {
        var consentId = Guid.NewGuid();
        var ordersTable = new TableIdentifier("erp", "dbo", "orders");
        var teamsTable = new TableIdentifier("erp", "dbo", "teams");
        var membersTable = new TableIdentifier("erp", "dbo", "team_members");

        var filter = new LegacySubqueryConsentFilter(
            Id: Guid.NewGuid(),
            FilterGroup: 0,
            ColumnName: "team_id",
            DependentTable: teamsTable,
            DependentTableAlias: "t",
            ForeignKeyColumn: "team_id",
            PrimaryKeyColumn: "id",
            SubqueryFilterPredicateJson: """[{"column":"active","op":"EQ","value":"true"}]""",
            TargetTemporalColumn: null,
            DependentValidFromColumn: null,
            DependentValidToColumn: null,
            TargetTableAlias: "target",
            AdditionalHops:
            [
                new LegacySubqueryHop(membersTable, "m", "t.id", "m.team_id")
            ]);

        var result = RowPolicyMigrationConverter.Convert(
            [new LegacyConsentRowFilterInput(consentId, Tenant, ordersTable, false, ConsentEffect.Allow, [filter])],
            []);

        result.RevokedConsents.ShouldBeEmpty();
        result.ConvertedFilters.Count.ShouldBe(1);

        var converted = result.ConvertedFilters[0];
        converted.ConsentId.ShouldBe(consentId);
        converted.GeneratedPolicy.Status.ShouldBe(FilterApprovalStatus.Active);
        converted.GeneratedPolicy.Name.ShouldBe($"migrated_{consentId:N}_1");
        converted.GeneratedPolicy.Source.ShouldBe("erp");
        converted.GeneratedPolicy.ManagedBy.ShouldBe(new ManagedBy("Migration", RowPolicyMigrationConverter.MigrationStepCommit));
        converted.GeneratedPolicy.Structured.ShouldNotBeNull();
        converted.GeneratedPolicy.Structured!.From.ShouldBe(teamsTable);
        converted.GeneratedPolicy.Structured.Joins.Count.ShouldBe(1);
        converted.GeneratedPolicy.Structured.Joins[0].Table.ShouldBe(membersTable);
        converted.GeneratedPolicy.Structured.Where.ShouldContain(c => c.Column == "t.active" && c.Operator == FilterConditionOperator.Eq && c.Value == "true");
        Should.NotThrow(() => converted.GeneratedPolicy.Validate());

        converted.RowScopeEntry.PolicyName.ShouldBe(converted.GeneratedPolicy.Name);
        converted.RowScopeEntry.Uncovered.ShouldBe(UncoveredPolicy.Deny);
        converted.RowScopeEntry.ColumnMap.ShouldNotBeNull();
        converted.RowScopeEntry.ColumnMap!["id"].ShouldBe("team_id");
    }

    [Fact]
    public void Unconvertible_filter_revokes_consent_with_reason()
    {
        var consentId = Guid.NewGuid();
        var ordersTable = new TableIdentifier("erp", "dbo", "orders");
        var teamsTable = new TableIdentifier("erp", "dbo", "teams");

        var temporalFilter = new LegacySubqueryConsentFilter(
            Id: Guid.NewGuid(),
            FilterGroup: 0,
            ColumnName: "team_id",
            DependentTable: teamsTable,
            DependentTableAlias: "t",
            ForeignKeyColumn: "team_id",
            PrimaryKeyColumn: "id",
            SubqueryFilterPredicateJson: null,
            TargetTemporalColumn: "created_at",
            DependentValidFromColumn: "valid_from",
            DependentValidToColumn: "valid_to",
            TargetTableAlias: "target");

        var result = RowPolicyMigrationConverter.Convert(
            [new LegacyConsentRowFilterInput(consentId, Tenant, ordersTable, false, ConsentEffect.Allow, [temporalFilter])],
            []);

        result.ConvertedFilters.ShouldBeEmpty();
        result.RevokedConsents.Count.ShouldBe(1);
        result.RevokedConsents[0].ConsentId.ShouldBe(consentId);
        result.RevokedConsents[0].TenantId.ShouldBe(Tenant);
        result.RevokedConsents[0].TargetTable.ShouldBe(ordersTable);
        result.RevokedConsents[0].RevokeReason.ShouldBe("OD38_ROW_FILTER_NOT_CONVERTIBLE");
    }

    [Fact]
    public void Profile_predicate_dropped_and_reported()
    {
        var result = RowPolicyMigrationConverter.Convert(
            [],
            [
                new LegacyAccessProfilePredicateInput(Tenant, "prof-with-pred", "status != 'DELIVERED'"),
                new LegacyAccessProfilePredicateInput(Tenant, "prof-empty", "   "),
                new LegacyAccessProfilePredicateInput(Tenant, "prof-none", null)
            ]);

        result.DroppedProfilePredicates.ShouldBe([(Tenant.Value, "prof-with-pred")]);
    }

    [Fact]
    public void Validator_accepts_active_policy_and_rejects_inactive_or_unknown_mapped_column()
    {
        var activePolicy = new VirtualFilter
        {
            TenantId = Tenant,
            Name = "by_region",
            Source = "erp",
            Status = FilterApprovalStatus.Active,
            EvaluationMode = RowPolicyEvaluationMode.Live,
            Parameters = [new RowPolicyParameter("region", RowPolicyParameterType.String, false)],
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("erp", "dbo", "regions"),
                FromAlias = "r",
                Where = [new FilterCondition("r.code", FilterConditionOperator.Eq, ":region")]
            },
            KeyColumns = ["r.id"]
        };

        var pendingPolicy = activePolicy with
        {
            Id = Guid.NewGuid(),
            Name = "pending_region",
            Status = FilterApprovalStatus.Rejected
        };

        var snapshot = new VirtualFilterSnapshot(1, [activePolicy, pendingPolicy], []);
        var targetTable = new TableMetadata
        {
            Identifier = new TableIdentifier("erp", "dbo", "orders"),
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "erp",
                SchemaName = "dbo",
                TableName = "orders"
            },
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "region_id", DataType = "int" }
            ]
        };

        var validParams = new Dictionary<string, JsonElement>
        {
            ["region"] = JsonDocument.Parse("\"EU\"").RootElement.Clone()
        };

        Should.NotThrow(() => GrantRowScopeValidator.Validate(
            [new GrantRowScopeEntry("by_region", validParams, new Dictionary<string, string> { ["id"] = "region_id" })],
            Tenant,
            snapshot,
            targetTable));

        var exInactive = Should.Throw<RowScopeValidationException>(() => GrantRowScopeValidator.Validate(
            [new GrantRowScopeEntry("pending_region", validParams)],
            Tenant,
            snapshot,
            targetTable));
        exInactive.Code.ShouldBe(GrantRowScopeValidator.PolicyNotActiveCode);

        var exUnknownCol = Should.Throw<RowScopeValidationException>(() => GrantRowScopeValidator.Validate(
            [new GrantRowScopeEntry("by_region", validParams, new Dictionary<string, string> { ["id"] = "missing_col" })],
            Tenant,
            snapshot,
            targetTable));
        exUnknownCol.Code.ShouldBe(GrantRowScopeValidator.PolicyColumnUnknownCode);
    }
}
