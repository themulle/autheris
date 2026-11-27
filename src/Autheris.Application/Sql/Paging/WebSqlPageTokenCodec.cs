namespace Autheris.Application.Sql.Paging;

using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using TrinoSqlEngine.Ast.Emit;

public sealed record WebSqlPageTokenPayload(
    [property: JsonPropertyName("v")] int v,
    [property: JsonPropertyName("o")] long o,
    [property: JsonPropertyName("n")] int n,
    [property: JsonPropertyName("p")] int p,
    [property: JsonPropertyName("exp")] long exp,
    [property: JsonPropertyName("h"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? h = null,
    [property: JsonPropertyName("k"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? k = null);

public sealed record WebSqlKeysetContinuation(string ShapeHash, ImmutableArray<object> Values);

public sealed record WebSqlDecodedPageToken(
    long Offset,
    int Size,
    int PageNumber,
    WebSqlKeysetContinuation? Keyset = null);

public interface IWebSqlPageTokenCodec
{
    string EncodeToken(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        ClaimsPrincipal user,
        TenantId tenantId,
        string resolvedDataSource,
        string transport,
        long offset,
        int size,
        int pageNumber,
        TimeSpan lifetime);

    /// <summary>
    /// Issues a keyset continuation token, or returns null when the key values cannot be represented
    /// (null or unsupported type, wrong count) or the token would exceed <see cref="WebSqlPageTokenCodec.MaxTokenLength"/>.
    /// </summary>
    string? EncodeKeysetToken(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        ClaimsPrincipal user,
        TenantId tenantId,
        string resolvedDataSource,
        string transport,
        long logicalOffset,
        int size,
        int pageNumber,
        string shapeHash,
        IReadOnlyList<object?> keyValues,
        TimeSpan lifetime);

    WebSqlDecodedPageToken DecodeToken(
        string token,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        ClaimsPrincipal user,
        TenantId tenantId,
        string resolvedDataSource,
        string transport,
        long maxOffset);
}

public sealed class WebSqlPageTokenCodec : IWebSqlPageTokenCodec
{
    public const int MaxTokenLength = 1024;
    public const int MaxKeyValues = 8;
    public const int MaxPageSize = 100_000;
    public const int ShapeHashLength = 16;
    public const string HkdfInfo = "autheris/websql-page-token/v1";
    private static readonly byte[] MacLabel = "autheris-wsql-page/v1"u8.ToArray();

    private readonly CompiledSqlAuditDigester _auditDigester;
    private readonly TimeProvider _timeProvider;

    public WebSqlPageTokenCodec(CompiledSqlAuditDigester auditDigester, TimeProvider? timeProvider = null)
    {
        _auditDigester = auditDigester ?? throw new ArgumentNullException(nameof(auditDigester));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string EncodeToken(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        ClaimsPrincipal user,
        TenantId tenantId,
        string resolvedDataSource,
        string transport,
        long offset,
        int size,
        int pageNumber,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(resolvedDataSource);
        ArgumentNullException.ThrowIfNull(transport);

        var userSid = user.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            throw new InvalidOperationException("Principal has no SID; caller is not bindable for paging tokens.");
        }

        long exp = _timeProvider.GetUtcNow().Add(lifetime).ToUnixTimeSeconds();
        var payloadObj = new WebSqlPageTokenPayload(1, offset, size, pageNumber, exp);
        return Seal(payloadObj, sql, parameters, userSid, tenantId.Value, resolvedDataSource, transport);
    }

    public string? EncodeKeysetToken(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        ClaimsPrincipal user,
        TenantId tenantId,
        string resolvedDataSource,
        string transport,
        long logicalOffset,
        int size,
        int pageNumber,
        string shapeHash,
        IReadOnlyList<object?> keyValues,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(resolvedDataSource);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(shapeHash);
        ArgumentNullException.ThrowIfNull(keyValues);

        var userSid = user.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            throw new InvalidOperationException("Principal has no SID; caller is not bindable for paging tokens.");
        }

        if (keyValues.Count is < 1 or > MaxKeyValues
            || !IsShapeHash(shapeHash)
            || size <= 0 || size > MaxPageSize
            || pageNumber <= 0
            || logicalOffset < 0
            || pageNumber != (logicalOffset / size) + 1)
        {
            return null;
        }

        var encodedKeys = new string[keyValues.Count];
        for (int i = 0; i < encodedKeys.Length; i++)
        {
            if (!WebSqlKeysetValueCodec.TryEncode(keyValues[i], out encodedKeys[i]!))
            {
                return null;
            }
        }

        long exp = _timeProvider.GetUtcNow().Add(lifetime).ToUnixTimeSeconds();
        var payloadObj = new WebSqlPageTokenPayload(2, logicalOffset, size, pageNumber, exp, shapeHash, encodedKeys);
        string token = Seal(payloadObj, sql, parameters, userSid, tenantId.Value, resolvedDataSource, transport);
        return token.Length > MaxTokenLength ? null : token;
    }

    internal string Seal(
        WebSqlPageTokenPayload payload,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        string userSid,
        string tenantId,
        string resolvedDataSource,
        string transport)
    {
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] mac = ComputeMac(payloadBytes, sql, parameters, userSid, tenantId, resolvedDataSource, transport);
        return $"{Base64Url.EncodeToString(payloadBytes)}.{Base64Url.EncodeToString(mac)}";
    }

    private static bool IsShapeHash(string value)
    {
        if (value.Length != ShapeHashLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    public WebSqlDecodedPageToken DecodeToken(
        string token,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        ClaimsPrincipal user,
        TenantId tenantId,
        string resolvedDataSource,
        string transport,
        long maxOffset)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            throw new SqlPagingTokenInvalidException();
        }

        int dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == token.Length - 1)
        {
            throw new SqlPagingTokenInvalidException();
        }

        string payloadB64 = token[..dot];
        string macB64 = token[(dot + 1)..];

        byte[] payloadBytes;
        byte[] providedMac;
        try
        {
            payloadBytes = Base64Url.DecodeFromChars(payloadB64);
            providedMac = Base64Url.DecodeFromChars(macB64);
        }
        catch
        {
            throw new SqlPagingTokenInvalidException();
        }

        var userSid = user.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            throw new SqlPagingTokenInvalidException();
        }

        byte[] expectedMac = ComputeMac(payloadBytes, sql, parameters, userSid, tenantId.Value, resolvedDataSource, transport);
        if (!CryptographicOperations.FixedTimeEquals(expectedMac, providedMac))
        {
            throw new SqlPagingTokenInvalidException();
        }

        WebSqlPageTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebSqlPageTokenPayload>(payloadBytes);
        }
        catch
        {
            throw new SqlPagingTokenInvalidException();
        }

        if (payload == null || payload.v is not (1 or 2))
        {
            throw new SqlPagingTokenInvalidException();
        }

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (payload.exp <= now)
        {
            throw new SqlPagingTokenInvalidException();
        }

        if (payload.o < 0 || payload.n <= 0 || payload.n > MaxPageSize || payload.p <= 0
            || payload.p != (payload.o / payload.n) + 1)
        {
            throw new SqlPagingTokenInvalidException();
        }

        if (payload.v == 1)
        {
            if (payload.k is not null || payload.h is not null || payload.o > maxOffset)
            {
                throw new SqlPagingTokenInvalidException();
            }

            return new WebSqlDecodedPageToken(payload.o, payload.n, payload.p);
        }

        // Keyset continuations only exist beyond the offset window.
        if (payload.o <= maxOffset
            || payload.h is null || !IsShapeHash(payload.h)
            || payload.k is null || payload.k.Length is < 1 or > MaxKeyValues)
        {
            throw new SqlPagingTokenInvalidException();
        }

        var values = ImmutableArray.CreateBuilder<object>(payload.k.Length);
        foreach (string entry in payload.k)
        {
            if (!WebSqlKeysetValueCodec.TryDecode(entry, out var value))
            {
                throw new SqlPagingTokenInvalidException();
            }

            values.Add(value);
        }

        return new WebSqlDecodedPageToken(
            payload.o,
            payload.n,
            payload.p,
            new WebSqlKeysetContinuation(payload.h, values.MoveToImmutable()));
    }

    private byte[] ComputeMac(
        byte[] payloadBytes,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        string userSid,
        string tenantId,
        string resolvedDataSource,
        string transport)
    {
        byte[] key = _auditDigester.DerivePurposeKey(HkdfInfo);

        byte[] f1Statement = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
        byte[] f2Params = ComputeParametersHash(parameters);
        byte[] f3Sid = Encoding.UTF8.GetBytes(userSid);
        byte[] f4Tenant = Encoding.UTF8.GetBytes(tenantId);
        byte[] f5DataSource = Encoding.UTF8.GetBytes(resolvedDataSource);
        byte[] f6Transport = Encoding.UTF8.GetBytes(transport);

        using var ms = new MemoryStream();
        ms.Write(MacLabel);
        WriteLengthPrefixed(ms, payloadBytes);
        WriteLengthPrefixed(ms, f1Statement);
        WriteLengthPrefixed(ms, f2Params);
        WriteLengthPrefixed(ms, f3Sid);
        WriteLengthPrefixed(ms, f4Tenant);
        WriteLengthPrefixed(ms, f5DataSource);
        WriteLengthPrefixed(ms, f6Transport);

        return HMACSHA256.HashData(key, ms.ToArray());
    }

    public static byte[] ComputeParametersHash(IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return SHA256.HashData("{}"u8);
        }

        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in parameters)
        {
            sorted[k] = v;
        }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(sorted);
        return SHA256.HashData(json);
    }

    private static void WriteLengthPrefixed(MemoryStream ms, ReadOnlySpan<byte> bytes)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, bytes.Length);
        ms.Write(len);
        ms.Write(bytes);
    }
}
