namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

public static class DataSourceDialectValidator
{
    private static readonly FrozenSet<DatabaseDialect> Admitted = FrozenSet.ToFrozenSet(
    [
        DatabaseDialect.PostgreSql,
        DatabaseDialect.SqlServer,
        DatabaseDialect.Oracle,
        DatabaseDialect.Databricks,
        DatabaseDialect.Sqlite
    ]);

    public static readonly string[] AdmittedDialects = Admitted
        .Select(static d => d.ToString())
        .OrderBy(static n => n, StringComparer.Ordinal)
        .ToArray();

    public static void Validate(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DataSources?.Connections is null)
        {
            return;
        }

        foreach (var (name, connection) in options.DataSources.Connections)
        {
            if (connection is null)
            {
                throw new ValidationException($"Data source '{name}' configuration cannot be null.");
            }

            string provider = connection.Provider ?? string.Empty;
            if (!DataSourceProvider.TryResolveDialect(provider, out var dialect) ||
                !Admitted.Contains(dialect))
            {
                throw new UnsupportedDataSourceDialectException(name, provider, AdmittedDialects);
            }
        }
    }
}

public sealed class UnsupportedDataSourceDialectException : ValidationException
{
    public string DataSourceName { get; }
    public string Provider { get; }

    public UnsupportedDataSourceDialectException(string dataSourceName, string provider, IReadOnlyCollection<string> admittedDialects)
        : base($"Data source '{dataSourceName}' specifies unsupported provider/dialect '{provider}'. Admitted dialects are: {string.Join(", ", admittedDialects)}.")
    {
        DataSourceName = dataSourceName;
        Provider = provider;
    }
}
