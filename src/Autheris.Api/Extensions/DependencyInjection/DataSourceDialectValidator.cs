namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

public static class DataSourceDialectValidator
{
    public static readonly string[] AdmittedDialects =
    [
        nameof(DatabaseDialect.PostgreSql),
        nameof(DatabaseDialect.SqlServer),
        nameof(DatabaseDialect.Oracle),
        nameof(DatabaseDialect.Databricks),
        nameof(DatabaseDialect.Sqlite)
    ];

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
                continue;
            }

            string provider = connection.Provider ?? string.Empty;
            if (!DataSourceProvider.TryResolveDialect(provider, out var dialect) ||
                dialect is not (DatabaseDialect.PostgreSql or DatabaseDialect.SqlServer or DatabaseDialect.Oracle or DatabaseDialect.Databricks or DatabaseDialect.Sqlite))
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
