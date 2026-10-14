namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <param name="LiteralValue">Only set for <see cref="ParameterOrigin.QueryLiteral"/> (fully determined by the raw SQL that is compared on a hit).</param>
public sealed record SqlSlot(
    string Marker,
    string Name,
    int Ordinal,
    SqlParameterType Type,
    ParameterOrigin Origin,
    string? SourceName,
    object? LiteralValue,
    string? ColumnType = null);

/// <summary>
/// Value-free cached compile output (SEC-ADG-01, INV-12). It holds the SQL text and slot descriptors; tenant, policy and
/// mask values are rebound from the current request on every hit. It is only created from verified output.
/// </summary>
public sealed record CompiledSqlTemplate(
    string KeyMaterial,
    string Sql,
    ImmutableArray<SqlSlot> Slots,
    TargetSqlDialect Dialect,
    SqlStatementClass StatementClass,
    ImmutableArray<SecurityPredicateId> AppliedPredicates,
    string CompilerVersion,
    ImmutableArray<TableDependency> Dependencies,
    int? ExpectedAffectedRows = null)
{
    public static CompiledSqlTemplate From(string keyMaterial, CompiledSql compiled, ImmutableArray<TableDependency> dependencies) =>
        new(
            keyMaterial,
            compiled.Sql,
            ToSlots(compiled.Parameters),
            compiled.Dialect,
            compiled.StatementClass,
            compiled.AppliedPredicates,
            compiled.CompilerVersion,
            dependencies,
            compiled.ExpectedAffectedRows);

    private static ImmutableArray<SqlSlot> ToSlots(ImmutableArray<BoundParameter> parameters)
    {
        var slots = ImmutableArray.CreateBuilder<SqlSlot>(parameters.Length);
        foreach (var p in parameters)
        {
            slots.Add(new SqlSlot(
                p.Marker, p.Name, p.Ordinal, p.Type, p.Origin, p.SourceName,
                p.Origin == ParameterOrigin.QueryLiteral ? p.Value : null, p.ColumnType));
        }

        return slots.MoveToImmutable();
    }

    /// <summary>Rebinds the slots with the values of the current request.</summary>
    public CompiledSql Rebind(IReadOnlyDictionary<string, PolicyValue> policyValues)
    {
        var parameters = ImmutableArray.CreateBuilder<BoundParameter>(Slots.Length);
        foreach (var slot in Slots)
        {
            object? value = slot.LiteralValue;
            var type = slot.Type;
            if (slot.Origin is ParameterOrigin.Tenant or ParameterOrigin.Policy or ParameterOrigin.Mask)
            {
                if (slot.SourceName is null || !policyValues.TryGetValue(slot.SourceName, out var current) || current.Type != slot.Type)
                {
                    throw new System.Security.SecurityException("A cached template could not be rebound with the current policy values.");
                }

                value = current.Value;
#if DEBUG
                if (CompilerTestSeams.IsDisabled(SecurityControl.CacheRebindPolicyValues))
                {
                    value = slot.LiteralValue;   // mutant: the template keeps the value of the request that created it
                }
#endif
            }

            parameters.Add(new BoundParameter(slot.Marker, slot.Name, slot.Ordinal, value, type, slot.Origin, slot.SourceName, slot.ColumnType));
        }

        return new CompiledSql(Sql, parameters.ToImmutable(), Dialect, StatementClass, AppliedPredicates, CompilerVersion)
        {
            ExpectedAffectedRows = ExpectedAffectedRows
        };
    }
}

public sealed record CompileCacheStats(long Hits, long Misses, int Entries);

/// <summary>
/// Bounded cache of verified, value-free templates. The key is a SHA-256 of the canonical request; the full key material is
/// stored and compared with ordinal equality on a hit (a hash alone is never an identity for a security decision).
/// </summary>
public sealed class CompiledSqlTemplateCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CompiledSqlTemplate> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly int _maxEntries;
    private long _hits, _misses;

    public CompiledSqlTemplateCache(int maxEntries = 512)
    {
        _maxEntries = maxEntries;
    }

    public CompileCacheStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new CompileCacheStats(_hits, _misses, _entries.Count);
            }
        }
    }

    public static string HashOf(string keyMaterial)
    {
        // G8: hash from a pooled/stack buffer instead of allocating the UTF-8 bytes of the whole key material.
        int max = Encoding.UTF8.GetMaxByteCount(keyMaterial.Length);
        Span<byte> digest = stackalloc byte[32];
        if (max <= 1024)
        {
            Span<byte> small = stackalloc byte[max];
            int n = Encoding.UTF8.GetBytes(keyMaterial, small);
            SHA256.HashData(small[..n], digest);
        }
        else
        {
            byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(max);
            try
            {
                int n = Encoding.UTF8.GetBytes(keyMaterial, rented);
                SHA256.HashData(rented.AsSpan(0, n), digest);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: true);   // holds the key material (tenant, policy)
            }
        }

        return Convert.ToHexString(digest);
    }

    public CompiledSqlTemplate? Find(string keyMaterial) => Find(keyMaterial, HashOf(keyMaterial));

    /// <summary>Lookup with a hash the caller computed once (<see cref="HashOf"/>); the full key material is still compared on a hit.</summary>
    public CompiledSqlTemplate? Find(string keyMaterial, string hash)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(hash, out var t) && string.Equals(t.KeyMaterial, keyMaterial, StringComparison.Ordinal))
            {
                return t;
            }

            return null;
        }
    }

    public void CountHit()
    {
        lock (_gate) { _hits++; }
    }

    public void CountMiss()
    {
        lock (_gate) { _misses++; }
    }

    public void Add(CompiledSqlTemplate template) => Add(template, HashOf(template.KeyMaterial));

    public void Add(CompiledSqlTemplate template, string key)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(key)) _order.Enqueue(key);
            _entries[key] = template;
            while (_entries.Count > _maxEntries && _order.Count > 0)
            {
                _entries.Remove(_order.Dequeue());
            }
        }
    }

    public IReadOnlyList<CompiledSqlTemplate> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values.ToList();
        }
    }
}

/// <summary>
/// Canonical serialization of the entire <see cref="CompileRequest"/> (SEC-ADG-01). Every property takes part, so a new
/// property can never be forgotten: an unknown property type throws. Providers, catalog and the tenant value are excluded; they
/// are validated per table on a hit (policy shape fingerprints) or rebound (values).
/// </summary>
public static class CompileCacheKey
{
    // CR-ADG-12: reflection and sorting once per type, not on every compile.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, KeyProperty[]> OrderedProperties = new();

    private sealed record KeyProperty(string Name, Func<object, object?> Get);

    private static readonly HashSet<string> ValidatedPerTable = new(StringComparer.Ordinal)
    {
        nameof(GovernancePolicy.RowFilters), nameof(GovernancePolicy.Masks), nameof(GovernancePolicy.Catalog)
    };

    public static string Material(CompileRequest request, string rawSql, DialectCapabilities capabilities, int engineMaxQueryLength = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rawSql);
        ArgumentNullException.ThrowIfNull(capabilities);

        var sb = _keyBuilder ??= new StringBuilder(4096);
        sb.Clear();
#if DEBUG
        if (CompilerTestSeams.Current?.OmittedCacheKeyFields?.Contains("Versions") != true)
#endif
        {
            sb.Append("v=").Append(CompilerInfo.Version).Append(";cap=").Append(DialectCapabilities.TableVersion).Append(';');
        }

        Write(sb, request, skipValue: false);
        // CR-ADG-41: the engine's input limit is checked while parsing, which a cache hit skips; it is part of the key instead.
        sb.Append(";maxq=").Append(engineMaxQueryLength.ToString(CultureInfo.InvariantCulture));
        sb.Append(";sql=").Append(rawSql.Length).Append(':').Append(rawSql);
        string material = sb.ToString();
        if (sb.Capacity > MaxRetainedBuilderCapacity)
        {
            _keyBuilder = null;   // do not pin a builder that a very large statement grew
        }

        return material;
    }

    // One builder per thread: the key is built on every compile, hit or miss.
    [ThreadStatic]
    private static StringBuilder? _keyBuilder;

    private const int MaxRetainedBuilderCapacity = 64 * 1024;

    private static void Write(StringBuilder sb, object? value, bool skipValue)
    {
        switch (value)
        {
            case null:
                sb.Append('~');
                return;
            case string s:
                sb.Append('"').Append(s.Length).Append(':').Append(s).Append('"');
                return;
            case bool flag:
                sb.Append(flag ? "True" : "False");
                return;
            case Enum when Enum.GetUnderlyingType(value.GetType()) == typeof(int):
                // The numeric value distinguishes the members as well as the name did; no string per member.
                sb.Append(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                return;
            case Enum:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            case TimeSpan ts:
                sb.Append(ts.Ticks.ToString(CultureInfo.InvariantCulture));
                return;
            case IFormattable f when value.GetType().IsPrimitive:
                sb.Append(f.ToString(null, CultureInfo.InvariantCulture));
                return;
            case IReadOnlyCollection<string> { Count: 0 }:
                sb.Append("{}");
                return;
            case IEnumerable<string> strings:
                sb.Append('{');
                foreach (var item in strings.OrderBy(x => x, StringComparer.Ordinal)) Write(sb, item, false);
                sb.Append('}');
                return;
            case TenantBinding tenant:
                // The tenant VALUE is deliberately not part of the key: the template is value-free and rebound per request.
                sb.Append("tenant(").Append(tenant.ParameterName).Append(',').Append(tenant.Type).Append(')');
                return;
        }

        var type = value.GetType();
        if (type.Namespace is null || !type.Namespace.StartsWith("TrinoSqlEngine", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"The cache key does not know how to serialize '{type.Name}'.");
        }

        sb.Append(type.Name).Append('(');
        foreach (var property in OrderedProperties.GetOrAdd(type, static t =>
                     t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetIndexParameters().Length == 0)
                         .OrderBy(p => p.Name, StringComparer.Ordinal)
                         .Select(p => new KeyProperty(p.Name, TrinoSqlEngine.Ast.Security.PropertyGetters.Create(p)))
                         .ToArray()))
        {
            if (value is GovernancePolicy && ValidatedPerTable.Contains(property.Name)) continue;
#if DEBUG
            if (value is CompileRequest && CompilerTestSeams.Current?.OmittedCacheKeyFields?.Contains(property.Name) == true) continue;
#endif
            sb.Append(property.Name).Append('=');
            Write(sb, property.Get(value), false);
            sb.Append(',');
        }

        sb.Append(')');
    }
}

/// <summary>
/// SEC-ADG-23: <c>HMAC-SHA256(auditKey, CompilerVersion || Dialect || Sql || parameter shape)</c>. The shape is
/// (ordinal, type, origin) only: no values and no value hashes (a hash of a low-entropy value can be brute-forced).
/// </summary>
public static class CompiledSqlDigest
{
    public static string Compute(byte[] auditKey, CompiledSql compiled)
    {
        ArgumentNullException.ThrowIfNull(auditKey);
        ArgumentNullException.ThrowIfNull(compiled);
        var sb = new StringBuilder(compiled.Sql.Length + 64);
        sb.Append(compiled.CompilerVersion).Append('|').Append(compiled.Dialect).Append('|').Append(compiled.Sql).Append('|');
        foreach (var p in compiled.Parameters)
        {
            sb.Append(p.Ordinal.ToString(CultureInfo.InvariantCulture)).Append(':').Append(p.Type).Append(':').Append(p.Origin).Append(';');
        }

        return Convert.ToHexString(HMACSHA256.HashData(auditKey, Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
