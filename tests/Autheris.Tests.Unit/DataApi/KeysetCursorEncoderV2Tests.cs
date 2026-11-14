namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class KeysetCursorEncoderV2Tests
{
    private const string PrimaryKey = "keyset-v2-primary-signing-key-0123456789";
    private const string RotatedKey = "keyset-v2-rotated-signing-key-9876543210";

    private static readonly KeysetCursorContext Context = new(
        TenantId: "tenant-1",
        DataSource: "sales",
        TableName: "orders",
        Direction: "asc",
        Columns: ["id"],
        Caller: "S-1-5-21-test-caller",
        Schema: "dbo",
        RequestHash: "request-hash-1");

    private static TableMetadata CreateTableMetadata(IReadOnlyList<string>? primaryKeys = null) => new()
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
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64UrlEncodeUtf8(string value) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(value));

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    [Fact]
    public void V1_cursor_rejected()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var encoder = new KeysetCursorEncoder(PrimaryKey, clock);
        var metadata = CreateTableMetadata();

        var v1Payload = Base64UrlEncodeUtf8("{\"id\":42}");
        var v1Body = $"{v1Payload}:tenant-1|sales|dbo|orders|asc|id|S-1-5-21-test-caller|request-hash-1";
        var v1Sig = Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(PrimaryKey), Encoding.UTF8.GetBytes(v1Body)));
        var v1Cursor = $"{v1Payload}.{v1Sig}";

        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(v1Cursor, metadata, Context));
        ex.Code.ShouldBe("cursor.invalid");
    }

    [Fact]
    public void Version_downgrade_to_v1_rejected_with_same_error()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var encoder = new KeysetCursorEncoder(PrimaryKey, clock);
        var metadata = CreateTableMetadata();

        var validV2 = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 7 }, Context);
        validV2.ShouldStartWith("v2|");

        var parts = validV2.Split('|');
        parts.Length.ShouldBe(4);
        var downgradedTag = $"v1|{parts[1]}|{parts[2]}|{parts[3]}";

        var v1SignedPayload = Base64UrlEncodeUtf8("{\"id\":7}");
        var v1Legacy = $"{v1SignedPayload}.{Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(PrimaryKey), Encoding.UTF8.GetBytes(v1SignedPayload)))}";

        var exDowngraded = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(downgradedTag, metadata, Context));
        var exLegacy = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(v1Legacy, metadata, Context));
        var exTampered = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(validV2 + "tamper", metadata, Context));

        exDowngraded.Code.ShouldBe("cursor.invalid");
        exLegacy.Code.ShouldBe("cursor.invalid");
        exDowngraded.Message.ShouldBe(exTampered.Message);
        exLegacy.Message.ShouldBe(exTampered.Message);
    }

    [Fact]
    public void Unknown_key_id_rejected()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var encoder = new KeysetCursorEncoder(PrimaryKey, clock);
        var metadata = CreateTableMetadata();

        var valid = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 10 }, Context);
        var parts = valid.Split('|');
        parts.Length.ShouldBe(4);

        var dotIndex = parts[3].LastIndexOf('.');
        var payload = parts[3][..dotIndex];
        var sig = parts[3][(dotIndex + 1)..];
        var foreignKid = "deadbeef";
        var forgedKidCursor = $"v2|{foreignKid}|{parts[2]}|{payload}.{sig}";

        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(forgedKidCursor, metadata, Context));
        ex.Code.ShouldBe("cursor.invalid");
    }

    [Fact]
    public void Key_rotation_rejects_previous_key_cursors()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var oldEncoder = new KeysetCursorEncoder(PrimaryKey, clock);
        var rotatedEncoder = new KeysetCursorEncoder(RotatedKey, clock);
        var metadata = CreateTableMetadata();

        var oldCursor = oldEncoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 99 }, Context);

        var ex = Should.Throw<InvalidCursorException>(() => rotatedEncoder.DecodeCursor(oldCursor, metadata, Context));
        ex.Code.ShouldBe("cursor.invalid");
    }

    [Fact]
    public void Expired_cursor_rejected()
    {
        var start = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var encoder = new KeysetCursorEncoder(PrimaryKey, clock);
        var metadata = CreateTableMetadata();

        var cursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 42 }, Context);

        clock.Advance(TimeSpan.FromHours(24));
        encoder.DecodeCursor(cursor, metadata, Context)["id"].ShouldBe(42);

        clock.Advance(TimeSpan.FromSeconds(1));
        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(cursor, metadata, Context));
        ex.Code.ShouldBe("cursor.invalid");
    }

    [Fact]
    public void Future_dated_iat_rejected()
    {
        var issuedAt = new DateTimeOffset(2026, 11, 13, 12, 10, 0, TimeSpan.Zero);
        var issuerClock = new FakeTimeProvider(issuedAt);
        var issuer = new KeysetCursorEncoder(PrimaryKey, issuerClock);
        var metadata = CreateTableMetadata();

        var cursor = issuer.EncodeCursor(new Dictionary<string, object?> { ["id"] = 42 }, Context);

        // Verifier is 5 minutes behind: within clock-skew allowance.
        var withinSkew = new KeysetCursorEncoder(PrimaryKey, new FakeTimeProvider(issuedAt - TimeSpan.FromMinutes(5)));
        withinSkew.DecodeCursor(cursor, metadata, Context)["id"].ShouldBe(42);

        // Verifier is 5 minutes 1 second behind: beyond allowed clock skew -> rejected.
        var beyondSkew = new KeysetCursorEncoder(PrimaryKey, new FakeTimeProvider(issuedAt - TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1)));
        var ex = Should.Throw<InvalidCursorException>(() => beyondSkew.DecodeCursor(cursor, metadata, Context));
        ex.Code.ShouldBe("cursor.invalid");
    }

    [Fact]
    public void Tampered_iat_does_not_verify()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var encoder = new KeysetCursorEncoder(PrimaryKey, clock);
        var metadata = CreateTableMetadata();

        var cursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 55 }, Context);
        var parts = cursor.Split('|');
        parts.Length.ShouldBe(4);
        var originalIat = long.Parse(parts[2]);
        var extendedIatCursor = $"{parts[0]}|{parts[1]}|{originalIat + 3600}|{parts[3]}";

        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(extendedIatCursor, metadata, Context));
        ex.Code.ShouldBe("cursor.invalid");
    }

    [Fact]
    public void Field_boundary_collision_does_not_verify()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var encoder = new KeysetCursorEncoder(PrimaryKey, clock);
        var metadata = CreateTableMetadata();

        var issuedContext = Context with { TenantId = "a|b", DataSource = "c" };
        var cursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 12 }, issuedContext);

        Should.Throw<InvalidCursorException>(() =>
            encoder.DecodeCursor(cursor, metadata, Context with { TenantId = "a", DataSource = "b|c" }));

        Should.Throw<InvalidCursorException>(() =>
            encoder.DecodeCursor(cursor, metadata, issuedContext with { Caller = "S-1-5-21-test-caller|extra" }));
    }

    [Fact]
    public void Error_does_not_reveal_failed_check()
    {
        var start = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var encoder = new KeysetCursorEncoder(PrimaryKey, clock);
        var otherEncoder = new KeysetCursorEncoder(RotatedKey, clock);
        var metadata = CreateTableMetadata();

        var valid = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 1 }, Context);
        var validParts = valid.Split('|');
        var dot = validParts[3].LastIndexOf('.');
        var tamperedSig = $"v2|{validParts[1]}|{validParts[2]}|{validParts[3][..dot]}.AAAA";
        var wrongKid = $"v2|00000000|{validParts[2]}|{validParts[3]}";
        var wrongVersion = $"v1|{validParts[1]}|{validParts[2]}|{validParts[3]}";
        var rotated = otherEncoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 1 }, Context);
        var extraKeyCursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 1, ["extra"] = 2 }, Context);
        var compositeMetadata = CreateTableMetadata(primaryKeys: ["tenant_id", "id"]);
        var missingKeyCursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["tenant_id"] = "t1" }, Context);
        var nullKeyCursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = null }, Context);
        var wrongTypeCursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = "not-an-int" }, Context);

        var futureIssuer = new KeysetCursorEncoder(PrimaryKey, new FakeTimeProvider(start + TimeSpan.FromMinutes(10)));
        var futureCursor = futureIssuer.EncodeCursor(new Dictionary<string, object?> { ["id"] = 1 }, Context);

        var expiredEncoder = new KeysetCursorEncoder(PrimaryKey, new FakeTimeProvider(start + TimeSpan.FromHours(25)));

        var failures = new List<InvalidCursorException>
        {
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor("", metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor("not-a-cursor", metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(tamperedSig, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(wrongKid, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(wrongVersion, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(rotated, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(futureCursor, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => expiredEncoder.DecodeCursor(valid, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(extraKeyCursor, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(missingKeyCursor, compositeMetadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(nullKeyCursor, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(wrongTypeCursor, metadata, Context))
        };

        var baselineMessage = failures[0].Message;
        foreach (var ex in failures)
        {
            ex.Code.ShouldBe("cursor.invalid");
            ex.Message.ShouldBe(baselineMessage);
            ex.InnerException.ShouldBeNull();
        }
    }

    [Fact]
    public void Constructor_without_valid_key_fails_closed()
    {
        Should.Throw<ArgumentException>(() => new KeysetCursorEncoder(""));
        Should.Throw<ArgumentException>(() => new KeysetCursorEncoder("   "));
        Should.Throw<ArgumentException>(() => new KeysetCursorEncoder("too-short-key"));
        Should.Throw<ArgumentException>(() => new KeysetCursorEncoder("changeme-changeme-changeme-changeme"));
        Should.Throw<ArgumentException>(() => new KeysetCursorEncoder(new byte[31]));

        Should.Throw<InvalidOperationException>(() =>
            new KeysetCursorEncoder(Options.Create(new GatewayOptions()), Env("Production")));

        Should.Throw<InvalidOperationException>(() =>
            new KeysetCursorEncoder(Options.Create(new GatewayOptions()), environment: null));
    }

    [Fact]
    public void Development_mode_uses_instance_ephemeral_key_without_static_sharing()
    {
        var metadata = CreateTableMetadata();
        var options = Options.Create(new GatewayOptions());
        var devA = new KeysetCursorEncoder(options, Env("Development"));
        var devB = new KeysetCursorEncoder(options, Env("Development"));

        var cursorA = devA.EncodeCursor(new Dictionary<string, object?> { ["id"] = 1 }, Context);

        Should.Throw<InvalidCursorException>(() => devB.DecodeCursor(cursorA, metadata, Context));
    }
}
