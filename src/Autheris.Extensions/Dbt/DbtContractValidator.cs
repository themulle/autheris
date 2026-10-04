namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class DbtContractValidator(
    ITableMetadataRepository metadataRepository,
    ILogger<DbtContractValidator> logger) : IDbtContractValidator
{
    private readonly ITableMetadataRepository _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
    private readonly ILogger<DbtContractValidator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<DbtContractValidationResult> ValidateContractsFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidateSafeFilePath(filePath);
        var fullPath = Path.GetFullPath(filePath);
        ValidateSafeFilePath(fullPath);

        if (!File.Exists(fullPath))
        {
            return new DbtContractValidationResult(false, 0, [], [$"File not found at: {fullPath}"]);
        }

        await using var stream = File.OpenRead(fullPath);
        return await ValidateContractsStreamAsync(stream, ct).ConfigureAwait(false);
    }

    public async Task<DbtContractValidationResult> ValidateContractsStreamAsync(Stream manifestStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifestStream);

        IReadOnlyList<DbtModelDefinition> models;
        try
        {
            models = await DbtArtifactStreamingParser.ParseManifestStreamAsync(manifestStream, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse dbt manifest stream for contract validation.");
            return new DbtContractValidationResult(false, 0, [], [$"Manifest parse failure: {ex.Message}"]);
        }

        var breakingChanges = new List<DbtContractBreakingChange>();
        var warnings = new List<string>();
        var validatedCount = 0;

        foreach (var model in models.Where(m => m.ContractEnforced))
        {
            ct.ThrowIfCancellationRequested();
            validatedCount++;

            var tableId = model.ToTableIdentifier();
            var existingTable = await _metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
            if (existingTable == null)
            {
                warnings.Add($"Table '{tableId}' not found in active metadata repository. Considered as new model contract.");
                continue;
            }

            // 1. Check for dropped columns
            foreach (var existingCol in existingTable.Columns)
            {
                if (!model.Columns.ContainsKey(existingCol.ColumnName))
                {
                    breakingChanges.Add(new DbtContractBreakingChange(
                        Table: tableId,
                        ColumnName: existingCol.ColumnName,
                        ChangeType: "DROPPED_COLUMN",
                        ExistingType: existingCol.DataType,
                        ProposedType: null,
                        Description: $"Column '{existingCol.ColumnName}' was removed from enforced contract model '{model.Name}', but exists in active gateway schema."
                    ));
                }
            }

            // 2. Check for data type mismatches
            foreach (var (colName, colDef) in model.Columns)
            {
                var existingCol = existingTable.GetColumn(colName);
                if (existingCol != null && !string.IsNullOrWhiteSpace(colDef.DataType) && !string.IsNullOrWhiteSpace(existingCol.DataType))
                {
                    if (!AreTypesCompatible(existingCol.DataType, colDef.DataType))
                    {
                        breakingChanges.Add(new DbtContractBreakingChange(
                            Table: tableId,
                            ColumnName: colName,
                            ChangeType: "DATA_TYPE_MISMATCH",
                            ExistingType: existingCol.DataType,
                            ProposedType: colDef.DataType,
                            Description: $"Column '{colName}' data type changed from '{existingCol.DataType}' to '{colDef.DataType}', which may break existing GraphQL clients."
                        ));
                    }
                }
            }
        }

        var isCompatible = breakingChanges.Count == 0;
        _logger.LogInformation("Completed dbt contract validation: {Validated} models, {Breaking} breaking changes, IsCompatible={IsCompatible}.",
            validatedCount, breakingChanges.Count, isCompatible);

        return new DbtContractValidationResult(
            IsCompatible: isCompatible,
            ValidatedModelsCount: validatedCount,
            BreakingChanges: breakingChanges,
            Warnings: warnings
        );
    }

    private static bool AreTypesCompatible(string existingType, string proposedType)
    {
        var e = NormalizeType(existingType);
        var p = NormalizeType(proposedType);
        return string.Equals(e, p, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeType(string type)
    {
        var t = type.Trim().ToLowerInvariant();
        var parenIndex = t.IndexOf('(');
        if (parenIndex > 0)
        {
            t = t[..parenIndex].Trim();
        }
        return t switch
        {
            "int" or "integer" or "int4" => "integer",
            "bigint" or "int8" => "bigint",
            "varchar" or "text" or "string" or "nvarchar" or "char" => "string",
            "bool" or "boolean" => "boolean",
            "float" or "double" or "real" or "numeric" or "decimal" => "decimal",
            "timestamp" or "timestamptz" or "datetime" or "datetime2" => "timestamp",
            _ => t
        };
    }

    private static void ValidateSafeFilePath(string fullPath)
    {
        if (fullPath.Contains('\0'))
        {
            throw new System.Security.SecurityException("File path must not contain null bytes.");
        }

        var ext = Path.GetExtension(fullPath);
        if (!string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.SecurityException($"Invalid file extension '{ext}'. Only JSON dbt artifacts (.json) are permitted.");
        }

        var normalized = fullPath.Replace('\\', '/').ToLowerInvariant();
        if (normalized.StartsWith("/etc") ||
            normalized.StartsWith("/proc") ||
            normalized.StartsWith("/sys") ||
            normalized.StartsWith("/dev") ||
            normalized.StartsWith("/var") ||
            normalized.StartsWith("/run") ||
            normalized.StartsWith("/root") ||
            normalized.StartsWith("/bin") ||
            normalized.StartsWith("/sbin") ||
            normalized.StartsWith("/usr") ||
            normalized.Contains("/.ssh") ||
            normalized.Contains("/appsettings") ||
            normalized.Contains("windows/system32"))
        {
            throw new System.Security.SecurityException($"Access to restricted path '{fullPath}' is strictly forbidden.");
        }
    }
}
