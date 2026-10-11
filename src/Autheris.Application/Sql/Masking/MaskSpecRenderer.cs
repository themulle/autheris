namespace Autheris.Application.Sql.Masking;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>Dialect text of a typed mask. <see cref="Parameters"/> is keyed by the marker that appears in <see cref="Sql"/>; no value is ever part of the text.</summary>
public sealed record RenderedMask(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// Renders a <see cref="MaskSpec"/> through the dialect generator of the AST compiler for the string consumers (WP-X0f), so the legacy
/// text and the compiler path read one source. The mask is emitted inside a one-table SELECT (the generator applies its normal
/// rules, bind limits and the emitted-SQL invariants); the constant frame is measured with a probe column and cut off again. Each
/// mask parameter becomes the caller's marker (<c>@name</c> by default) and its value goes into <see cref="RenderedMask.Parameters"/>.
/// </summary>
public sealed partial class MaskSpecRenderer
{
    private const string ProbeColumn = "mask_probe_col";

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SafeNameRegex();

    /// <param name="markerFormatter">Marker text for a parameter name; defaults to <c>@name</c>. Must be injective.</param>
    /// <exception cref="ArgumentOutOfRangeException">The dialect has no generator (no silent ANSI fallback).</exception>
    /// <exception cref="System.Security.SecurityException">The spec needs a catalog data type that is missing or not permitted.</exception>
    public RenderedMask Render(MaskSpec spec, string column, string? dataType, TargetSqlDialect dialect, Func<string, string>? markerFormatter = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        markerFormatter ??= static name => "@" + name;

        var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
        var source = new ParameterSource(spec.Parameters, new Dictionary<string, object?>());

        CompiledSql Emit(Expression expression) => generator.Generate(new SelectStatement(null,
            new QuerySpecification(false, [new ColumnSelectItem(expression, null)],
                new NamedTableSource(new SqlQualifiedName("mask_probe"), null), null, null, null), null, null), source);

        ColumnReference Column(string name) => new(new SqlQualifiedName([new SqlIdentifier(name, IsQuoted: true)]));

        var framed = Emit(Column(ProbeColumn)).Sql;
        int at = framed.IndexOf(ProbeColumn, StringComparison.Ordinal);
        if (at < 1)
        {
            throw new InvalidOperationException("The dialect generator produced an unexpected statement frame for the column mask.");
        }

        // The probe is quoted by one character on each side in every dialect.
        var prefix = framed[..(at - 1)];
        var suffix = framed[(at + ProbeColumn.Length + 1)..];
        var compiled = Emit(new MaskExpression(spec.Kind, Column(column), spec.Arguments, dataType));
        if (!compiled.Sql.StartsWith(prefix, StringComparison.Ordinal) || !compiled.Sql.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The dialect generator produced an unexpected statement frame for the column mask.");
        }

        var sql = compiled.Sql[prefix.Length..^suffix.Length];
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var markers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bound in compiled.Parameters)
        {
            if (bound.Origin != ParameterOrigin.Mask || bound.SourceName is null || !SafeNameRegex().IsMatch(bound.SourceName))
            {
                // Fail closed: only declared mask parameters with a safe name are rendered (a literal would be a value in text).
                throw new InvalidOperationException("The column mask references a value that is not a declared mask parameter.");
            }

            var marker = markerFormatter(bound.SourceName);
            markers[bound.Marker] = marker;
            parameters[marker] = bound.Value;
        }

        if (markers.Count > 0)
        {
            var pattern = string.Join('|', markers.Keys.OrderByDescending(m => m.Length).Select(Regex.Escape));
            sql = Regex.Replace(sql, "(?:" + pattern + ")(?![A-Za-z0-9_])", m => markers[m.Value],
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }

        return new RenderedMask(sql, parameters);
    }
}
