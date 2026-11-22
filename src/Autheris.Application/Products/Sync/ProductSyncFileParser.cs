namespace Autheris.Application.Products.Sync;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Reads the files of a repository commit. Every file is untrusted: counts, sizes and paths are capped, links and traversal are refused,
/// YAML is scanned for anchors, aliases, tags, multiple documents and deep nesting before it is deserialized, unknown keys are refused
/// and a declassification needs a reason. The first violation rejects the whole commit.
/// </summary>
internal static partial class ProductSyncFileParser
{
    internal const int MaxFiles = 200;
    internal const int MaxFileBytes = 64 * 1024;
    internal const int MaxTotalBytes = 2 * 1024 * 1024;
    internal const int MaxPathLength = 200;
    internal const int MaxYamlDepth = 8;
    internal const int MaxColumns = 500;

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}(/[A-Za-z0-9][A-Za-z0-9._-]{0,63}){0,7}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NameRegex();

    public static IReadOnlyList<ProductSyncEntry> Parse(IReadOnlyList<ProductSyncFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > MaxFiles)
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.TooManyFiles);
        }

        long total = 0;
        var entries = new List<ProductSyncEntry>(files.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenProducts = new HashSet<(string, string)>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i] ?? throw new ProductSyncRejectedException(ProductSyncReasons.PathInvalid, i);
            CheckPath(file, i, seenPaths);
            if (file.IsSymlink)
            {
                throw new ProductSyncRejectedException(ProductSyncReasons.Symlink, i);
            }

            var bytes = Encoding.UTF8.GetByteCount(file.Content ?? string.Empty);
            total += bytes;
            if (bytes > MaxFileBytes)
            {
                throw new ProductSyncRejectedException(ProductSyncReasons.FileTooLarge, i);
            }

            if (total > MaxTotalBytes)
            {
                throw new ProductSyncRejectedException(ProductSyncReasons.TotalTooLarge, i);
            }

            ScanYaml(file.Content ?? string.Empty, i);
            var entry = IsProcedureFile(file.Path) ? ParseProcedure(file, i) : ParseProduct(file, i);
            if (!seenProducts.Add((entry.Schema, entry.Name)))
            {
                throw new ProductSyncRejectedException(ProductSyncReasons.Duplicate, i);
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static bool IsProcedureFile(string path) =>
        path.EndsWith(".proc.yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".proc.yml", StringComparison.OrdinalIgnoreCase);

    private static bool IsProductFile(string path) =>
        path.EndsWith(".product.yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".product.yml", StringComparison.OrdinalIgnoreCase);

    private static void CheckPath(ProductSyncFile file, int index, HashSet<string> seen)
    {
        var path = file.Path;
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || !PathRegex().IsMatch(path) || path.Contains("..", StringComparison.Ordinal) || !seen.Add(path))
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.PathInvalid, index);
        }

        if (!IsProcedureFile(path) && !IsProductFile(path))
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.FileType, index);
        }
    }

    /// <summary>Refuses anchors, aliases, tags, a second document and deep nesting before any object is built (no expansion bombs, no type tags).</summary>
    private static void ScanYaml(string content, int index)
    {
        try
        {
            var parser = new Parser(new StringReader(content));
            var depth = 0;
            var documents = 0;
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case AnchorAlias:
                        throw new ProductSyncRejectedException(ProductSyncReasons.YamlUnsafe, index);
                    case DocumentStart when ++documents > 1:
                        throw new ProductSyncRejectedException(ProductSyncReasons.YamlUnsafe, index);
                    case MappingStart or SequenceStart:
                        if (++depth > MaxYamlDepth)
                        {
                            throw new ProductSyncRejectedException(ProductSyncReasons.YamlUnsafe, index);
                        }

                        break;
                    case MappingEnd or SequenceEnd:
                        depth--;
                        break;
                }

                if (parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                {
                    throw new ProductSyncRejectedException(ProductSyncReasons.YamlUnsafe, index);
                }
            }
        }
        catch (YamlException)
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.YamlInvalid, index);
        }
    }

    private static ProductSyncEntry ParseProcedure(ProductSyncFile file, int index)
    {
        ProcedureDefinition definition;
        try
        {
            var stem = Path.GetFileName(file.Path);
            stem = stem[..stem.IndexOf('.', StringComparison.Ordinal)];
            definition = ProcedureDefinitionParser.ParseYaml(file.Content, stem, allowRlsNone: false, maxTimeoutSeconds: 300);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.FieldInvalid, index);
        }

        var schema = definition.ProductSchema?.Trim().ToLowerInvariant();
        var name = definition.Name.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(definition.DataSource) || schema is null || !NameRegex().IsMatch(schema) || !NameRegex().IsMatch(name) ||
            string.IsNullOrWhiteSpace(definition.ProductOwner))
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.FieldInvalid, index);
        }

        if (definition.ClearedResultColumns.Count > 0 && string.IsNullOrWhiteSpace(definition.ClearedReason))
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.ReasonRequired, index);
        }

        var target = $"{definition.DataSource.Trim()}.{definition.ProcedureName.Trim()}".ToLowerInvariant();
        return new ProductSyncEntry(index, schema, name, DataProductKind.Procedure, target, new Sid(definition.ProductOwner.Trim()), null, null, definition.Description ?? definition.Summary, [], definition);
    }

    private static ProductSyncEntry ParseProduct(ProductSyncFile file, int index)
    {
        ProductYamlModel? model;
        try
        {
            model = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).WithDuplicateKeyChecking().Build()
                .Deserialize<ProductYamlModel>(file.Content);
        }
        catch (YamlException)
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.YamlInvalid, index);
        }

        var ownerRaw = string.IsNullOrWhiteSpace(model.OwnerGroupSid) ? model.ProductOwner : model.OwnerGroupSid;
        if (model is null || string.IsNullOrWhiteSpace(model.Definition) || string.IsNullOrWhiteSpace(ownerRaw))
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.FieldInvalid, index);
        }

        var schema = model.Schema?.Trim().ToLowerInvariant();
        var name = model.Name?.Trim().ToLowerInvariant();
        if (schema is null || name is null || !NameRegex().IsMatch(schema) || !NameRegex().IsMatch(name) ||
            !Enum.TryParse<DataProductKind>(model.Kind?.Trim() ?? "View", ignoreCase: true, out var kind) || kind != DataProductKind.View)
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.FieldInvalid, index);
        }

        var columns = ParseColumns(model.Columns, index);
        return new ProductSyncEntry(
            index, schema, name, kind, model.Definition.Trim(), new Sid(ownerRaw.Trim()), ParseLoad(model.Load, index), ParseLimits(model.Limits), model.Description, columns);
    }

    private static IReadOnlyList<ProductSyncClassification> ParseColumns(List<ColumnYamlModel>? columns, int index)
    {
        if (columns is null)
        {
            return [];
        }

        if (columns.Count > MaxColumns)
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.FieldInvalid, index);
        }

        var result = new List<ProductSyncClassification>(columns.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            if (column is null || string.IsNullOrWhiteSpace(column.Name) || !seen.Add(column.Name.Trim()) ||
                !Enum.TryParse<ProductColumnClass>(column.Class?.Trim().Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var target) || !Enum.IsDefined(target))
            {
                throw new ProductSyncRejectedException(ProductSyncReasons.FieldInvalid, index);
            }

            var reason = column.Reason?.Trim();
            if (target != ProductColumnClass.PiiMasked && (string.IsNullOrEmpty(reason) || reason.Length > ProductColumnClassifier.MaxReasonLength))
            {
                throw new ProductSyncRejectedException(ProductSyncReasons.ReasonRequired, index);
            }

            result.Add(new ProductSyncClassification(column.Name.Trim(), target, reason));
        }

        return result;
    }

    private static LoadSettings? ParseLoad(LoadYamlModel? load, int index)
    {
        if (load is null)
        {
            return null;
        }

        if (!Enum.TryParse<LoadMode>(load.Mode?.Trim().Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.FieldInvalid, index);
        }

        return new LoadSettings(mode, load.TtlMinutes ?? 0, load.IntervalMinutes is { } m ? TimeSpan.FromMinutes(m) : null, load.IdleDropMinutes ?? 0, load.MaxStalenessMinutes);
    }

    private static ProductLimits? ParseLimits(LimitsYamlModel? limits) =>
        limits is null ? null : new ProductLimits(limits.MaxRows ?? 0, limits.MaxBytes ?? 0, limits.MaxRuntimeSeconds ?? 0);
}

internal sealed class ProductYamlModel
{
    public string? Schema { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? Definition { get; set; }
    public string? OwnerGroupSid { get; set; }
    public string? ProductOwner { get; set; }
    public string? Description { get; set; }
    public LoadYamlModel? Load { get; set; }
    public LimitsYamlModel? Limits { get; set; }
    public List<ColumnYamlModel>? Columns { get; set; }
}

internal sealed class LoadYamlModel
{
    public string? Mode { get; set; }
    public int? IntervalMinutes { get; set; }
    public int? TtlMinutes { get; set; }
    public int? IdleDropMinutes { get; set; }
    public int? MaxStalenessMinutes { get; set; }
}

internal sealed class LimitsYamlModel
{
    public long? MaxRows { get; set; }
    public long? MaxBytes { get; set; }
    public int? MaxRuntimeSeconds { get; set; }
}

internal sealed class ColumnYamlModel
{
    public string? Name { get; set; }
    public string? Class { get; set; }
    public string? Reason { get; set; }
}
