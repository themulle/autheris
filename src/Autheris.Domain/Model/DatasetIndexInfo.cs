namespace Autheris.Domain.Model;

using System.Collections.Generic;

/// <summary>
/// A declared index as a catalog caller sees it (I404-SC-L3): columns and uniqueness only. The operator-chosen index name is never
/// part of any caller-facing output, because it is free text that can embed the names of columns the caller must not see.
/// </summary>
public sealed record DatasetIndexInfo(IReadOnlyList<string> Columns, bool Unique);
