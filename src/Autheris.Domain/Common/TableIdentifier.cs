using System;

namespace Autheris.Domain.Common;

public readonly record struct TableIdentifier : IEquatable<TableIdentifier>
{
    private readonly string _domain = null!;
    private readonly string _schema = null!;
    private readonly string _tableName = null!;

    public string Domain
    {
        get => _domain;
        init => _domain = NormalizeComponent(value, nameof(Domain));
    }

    public string Schema
    {
        get => _schema;
        init => _schema = NormalizeComponent(value, nameof(Schema));
    }

    public string TableName
    {
        get => _tableName;
        init => _tableName = NormalizeComponent(value, nameof(TableName));
    }

    public TableIdentifier(string domain, string schema, string tableName)
    {
        _domain = NormalizeComponent(domain, nameof(domain));
        _schema = NormalizeComponent(schema, nameof(schema));
        _tableName = NormalizeComponent(tableName, nameof(tableName));
    }

    private static string NormalizeComponent(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        ValidateComponent(value, paramName);
        return value.Trim();
    }

    /// <summary>
    /// Non-throwing variant of the constructor for caller-controlled text: false for a blank component or a component with a null byte,
    /// line break or dot (the same rules the constructor enforces).
    /// </summary>
    public static bool TryCreate(string? domain, string? schema, string? tableName, out TableIdentifier id)
    {
        id = default;
        if (!IsValidComponent(domain) || !IsValidComponent(schema) || !IsValidComponent(tableName))
        {
            return false;
        }

        id = new TableIdentifier(domain!, schema!, tableName!);
        return true;
    }

    private static bool IsValidComponent(string? component) =>
        !string.IsNullOrWhiteSpace(component) && component.AsSpan().IndexOfAny("\0\r\n.") < 0;

    private static void ValidateComponent(string component, string paramName)
    {
        if (component.Contains('\0', StringComparison.Ordinal) || component.Contains('\r', StringComparison.Ordinal) || component.Contains('\n', StringComparison.Ordinal) || component.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException($"TableIdentifier component '{paramName}' must not contain null bytes, control characters, or dot separators: '{component}'", paramName);
        }
    }

    public override string ToString() => $"{Domain}.{Schema}.{TableName}";

    public string ToQualifiedName() => $"{Schema}.{TableName}";

    public bool Equals(TableIdentifier other) =>
        string.Equals(Domain, other.Domain, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(TableName, other.TableName, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Domain ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Schema ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(TableName ?? string.Empty));

    public static TableIdentifier Parse(string s)
    {
        if (TryParse(s, out var id)) return id;
        throw new FormatException($"Invalid TableIdentifier format: '{s}'. Expected 'domain.schema.table' or 'schema.table'.");
    }

    public static bool TryParse(string? s, out TableIdentifier id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split('.');
        if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]) && !string.IsNullOrWhiteSpace(parts[2]))
        {
            id = new TableIdentifier(parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
            return true;
        }
        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
        {
            id = new TableIdentifier("default", parts[0].Trim(), parts[1].Trim());
            return true;
        }
        return false;
    }
}
