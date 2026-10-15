namespace Autheris.Application.Sql.Dialects;

using System.Collections.Generic;
using Autheris.Domain.Common;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;

/// <summary>
/// WP-ARC-B-09: Declarative descriptor for a SQL database dialect, consolidating AST target mapping,
/// capability table metadata, execution admission, GraphQL tree-compilation support, CDC support, and parameter limits.
/// </summary>
public sealed record SqlDialectDescriptor(
    DatabaseDialect Dialect,
    TargetSqlDialect Target,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    DialectCapabilities Capabilities,
    bool IsExecutable,
    bool SupportsTreeCompile,
    bool SupportsCdc,
    int MaxParameterCount);

/// <summary>
/// WP-ARC-B-09: Port providing dialect-specific metadata, identifier quoting, and parameter formatting.
/// </summary>
public interface ISqlDialectProvider
{
    SqlDialectDescriptor Descriptor { get; }

    string QuoteIdentifier(string identifier);

    string FormatParameterName(int index);

    string FormatParameterName(string name) => Descriptor.Dialect.FormatParameterName(name);

    string FormatParameterMarker(string name) => Descriptor.Dialect.FormatParameterMarker(name);
}
