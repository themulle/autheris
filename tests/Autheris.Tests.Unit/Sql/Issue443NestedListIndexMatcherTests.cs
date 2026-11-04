namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql.Indexing;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class Issue443NestedListIndexMatcherTests
{
    [Fact]
    public void Pk_TsDeviceId_JoinDeviceId_OrderTsDesc_NoMatch()
    {
        // PK: (ts ASC, device_id ASC)
        // Join: device_id
        // Order: ts DESC
        // Maximal leading run of PK columns in {device_id} is empty (ts is not in E).
        // Leading run does not contain device_id -> no match.
        var meta = new TableMetadata
        {
            PrimaryKeyColumns = ["ts", "device_id"],
            PrimaryKeyDirections = [KeyColumnDirection.Ascending, KeyColumnDirection.Ascending]
        };

        var matched = NestedListIndexMatcher.Matches(
            meta,
            joinColumns: ["device_id"],
            equalityColumns: Array.Empty<string>(),
            order: [("ts", true)]);

        matched.ShouldBeFalse();
    }

    [Fact]
    public void Index_DeviceIdTs_OrderTsDesc_MatchesBackward()
    {
        // Index: (device_id ASC, ts ASC)
        // Join: device_id
        // Order: ts DESC
        // Leading run is [device_id]. Remaining order [ts DESC] matches candidate [ts ASC] backward (inverted).
        var meta = new TableMetadata
        {
            Indexes =
            [
                new TableIndexDefinition("ix_dev_ts", ["device_id", "ts"], false, [KeyColumnDirection.Ascending, KeyColumnDirection.Ascending])
            ]
        };

        var matched = NestedListIndexMatcher.Matches(
            meta,
            joinColumns: ["device_id"],
            equalityColumns: Array.Empty<string>(),
            order: [("ts", true)]);

        matched.ShouldBeTrue();
    }

    [Fact]
    public void Index_DeviceIdTsDesc_OrderTsAsc_MatchesBackward()
    {
        // Index: (device_id ASC, ts DESC)
        // Join: device_id
        // Order: ts ASC
        // Leading run is [device_id]. Remaining order [ts ASC] matches candidate [ts DESC] backward (inverted).
        var meta = new TableMetadata
        {
            Indexes =
            [
                new TableIndexDefinition("ix_dev_ts_desc", ["device_id", "ts"], false, [KeyColumnDirection.Ascending, KeyColumnDirection.Descending])
            ]
        };

        var matched = NestedListIndexMatcher.Matches(
            meta,
            joinColumns: ["device_id"],
            equalityColumns: Array.Empty<string>(),
            order: [("ts", false)]);

        matched.ShouldBeTrue();
    }

    [Fact]
    public void Index_DeviceIdTs_MixedDirections_NoMatch()
    {
        // Index: (device_id ASC, col_a ASC, col_b ASC)
        // Join: device_id
        // Order: col_a ASC, col_b DESC
        // Directions are mixed (one forward, one inverted) -> neither all equal nor all inverted -> no match.
        var meta = new TableMetadata
        {
            Indexes =
            [
                new TableIndexDefinition("ix_multi", ["device_id", "col_a", "col_b"], false,
                    [KeyColumnDirection.Ascending, KeyColumnDirection.Ascending, KeyColumnDirection.Ascending])
            ]
        };

        var matched = NestedListIndexMatcher.Matches(
            meta,
            joinColumns: ["device_id"],
            equalityColumns: Array.Empty<string>(),
            order: [("col_a", false), ("col_b", true)]);

        matched.ShouldBeFalse();
    }

    [Fact]
    public void WhereEquality_InPrefix_AnyOrder_Matches()
    {
        // Index: (tenant_id, device_id, site_id, ts)
        // Join: device_id
        // Equality: tenant_id, site_id
        // Leading run of columns in {device_id, tenant_id, site_id} is [tenant_id, device_id, site_id],
        // which contains device_id.
        // Remaining order [ts DESC] matches candidate [ts ASC].
        var meta = new TableMetadata
        {
            Indexes =
            [
                new TableIndexDefinition("ix_composite", ["tenant_id", "device_id", "site_id", "ts"], false)
            ]
        };

        var matched = NestedListIndexMatcher.Matches(
            meta,
            joinColumns: ["device_id"],
            equalityColumns: ["tenant_id", "site_id"],
            order: [("ts", true)]);

        matched.ShouldBeTrue();
    }

    [Fact]
    public void JoinColumn_NotInPrefix_NoMatch()
    {
        // Index: (tenant_id, ts, device_id)
        // Join: device_id
        // Equality: tenant_id
        // E = {device_id, tenant_id}
        // Maximal leading run is [tenant_id] (ts is not in E).
        // Leading run does not contain device_id -> no match.
        var meta = new TableMetadata
        {
            Indexes =
            [
                new TableIndexDefinition("ix_not_prefix", ["tenant_id", "ts", "device_id"], false)
            ]
        };

        var matched = NestedListIndexMatcher.Matches(
            meta,
            joinColumns: ["device_id"],
            equalityColumns: ["tenant_id"],
            order: [("ts", true)]);

        matched.ShouldBeFalse();
    }

    [Fact]
    public void OrderColumnInEqualitySet_IsSkipped()
    {
        // Index: (device_id, status, ts)
        // Join: device_id
        // Equality: status
        // Order: status ASC, ts DESC
        // status is in E, so it is dropped from remaining order.
        // Remaining order is [ts DESC]. Leading run is [device_id, status].
        // Next candidate col is ts -> matches!
        var meta = new TableMetadata
        {
            Indexes =
            [
                new TableIndexDefinition("ix_skip_eq", ["device_id", "status", "ts"], false)
            ]
        };

        var matched = NestedListIndexMatcher.Matches(
            meta,
            joinColumns: ["device_id"],
            equalityColumns: ["status"],
            order: [("status", false), ("ts", true)]);

        matched.ShouldBeTrue();
    }

    [Fact]
    public void CompositeJoin_BothColumnsInPrefix_Matches()
    {
        // Index: (org_id, dept_id, ts)
        // Join: (dept_id, org_id)
        // Order: ts DESC
        // Leading run contains both join columns regardless of order.
        var meta = new TableMetadata
        {
            Indexes =
            [
                new TableIndexDefinition("ix_comp_join", ["org_id", "dept_id", "ts"], false)
            ]
        };

        var matched = NestedListIndexMatcher.Matches(
            meta,
            joinColumns: ["dept_id", "org_id"],
            equalityColumns: Array.Empty<string>(),
            order: [("ts", true)]);

        matched.ShouldBeTrue();
    }

    [Fact]
    public void NoCandidates_NoMatch()
    {
        var meta = new TableMetadata();

        var matched = NestedListIndexMatcher.Matches(
            meta,
            joinColumns: ["device_id"],
            equalityColumns: Array.Empty<string>(),
            order: [("ts", true)]);

        matched.ShouldBeFalse();
    }

    [Fact]
    public void OrWhereEquality_NotCountedAsEquality()
    {
        // If equalityColumns only includes true top-level AND equalities,
        // any OR branch column passed should not count.
        // When equalityColumns is empty, index (tenant_id, device_id, ts) has leading run [].
        var meta = new TableMetadata
        {
            Indexes =
            [
                new TableIndexDefinition("ix_test", ["tenant_id", "device_id", "ts"], false)
            ]
        };

        // When equalityColumns does not contain tenant_id, device_id is not in the leading run.
        var matched = NestedListIndexMatcher.Matches(
            meta,
            joinColumns: ["device_id"],
            equalityColumns: Array.Empty<string>(),
            order: [("ts", true)]);

        matched.ShouldBeFalse();
    }
}
