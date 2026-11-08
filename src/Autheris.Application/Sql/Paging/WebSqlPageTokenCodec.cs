namespace Autheris.Application.Sql.Paging;

using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
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
    [property: JsonPropertyName("exp")] long exp);

public sealed record WebSqlDecodedPageToken(
    long Offset,
    int Size,
    int PageNumber);

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
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payloadObj);

        byte[] mac = ComputeMac(payloadBytes, sql, parameters, userSid, tenantId.Value, resolvedDataSource, transport);

        string payloadB64 = Base64Url.EncodeToString(payloadBytes);
        string macB64 = Base64Url.EncodeToString(mac);

        return $"{payloadB64}.{macB64}";
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
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512)
        {
            throw new SqlPagingTokenInvalidException();
        }

        int dot = token.IndexOf('.');
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

        if (payload == null || payload.v != 1)
        {
            throw new SqlPagingTokenInvalidException();
        }

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (payload.exp <= now)
        {
            throw new SqlPagingTokenInvalidException();
        }

        if (payload.o < 0 || payload.o > maxOffset || payload.n <= 0 || payload.p <= 0 || payload.o != (payload.p - 1) * (long)payload.n)
        {
            throw new SqlPagingTokenInvalidException();
        }

        return new WebSqlDecodedPageToken(payload.o, payload.n, payload.p);
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
