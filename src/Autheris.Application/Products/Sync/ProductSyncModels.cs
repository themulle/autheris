namespace Autheris.Application.Products.Sync;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>A file of the repository state handed to the synchronisation. <see cref="IsSymlink"/> is set by the reader of the repository.</summary>
public sealed record ProductSyncFile(string Path, string Content, bool IsSymlink = false);

/// <summary>
/// The complete repository state of one commit. The caller carries the tenant (from the authenticated sync identity, never from a file)
/// and has <c>ViaSyncPath</c> set.
/// </summary>
public sealed record ProductSyncRequest(ProductCaller Caller, string Commit, IReadOnlyList<ProductSyncFile> Files);

public enum ProductSyncAction { Create, Change, Unchanged, Retire, Drift }

public sealed record ProductSyncItem(string Schema, string Name, ProductSyncAction Action, string? Reason = null);

/// <summary>The outcome of a plan or an apply: one item per product, the commit and whether anything was written.</summary>
public sealed record ProductSyncResult(string Commit, bool Applied, IReadOnlyList<ProductSyncItem> Items)
{
    public int Count(ProductSyncAction action) => Items.Count(i => i.Action == action);
}

/// <summary>Stable reason codes of a rejected commit (never caller input).</summary>
public static class ProductSyncReasons
{
    public const string PathInvalid = "PATH_INVALID";
    public const string Symlink = "SYMLINK";
    public const string TooManyFiles = "TOO_MANY_FILES";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string TotalTooLarge = "TOTAL_TOO_LARGE";
    public const string FileType = "FILE_TYPE";
    public const string YamlInvalid = "YAML_INVALID";
    public const string YamlUnsafe = "YAML_UNSAFE";
    public const string FieldInvalid = "FIELD_INVALID";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string Duplicate = "DUPLICATE_PRODUCT";
    public const string ProcedureNotBound = "PROCEDURE_NOT_BOUND";
    public const string CommitInvalid = "COMMIT_INVALID";
    public const string ColumnUnknown = "COLUMN_UNKNOWN";
    public const string TooManyRetirements = "TOO_MANY_RETIREMENTS";
    public const string NotAllowed = "NOT_ALLOWED";

    /// <summary>The drift and retirement scan hit its page limit: the tenant has more products than one scan covers, so the commit is refused instead of scanned partially.</summary>
    public const string TooManyProducts = "PRODUCT_SYNC_TOO_MANY_PRODUCTS";
}

/// <summary>The whole commit is refused; nothing was written. <see cref="Reason"/> and <see cref="FileIndex"/> never echo file content.</summary>
public sealed class ProductSyncRejectedException(string reason, int fileIndex = -1, string? productCode = null) : Exception(ProductSyncRejectedException.StableCode)
{
    public const string StableCode = "PRODUCT_SYNC_REJECTED";

    public string Reason { get; } = reason;

    public int FileIndex { get; } = fileIndex;

    /// <summary>The product error code when the product rules refused a definition (authority, owner, invalid definition).</summary>
    public string? ProductCode { get; } = productCode;
}

/// <summary>A column classification declared by a file; non-masked classes always carry a reason.</summary>
internal sealed record ProductSyncClassification(string Column, ProductColumnClass Class, string? Reason);

/// <summary>One validated declaration of the repository.</summary>
internal sealed record ProductSyncEntry(
    int FileIndex,
    string Schema,
    string Name,
    DataProductKind Kind,
    string Definition,
    Sid Owner,
    LoadSettings? Load,
    ProductLimits? Limits,
    string? Description,
    IReadOnlyList<ProductSyncClassification> Classifications,
    ProcedureDefinition? Procedure = null);
