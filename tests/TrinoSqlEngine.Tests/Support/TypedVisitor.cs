using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Tests;

/// <summary>
/// Builds the <see cref="AstSecurityVisitor"/> the way <c>GovernedSqlCompiler</c> does, for the tests that drive the typed visitor
/// directly (injection shape, mask projection). The only visitor configuration is the typed policy context; the legacy string
/// provider bag no longer exists.
/// </summary>
internal static class TypedVisitor
{
    public static AstSecurityVisitor Create(TypedPolicyContext typed) => new(Options(typed), typed);

    public static SqlStatement Secure(SqlStatement statement, TypedPolicyContext typed) => (SqlStatement)Create(typed).Visit(statement);

    public static AstSecurityVisitorOptions Options(TypedPolicyContext typed) => new()
    {
        MaskedColumns = new MaskLookup(typed),
        TableColumns = name => typed.Catalog.Resolve(Parse(name))?.Columns.Select(c => c.Name).ToList(),
        TablesWithConsentRowFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        TablesWithMaskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    };

    private static SqlQualifiedName Parse(string normalized) =>
        new(normalized.Split('.').Select(p => new SqlIdentifier(p)).ToArray());

    private sealed class MaskLookup(TypedPolicyContext typed) : IMaskedColumnLookup
    {
        public bool HasMask(string normalizedTableName, string columnName)
        {
            var entry = typed.Catalog.Resolve(Parse(normalizedTableName));
            return entry is not null && typed.Masks.HasMask(entry.Identity, columnName);
        }
    }
}
