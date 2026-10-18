namespace Autheris.Api.Configuration.Validation;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #132 (PLAN-DATA-132, SEC-DATA132-06): Fail-closed start-up validation of co-located SQL Server data sources.
/// Enforces symmetric providers (SQL Server only), normalized server host, port, instance, credentials,
/// and session isolation settings across all members of each administrative CoLocationGroup.
/// Groups with fewer than 2 members or asymmetric configurations abort startup immediately.
/// </summary>
public sealed class CoLocationOptionsValidator : IValidateOptions<GatewayOptions>
{
    private readonly IConfiguration? _configuration;
    private readonly ILogger<CoLocationOptionsValidator>? _logger;

    public CoLocationOptionsValidator(IConfiguration? configuration = null, ILogger<CoLocationOptionsValidator>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var connections = options.DataSources?.Connections;
        if (connections == null || connections.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        // Group data sources by declared CoLocationGroup
        var coLocatedGroups = connections
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value?.CoLocationGroup))
            .GroupBy(kvp => kvp.Value.CoLocationGroup!.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in coLocatedGroups)
        {
            string groupName = group.Key;
            var members = group.ToList();

            // 1. Cardinality check: must contain >= 2 members
            if (members.Count < 2)
            {
                failures.Add($"Co-location group '{groupName}' must contain at least 2 data sources, but found {members.Count}.");
                continue;
            }

            // 2. Provider check: provider must be SqlServer
            foreach (var (sourceName, conn) in members)
            {
                if (!DataSourceProvider.Is(conn.Provider, DatabaseDialect.SqlServer))
                {
                    failures.Add(
                        $"Co-location group '{groupName}' contains data source '{sourceName}' with provider '{conn.Provider}'. " +
                        "Only 'SqlServer' is supported for co-located data sources.");
                }

                // Disallow delegated authentication modes
                if (conn.WindowsAuth != null)
                {
                    var modeStr = conn.WindowsAuth.Mode.ToString();
                    if (modeStr.Contains("Delegated", StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add(
                            $"Co-location group '{groupName}' contains data source '{sourceName}' with prohibited delegated authentication mode '{modeStr}'.");
                    }
                }
            }

            // 3. Symmetric Host, Instance, Credentials, and Session options across members
            var parsedMembers = new List<(string Name, DataSourceConnectionOptions Conn, Microsoft.Data.SqlClient.SqlConnectionStringBuilder? Builder)>();
            foreach (var (sourceName, conn) in members)
            {
                Microsoft.Data.SqlClient.SqlConnectionStringBuilder? builder = null;
                if (!string.IsNullOrWhiteSpace(conn.ConnectionString))
                {
                    try
                    {
                        builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(conn.ConnectionString);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"Co-location group '{groupName}' contains data source '{sourceName}' with invalid connection string: {ex.Message}");
                    }
                }
                else
                {
                    failures.Add($"Co-location group '{groupName}' contains data source '{sourceName}' with missing connection string.");
                }

                parsedMembers.Add((sourceName, conn, builder));
            }

            if (parsedMembers.Count >= 2 && parsedMembers.All(m => m.Builder != null))
            {
                var first = parsedMembers[0];
                string firstHost = NormalizeHost(first.Builder!.DataSource, first.Conn.ServerName);
                string firstPort = ExtractPort(first.Builder.DataSource);
                string firstInstance = ExtractInstance(first.Builder.DataSource);

                bool firstIntegrated = first.Builder.IntegratedSecurity;
                string firstUser = (first.Builder.UserID ?? string.Empty).Trim().ToLowerInvariant();
                string firstPass = first.Builder.Password ?? string.Empty;
                var firstWin = first.Conn.WindowsAuth;

                bool firstReadUncommitted = first.Conn.ReadUncommitted;
                int firstTimeout = first.Conn.CommandTimeoutSeconds;

                for (int i = 1; i < parsedMembers.Count; i++)
                {
                    var current = parsedMembers[i];
                    string currentHost = NormalizeHost(current.Builder!.DataSource, current.Conn.ServerName);
                    string currentPort = ExtractPort(current.Builder.DataSource);
                    string currentInstance = ExtractInstance(current.Builder.DataSource);

                    // Host & Instance parity
                    if (!string.Equals(firstHost, currentHost, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(firstPort, currentPort, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(firstInstance, currentInstance, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add(
                            $"Co-location group '{groupName}' contains data sources with mismatched server hosts or instances: " +
                            $"'{first.Name}' ({firstHost}:{firstPort}\\{firstInstance}) vs '{current.Name}' ({currentHost}:{currentPort}\\{currentInstance}).");
                    }

                    // Credential identity
                    bool currentIntegrated = current.Builder.IntegratedSecurity;
                    string currentUser = (current.Builder.UserID ?? string.Empty).Trim().ToLowerInvariant();
                    string currentPass = current.Builder.Password ?? string.Empty;
                    var currentWin = current.Conn.WindowsAuth;

                    bool credsMatch = firstIntegrated == currentIntegrated &&
                                     string.Equals(firstUser, currentUser, StringComparison.Ordinal) &&
                                     string.Equals(firstPass, currentPass, StringComparison.Ordinal);

                    if (credsMatch && (firstWin != null || currentWin != null))
                    {
                        if (firstWin == null || currentWin == null ||
                            firstWin.Mode != currentWin.Mode ||
                            !string.Equals(firstWin.Domain, currentWin.Domain, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(firstWin.Username, currentWin.Username, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(firstWin.PasswordRef, currentWin.PasswordRef, StringComparison.Ordinal))
                        {
                            credsMatch = false;
                        }
                    }

                    if (!credsMatch)
                    {
                        failures.Add(
                            $"Co-location group '{groupName}' contains data sources with asymmetric authentication credentials or modes: " +
                            $"'{first.Name}' vs '{current.Name}'.");
                    }

                    // Session options parity
                    if (firstReadUncommitted != current.Conn.ReadUncommitted ||
                        firstTimeout != current.Conn.CommandTimeoutSeconds)
                    {
                        failures.Add(
                            $"Co-location group '{groupName}' contains data sources with mismatched session settings " +
                            $"(ReadUncommitted or CommandTimeoutSeconds): '{first.Name}' vs '{current.Name}'.");
                    }
                }
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static string NormalizeHost(string dataSource, string? serverName)
    {
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            return serverName.Trim().ToLowerInvariant();
        }

        var ds = dataSource.Trim();
        if (ds.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            ds = ds[4..].Trim();
        }

        var commaIdx = ds.IndexOf(',');
        if (commaIdx >= 0)
        {
            ds = ds[..commaIdx].Trim();
        }

        var slashIdx = ds.IndexOf('\\');
        if (slashIdx >= 0)
        {
            ds = ds[..slashIdx].Trim();
        }

        return ds.ToLowerInvariant();
    }

    private static string ExtractPort(string dataSource)
    {
        var ds = dataSource.Trim();
        if (ds.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            ds = ds[4..].Trim();
        }

        var commaIdx = ds.IndexOf(',');
        if (commaIdx >= 0)
        {
            return ds[(commaIdx + 1)..].Trim();
        }

        return "1433";
    }

    private static string ExtractInstance(string dataSource)
    {
        var ds = dataSource.Trim();
        if (ds.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            ds = ds[4..].Trim();
        }

        var slashIdx = ds.IndexOf('\\');
        if (slashIdx >= 0)
        {
            var end = ds.IndexOf(',', slashIdx);
            return end >= 0 ? ds[(slashIdx + 1)..end].Trim().ToLowerInvariant() : ds[(slashIdx + 1)..].Trim().ToLowerInvariant();
        }

        return string.Empty;
    }
}
