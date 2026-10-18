namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Common;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>
/// Everything that determines the result of a key query (PLAN-GOV-107 A-6, SEC-RF107-03). The compiled key SQL carries canonical table names,
/// the projected catalog columns and every predicate shape; the binds carry the tenant and the policy values and the row limit of SQLite
/// (D-WPC-8). The statement's text budget is deliberately not an input (SEC-RF107-18): entries stay statement independent.
/// </summary>
/// <param name="Provider">Provider name of the governed read connection.</param>
/// <param name="ConnectionString">Hashed, never retained: a reconfigured connection never reuses entries.</param>
/// <param name="Epochs">Authoritative epochs of the target table and of every governance identifier the dependents resolve to.</param>
public sealed record RowFilterKeySetCacheKeyInput(
    string Provider,
    string ConnectionString,
    TargetSqlDialect Dialect,
    string Tenant,
    string DataSource,
    CompiledKeyQuery Query,
    int MaxKeys,
    IReadOnlyDictionary<TableIdentifier, long> Epochs)
{
    // Never print the connection string (it may carry credentials) or anything key related.
    public override string ToString() => $"RowFilterKeySetCacheKeyInput {{ DataSource = {DataSource}, Dialect = {Dialect}, MaxKeys = {MaxKeys}, Epochs = {Epochs.Count} }}";
}

/// <summary>
/// SEC-RF107-03: SHA-256 over a length-prefixed (<c>&lt;byte length&gt;:&lt;bytes&gt;</c>, UTF-8) encoding of every input, full 64 lower-case hex digits,
/// no truncation anywhere, culture invariant. Bind values carry a type tag and an invariant canonical form.
/// </summary>
public static class RowFilterKeySetCacheKey
{
    private const string Version = "rfks/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Compute(RowFilterKeySetCacheKeyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var w = new Writer(hash);
        var query = input.Query;

        w.Text(Version);
        w.Text(input.Provider);
        w.Text(Sha256Hex(input.ConnectionString));
        w.Text(input.Dialect.ToString());
        w.Text(input.Tenant);
        w.Text(input.DataSource);
        w.Text(query.Sql);
        w.Text(query.KeyColumnType ?? string.Empty, query.KeyColumnType is null);
        w.Text(query.ForeignKeyColumnType ?? string.Empty, query.ForeignKeyColumnType is null);
        w.Text(query.ForeignKeyColumn);
        w.Text(query.CompilerVersion);
        w.Number(input.MaxKeys);

        w.Number(query.Parameters.Length);
        foreach (var p in query.Parameters)
        {
            w.Text(p.Name);
            w.Text(p.Type.ToString());
            w.Text(p.Origin.ToString());
            Value(w, p.Value);
        }

        var epochs = input.Epochs
            .OrderBy(e => e.Key.Domain, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Schema, StringComparer.Ordinal)
            .ThenBy(e => e.Key.TableName, StringComparer.Ordinal)
            .ToList();
        w.Number(epochs.Count);
        foreach (var (table, epoch) in epochs)
        {
            w.Text(table.Domain);
            w.Text(table.Schema);
            w.Text(table.TableName);
            w.Number(epoch);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The only hash that may appear in diagnostics: the first 12 hex digits of the cache key (SEC-RF107-09 b).</summary>
    public static string DiagnosticId(string cacheKey)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        return cacheKey.Length <= 12 ? cacheKey : cacheKey[..12];
    }

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    private static void Value(Writer w, object? value)
    {
        switch (value)
        {
            case null:
                w.Text("n", isNull: true);
                break;
            case string s:
                w.Text("s");
                w.Text(s);
                break;
            case sbyte or byte or short or ushort or int or uint or long:
                w.Text("i");
                w.Text(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                break;
            case ulong u:
                w.Text("i");
                w.Text(u.ToString(CultureInfo.InvariantCulture));
                break;
            case decimal d:
                w.Text("d");
                w.Text(d.ToString("G29", CultureInfo.InvariantCulture));
                break;
            case double f:
                w.Text("f");
                w.Text(f.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float f32:
                w.Text("f");
                w.Text(((double)f32).ToString("R", CultureInfo.InvariantCulture));
                break;
            case bool b:
                w.Text("t");
                w.Text(b ? "1" : "0");
                break;
            case byte[] bytes:
                w.Text("b");
                w.Text(Convert.ToHexStringLower(bytes));
                break;
            case Guid g:
                w.Text("g");
                w.Text(g.ToString("D", CultureInfo.InvariantCulture));
                break;
            case DateTime dt:
                w.Text("o");
                w.Text(dt.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                w.Text("o");
                w.Text(dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                w.Text("o");
                w.Text(date.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                w.Text("o");
                w.Text(time.ToString("O", CultureInfo.InvariantCulture));
                break;
            default:
                w.Text("x");
                w.Text(value.GetType().FullName ?? "?");
                w.Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private readonly struct Writer(IncrementalHash hash)
    {
        public void Number(long value) => Text(value.ToString(CultureInfo.InvariantCulture));

        /// <summary>Component <c>&lt;len&gt;:&lt;bytes&gt;</c>; an absent value is tagged with a length that no real component can have.</summary>
        public void Text(string value, bool isNull = false)
        {
            if (isNull)
            {
                hash.AppendData("-:"u8);
                return;
            }

            byte[] bytes;
            string prefix;
            try
            {
                bytes = StrictUtf8.GetBytes(value);
                prefix = bytes.Length.ToString(CultureInfo.InvariantCulture) + ":";
            }
            catch (EncoderFallbackException)
            {
                // An unpaired surrogate has no UTF-8 form; hash its UTF-16 code units under a distinct prefix instead of a replacement character.
                bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(value.AsSpan()).ToArray();   // raw code units, no replacement
                prefix = "u" + bytes.Length.ToString(CultureInfo.InvariantCulture) + ":";
            }

            hash.AppendData(Encoding.ASCII.GetBytes(prefix));
            hash.AppendData(bytes);
        }
    }
}
