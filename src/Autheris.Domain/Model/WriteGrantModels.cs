using System.Text.Json;
using Autheris.Domain.Common;

namespace Autheris.Domain.Model;

public enum WriteOperation
{
    Insert = 1,
    Update = 2,
    Delete = 3
}

public enum WriteRowScope
{
    OwnRows = 0,
    AllRows = 1
}

public sealed record GrantRowScopeEntry(
    string PolicyName,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    IReadOnlyDictionary<string, string>? ColumnMap = null,
    UncoveredPolicy Uncovered = UncoveredPolicy.Deny);

public sealed record WriteRule(
    Guid Id,
    WriteOperation Operation,
    IReadOnlyList<string> Columns,
    WriteRowScope RowScope,
    IReadOnlyList<GrantRowScopeEntry>? RowFilter = null,
    long? MaxRowsPerStatement = null)
{
    public void Validate()
    {
        if (Operation is WriteOperation.Insert or WriteOperation.Update)
        {
            if (Columns == null || Columns.Count == 0)
            {
                throw new InvalidOperationException($"Write rule {Id}: {Operation} requires at least one column.");
            }
        }
        else if (Operation is WriteOperation.Delete)
        {
            if (Columns != null && Columns.Count > 0)
            {
                throw new InvalidOperationException($"Write rule {Id}: Delete must not specify columns.");
            }
        }
        else
        {
            throw new InvalidOperationException($"Write rule {Id}: Unknown write operation {Operation}.");
        }

        if (Columns != null && Columns.Count > 0)
        {
            if (Columns.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException($"Write rule {Id}: Column names cannot be null or whitespace.");
            }

            if (Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Columns.Count)
            {
                throw new InvalidOperationException($"Write rule {Id}: Columns must be distinct (case-insensitive).");
            }
        }

        if (MaxRowsPerStatement.HasValue && MaxRowsPerStatement.Value <= 0)
        {
            throw new InvalidOperationException($"Write rule {Id}: MaxRowsPerStatement must be positive when set.");
        }
    }
}

public sealed record AccessProfileWriteRule(
    Guid Id,
    TableIdentifier TableIdentifier,
    WriteOperation Operation,
    IReadOnlyList<string> Columns,
    WriteRowScope RowScope,
    IReadOnlyList<GrantRowScopeEntry>? RowFilter = null,
    long? MaxRowsPerStatement = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TableIdentifier.Domain) ||
            string.IsNullOrWhiteSpace(TableIdentifier.Schema) ||
            string.IsNullOrWhiteSpace(TableIdentifier.TableName) ||
            TableIdentifier.Domain.Contains('*') || TableIdentifier.Domain.Contains('?') ||
            TableIdentifier.Schema.Contains('*') || TableIdentifier.Schema.Contains('?') ||
            TableIdentifier.TableName.Contains('*') || TableIdentifier.TableName.Contains('?'))
        {
            throw new InvalidOperationException($"Profile write rule {Id}: Wildcard table identifiers are not permitted.");
        }

        new WriteRule(Id, Operation, Columns, RowScope, RowFilter, MaxRowsPerStatement).Validate();
    }
}
