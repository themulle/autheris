namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Common;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

/// <summary>How the statement is executed: as is, or as the full statement behind a paged request.</summary>
public enum WebSqlResultCacheKeyMode
{
    Plain = 0,
    PagedFull = 1
}

/// <summary>
/// Everything that determines the rows a cached result holds. There is deliberately no page window: a page is cut from the cached full
/// result. <paramref name="ConnectionFingerprint"/> is a digest of the connection settings and is never logged.
/// </summary>
public sealed record WebSqlResultCacheKeyInput(
    TenantId Tenant,
    string? UserSid,
    string? PrincipalKey,
    string LogicalSource,
    string EffectiveDataSourceName,
    string ConnectionKey,
    string ConnectionFingerprint,
    TargetSqlDialect Dialect,
    string CompilerVersion,
    string CompiledSql,
    WebSqlResultCacheKeyMode Mode,
    long DeliveredRowLimit,
    string Format,
    string? Transport,
    ImmutableArray<bool> OutputColumnMasks,
    IReadOnlyList<BoundParameter> Parameters,
    IReadOnlyDictionary<string, object?>? ClientValues,
    int MetricViewMinGroupSize,
    string? MetricViewHiddenCountAlias,
    string? Purpose,
    IReadOnlyList<TableIdentifier> Tables);

/// <summary>Cache key of one caller and statement. <paramref name="Hash"/> is 64 lowercase hex characters and is never logged or returned.</summary>
public sealed record WebSqlResultCacheKey(string Hash, TenantId Tenant, string UserSid, IReadOnlyList<TableIdentifier> Tables);

/// <summary>
/// Builds the cache key as SHA-256 over a versioned, canonical stream: every field is a tag byte, a 4-byte little-endian length and the
/// payload, so no two different inputs share an encoding. A value the builder cannot encode makes the statement unkeyable.
/// </summary>
public static class WebSqlResultCacheKeyBuilder
{
    private const string Version = "autheris.websql.result-cache.v1";

    private const byte TagNull = 0;
    private const byte TagString = 1;
    private const byte TagBool = 2;
    private const byte TagInt32 = 3;
    private const byte TagInt64 = 4;
    private const byte TagInt16 = 5;
    private const byte TagByte = 6;
    private const byte TagSByte = 7;
    private const byte TagUInt32 = 8;
    private const byte TagUInt64 = 9;
    private const byte TagUInt16 = 10;
    private const byte TagDecimal = 11;
    private const byte TagDouble = 12;
    private const byte TagSingle = 13;
    private const byte TagDateTime = 14;
    private const byte TagDateTimeOffset = 15;
    private const byte TagDateOnly = 16;
    private const byte TagTimeOnly = 17;
    private const byte TagTimeSpan = 18;
    private const byte TagGuid = 19;
    private const byte TagBytes = 20;
    private const byte TagList = 21;
    private const byte TagStructInt = 22;
    private const byte TagStructBytes = 23;

    /// <summary>Builds the key. Never throws: any failure returns <c>false</c> and the request runs uncached.</summary>
    public static bool TryBuild(WebSqlResultCacheKeyInput input, out WebSqlResultCacheKey key)
    {
        key = null!;
        try
        {
            if (input is null
                || string.IsNullOrWhiteSpace(input.UserSid)
                || string.IsNullOrEmpty(input.PrincipalKey)
                || input.Parameters is null
                || input.Tables is null)
            {
                return false;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var writer = new Writer(hash);
            writer.Str(Version);
            writer.Str(input.Tenant.Value);
            writer.Str(input.UserSid);
            writer.Str(input.PrincipalKey);
            writer.Str(input.LogicalSource?.ToLowerInvariant());
            writer.Str(input.EffectiveDataSourceName);
            writer.Str(input.ConnectionKey);
            writer.Str(input.ConnectionFingerprint);
            writer.Int((int)input.Dialect);
            writer.Str(input.CompilerVersion);
            writer.Str(input.CompiledSql);
            writer.Int((int)input.Mode);
            writer.Long(input.DeliveredRowLimit);
            writer.Str(input.Format);
            writer.Str(input.Transport);
            writer.Int(input.MetricViewMinGroupSize);
            writer.Str(input.MetricViewHiddenCountAlias);
            writer.Str(input.Purpose);
            WriteMasks(writer, input.OutputColumnMasks);

            var clientValues = ResolveClientValues(input.ClientValues, out var sortedClient);
            if (clientValues is null)
            {
                return false;
            }

            var ordered = input.Parameters.OrderBy(static p => p.Ordinal).ToArray();
            writer.Int(ordered.Length);
            foreach (var p in ordered)
            {
                if (!WriteParameter(writer, p, clientValues))
                {
                    return false;
                }
            }

            writer.Int(sortedClient.Count);
            foreach (var (name, value) in sortedClient)
            {
                writer.Str(name.ToLowerInvariant());
                if (!writer.Value(value))
                {
                    return false;
                }
            }

            key = new WebSqlResultCacheKey(
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                input.Tenant,
                input.UserSid,
                input.Tables.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            key = null!;
            return false;
        }
    }

    private static void WriteMasks(Writer writer, ImmutableArray<bool> masks)
    {
        var bytes = masks.IsDefaultOrEmpty ? [] : new byte[masks.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = masks[i] ? (byte)1 : (byte)0;
        }

        writer.Raw(TagStructBytes, bytes);
    }

    private static Dictionary<string, object?>? ResolveClientValues(
        IReadOnlyDictionary<string, object?>? source,
        out List<KeyValuePair<string, object?>> sorted)
    {
        sorted = [];
        var lookup = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return lookup;
        }

        foreach (var pair in source)
        {
            if (pair.Key is null || !lookup.TryAdd(pair.Key, pair.Value))
            {
                // A name that differs only by case is ambiguous for the binder, so it is not keyable.
                return null;
            }

            sorted.Add(pair);
        }

        sorted.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));
        return lookup;
    }

    private static bool WriteParameter(Writer writer, BoundParameter p, Dictionary<string, object?> clientValues)
    {
        writer.Str(p.Marker);
        writer.Str(p.Name);
        writer.Int(p.Ordinal);
        writer.Int((int)p.Origin);
        writer.Int((int)p.Type);
        writer.Str(p.SourceName);
        writer.Str(p.ColumnType);
        writer.Int(p.ListIndex ?? -1);
        writer.Int(p.ListCount ?? -1);
        writer.Int(p.ZoneArgument ? 1 : 0);
        writer.Int(p.EscapeLikePattern ? 1 : 0);
        writer.Int(p.RequiredFamily is { } family ? (int)family : -1);

        switch (p.Origin)
        {
            case ParameterOrigin.ClientNamed:
                return p.SourceName is not null
                    && clientValues.TryGetValue(p.SourceName, out var clientValue)
                    && writer.Value(clientValue);
            case ParameterOrigin.ClientPositional:
            case ParameterOrigin.PageOffset:
                return false;
            default:
                return writer.Value(p.Value);
        }
    }

    private sealed class Writer(IncrementalHash hash)
    {
        private readonly byte[] _scratch = new byte[16];

        public void Str(string? value)
        {
            if (value is null)
            {
                Raw(TagNull, []);
                return;
            }

            Raw(TagString, Encoding.UTF8.GetBytes(value));
        }

        public void Int(int value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_scratch, value);
            Raw(TagStructInt, _scratch.AsSpan(0, 8));
        }

        public void Long(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_scratch, value);
            Raw(TagStructInt, _scratch.AsSpan(0, 8));
        }

        public bool Value(object? value)
        {
            switch (value)
            {
                case null:
                    Raw(TagNull, []);
                    return true;
                case PolicyValue policy:
                    return policy.Value is not PolicyValue && Value(policy.Value);
                case string s:
                    Raw(TagString, Encoding.UTF8.GetBytes(s));
                    return true;
                case bool b:
                    _scratch[0] = b ? (byte)1 : (byte)0;
                    Raw(TagBool, _scratch.AsSpan(0, 1));
                    return true;
                case int i:
                    return Fixed(TagInt32, i);
                case long l:
                    return Fixed(TagInt64, l);
                case short sh:
                    return Fixed(TagInt16, sh);
                case byte by:
                    return Fixed(TagByte, by);
                case sbyte sb:
                    return Fixed(TagSByte, sb);
                case uint ui:
                    return Fixed(TagUInt32, ui);
                case ulong ul:
                    return Fixed(TagUInt64, unchecked((long)ul));
                case ushort us:
                    return Fixed(TagUInt16, us);
                case decimal d:
                    var bits = decimal.GetBits(d);
                    for (var n = 0; n < 4; n++)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(_scratch.AsSpan(n * 4), bits[n]);
                    }

                    Raw(TagDecimal, _scratch.AsSpan(0, 16));
                    return true;
                case double dbl:
                    return Fixed(TagDouble, BitConverter.DoubleToInt64Bits(dbl));
                case float f:
                    return Fixed(TagSingle, BitConverter.SingleToInt32Bits(f));
                case DateTime dt:
                    BinaryPrimitives.WriteInt64LittleEndian(_scratch, dt.Ticks);
                    BinaryPrimitives.WriteInt64LittleEndian(_scratch.AsSpan(8), (long)dt.Kind);
                    Raw(TagDateTime, _scratch.AsSpan(0, 16));
                    return true;
                case DateTimeOffset dto:
                    BinaryPrimitives.WriteInt64LittleEndian(_scratch, dto.UtcTicks);
                    BinaryPrimitives.WriteInt64LittleEndian(_scratch.AsSpan(8), dto.Offset.Ticks);
                    Raw(TagDateTimeOffset, _scratch.AsSpan(0, 16));
                    return true;
                case DateOnly date:
                    return Fixed(TagDateOnly, date.DayNumber);
                case TimeOnly time:
                    return Fixed(TagTimeOnly, time.Ticks);
                case TimeSpan span:
                    return Fixed(TagTimeSpan, span.Ticks);
                case Guid guid:
                    guid.TryWriteBytes(_scratch);
                    Raw(TagGuid, _scratch.AsSpan(0, 16));
                    return true;
                case byte[] bytes:
                    Raw(TagBytes, bytes);
                    return true;
                case ClientListValue list:
                    BinaryPrimitives.WriteInt64LittleEndian(_scratch, list.Count);
                    Raw(TagList, _scratch.AsSpan(0, 8));
                    BinaryPrimitives.WriteInt64LittleEndian(_scratch, (long)list.ElementType);
                    Raw(TagStructInt, _scratch.AsSpan(0, 8));
                    for (var n = 0; n < list.Count; n++)
                    {
                        if (list[n] is ClientListValue || !Value(list[n]))
                        {
                            return false;
                        }
                    }

                    return true;
                default:
                    return false;
            }
        }

        public void Raw(byte tag, ReadOnlySpan<byte> payload)
        {
            Span<byte> header = stackalloc byte[5];
            header[0] = tag;
            BinaryPrimitives.WriteInt32LittleEndian(header[1..], payload.Length);
            hash.AppendData(header);
            hash.AppendData(payload);
        }

        private bool Fixed(byte tag, long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_scratch, value);
            Raw(tag, _scratch.AsSpan(0, 8));
            return true;
        }
    }
}
