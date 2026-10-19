namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc.CustomQuery;
using Shouldly;
using Xunit;

public sealed class CustomQueryPollerOperationClassifierTests
{
    [Fact]
    public void ClassifyRow_SoftDelete_NotNullTimestamp_ClassifiesAsDelete()
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["name"] = "Alice",
            ["deleted_at"] = DateTimeOffset.UtcNow
        };
        var softDelete = new SoftDeleteDefinition
        {
            Enabled = true,
            Column = "deleted_at",
            Strategy = SoftDeleteStrategy.NotNullTimestamp
        };

        var op = CdcOperationClassifier.ClassifyRow(row, softDelete, null);
        op.ShouldBe(CdcOperation.Delete);
    }

    [Fact]
    public void ClassifyRow_SoftDelete_BooleanFlag_ClassifiesAsDelete()
    {
        var rowDeleted = new Dictionary<string, object?> { ["id"] = 1, ["is_deleted"] = true };
        var rowActive = new Dictionary<string, object?> { ["id"] = 2, ["is_deleted"] = false };
        var softDelete = new SoftDeleteDefinition
        {
            Enabled = true,
            Column = "is_deleted",
            Strategy = SoftDeleteStrategy.BooleanFlag
        };

        CdcOperationClassifier.ClassifyRow(rowDeleted, softDelete, null).ShouldBe(CdcOperation.Delete);
        CdcOperationClassifier.ClassifyRow(rowActive, softDelete, null).ShouldBe(CdcOperation.Insert);
    }

    [Fact]
    public void ClassifyRow_SoftDelete_StringStatus_ClassifiesAsDelete()
    {
        var rowDeleted = new Dictionary<string, object?> { ["id"] = 1, ["status"] = "ARCHIVED" };
        var rowActive = new Dictionary<string, object?> { ["id"] = 2, ["status"] = "ACTIVE" };
        var softDelete = new SoftDeleteDefinition
        {
            Enabled = true,
            Column = "status",
            Strategy = SoftDeleteStrategy.StringStatus,
            DeletedValue = "ARCHIVED"
        };

        CdcOperationClassifier.ClassifyRow(rowDeleted, softDelete, null).ShouldBe(CdcOperation.Delete);
        CdcOperationClassifier.ClassifyRow(rowActive, softDelete, null).ShouldBe(CdcOperation.Insert);
    }

    [Fact]
    public void ClassifyRow_NoPriorImage_ClassifiesAsInsert()
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = 10,
            ["created_at"] = "2026-10-19T00:00:00Z",
            ["updated_at"] = "2026-10-19T00:00:00Z"
        };

        var op = CdcOperationClassifier.ClassifyRow(row, null, null);
        op.ShouldBe(CdcOperation.Insert);
    }

    [Fact]
    public void ClassifyRow_UpdatedTimestampLaterThanCreated_ClassifiesAsUpdate()
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = 10,
            ["created_at"] = "2026-10-19T00:00:00Z",
            ["updated_at"] = "2026-10-19T01:00:00Z"
        };

        var op = CdcOperationClassifier.ClassifyRow(row, null, null);
        op.ShouldBe(CdcOperation.Update);
    }

    [Fact]
    public void ClassifyRow_WithPriorImage_ClassifiesAsUpdate()
    {
        var prior = new Dictionary<string, object?> { ["id"] = 10, ["name"] = "Alice" };
        var current = new Dictionary<string, object?> { ["id"] = 10, ["name"] = "Alicia" };

        var op = CdcOperationClassifier.ClassifyRow(current, null, prior);
        op.ShouldBe(CdcOperation.Update);
    }

    [Fact]
    public void IsPayloadIdentical_IdenticalDictionaries_ReturnsTrue()
    {
        var d1 = new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Test", ["score"] = 99 };
        var d2 = new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Test", ["score"] = 99 };

        CdcOperationClassifier.IsPayloadIdentical(d1, d2).ShouldBeTrue();
    }

    [Fact]
    public void IsPayloadIdentical_DifferentDictionaries_ReturnsFalse()
    {
        var d1 = new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Test", ["score"] = 99 };
        var d2 = new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Test", ["score"] = 100 };

        CdcOperationClassifier.IsPayloadIdentical(d1, d2).ShouldBeFalse();
    }

    [Fact]
    public void CompareFingerprints_FixedTimeComparison()
    {
        byte[] fp1 = [1, 2, 3, 4, 5];
        byte[] fp2 = [1, 2, 3, 4, 5];
        byte[] fp3 = [1, 2, 3, 4, 6];

        CdcOperationClassifier.CompareFingerprints(fp1, fp2).ShouldBeTrue();
        CdcOperationClassifier.CompareFingerprints(fp1, fp3).ShouldBeFalse();
    }
}
