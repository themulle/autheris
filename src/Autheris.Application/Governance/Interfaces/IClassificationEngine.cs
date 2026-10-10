namespace Autheris.Application.Governance.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Column metadata context for classification.
/// </summary>
public sealed record ColumnMetadataContext(
    string ColumnName,
    string DataType,
    string? Comment = null);

/// <summary>
/// Table schema context passed to the classification engine.
/// </summary>
public sealed record TableClassificationContext(
    TableIdentifier TableIdentifier,
    IReadOnlyList<ColumnMetadataContext> Columns,
    string? ExistingDataOwnerSid = null,
    IReadOnlyDictionary<string, string>? MetadataTags = null);

/// <summary>
/// Optional AI provider abstraction for LLM-based pre-classification (e.g. OpenJEV / Ollama / vLLM).
/// </summary>
public interface IClassificationAiProvider
{
    ValueTask<IReadOnlyList<ColumnClassificationProposal>?> ProposeClassificationsAsync(
        TableClassificationContext context,
        IReadOnlyList<SensitivityLevelDefinition> sensitivityLevels,
        IReadOnlyList<PiiCategoryDefinition> piiCategories,
        CancellationToken ct = default);
}

/// <summary>
/// Core classification engine capable of 100% deterministic regex classification and optional AI pre-classification.
/// </summary>
public interface IClassificationEngine
{
    ValueTask<TableClassificationProposal> ClassifyTableAsync(
        TableClassificationContext context,
        CancellationToken ct = default);
}
