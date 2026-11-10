namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

public static partial class CatalogApiEndpoints
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$#]{0,127}\z")]
    private static partial System.Text.RegularExpressions.Regex DefinitionColumnNameRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z][a-z0-9 _]*(\((\d+(,\s*\d+)?|max)\))?\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DefinitionNativeTypeRegex();

    internal static async Task<IResult> PutDatasetDefinitionHandler(
        string id,
        CatalogDatasetDefinitionRequest? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Common.Interfaces.ITableMetadataRepository metadataRepository,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Audit.IAuditWriter auditWriter,
        HttpContext httpContext,
        CancellationToken ct)
    {
        httpContext.Request.ApplyMaxRequestBodySize(1024 * 1024);

        if (!TableIdentifier.TryParse(id.Replace('/', '.'), out var tableId))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The dataset identifier is invalid.", httpContext);
        }

        if (request == null)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The definition request body is required.", httpContext);
        }

        if (request.SchemaVersion != 1)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Only schemaVersion 1 is supported.", httpContext);
        }

        if (string.IsNullOrWhiteSpace(request.DataSource))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The dataSource is required.", httpContext);
        }

        if (!string.Equals(request.DataSource.Trim(), tableId.Domain, StringComparison.OrdinalIgnoreCase))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The dataSource does not match the dataset identifier domain.", httpContext);
        }

        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind is not ("table" or "view"))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The kind must be 'table' or 'view'.", httpContext);
        }

        if (CatalogSourceNameValidator.IsReserved(tableId.Domain, tableId.Schema, tableId.TableName))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.CatalogReservedSourceName, "The dataset references a reserved system schema, catalog or dictionary view name.", httpContext);
        }

        if (request.Columns == null || request.Columns.Count == 0)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "At least one column is required.", httpContext);
        }

        if (request.Columns.Count > 1024)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The column count exceeds the limit of 1024.", httpContext);
        }

        if (request.Indexes is { Count: > 32 })
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The index count exceeds the limit of 32.", httpContext);
        }

        var colNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in request.Columns)
        {
            if (string.IsNullOrWhiteSpace(col.Name) || !DefinitionColumnNameRegex().IsMatch(col.Name))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "A column name is invalid.", httpContext);
            }

            if (!colNames.Add(col.Name))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Duplicate column names are not allowed.", httpContext);
            }

            if (string.IsNullOrWhiteSpace(col.NativeType) || !DefinitionNativeTypeRegex().IsMatch(col.NativeType.Trim()))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "A column declared an invalid native type.", httpContext);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.TenantColumn) && !colNames.Contains(request.TenantColumn))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The declared tenant column does not exist among the columns.", httpContext);
        }

        var pkCols = new List<string>();
        var pkDirs = new List<KeyColumnDirection>();
        if (request.PrimaryKey is { Count: > 0 } pkList)
        {
            foreach (var item in pkList)
            {
                if (!KeyColumnSpec.TryParse(item, out var colName, out var dir))
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The primary key column specification is invalid.", httpContext);
                }

                if (!colNames.Contains(colName))
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "A primary key column does not exist among the declared columns.", httpContext);
                }

                pkCols.Add(colName);
                pkDirs.Add(dir);
            }
        }

        var indexDefs = new List<TableIndexDefinition>();
        if (request.Indexes is { Count: > 0 } idxList)
        {
            foreach (var idx in idxList)
            {
                if (string.IsNullOrWhiteSpace(idx.Name))
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "An index name is required.", httpContext);
                }

                if (idx.Columns == null || idx.Columns.Count == 0)
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "An index must specify at least one column.", httpContext);
                }

                var idxCols = new List<string>();
                var idxDirs = new List<KeyColumnDirection>();
                foreach (var colSpec in idx.Columns)
                {
                    if (!KeyColumnSpec.TryParse(colSpec, out var colName, out var dir))
                    {
                        return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The index column specification is invalid.", httpContext);
                    }

                    if (!colNames.Contains(colName))
                    {
                        return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "An index column does not exist among the declared columns.", httpContext);
                    }

                    idxCols.Add(colName);
                    idxDirs.Add(dir);
                }

                indexDefs.Add(new TableIndexDefinition(idx.Name, idxCols, idx.Unique, idxDirs));
            }
        }

        var existing = await metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        TableMetadata metadata;
        if (existing != null)
        {
            var existingColsByName = existing.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
            var columns = request.Columns.Select(c => new TableColumn
            {
                Id = existingColsByName.TryGetValue(c.Name, out var exCol) ? exCol.Id : Guid.NewGuid(),
                TableId = existing.Table.Id,
                ColumnName = c.Name,
                DataType = c.NativeType.Trim(),
                Collation = c.Collation,
                LengthSemantics = c.LengthSemantics,
                Description = c.Description ?? (existingColsByName.TryGetValue(c.Name, out var ec) ? ec.Description : null)
            }).ToList();

            metadata = existing with
            {
                Columns = columns,
                TenantColumnName = request.TenantColumn ?? existing.TenantColumnName,
                PrimaryKeyColumns = pkCols,
                PrimaryKeyDirections = pkDirs,
                Indexes = indexDefs,
                ReplaceKeyDefinition = true,
                ReplaceColumns = true
            };
        }
        else
        {
            var tableGuid = Guid.NewGuid();
            var columns = request.Columns.Select(c => new TableColumn
            {
                Id = Guid.NewGuid(),
                TableId = tableGuid,
                ColumnName = c.Name,
                DataType = c.NativeType.Trim(),
                Collation = c.Collation,
                LengthSemantics = c.LengthSemantics,
                Description = c.Description
            }).ToList();

            metadata = new TableMetadata
            {
                Identifier = tableId,
                Table = new Table
                {
                    Id = tableGuid,
                    SourceName = tableId.Domain,
                    SchemaName = tableId.Schema,
                    TableName = tableId.TableName,
                    SourceType = request.DataSource,
                    DisplayName = tableId.TableName,
                    Sensitivity = "Confidential",
                    RequiresFourEyes = false,
                    IsActive = true,
                    DataSourceType = DataSourceType.Sql
                },
                Columns = columns,
                TenantColumnName = request.TenantColumn,
                PrimaryKeyColumns = pkCols,
                PrimaryKeyDirections = pkDirs,
                Indexes = indexDefs,
                ReplaceKeyDefinition = true,
                ReplaceColumns = true
            };
        }

        try
        {
            metadata = TableKeyDefinitionValidator.Validate(metadata);
        }
        catch (TableDefinitionException)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The table key or index definition is invalid.", httpContext);
        }

        await metadataRepository.UpsertTableMetadataAsync(metadata, ct).ConfigureAwait(false);

        var actorSid = httpContext.User.GetUserSid()?.Value ?? httpContext.User.Identity?.Name ?? "system-admin";
        await auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = EndpointSecurity.GetRequestTenant(httpContext),
            EventType = AuditEventTypes.CatalogKeyDefinitionChanged,
            ActorSid = actorSid,
            TargetTable = tableId.ToString(),
            Decision = "ALLOW",
            TraceId = httpContext.TraceIdentifier,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                datasetId = tableId.ToString(),
                action = existing == null ? "create" : "replace",
                columns = metadata.Columns.Count,
                primaryKey = pkCols,
                indexes = indexDefs.Select(i => i.Name).ToArray()
            })
        }, CancellationToken.None).ConfigureAwait(false);

        return Results.Ok(new
        {
            datasetId = tableId.ToString(),
            domain = tableId.Domain,
            schema = tableId.Schema,
            table = tableId.TableName,
            status = "saved"
        });
    }
}

public sealed record CatalogDatasetDefinitionRequest(
    int SchemaVersion,
    string DataSource,
    string Kind,
    System.Collections.Generic.IReadOnlyList<CatalogDatasetDefinitionColumn> Columns,
    string? TenantColumn = null,
    System.Collections.Generic.IReadOnlyList<string>? PrimaryKey = null,
    System.Collections.Generic.IReadOnlyList<CatalogDatasetDefinitionIndex>? Indexes = null);

public sealed record CatalogDatasetDefinitionColumn(
    string Name,
    string NativeType,
    string? Collation = null,
    string? LengthSemantics = null,
    string? Description = null);

public sealed record CatalogDatasetDefinitionIndex(
    string Name,
    System.Collections.Generic.IReadOnlyList<string> Columns,
    bool Unique = false);

public sealed record AdminSetDatasetStateDto(string State, string? Reason = null);

public sealed record DatasetIndexDto(
    string Name,
    System.Collections.Generic.IReadOnlyList<string> Columns,
    bool? IsUnique = null,
    bool? Unique = null,
    System.Collections.Generic.IReadOnlyList<string?>? Directions = null);

public sealed record SetDatasetKeysDto(
    System.Collections.Generic.IReadOnlyList<string>? PrimaryKey = null,
    System.Collections.Generic.IReadOnlyList<string>? PrimaryKeyColumns = null,
    System.Collections.Generic.IReadOnlyList<DatasetIndexDto>? Indexes = null,
    System.Collections.Generic.IReadOnlyList<string?>? PrimaryKeyDirections = null);


/// <summary>Issue #111 (C-4): replace the admin-source tags of a dataset.</summary>
public sealed record SetDatasetTagsDto(System.Collections.Generic.IReadOnlyList<string>? Tags);

/// <summary>Issue #111 (C-4): add and remove admin-source tags.</summary>
public sealed record ChangeDatasetTagsDto(System.Collections.Generic.IReadOnlyList<string>? Add, System.Collections.Generic.IReadOnlyList<string>? Remove);

/// <summary>Issue #111 (C-4): the requested tier name; parsed by the service, never bound to an enum.</summary>
public sealed record SetDatasetVisibilityDto(string? Visibility);

/// <summary>Issue #448: the requested privacy settings (minimum group size and non-personal flag).</summary>
public sealed record SetDatasetPrivacyDto(string? MinGroupSize = null, bool? NonPersonal = null);

public sealed record DatasetTagDto(string Tag, string Source);

public sealed record DatasetCatalogSettingsDto(
    string DatasetId,
    System.Collections.Generic.IReadOnlyList<DatasetTagDto> Tags,
    string Visibility,
    bool TierEffective,
    int? MinGroupSize = null,
    bool? NonPersonal = null,
    string? Warning = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    System.Collections.Generic.IReadOnlyList<string>? RequiredFilterColumns = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<string>>? RequiredFilterColumnGroups = null);

public sealed record SetDatasetRequiredFiltersDto(
    System.Collections.Generic.IReadOnlyList<string>? RequiredFilterColumns = null,
    System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<string>>? RequiredFilterColumnGroups = null);

public sealed record SetRelationCrossDataSourceDto(bool AllowCrossDataSource);
