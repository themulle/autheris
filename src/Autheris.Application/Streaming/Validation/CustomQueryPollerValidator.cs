namespace Autheris.Application.Streaming.Validation;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using Autheris.Application.Streaming;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

public sealed record CustomQueryPollerValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static CustomQueryPollerValidationResult Success() => new(true, []);
    public static CustomQueryPollerValidationResult Fail(string error) => new(false, [error]);
    public static CustomQueryPollerValidationResult Fail(IReadOnlyList<string> errors) => new(false, errors);
}

/// <summary>
/// Fail-closed AST DQL safety gate and configuration validator for multi-watermark query pollers (101 D-1).
/// </summary>
public sealed class CustomQueryPollerValidator
{
    private static readonly Regex SelectIntoRegex = new(@"\bINTO\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    private static readonly Regex ProceduralExecRegex = new(@"\b(EXEC|EXECUTE|CALL|sp_executesql)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));

    private readonly ISqlEngine _sqlEngine;

    public CustomQueryPollerValidator() : this(FastSqlEngine.Default) { }

    public CustomQueryPollerValidator(ISqlEngine? sqlEngine)
    {
        _sqlEngine = sqlEngine ?? FastSqlEngine.Default;
    }

    /// <summary>
    /// Resolves and verifies that the configured data source provider maps to a supported relational dialect
    /// for custom query polling. Databricks and unrecognized providers are rejected (fail closed).
    /// </summary>
    public static bool TryResolveSupportedDialect(string? provider, out DatabaseDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(provider) || !DataSourceProvider.TryResolveDialect(provider, out dialect))
        {
            dialect = default;
            return false;
        }

        return dialect is DatabaseDialect.PostgreSql
            or DatabaseDialect.SqlServer
            or DatabaseDialect.Oracle;
    }

    /// <summary>
    /// Computes a deterministic SHA-256 signature over the query template and governed bindings
    /// for cache invalidation across option reloads.
    /// </summary>
    public static string ComputeTemplateHash(CustomQueryPollerStreamOptions poller)
    {
        ArgumentNullException.ThrowIfNull(poller);

        var sb = new StringBuilder(256);
        sb.Append(poller.StreamName).Append('\0');
        sb.Append(poller.Table).Append('\0');
        sb.Append(poller.DataSource).Append('\0');
        sb.Append(poller.OwnerTenantId).Append('\0');
        sb.Append(poller.QueryTemplate).Append('\0');
        if (poller.KeyColumns is not null)
        {
            foreach (var kc in poller.KeyColumns)
            {
                sb.Append(kc).Append('\u001f');
            }
        }
        sb.Append('\0');
        if (poller.Watermarks is not null)
        {
            foreach (var wm in poller.Watermarks)
            {
                sb.Append(wm.Name).Append(':')
                  .Append((int)wm.Type).Append(':')
                  .Append(wm.ParameterName).Append(':')
                  .Append(wm.Column).Append(':')
                  .Append(wm.InitialValue).Append('\u001f');
            }
        }
        sb.Append('\0');
        if (poller.SoftDelete is { } sd)
        {
            sb.Append(sd.Enabled ? '1' : '0').Append(':')
              .Append((int)sd.Strategy).Append(':')
              .Append(sd.Column).Append(':')
              .Append(sd.DeletedValue);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    public CustomQueryPollerValidationResult ValidatePollerOptions(CustomQueryPollerStreamOptions poller)
    {
        if (poller is null)
        {
            return CustomQueryPollerValidationResult.Fail("Poller options cannot be null.");
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(poller.StreamName))
        {
            errors.Add("StreamName is required.");
        }

        if (string.IsNullOrWhiteSpace(poller.QueryTemplate))
        {
            errors.Add("QueryTemplate cannot be empty.");
            return CustomQueryPollerValidationResult.Fail(errors);
        }

        if (poller.KeyColumns == null || poller.KeyColumns.Count == 0)
        {
            errors.Add("At least one declared primary key column is required.");
        }
        else
        {
            foreach (var pk in poller.KeyColumns)
            {
                if (!SqlIdentifierGuard.IsValidIdentifier(pk, maxLength: null, allowLeadingDigit: true))
                {
                    errors.Add($"Declared primary key column '{pk}' is invalid. Only alphanumeric characters and underscores are allowed.");
                }
            }
        }

        if (poller.Watermarks == null || poller.Watermarks.Count == 0)
        {
            errors.Add("At least one watermark dimension is required.");
        }
        else
        {
            foreach (var wm in poller.Watermarks)
            {
                ValidateDimension(wm, poller.QueryTemplate, errors);
            }
        }

        if (poller.SoftDelete is { Enabled: true } sd)
        {
            if (!SqlIdentifierGuard.IsValidIdentifier(sd.Column, maxLength: null, allowLeadingDigit: true))
            {
                errors.Add($"Soft-delete column '{sd.Column}' is invalid. Only alphanumeric characters and underscores are allowed.");
            }

            if (sd.Strategy == SoftDeleteStrategy.StringStatus && string.IsNullOrWhiteSpace(sd.DeletedValue))
            {
                errors.Add("Soft-delete strategy StringStatus requires a non-empty DeletedValue.");
            }
        }

        // Pre-checks for obvious forbidden procedural calls or SELECT INTO
        if (SelectIntoRegex.IsMatch(poller.QueryTemplate))
        {
            errors.Add("SELECT ... INTO statements are strictly prohibited.");
        }

        if (ProceduralExecRegex.IsMatch(poller.QueryTemplate))
        {
            errors.Add("Stored procedure execution (EXEC, EXECUTE, CALL, sp_executesql) is strictly prohibited.");
        }

        // Multi-statement semicolon check in raw text
        var trimmedQuery = poller.QueryTemplate.Trim();
        var semicolonIndex = trimmedQuery.IndexOf(';', StringComparison.Ordinal);
        if (semicolonIndex >= 0 && semicolonIndex < trimmedQuery.Length - 1)
        {
            var remainder = trimmedQuery[(semicolonIndex + 1)..].Trim();
            if (!string.IsNullOrEmpty(remainder))
            {
                errors.Add("Multiple SQL statements detected. Strictly single statement allowed.");
            }
        }

        // AST DQL Safety Gate via ISqlEngine
        try
        {
            // Normalize named parameters (@param or :param) to ? for ISqlEngine ANTLR AST validation
            var queryForAst = Regex.Replace(poller.QueryTemplate, @"[@:][a-zA-Z0-9_]+", "?");

            var (tree, tokens) = _sqlEngine.Parse(queryForAst.AsMemory());

            // Verify EOF immediately following the statement
            var tokenCount = tokens.Size;
            var eofFound = false;
            for (var i = 0; i < tokenCount; i++)
            {
                var token = tokens.Get(i);
                if (token.Type == SqlBaseLexer.Eof)
                {
                    eofFound = true;
                    break;
                }
            }

            if (!eofFound)
            {
                errors.Add("SQL statement does not properly terminate with EOF.");
            }

            // Verify root statement type
            var stmt = tree.statement();
            if (stmt is not SqlBaseParser.StatementDefaultContext stmtDefault || stmtDefault.rootQueryWithSession()?.rootQuery()?.query() is null)
            {
                errors.Add("Query root is not a standard SELECT query.");
            }

            // Analyze statement metadata
            var metadata = _sqlEngine.Analyze(queryForAst.AsMemory());
            if (metadata.StatementType != SqlStatementType.Select)
            {
                errors.Add($"Forbidden statement type '{metadata.StatementType}'. Only SELECT statements are permitted.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"AST validation failed closed: {ex.Message}");
        }

        return errors.Count == 0
            ? CustomQueryPollerValidationResult.Success()
            : CustomQueryPollerValidationResult.Fail(errors);
    }

    public void ValidateOrThrow(CustomQueryPollerStreamOptions poller)
    {
        var result = ValidatePollerOptions(poller);
        if (!result.IsValid)
        {
            throw new GovernedCdcConfigurationException($"Governed query poller validation failed: {string.Join("; ", result.Errors)}");
        }
    }

    private static void ValidateDimension(WatermarkDimensionDefinition wm, string queryTemplate, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(wm.Name))
        {
            errors.Add("Watermark dimension Name cannot be empty.");
            return;
        }

        // Whitelist pattern ^[a-zA-Z0-9_]+$
        if (!SqlIdentifierGuard.IsValidIdentifier(wm.Name, maxLength: null, allowLeadingDigit: true))
        {
            errors.Add($"Watermark dimension Name '{wm.Name}' contains invalid characters. Must match ^[a-zA-Z0-9_]+$.");
        }

        var paramName = string.IsNullOrWhiteSpace(wm.ParameterName) ? "p_wm_" + wm.Name : wm.ParameterName.TrimStart('@');
        if (!SqlIdentifierGuard.IsValidIdentifier(paramName, maxLength: null, allowLeadingDigit: true))
        {
            errors.Add($"Watermark dimension ParameterName '{wm.ParameterName}' contains invalid characters. Must match ^[a-zA-Z0-9_]+$.");
        }

        // Silent-NULL Defense
        if (string.IsNullOrWhiteSpace(wm.InitialValue))
        {
            errors.Add($"Watermark dimension '{wm.Name}' must declare a non-empty InitialValue to prevent SQL three-valued logic silent NULL starvation.");
        }
        else
        {
            if (!IsValidInitialValue(wm.InitialValue, wm.Type))
            {
                errors.Add($"Watermark dimension '{wm.Name}' InitialValue '{wm.InitialValue}' cannot be parsed as {wm.Type}.");
            }
        }

        // Verify parameter token exists in query template
        var requiredToken = "@" + paramName;
        if (!queryTemplate.Contains(requiredToken, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"QueryTemplate does not contain required parameter token '{requiredToken}' for watermark '{wm.Name}'.");
        }
    }

    private static bool IsValidInitialValue(string value, WatermarkDimensionType type)
    {
        return type switch
        {
            WatermarkDimensionType.DateTimeOffset => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            WatermarkDimensionType.Int64 => long.TryParse(value, CultureInfo.InvariantCulture, out _),
            WatermarkDimensionType.Int32 => int.TryParse(value, CultureInfo.InvariantCulture, out _),
            WatermarkDimensionType.String => true,
            _ => true
        };
    }
}
