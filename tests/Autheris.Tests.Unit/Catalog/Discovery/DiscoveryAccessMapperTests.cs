namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using Autheris.Application.Catalog.Discovery;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

public sealed class DiscoveryAccessMapperTests
{
    [Fact]
    public void Map_CarriesTheColumnLongDescription_Sanitized()
    {
        var table = DiscoveryTestBuilder.MakeTable("sales", "orders", columns: [("id", false, false)]);
        table = table with { Columns = [new TableColumn { ColumnName = "id", DataType = "varchar", LongDescription = "Line one\nLine two\u0007" }] };
        var decision = TableAccessDecision.Allowed(
            table.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["id"] = ColumnAccessLevel.Clear });

        var mapped = DiscoveryAccessMapper.Map(table, decision, [], false, false, null, language: null, isHidden: false);

        var longDescription = mapped.Columns.ShouldHaveSingleItem().LongDescription;
        longDescription.ShouldNotBeNull();
        longDescription.ShouldContain("Line one\nLine two");
        longDescription.ShouldNotContain("\u0007");
    }

    [Fact]
    public void Map_GrantedWithRowFilter_ProjectsRowRestricted_AndMasksRowDependentColumns()
    {
        var table = DiscoveryTestBuilder.MakeTable(
            "sales",
            "orders",
            columns:
            [
                ("id", false, false),
                ("email", true, false),
                ("secret_internal", true, true),
                ("region_note", false, false),
                ("blocked", false, false)
            ]);

        var decision = TableAccessDecision.Allowed(
            table.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Mask,
                ["blocked"] = ColumnAccessLevel.Deny
            },
            rowFilterSql: "region = 'EU'",
            hasUnconstrainedColumnAllow: true) with
        {
            RowDependentColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "region_note" }
        };

        var activeOwner = new DataOwner
        {
            AdSid = new Sid("S-OWNER"),
            AdAccount = "CORP\\alice",
            DisplayName = "Alice Owner",
            Email = "alice@example.com",
            IsActive = true
        };
        var inactiveOwner = new DataOwner
        {
            AdSid = new Sid("S-OLD"),
            AdAccount = "CORP\\bob",
            DisplayName = "Bob Former",
            Email = "bob@example.com",
            IsActive = false
        };

        var artifacts = new DiscoveryArtifacts(
            table.Table.Id,
            "tenant-a",
            SamplesJson: "[{\"id\":1}]",
            StatisticsJson: "{\"table\":{\"approxRowCount\":42,\"partitionColumns\":[\"id\",\"secret_internal\"]}}",
            ContentSha256: "abc",
            UploadedBy: "S-ADMIN",
            UploadedAt: DateTimeOffset.UtcNow);

        var mapped = DiscoveryAccessMapper.Map(
            table,
            decision,
            [activeOwner, inactiveOwner],
            isPendingRequest: false,
            isRecentlyExpired: false,
            artifacts,
            language: null,
            isHidden: false);

        mapped.Access.ShouldBe(DiscoveryAccessState.Granted);
        mapped.RowScope.ShouldBe(DiscoveryRowScope.RowRestricted);
        mapped.SamplesAvailable.ShouldBeTrue();
        mapped.Owners.ShouldBe([new DiscoveryOwner("Alice Owner", "alice@example.com")]);
        mapped.Statistics.ShouldNotBeNull();
        mapped.Statistics!.PartitionColumns.ShouldBe(["id"]);
        mapped.Columns.ShouldNotContain(c => c.Name == "secret_internal");

        var idCol = mapped.Columns.Single(c => c.Name == "id");
        idCol.State.ShouldBe(DiscoveryColumnState.Clear);

        var emailCol = mapped.Columns.Single(c => c.Name == "email");
        emailCol.State.ShouldBe(DiscoveryColumnState.Masked);
        emailCol.Pii.ShouldBeTrue();
        emailCol.ClearAccessRequiresSeparateApproval.ShouldBeTrue();

        var regionCol = mapped.Columns.Single(c => c.Name == "region_note");
        regionCol.State.ShouldBe(DiscoveryColumnState.Masked);
        regionCol.ClearInSomeRows.ShouldBeTrue();

        var blockedCol = mapped.Columns.Single(c => c.Name == "blocked");
        blockedCol.State.ShouldBe(DiscoveryColumnState.NoAccess);
    }

    [Fact]
    public void Map_DeniedDecision_MapsPendingExpiredNone_AndRespectsSamplesDisabled()
    {
        var table = DiscoveryTestBuilder.MakeTable("sales", "invoices", samplesDisabled: true);
        var decision = TableAccessDecision.Denied(table.Identifier, "No grant");
        var artifacts = new DiscoveryArtifacts(
            table.Table.Id,
            "tenant-a",
            SamplesJson: "[{\"id\":1}]",
            StatisticsJson: null,
            ContentSha256: "abc",
            UploadedBy: "S-ADMIN",
            UploadedAt: DateTimeOffset.UtcNow);

        var requested = DiscoveryAccessMapper.Map(table, decision, [], isPendingRequest: true, isRecentlyExpired: true, artifacts, null, isHidden: false);
        requested.Access.ShouldBe(DiscoveryAccessState.Requested);
        requested.RowScope.ShouldBeNull();
        requested.SamplesAvailable.ShouldBeFalse();

        var expired = DiscoveryAccessMapper.Map(table, decision, [], isPendingRequest: false, isRecentlyExpired: true, artifacts, null, isHidden: false);
        expired.Access.ShouldBe(DiscoveryAccessState.Expired);

        var none = DiscoveryAccessMapper.Map(table, decision, [], isPendingRequest: false, isRecentlyExpired: false, artifacts, null, isHidden: false);
        none.Access.ShouldBe(DiscoveryAccessState.None);
    }

    [Fact]
    public void Map_NoArtifacts_LeavesStatisticsNullAndSamplesUnavailable()
    {
        var table = DiscoveryTestBuilder.MakeTable("sales", "empty");
        var decision = TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var mapped = DiscoveryAccessMapper.Map(table, decision, [], false, false, artifacts: null, language: null, isHidden: false);

        mapped.Access.ShouldBe(DiscoveryAccessState.Granted);
        mapped.RowScope.ShouldBe(DiscoveryRowScope.Full);
        mapped.Statistics.ShouldBeNull();
        mapped.SamplesAvailable.ShouldBeFalse();
    }
}
