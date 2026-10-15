namespace Autheris.Infrastructure.Persistence.Dialects;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Autheris.Application.Sql.Dialects;
using Autheris.Domain.Common;
using TrinoSqlEngine;

/// <summary>
/// WP-ARC-B-09: Infrastructure implementation of <see cref="ISqlDialectRegistry"/> backed by the registered
/// <see cref="ISqlDialectProvider"/> instances. Fail-closed for unknown or unmapped dialect names.
/// </summary>
public sealed class SqlDialectRegistry : ISqlDialectRegistry
{
    public static SqlDialectRegistry Default { get; } = new();

    private readonly IReadOnlyList<ISqlDialectProvider> _providers;
    private readonly FrozenDictionary<DatabaseDialect, ISqlDialectProvider> _byDialect;
    private readonly FrozenDictionary<TargetSqlDialect, ISqlDialectProvider> _byTarget;
    private readonly FrozenDictionary<string, ISqlDialectProvider> _byName;

    public SqlDialectRegistry()
        : this(CreateDefaultProviders())
    {
    }

    public SqlDialectRegistry(IEnumerable<ISqlDialectProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var list = new List<ISqlDialectProvider>(providers);
        if (list.Count == 0)
        {
            list = CreateDefaultProviders();
        }

        _providers = list.AsReadOnly();

        var byDialect = new Dictionary<DatabaseDialect, ISqlDialectProvider>();
        var byTarget = new Dictionary<TargetSqlDialect, ISqlDialectProvider>();
        var byName = new Dictionary<string, ISqlDialectProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            var desc = provider.Descriptor;
            byDialect[desc.Dialect] = provider;
            byTarget[desc.Target] = provider;
            byName[desc.CanonicalName] = provider;
            byName[desc.Dialect.ToString()] = provider;

            foreach (var alias in desc.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    byName[alias.Trim()] = provider;
                }
            }
        }

        _byDialect = byDialect.ToFrozenDictionary();
        _byTarget = byTarget.ToFrozenDictionary();
        _byName = byName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public ISqlDialectProvider GetProvider(DatabaseDialect dialect)
    {
        if (_byDialect.TryGetValue(dialect, out var provider))
        {
            return provider;
        }

        throw new NotSupportedException($"SQL dialect '{dialect}' is not registered.");
    }

    public ISqlDialectProvider GetProvider(TargetSqlDialect target)
    {
        if (_byTarget.TryGetValue(target, out var provider))
        {
            return provider;
        }

        throw new NotSupportedException($"Target SQL dialect '{target}' is not registered.");
    }

    public bool TryGetByName(string? dialectName, [NotNullWhen(true)] out ISqlDialectProvider? provider)
    {
        provider = null;
        if (string.IsNullOrWhiteSpace(dialectName))
        {
            return false;
        }

        var normalized = dialectName.Trim();
        if (normalized.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        return _byName.TryGetValue(normalized, out provider);
    }

    public IReadOnlyCollection<ISqlDialectProvider> GetAll() => _providers;

    private static List<ISqlDialectProvider> CreateDefaultProviders() =>
    [
        SqlServerDialectProvider.Instance,
        PostgreSqlDialectProvider.Instance,
        SqliteDialectProvider.Instance,
        OracleDialectProvider.Instance,
        DatabricksDialectProvider.Instance
    ];
}
