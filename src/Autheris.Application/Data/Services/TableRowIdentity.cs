namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Issue #94 (D-10, SD-4): the row identity that catalog consumers report (CSDL <c>&lt;Key&gt;</c>, MCP, OpenAPI, discovery).
/// Declared primary key, else the declared unique index with the fewest columns, else a Meta marker key, else the
/// <c>(client_id, ts)</c> convention (both columns required). Every candidate needs all of its columns in the metadata that is
/// passed in, so callers pass the metadata filtered for the caller: a key that touches a hidden column is dropped as a whole.
/// Never a subset, never an <c>id</c> guess, never <c>client_id</c> alone. Empty when nothing qualifies.
/// </summary>
public static class TableRowIdentity
{
    public static IReadOnlyList<string> Select(TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return DefaultReadOrdering.TryKey(metadata, static _ => true, out var key, out _) ? key : Array.Empty<string>();
    }
}
