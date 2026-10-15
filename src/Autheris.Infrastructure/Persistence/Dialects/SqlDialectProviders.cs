namespace Autheris.Infrastructure.Persistence.Dialects;

using System;
using System.Collections.Generic;
using System.Globalization;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Dialects;
using Autheris.Domain.Common;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;

/// <summary>
/// WP-ARC-B-09: Base implementation for infrastructure SQL dialect providers.
/// </summary>
public abstract class SqlDialectProviderBase : ISqlDialectProvider
{
    public SqlDialectDescriptor Descriptor { get; }

    protected SqlDialectProviderBase(
        DatabaseDialect dialect,
        TargetSqlDialect target,
        string canonicalName,
        IReadOnlyList<string> aliases,
        bool supportsTreeCompile,
        bool supportsCdc,
        int maxParameterCount,
        IDialectCapabilityProvider? capabilityProvider = null)
    {
        var table = capabilityProvider ?? DialectCapabilityTable.Default;
        var caps = table.Get(target);
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

    public virtual string FormatParameterName(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var name = "p" + index.ToString(CultureInfo.InvariantCulture);
        return Descriptor.Dialect == DatabaseDialect.Oracle ? ":" + name : "@" + name;
    }

    public string FormatParameterName(string name) => Descriptor.Dialect.FormatParameterName(name);

    public string FormatParameterMarker(string name) => Descriptor.Dialect.FormatParameterMarker(name);
}

public sealed class SqlServerDialectProvider : SqlDialectProviderBase
{
    public static SqlServerDialectProvider Instance { get; } = new();

    public SqlServerDialectProvider(IDialectCapabilityProvider? capabilityProvider = null)
        : base(
            DatabaseDialect.SqlServer,
            TargetSqlDialect.SqlServer,
            "SqlServer",
            ["mssql", "sqlserver", "sql_server", "microsoft sql server"],
            supportsTreeCompile: true,
            supportsCdc: true,
            maxParameterCount: 2100,
            capabilityProvider)
    {
    }
}

public sealed class PostgreSqlDialectProvider : SqlDialectProviderBase
{
    public static PostgreSqlDialectProvider Instance { get; } = new();

    public PostgreSqlDialectProvider(IDialectCapabilityProvider? capabilityProvider = null)
        : base(
            DatabaseDialect.PostgreSql,
            TargetSqlDialect.PostgreSql,
            "PostgreSql",
            ["postgres", "postgresql", "pgsql", "npgsql"],
            supportsTreeCompile: true,
            supportsCdc: true,
            maxParameterCount: 10000,
            capabilityProvider)
    {
    }
}

public sealed class SqliteDialectProvider : SqlDialectProviderBase
{
    public static SqliteDialectProvider Instance { get; } = new();

    public SqliteDialectProvider(IDialectCapabilityProvider? capabilityProvider = null)
        : base(
            DatabaseDialect.Sqlite,
            TargetSqlDialect.Sqlite,
            "Sqlite",
            ["sqlite", "sqlite3"],
            supportsTreeCompile: true,
            supportsCdc: true,
            maxParameterCount: 999,
            capabilityProvider)
    {
    }
}

public sealed class OracleDialectProvider : SqlDialectProviderBase
{
    public static OracleDialectProvider Instance { get; } = new();

    public OracleDialectProvider(IDialectCapabilityProvider? capabilityProvider = null)
        : base(
            DatabaseDialect.Oracle,
            TargetSqlDialect.Oracle,
            "Oracle",
            ["oracle", "oracledb", "odp"],
            supportsTreeCompile: false,
            supportsCdc: false,
            maxParameterCount: 1000,
            capabilityProvider)
    {
    }
}

public sealed class DatabricksDialectProvider : SqlDialectProviderBase
{
    public static DatabricksDialectProvider Instance { get; } = new();

    public DatabricksDialectProvider(IDialectCapabilityProvider? capabilityProvider = null)
        : base(
            DatabaseDialect.Databricks,
            TargetSqlDialect.Databricks,
            "Databricks",
            ["databricks", "spark", "sparksql"],
            supportsTreeCompile: false,
            supportsCdc: false,
            maxParameterCount: 1000,
            capabilityProvider)
    {
    }
}
