namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Globalization;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class BulkKeysetCursorEncoderTests
{
    private static readonly KeysetCursorContext Context = new(
        TenantId: "tenant-1",
        DataSource: "sales",
        TableName: "orders",
        Direction: "asc",
        Columns: ["id"],
        Caller: "S-1-5-21-test",
        Schema: "dbo",
        RequestHash: "request-hash");

    [Fact]
    public void DecodeCursor_BareUnboundSignature_IsRejected()
    {
        const string key = "bare-signature-test-key-0123456789abcdef";
        var encoder = new KeysetCursorEncoder(key);
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"id\":1}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var bareSig = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(key),
                System.Text.Encoding.UTF8.GetBytes(payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // A cursor signed over the payload alone (the former unbound mode) never verifies against a bound context.
        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor($"{payload}.{bareSig}", metadata, Context));
    }

    [Fact]
    public void EncodeAndDecodeCursor_NullContext_Throws()
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var pkValues = new Dictionary<string, object?> { ["id"] = 1 };

        Should.Throw<ArgumentNullException>(() => encoder.EncodeCursor(pkValues, null!));
        var encoded = encoder.EncodeCursor(pkValues, Context);
        Should.Throw<ArgumentNullException>(() => encoder.DecodeCursor(encoded, metadata, null!));
    }

    [Fact]
    public void DecodeCursor_ContextFieldDelimiterShift_IsRejected()
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var pkValues = new Dictionary<string, object?> { ["id"] = 1 };
        var issued = Context with { TenantId = "a|b", DataSource = "c" };
        var encoded = encoder.EncodeCursor(pkValues, issued);

        // Moving a delimiter between neighbouring fields must not produce the same signed context.
        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(encoded, metadata, Context with { TenantId = "a", DataSource = "b|c" }));
    }
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

        var encoded = encoder.EncodeCursor(pkValues, Context);
        encoded.ShouldNotBeNullOrWhiteSpace();

        var decoded = encoder.DecodeCursor(encoded, metadata, Context);
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

        var encoded = encoder.EncodeCursor(pkValues, Context);
        var decoded = encoder.DecodeCursor(encoded, metadata, Context);

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

        var encoded = encoder.EncodeCursor(pkValues, Context);
        var decoded = encoder.DecodeCursor(encoded, metadata, Context);

        decoded.Count.ShouldBe(1);
        decoded["uuid"].ShouldBe(expectedGuid);
    }

    [Fact]
    public void DecodeCursor_EmptyOrMalformedBase64_ThrowsInvalidCursor()
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata();

        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor("", metadata, Context));
        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor("!@#$%^&*", metadata, Context));
        Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor("a", metadata, Context)); // invalid base64 length mod 4 == 1
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

        var encoded = encoder.EncodeCursor(pkValues, Context);
        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(encoded, metadata, Context));
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

        var encoded = encoder.EncodeCursor(pkValues, Context);
        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(encoded, metadata, Context));
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

        var encoded = encoder.EncodeCursor(pkValues, Context);
        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(encoded, metadata, Context));
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

        var encoded = encoder.EncodeCursor(pkValues, Context);
        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(encoded, metadata, Context));
        ex.Message.ShouldContain("INVALID_CURSOR");
        ex.Message.ShouldContain("null");
    }
}
