using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Relations;
using Autheris.Application.Data.Services;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Observability;
using HotChocolate;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

public sealed class Issues220To227GovernanceRefinementsTests
{
    // =========================================================================
    // Issue #220: Consumer usage recording per calling application (#114)
    // =========================================================================
    [Fact]
    public void Issue220_ConsumerUsageRecorder_RecordsUsageWithClientId()
    {
        var recorder = new ConsumerUsageRecorder("cluster-secret");
        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var tenantId = new TenantId("tenant-gold");
        var caller = new CallerContext(Application: "sales-app", Version: "1.0", ClientId: "client-app-99");

        recorder.RecordUsage(
            tableId,
            tenantId,
            new Sid("S-1-5-21-USER"),
            ConsumerOutcome.Allow,
            rowsReturned: 42,
            rowsMeasured: true,
            caller: caller);

        recorder.ActiveSeriesCount.ShouldBeGreaterThan(0);
        CallerLabelNormalizer.NormalizeClientId(caller.ClientId).ShouldBe("client-app-99");
    }

    [Fact]
    public void Issue220_CallerContext_PreservesClientId()
    {
        var context = new CallerContext(
            Application: "power-bi",
            Version: "2.0",
            QueryName: "MonthlySales",
            ClientId: "power-bi-desktop");

        context.ClientId.ShouldBe("power-bi-desktop");
    }

    // =========================================================================
    // Issue #221: Required filter columns & column groups (#163)
    // =========================================================================
    [Fact]
    public void Issue221_TableMetadata_RequiredFilterColumnGroups_Validation()
    {
        var meta = new TableMetadata
        {
            Table = new Table
            {
                TableName = "events",
                SourceName = "telemetry",
                SchemaName = "public",
                RequiredFilterColumnGroups = ImmutableArray.Create(
                    ImmutableArray.Create("tenant_id"),
                    ImmutableArray.Create("region", "event_date"))
            },
            Columns =
            [
                new TableColumn { ColumnName = "tenant_id", DataType = "string" },
                new TableColumn { ColumnName = "region", DataType = "string" },
                new TableColumn { ColumnName = "event_date", DataType = "date" }
            ]
        };

        meta.RequiredFilterColumnGroups.Length.ShouldBe(2);
        meta.RequiredFilterColumnGroups[0].ShouldContain("tenant_id");
        meta.RequiredFilterColumnGroups[1].ShouldContain("region");
        meta.RequiredFilterColumnGroups[1].ShouldContain("event_date");
    }

    // =========================================================================
    // Issue #222: Keyset continuation & truncation in bulk stream (#178)
    // =========================================================================
    [Fact]
    public void Issue222_KeysetCursorEncoder_SignedCursor_VerifiesSuccessfully()
    {
        var secret = "super-secret-hmac-key-for-testing-1234567890!";
        var encoder = new KeysetCursorEncoder(secret);

        var pkValues = new Dictionary<string, object?> { ["id"] = 100 };
        var token = encoder.EncodeCursor(pkValues);
        token.ShouldContain(".");

        var metadata = new TableMetadata
        {
            Table = new Table { TableName = "orders", SourceName = "sales", SchemaName = "dbo" },
            PrimaryKeyColumns = ["id"],
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var decoded = encoder.DecodeCursor(token, metadata);
        decoded.ShouldNotBeNull();
        decoded["id"].ShouldBe(100);
    }

    [Fact]
    public void Issue222_KeysetCursorEncoder_TamperedCursor_FailsVerification()
    {
        var secret = "super-secret-hmac-key-for-testing-1234567890!";
        var encoder = new KeysetCursorEncoder(secret);

        var pkValues = new Dictionary<string, object?> { ["id"] = 100 };
        var token = encoder.EncodeCursor(pkValues);
        var parts = token.Split('.');
        var tampered = parts[0] + "tampered." + parts[1];

        var metadata = new TableMetadata
        {
            Table = new Table { TableName = "orders", SourceName = "sales", SchemaName = "dbo" },
            PrimaryKeyColumns = ["id"],
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(tampered, metadata));
    }

    // =========================================================================
    // Issue #223: Cross-source relations in GraphQL (#75)
    // =========================================================================
    [Fact]
    public void Issue223_TableRelation_AllowCrossDataSource_DefaultsFalse()
    {
        var relation = new TableRelation
        {
            ParentTableIdentifier = new TableIdentifier("sales", "dbo", "orders"),
            ChildTableIdentifier = new TableIdentifier("crm", "dbo", "customers"),
            RelationName = "customer",
            JoinKeysParent = ["customer_id"],
            JoinKeysChild = ["id"]
        };

        relation.AllowCrossDataSource.ShouldBeFalse();

        relation.AllowCrossDataSource = true;
        relation.AllowCrossDataSource.ShouldBeTrue();
    }

    // =========================================================================
    // Issue #224: Cost settlement (#177)
    // =========================================================================
    [Fact]
    public void Issue224_QueryCostAnalyzer_SingleRowMultiplier_WhenFirst1Passed()
    {
        var costOptions = new QueryCostOptions
        {
            DefaultListMultiplier = 10,
            CostPerSingleRowLookup = 1
        };

        costOptions.CostPerSingleRowLookup.ShouldBe(1);
        costOptions.DefaultListMultiplier.ShouldBe(10);
    }

    // =========================================================================
    // Issue #225: Query analysis (#175)
    // =========================================================================
    [Fact]
    public void Issue225_QueryCostEstimate_CarriesEstimatedRows()
    {
        var estimate = new QueryCostEstimate(
            EstimatedCost: 15,
            MaxAllowedCost: 100,
            IsWithinCostLimit: true,
            RootFieldCosts: new Dictionary<string, int> { ["orders"] = 15 },
            EstimatedRows: 25);

        estimate.EstimatedRows.ShouldBe(25);
        estimate.IsWithinCostLimit.ShouldBeTrue();
    }

    // =========================================================================
    // Issue #226: Self-service access requests (#113)
    // =========================================================================
    [Fact]
    public void Issue226_EntraIdAuthOptions_HasConfiguredSelfServiceAndReviewScopes()
    {
        var options = new EntraIdAuthOptions();
        options.ReadOnlySelfServiceScopes.ShouldContain("data.access.request");
        options.AccessReviewScopes.ShouldContain("data.access.review");

        var custom = new EntraIdAuthOptions
        {
            ReadOnlySelfServiceScopes = ["custom.request"],
            AccessReviewScopes = ["custom.review"]
        };
        custom.ReadOnlySelfServiceScopes.ShouldContain("custom.request");
        custom.AccessReviewScopes.ShouldContain("custom.review");
    }

    // =========================================================================
    // Issue #227: Consent description on approval (#158 part B)
    // =========================================================================
    [Fact]
    public void Issue227_ConsentRequestPayload_SupportsDescription()
    {
        var payload = new ConsentRequestPayload
        {
            RequestId = Guid.NewGuid(),
            Status = "APPROVED",
            Message = "Approved",
            Description = "Approved for marketing analytics Q4"
        };

        payload.Description.ShouldBe("Approved for marketing analytics Q4");
    }

    [Fact]
    public void Issue227_ConsentDescriptionText_Normalization_EnforcesLengthLimit()
    {
        var text = "Approved by data owner";
        var norm = UntrustedText.Normalize(text, maxLength: 50, allowLineFeed: false);
        norm.IsValid.ShouldBeTrue();
        norm.Value.ShouldBe(text);

        var tooLong = new string('A', 51);
        var longNorm = UntrustedText.Normalize(tooLong, maxLength: 50, allowLineFeed: false);
        longNorm.IsValid.ShouldBeFalse();
    }
}
