namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Globalization;
using Autheris.Application.Data.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class BulkKeysetCursorEncoderTests
{
    private static TableMetadata CreateTableMetadata(
        IReadOnlyList<string>? primaryKeys = null,
        IReadOnlyList<TableIndexDefinition>? indexes = null)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = new Table
            {
                TableName = "orders",
                SchemaName = "dbo",
                SourceName = "sales",
                SourceType = "PostgreSQL"
            },
            PrimaryKeyColumns = primaryKeys ?? ["id"],
            Indexes = indexes ?? Array.Empty<TableIndexDefinition>(),
            Columns =
            [
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "created_at", DataType = "timestamp" },
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "uuid", DataType = "uuid" },
                new TableColumn { ColumnName = "amount", DataType = "numeric" }
            ]
        };
    }

    [Fact]
    public void EncodeAndDecodeCursor_SinglePk_RoundtripsSuccessfully()
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var pkValues = new Dictionary<string, object?> { ["id"] = 12345 };

        var encoded = encoder.EncodeCursor(pkValues);
        encoded.ShouldNotBeNullOrWhiteSpace();

        var decoded = encoder.DecodeCursor(encoded, metadata);
        decoded.Count.ShouldBe(1);
        decoded["id"].ShouldBe(12345);
    }

    [Fact]
    public void EncodeAndDecodeCursor_CompositePk_RoundtripsSuccessfully()
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata(primaryKeys: ["tenant_id", "created_at", "id"]);
        var now = DateTimeOffset.UtcNow;
        var pkValues = new Dictionary<string, object?>
        {
            ["tenant_id"] = "t1",
            ["created_at"] = now,
            ["id"] = 42
        };

        var encoded = encoder.EncodeCursor(pkValues);
        var decoded = encoder.DecodeCursor(encoded, metadata);

        decoded.Count.ShouldBe(3);
        decoded["tenant_id"].ShouldBe("t1");
        decoded["created_at"].ShouldBeOfType<DateTimeOffset>();
        decoded["id"].ShouldBe(42);
    }

    [Fact]
    public void DecodeCursor_UniqueIndexFallback_WorksWhenPrimaryKeyEmpty()
    {
        var encoder = new KeysetCursorEncoder();
        var uniqueIndex = new TableIndexDefinition("idx_orders_uuid", ["uuid"], IsUnique: true);
        var metadata = CreateTableMetadata(primaryKeys: Array.Empty<string>(), indexes: [uniqueIndex]);
        var expectedGuid = Guid.NewGuid();
        var pkValues = new Dictionary<string, object?> { ["uuid"] = expectedGuid };

        var encoded = encoder.EncodeCursor(pkValues);
        var decoded = encoder.DecodeCursor(encoded, metadata);

        decoded.Count.ShouldBe(1);
        decoded["uuid"].ShouldBe(expectedGuid);
    }

    [Fact]
    public void DecodeCursor_EmptyOrMalformedBase64_ThrowsInvalidCursor()
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata();

        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor("", metadata));
        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor("!@#$%^&*", metadata));
        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor("a", metadata)); // invalid base64 length mod 4 == 1
    }

    [Fact]
    public void DecodeCursor_ExtraneousKeys_ThrowsInvalidCursor()
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var pkValues = new Dictionary<string, object?>
        {
            ["id"] = 100,
            ["extra_field"] = "hacked"
        };

        var encoded = encoder.EncodeCursor(pkValues);
        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(encoded, metadata));
        ex.Message.ShouldContain("INVALID_CURSOR");
        ex.Message.ShouldContain("extra_field");
    }

    [Fact]
    public void DecodeCursor_MissingKeys_ThrowsInvalidCursor()
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata(primaryKeys: ["tenant_id", "id"]);
        var pkValues = new Dictionary<string, object?>
        {
            ["tenant_id"] = "t1"
        };

        var encoded = encoder.EncodeCursor(pkValues);
        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(encoded, metadata));
        ex.Message.ShouldContain("INVALID_CURSOR");
        ex.Message.ShouldContain("id");
    }

    [Fact]
    public void DecodeCursor_TypeMismatch_ThrowsInvalidCursor()
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]); // id is integer
        var pkValues = new Dictionary<string, object?>
        {
            ["id"] = "not-a-number"
        };

        var encoded = encoder.EncodeCursor(pkValues);
        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(encoded, metadata));
        ex.Message.ShouldContain("INVALID_CURSOR");
    }

    [Fact]
    public void DecodeCursor_NullValue_ThrowsInvalidCursor()
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var pkValues = new Dictionary<string, object?>
        {
            ["id"] = null
        };

        var encoded = encoder.EncodeCursor(pkValues);
        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(encoded, metadata));
        ex.Message.ShouldContain("INVALID_CURSOR");
        ex.Message.ShouldContain("null");
    }
}
