namespace Autheris.Application.Sql.Dialects;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Autheris.Domain.Common;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;

/// <summary>
/// WP-ARC-B-09: Central registry for SQL dialect providers. Fail-closed on unknown or unsupported dialect names.
/// </summary>
public interface ISqlDialectRegistry
{
    /// <summary>Default immutable dialect registry instance for callers without DI injection.</summary>
    static ISqlDialectRegistry Default { get; } = BuiltInSqlDialectRegistry.Instance;

    ISqlDialectProvider GetProvider(DatabaseDialect dialect);

    ISqlDialectProvider GetProvider(TargetSqlDialect target);

    bool TryGetByName(string? dialectName, [NotNullWhen(true)] out ISqlDialectProvider? provider);

    IReadOnlyCollection<ISqlDialectProvider> GetAll();
}

internal sealed class BuiltInSqlDialectRegistry : ISqlDialectRegistry
{
    public static BuiltInSqlDialectRegistry Instance { get; } = new();

    private readonly IReadOnlyList<ISqlDialectProvider> _providers;
    private readonly FrozenDictionary<DatabaseDialect, ISqlDialectProvider> _byDialect;
    private readonly FrozenDictionary<TargetSqlDialect, ISqlDialectProvider> _byTarget;
    private readonly FrozenDictionary<string, ISqlDialectProvider> _byName;

    public BuiltInSqlDialectRegistry(IEnumerable<ISqlDialectProvider>? providers = null)
    {
        var list = providers is not null ? new List<ISqlDialectProvider>(providers) : CreateDefaultProviders();
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
        new BuiltInDialectProvider(
            DatabaseDialect.SqlServer,
            TargetSqlDialect.SqlServer,
            "SqlServer",
            ["mssql", "sqlserver", "sql_server", "microsoft sql server"],
            supportsTreeCompile: true,
            supportsCdc: true,
            maxParameterCount: 2100),
        new BuiltInDialectProvider(
            DatabaseDialect.PostgreSql,
            TargetSqlDialect.PostgreSql,
            "PostgreSql",
            ["postgres", "postgresql", "pgsql", "npgsql"],
            supportsTreeCompile: true,
            supportsCdc: true,
            maxParameterCount: 10000),
        new BuiltInDialectProvider(
            DatabaseDialect.Sqlite,
            TargetSqlDialect.Sqlite,
            "Sqlite",
            ["sqlite", "sqlite3"],
            supportsTreeCompile: true,
            supportsCdc: true,
            maxParameterCount: 999),
        new BuiltInDialectProvider(
            DatabaseDialect.Oracle,
            TargetSqlDialect.Oracle,
            "Oracle",
            ["oracle", "oracledb", "odp"],
            supportsTreeCompile: false,
            supportsCdc: false,
            maxParameterCount: 1000),
        new BuiltInDialectProvider(
            DatabaseDialect.Databricks,
            TargetSqlDialect.Databricks,
            "Databricks",
            ["databricks", "spark", "sparksql"],
            supportsTreeCompile: false,
            supportsCdc: false,
            maxParameterCount: 1000)
    ];

    private sealed class BuiltInDialectProvider : ISqlDialectProvider
    {
        public SqlDialectDescriptor Descriptor { get; }

        public BuiltInDialectProvider(
            DatabaseDialect dialect,
            TargetSqlDialect target,
            string canonicalName,
            IReadOnlyList<string> aliases,
            bool supportsTreeCompile,
            bool supportsCdc,
            int maxParameterCount)
        {
            var caps = DialectCapabilityTable.Default.Get(target);
            bool isExecutable = caps.Tier == DialectSupportTier.Production && SqlDialectMapper.ConnectionFactorySupports(dialect);
            Descriptor = new SqlDialectDescriptor(
                Dialect: dialect,
                Target: target,
                CanonicalName: canonicalName,
                Aliases: aliases,
                Capabilities: caps,
                IsExecutable: isExecutable,
                SupportsTreeCompile: supportsTreeCompile,
                SupportsCdc: supportsCdc,
                MaxParameterCount: maxParameterCount);
        }

        public string QuoteIdentifier(string identifier) => Descriptor.Dialect.QuoteIdentifier(identifier);

        public string FormatParameterName(int index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            var name = "p" + index.ToString(CultureInfo.InvariantCulture);
            return Descriptor.Dialect == DatabaseDialect.Oracle ? ":" + name : "@" + name;
        }
    }
}
