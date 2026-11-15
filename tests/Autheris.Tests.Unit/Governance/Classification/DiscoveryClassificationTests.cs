namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DiscoveryClassificationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static TableMetadata BuildTable(bool tableHidden = false, bool hiddenColHidden = true)
    {
        var tableId = Guid.NewGuid();
        var ssnId = Guid.NewGuid();
        var secretId = Guid.NewGuid();
        var cityId = Guid.NewGuid();
        var hiddenId = Guid.NewGuid();

        return new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "customers"),
            OwnerTenantId = "tenant-a",
            Table = new Table { Id = tableId, SourceName = "crm", SchemaName = "public", TableName = "customers", IsActive = true, DiscoveryHidden = tableHidden },
            Columns =
            [
                new TableColumn { Id = ssnId, TableId = tableId, ColumnName = "ssn", DataType = "varchar" },
                new TableColumn { Id = secretId, TableId = tableId, ColumnName = "api_token", DataType = "varchar" },
                new TableColumn { Id = cityId, TableId = tableId, ColumnName = "city", DataType = "varchar" },
                new TableColumn { Id = hiddenId, TableId = tableId, ColumnName = "internal_audit_key", DataType = "varchar", DiscoveryHidden = hiddenColHidden }
            ],
            ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] = [new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = ssnId.ToString(), Code = ClassificationScopes.PiiSpecialCode, Source = "OWNER", AssignedAt = FixedNow }],
                ["api_token"] = [new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = secretId.ToString(), Code = ClassificationScopes.CredentialsCode, Source = "ADMIN", AssignedAt = FixedNow }],
                ["city"] = [new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = cityId.ToString(), Code = ClassificationScopes.PublicCode, Source = "OWNER", AssignedAt = FixedNow }],
                ["internal_audit_key"] = [new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = hiddenId.ToString(), Code = ClassificationScopes.CredentialsCode, Source = "ADMIN", AssignedAt = FixedNow }]
            }
        };
    }

    [Fact]
    public void Visible_columns_expose_classifications_and_clearRequestable_across_discovery_rest_graphql_and_mcp()
    {
        var table = BuildTable();
        var decision = new TableAccessDecision(
            table.Identifier,
            IsAllowed: true,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] = ColumnAccessLevel.Mask,
                ["api_token"] = ColumnAccessLevel.Mask,
                ["city"] = ColumnAccessLevel.Clear,
                ["internal_audit_key"] = ColumnAccessLevel.Deny
            },
            CombinedRowFilterSql: null,
            DeniedReasons: []);

        var mapped = DiscoveryAccessMapper.Map(table, decision, [], isPendingRequest: false, isRecentlyExpired: false, artifacts: null, language: null, isHidden: false);

        mapped.Columns.Select(c => c.Name).ShouldNotContain("internal_audit_key");

        var ssn = mapped.Columns.Single(c => c.Name == "ssn");
        ssn.Pii.ShouldBeTrue();
        ssn.ClearAccessRequiresSeparateApproval.ShouldBeTrue();
        ssn.Classifications.Select(c => c.Code).ShouldBe(["PII_SPECIAL"]);
        ssn.Classifications[0].DisplayName.ShouldNotBeNullOrWhiteSpace();
        ssn.ClearRequestable.ShouldBe(["PII_SPECIAL"]);

        var token = mapped.Columns.Single(c => c.Name == "api_token");
        token.Pii.ShouldBeFalse();
        token.ClearAccessRequiresSeparateApproval.ShouldBeTrue();
        token.Classifications.Select(c => c.Code).ShouldBe(["CREDENTIALS"]);
        token.ClearRequestable.ShouldBeEmpty(); // CREDENTIALS is never clear-grantable

        var city = mapped.Columns.Single(c => c.Name == "city");
        city.Pii.ShouldBeFalse();
        city.ClearAccessRequiresSeparateApproval.ShouldBeFalse();
        city.Classifications.Select(c => c.Code).ShouldBe(["PUBLIC"]);
        city.ClearRequestable.ShouldBeEmpty();

        // REST projection carries classifications & clearRequestable cleanly
        var restDetail = CatalogDiscoveryProjection.ToDatasetDetail(mapped, language: null);
        var restSsn = restDetail.Columns.Single(c => c.Name == "ssn");
        restSsn.Classifications!.Select(c => c.Code).ShouldBe(["PII_SPECIAL"]);
        restSsn.ClearRequestable!.ShouldBe(["PII_SPECIAL"]);

        // GraphQL projection carries classifications & omits hidden columns' classifications
        var gqlDto = Query.ToCatalogDto(table, isAdminView: true, status: "GRANTED");
        gqlDto.ShouldNotBeNull();
        gqlDto!.Columns.Single(c => c.Name == "ssn").ClearRequestable.ShouldBe(["PII_SPECIAL"]);
        gqlDto.Columns.Single(c => c.Name == "internal_audit_key").Classifications.ShouldBeEmpty();
        gqlDto.Columns.Single(c => c.Name == "internal_audit_key").ClearRequestable.ShouldBeEmpty();
    }

    [Fact]
    public void Hidden_object_and_hidden_column_never_expose_classifications_or_clearRequestable()
    {
        var hiddenTable = BuildTable(tableHidden: true);
        var decision = new TableAccessDecision(
            hiddenTable.Identifier,
            IsAllowed: true,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["ssn"] = ColumnAccessLevel.Mask },
            CombinedRowFilterSql: null,
            DeniedReasons: []);

        var hiddenMapped = DiscoveryAccessMapper.Map(hiddenTable, decision, [], isPendingRequest: false, isRecentlyExpired: false, artifacts: null, language: null, isHidden: true);
        foreach (var col in hiddenMapped.Columns)
        {
            col.Classifications.ShouldBeEmpty();
            col.ClearRequestable.ShouldBeEmpty();
        }

        var gqlHidden = Query.ToCatalogDto(hiddenTable, isAdminView: true, status: "GRANTED");
        gqlHidden.ShouldNotBeNull();
        foreach (var col in gqlHidden!.Columns)
        {
            col.Classifications.ShouldBeEmpty();
            col.ClearRequestable.ShouldBeEmpty();
        }
    }
}
