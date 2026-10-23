namespace Autheris.Application.SchemaRegistry;

/// <summary>MISC-16: a schema (stored baseline or submitted target) cannot be normalised for comparison (duplicates, dangling extensions).</summary>
public sealed class SchemaCompareException(IReadOnlyList<string> errors)
    : Exception("Schema cannot be compared: " + string.Join("; ", errors))
{
    public const string ErrorCode = "SCHEMA_COMPARE_FAILED";

    public IReadOnlyList<string> Errors { get; } = errors;
}
