namespace Autheris.Application.Procedures.Tools;

using System;
using System.Collections.Generic;
using System.Text;
using Autheris.Domain.Model;

public sealed record ProcedureOutputColumnInfo(
    string Name,
    string SqlType,
    string? SourceSchema,
    string? SourceTable,
    string? SourceColumn);

public sealed record ProcedureGenerationRequest(
    string Name,
    string ProcedureName,
    string? Summary,
    IReadOnlyList<ProcedureParameter> Parameters,
    IReadOnlyList<ProcedureContextBinding> ContextBindings,
    IReadOnlyList<ProcedureOutputColumnInfo> Outputs,
    IReadOnlyList<string> ReferencedTables,
    string? ResultTable = null,
    string? DdlHash = null,
    int TimeoutSeconds = 30,
    IReadOnlyList<string>? RequiredRoles = null,
    ProcedureKind Kind = ProcedureKind.Procedure);

public interface IProcedureYamlGenerator
{
    string GenerateYaml(ProcedureGenerationRequest request);
}

/// <summary>
/// Offline tool helper for generating contract-first .proc.yaml declarations.
/// Enables compile-time / CI/CD extraction of procedure signatures, first result-set profiling,
/// and source table mappings so the runtime gateway requires zero metadata rights on the database (least privilege).
/// </summary>
public sealed class ProcedureYamlGenerator : IProcedureYamlGenerator
{
    public string GenerateYaml(ProcedureGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProcedureName);

        var sb = new StringBuilder();
        sb.AppendLine($"name: {request.Name}");
        sb.AppendLine($"procedure: {request.ProcedureName}");
        if (!string.IsNullOrWhiteSpace(request.Summary))
        {
            sb.AppendLine($"summary: \"{EscapeYaml(request.Summary)}\"");
        }
        sb.AppendLine($"kind: {(request.Kind == ProcedureKind.TableValuedFunction ? "tvf" : "procedure")}");
        sb.AppendLine("validation: declared");
        sb.AppendLine("mode: read");
        sb.AppendLine($"timeout: {Math.Max(1, request.TimeoutSeconds)}");

        if (!string.IsNullOrWhiteSpace(request.ResultTable))
        {
            sb.AppendLine($"result_table: {request.ResultTable}");
        }

        if (request.ReferencedTables.Count > 0)
        {
            sb.AppendLine("referenced_tables:");
            foreach (var table in request.ReferencedTables)
            {
                sb.AppendLine($"  - {table}");
            }
        }

        if (request.RequiredRoles != null && request.RequiredRoles.Count > 0)
        {
            sb.AppendLine("required_roles:");
            foreach (var role in request.RequiredRoles)
            {
                sb.AppendLine($"  - {role}");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.DdlHash))
        {
            sb.AppendLine("integrity:");
            sb.AppendLine($"  ddl_hash: \"{EscapeYaml(request.DdlHash)}\"");
        }

        if (request.Parameters.Count > 0 || request.ContextBindings.Count > 0)
        {
            sb.AppendLine("parameters:");
            foreach (var p in request.Parameters)
            {
                sb.AppendLine($"  - name: {p.Name}");
                sb.AppendLine($"    type: {p.SqlType}");
                sb.AppendLine($"    required: {(p.IsRequired ? "true" : "false")}");
                if (!string.IsNullOrWhiteSpace(p.Description))
                {
                    sb.AppendLine($"    description: \"{EscapeYaml(p.Description)}\"");
                }
            }

            foreach (var c in request.ContextBindings)
            {
                sb.AppendLine($"  - name: {c.ParameterName.TrimStart('@')}");
                string contextStr = c.Key switch
                {
                    ProcedureContextKey.TenantId => "tenant_id",
                    ProcedureContextKey.UserSid => "user_sid",
                    ProcedureContextKey.Purpose => "purpose",
                    _ => c.Key.ToString().ToLowerInvariant()
                };
                sb.AppendLine($"    context: {contextStr}");
            }
        }

        if (request.Outputs.Count > 0)
        {
            sb.AppendLine("outputs:");
            foreach (var outCol in request.Outputs)
            {
                sb.AppendLine($"  - name: {outCol.Name}");
                sb.AppendLine($"    type: {outCol.SqlType}");
                if (!string.IsNullOrWhiteSpace(outCol.SourceTable) && !string.IsNullOrWhiteSpace(outCol.SourceColumn))
                {
                    string fullTable = string.IsNullOrWhiteSpace(outCol.SourceSchema)
                        ? outCol.SourceTable
                        : $"{outCol.SourceSchema}.{outCol.SourceTable}";
                    sb.AppendLine($"    source_table: {fullTable}");
                    sb.AppendLine($"    source_column: {outCol.SourceColumn}");
                }
            }
        }

        return sb.ToString();
    }

    private static string EscapeYaml(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);
}
