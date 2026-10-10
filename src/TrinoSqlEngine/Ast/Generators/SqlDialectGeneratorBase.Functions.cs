using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using System.Text.RegularExpressions;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Nodes;

namespace TrinoSqlEngine.Ast.Generators;

public abstract partial class SqlDialectGeneratorBase : ISqlDialectGenerator
{
    /// <summary>Wunsch 4: the dialect accepts <c>JOIN … USING (…)</c> (all but SQL Server).</summary>
    protected virtual bool SupportsJoinUsing => true;

    /// <summary>Wunsch 4: the dialect has TRY_CAST (SQL Server, DuckDB, Snowflake).</summary>
    protected virtual bool SupportsTryCast => false;

    /// <summary>A validated Trino type: lower-case base name (e.g. <c>timestamp</c>), argument list without spaces, time zone flag.</summary>
    protected readonly record struct TrinoType(string Name, string? Arguments, bool WithTimeZone, string Normalized);

    private static readonly Regex TypeNameParts = new(@"^(?<name>[a-z][a-z0-9_]*(?: [a-z][a-z0-9_]*)*?)(?<args>\([0-9, ]+\))?(?<tz> with time zone)?$", RegexOptions.CultureInvariant);

    protected static TrinoType ParseTypeName(string type)
    {
        // SQL-3: only plain tokens reach the target database.
        string normalized = TrinoSqlEngine.Ast.SqlSafeTokens.EnsureTypeName(type);
        string lower = normalized.ToLowerInvariant();
        var match = TypeNameParts.Match(lower);
        if (!match.Success)
        {
            return new TrinoType(lower, null, false, normalized);
        }

        string? args = match.Groups["args"].Success ? match.Groups["args"].Value.Replace(" ", string.Empty, StringComparison.Ordinal) : null;
        return new TrinoType(match.Groups["name"].Value, args, match.Groups["tz"].Success, normalized);
    }

    /// <summary>
    /// Wunsch 4: CAST target type in the dialect's spelling. The default keeps the (validated) Trino spelling, which is valid
    /// for SQLite, DuckDB, Snowflake and ANSI.
    /// </summary>
    protected virtual string FormatTypeName(TrinoType type) => StandardTypeName(type);

    /// <summary>
    /// CR-ADG-42: the native catalog type of a check-option cast. It must resolve through the closed per-dialect map and be
    /// spelled exactly as the map spells it, so the emitted text is never request text; anything else fails closed.
    /// </summary>
    private string NativeTypeName(CastExpression cast)
    {
        if (cast.IsTryCast ||
            !TrinoSqlEngine.Ast.Security.CatalogTypeMap.TryResolve(TargetDialect, cast.TargetType, out string native, out _) ||
            !string.Equals(native, cast.TargetType, StringComparison.Ordinal))
        {
            throw UnsupportedConstruct("CAST to a native column type", TargetDialect);
        }

        return TrinoSqlEngine.Ast.SqlSafeTokens.EnsureTypeName(native);
    }

    /// <summary>
    /// CR-ADG-25: closed CAST target type set shared by the dialects without their own type map (PostgreSQL, DuckDB, SQLite, ANSI,
    /// Snowflake). Anything else (<c>regclass</c>, <c>regrole</c>, <c>xml</c>, <c>json</c>, <c>oid</c>, ...) fails closed.
    /// </summary>
    protected string StandardTypeName(TrinoType type) => type.Name switch
    {
        "boolean" or "tinyint" or "smallint" or "integer" or "int" or "bigint" or "real" or "double" or "double precision"
            or "decimal" or "numeric" or "varchar" or "char" or "text" or "varbinary" or "date" when !type.WithTimeZone => type.Normalized,
        "time" or "timestamp" => type.Normalized,
        _ => throw UnsupportedConstruct($"CAST(… AS {type.Normalized})", TargetDialect)
    };

    protected static TrinoSqlEngine.Ast.Builder.AstBuildException UnsupportedConstruct(string construct, TargetSqlDialect dialect) =>
        new($"SQL construct {construct} is not supported for {dialect}.");

    /// <summary>Wunsch 4: ANSI <c>CURRENT_DATE</c> … <c>LOCALTIMESTAMP</c>.</summary>
    protected virtual void FormatCurrentDateTime(ref ValueStringBuilder builder, CurrentDateTimeKind kind, SqlEmitterContext context)
    {
        builder.Append(kind switch
        {
            CurrentDateTimeKind.CurrentDate => "CURRENT_DATE",
            CurrentDateTimeKind.CurrentTime => "CURRENT_TIME",
            CurrentDateTimeKind.CurrentTimestamp => "CURRENT_TIMESTAMP",
            CurrentDateTimeKind.LocalTime => "LOCALTIME",
            _ => "LOCALTIMESTAMP"
        });
    }

    protected virtual string SubstringFunctionName => "SUBSTRING";

    /// <summary>Wunsch 4: <c>SUBSTRING(x, start[, length])</c>.</summary>
    protected virtual void FormatSubstring(ref ValueStringBuilder builder, SubstringExpression substring, SqlEmitterContext context)
    {
        builder.Append(SubstringFunctionName);
        builder.Append('(');
        GenerateExpression(substring.Source, ref builder, context);
        builder.Append(", ");
        GenerateExpression(substring.Start, ref builder, context);
        if (substring.Length != null)
        {
            builder.Append(", ");
            GenerateExpression(substring.Length, ref builder, context);
        }
        builder.Append(')');
    }

    /// <summary>Wunsch 4: ANSI <c>TRIM(BOTH|LEADING|TRAILING [chars] FROM x)</c>.</summary>
    protected virtual void FormatTrim(ref ValueStringBuilder builder, TrimExpression trim, SqlEmitterContext context)
    {
        builder.Append(trim.Specification switch
        {
            TrimSpecification.Leading => "TRIM(LEADING ",
            TrimSpecification.Trailing => "TRIM(TRAILING ",
            _ => "TRIM(BOTH "
        });
        if (trim.Characters != null)
        {
            GenerateExpression(trim.Characters, ref builder, context);
            builder.Append(' ');
        }
        builder.Append("FROM ");
        GenerateExpression(trim.Source, ref builder, context);
        builder.Append(')');
    }

    /// <summary>Wunsch 4: ANSI <c>POSITION(needle IN haystack)</c>.</summary>
    protected virtual void FormatPosition(ref ValueStringBuilder builder, PositionExpression position, SqlEmitterContext context)
    {
        builder.Append("POSITION(");
        GenerateExpression(position.Needle, ref builder, context);
        builder.Append(" IN ");
        GenerateExpression(position.Haystack, ref builder, context);
        builder.Append(')');
    }

    /// <summary>INSTR(haystack, needle), used by SQLite and Oracle for POSITION.</summary>
    protected void FormatInstr(ref ValueStringBuilder builder, PositionExpression position, SqlEmitterContext context)
    {
        builder.Append("INSTR(");
        GenerateExpression(position.Haystack, ref builder, context);
        builder.Append(", ");
        GenerateExpression(position.Needle, ref builder, context);
        builder.Append(')');
    }

    /// <summary>Wunsch 4: the dialect supports ROLLUP, CUBE and GROUPING SETS.</summary>
    protected virtual bool SupportsGroupingSets => true;

    /// <summary>Wunsch 4: the dialect supports <c>GROUP BY DISTINCT</c>.</summary>
    protected virtual bool SupportsGroupByDistinct => false;

    protected virtual void GenerateGroupBy(GroupByClause groupBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append(" GROUP BY ");
        if (groupBy.Distinct)
        {
            if (!SupportsGroupByDistinct)
            {
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"SQL construct GROUP BY DISTINCT is not supported for {TargetDialect}.");
            }

            builder.Append("DISTINCT ");
        }

        bool first = true;
        foreach (var expr in groupBy.GroupingExpressions)
        {
            if (!first) builder.Append(", ");
            GenerateStructuralInteger(expr, ref builder, context);
            first = false;
        }

        foreach (var element in groupBy.AdvancedElements ?? [])
        {
            if (!SupportsGroupingSets)
            {
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"SQL construct GROUP BY {element.Kind} is not supported for {TargetDialect}.");
            }

            if (!first) builder.Append(", ");
            first = false;
            builder.Append(element.Kind switch
            {
                GroupingElementKind.Rollup => "ROLLUP (",
                GroupingElementKind.Cube => "CUBE (",
                _ => "GROUPING SETS ("
            });
            for (int i = 0; i < element.Sets.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                var set = element.Sets[i];
                // GROUPING SETS lists each set in parentheses; ROLLUP/CUBE only composite sets.
                bool parenthesize = element.Kind == GroupingElementKind.GroupingSets || set.Count != 1;
                if (parenthesize) builder.Append('(');
                for (int j = 0; j < set.Count; j++)
                {
                    if (j > 0) builder.Append(", ");
                    GenerateStructuralInteger(set[j], ref builder, context);
                }
                if (parenthesize) builder.Append(')');
            }
            builder.Append(')');
        }
    }

    /// <summary>Wunsch 4: <c>GROUPING(a, b)</c>; several columns give a bitmask in Trino, PostgreSQL and Oracle.</summary>
    protected virtual void FormatGroupingOperation(ref ValueStringBuilder builder, GroupingOperationExpression grouping, SqlEmitterContext context)
    {
        if (!SupportsGroupingSets)
        {
            throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"SQL construct GROUPING() is not supported for {TargetDialect}.");
        }

        builder.Append("GROUPING(");
        for (int i = 0; i < grouping.Columns.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            GenerateExpression(grouping.Columns[i], ref builder, context);
        }
        builder.Append(')');
    }

    /// <summary>Wunsch 4: the dialect evaluates <c>agg(…) FILTER (WHERE …)</c> natively; otherwise it is emulated with CASE.</summary>
    protected virtual bool SupportsAggregateFilter => false;

    /// <summary>Wunsch 4: the dialect accepts <c>ORDER BY</c> inside an aggregate's argument list.</summary>
    protected virtual bool SupportsOrderedAggregates => false;

    protected virtual void GenerateFunctionCall(FunctionCallExpression fn, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        FormatFunctionName(ref builder, fn.Name, context);
        builder.Append('(');
        if (fn.Distinct) builder.Append("DISTINCT ");

        bool emulateFilter = fn.Filter != null && !SupportsAggregateFilter;
        if (emulateFilter)
        {
            // agg(x) FILTER (WHERE c) == agg(CASE WHEN c THEN x END): aggregates ignore NULL; COUNT(*) counts 1 per row.
            if (!fn.IsStar && fn.Arguments.Count != 1)
            {
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException(
                    $"SQL construct FILTER (WHERE …) on {fn.Name.NormalizedName} with {fn.Arguments.Count} arguments is not supported for {TargetDialect}.");
            }

            builder.Append("CASE WHEN ");
            GeneratePredicate(fn.Filter!, ref builder, context);
            builder.Append(" THEN ");
            if (fn.IsStar) AppendStructural(ref builder, context, "1");
            else GenerateExpression(fn.Arguments[0], ref builder, context);
            builder.Append(" END");
        }
        else
        {
            if (fn.IsStar) builder.Append('*');
            for (int i = 0; i < fn.Arguments.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                GenerateExpression(fn.Arguments[i], ref builder, context);
            }
        }

        if (fn.OrderWithin != null)
        {
            if (!SupportsOrderedAggregates)
            {
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException(
                    $"SQL construct ORDER BY inside {fn.Name.NormalizedName}(…) is not supported for {TargetDialect}.");
            }

            builder.Append(' ');
            GenerateOrderBy(fn.OrderWithin, ref builder, context);
        }

        builder.Append(')');

        if (fn.Filter != null && !emulateFilter)
        {
            builder.Append(" FILTER (WHERE ");
            GeneratePredicate(fn.Filter, ref builder, context);
            builder.Append(')');
        }

        if (fn.Window != null)
        {
            GenerateWindow(fn.Window, ref builder, context);
        }
    }

    private void GenerateWindow(WindowSpecification window, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append(" OVER (");
        bool needsSpace = false;
        if (window.PartitionBy != null && window.PartitionBy.Count > 0)
        {
            builder.Append("PARTITION BY ");
            for (int i = 0; i < window.PartitionBy.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                GenerateExpression(window.PartitionBy[i], ref builder, context);
            }
            needsSpace = true;
        }

        if (window.OrderBy != null)
        {
            if (needsSpace) builder.Append(' ');
            GenerateOrderBy(window.OrderBy, ref builder, context);
            needsSpace = true;
        }

        if (window.Frame != null)
        {
            if (needsSpace) builder.Append(' ');
            builder.Append(window.Frame.Type == WindowFrameType.Rows ? "ROWS " : "RANGE ");
            if (window.Frame.End != null)
            {
                builder.Append("BETWEEN ");
                FormatFrameBound(ref builder, window.Frame.Start, context);
                builder.Append(" AND ");
                FormatFrameBound(ref builder, window.Frame.End, context);
            }
            else
            {
                FormatFrameBound(ref builder, window.Frame.Start, context);
            }
        }

        builder.Append(')');
    }

    private static void FormatFrameBound(ref ValueStringBuilder builder, FrameBound bound, SqlEmitterContext context)
    {
        switch (bound.Kind)
        {
            case FrameBoundKind.UnboundedPreceding: builder.Append("UNBOUNDED PRECEDING"); break;
            case FrameBoundKind.UnboundedFollowing: builder.Append("UNBOUNDED FOLLOWING"); break;
            case FrameBoundKind.CurrentRow: builder.Append("CURRENT ROW"); break;
            case FrameBoundKind.Preceding:
                AppendInlineInteger(ref builder, bound.Offset, context);
                builder.Append(" PRECEDING");
                break;
            case FrameBoundKind.Following:
                AppendInlineInteger(ref builder, bound.Offset, context);
                builder.Append(" FOLLOWING");
                break;
        }
    }

    /// <summary>
    /// A boolean condition (CASE WHEN, FILTER): generated outside the projection context, so dialects that wrap predicates
    /// in projections (SQL Server, Oracle) do not wrap it a second time.
    /// </summary>
    protected void GeneratePredicate(Expression predicate, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        bool prevProjection = context.InProjectionContext;
        bool prevPredicate = context.InPredicateContext;
        context.InProjectionContext = false;
        context.InPredicateContext = true;
        GenerateExpression(predicate, ref builder, context);
        context.InProjectionContext = prevProjection;
        context.InPredicateContext = prevPredicate;
    }

    /// <summary>Wunsch 4: ANSI <c>DATE '…'</c>, <c>TIME '…'</c>, <c>TIMESTAMP '…'</c>.</summary>
    protected virtual void FormatTypedLiteral(ref ValueStringBuilder builder, TypedLiteralExpression literal, SqlEmitterContext context)
    {
        builder.Append(literal.Kind switch
        {
            TypedLiteralKind.Date => "DATE ",
            TypedLiteralKind.Time => "TIME ",
            _ => "TIMESTAMP "
        });
        FormatStringLiteral(ref builder, literal.Value, context);
    }

    protected static TrinoSqlEngine.Ast.Builder.AstBuildException UnsupportedDateFunction(string function, DateUnit unit, TargetSqlDialect dialect) =>
        new($"SQL construct {function}('{DateUnitName(unit)}', …) is not supported for {dialect}.");

    /// <summary>Lower-case Trino name of <paramref name="unit"/> (<c>day</c>).</summary>
    protected static string DateUnitName(DateUnit unit) => unit switch
    {
        DateUnit.Second => "second",
        DateUnit.Minute => "minute",
        DateUnit.Hour => "hour",
        DateUnit.Day => "day",
        DateUnit.Week => "week",
        DateUnit.Month => "month",
        _ => "year"
    };

    /// <summary>
    /// Virtual filters (phase 7b): ANSI <c>(x ± INTERVAL 'n' UNIT)</c>; a week is seven days (ANSI has no WEEK field).
    /// </summary>
    protected virtual void FormatDateAdd(ref ValueStringBuilder builder, DateUnit unit, long amount, Expression source, SqlEmitterContext context)
    {
        (long value, string field) = unit == DateUnit.Week ? (amount * 7, "DAY") : (amount, DateUnitName(unit).ToUpperInvariant());
        builder.Append('(');
        GenerateExpression(source, ref builder, context);
        builder.Append(value < 0 ? " - INTERVAL '" : " + INTERVAL '");
        builder.Append(Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("' ");
        builder.Append(field);
        builder.Append(')');
    }

    /// <summary>Virtual filters (phase 7b): <c>DATE_TRUNC('unit', x)</c> (PostgreSQL, DuckDB, Snowflake, ANSI fallback).</summary>
    protected virtual void FormatDateTrunc(ref ValueStringBuilder builder, DateUnit unit, Expression source, SqlEmitterContext context)
    {
        builder.Append("DATE_TRUNC('");
        builder.Append(DateUnitName(unit));
        builder.Append("', ");
        GenerateExpression(source, ref builder, context);
        builder.Append(')');
    }

    /// <summary>Wunsch 4: ANSI <c>INTERVAL '…' FIELD</c>.</summary>
    protected virtual void FormatIntervalLiteral(ref ValueStringBuilder builder, IntervalLiteralExpression interval, SqlEmitterContext context)
    {
        builder.Append("INTERVAL ");
        FormatStringLiteral(ref builder, interval.Value, context);
        builder.Append(' ');
        builder.Append(interval.Field);
    }

    /// <summary>
    /// Wunsch 4: a function name is not an identifier. Quoting it (<c>"coalesce"</c>, <c>[SUM]</c>) makes PostgreSQL and
    /// SQL Server look for a user-defined object, so built-ins fail. An unquoted one-part name (it passed
    /// <see cref="SqlFunctionPolicy"/> in the builder) is emitted bare and upper-case; quoted or qualified names keep
    /// delimited parts.
    /// </summary>
    public virtual void FormatFunctionName(ref ValueStringBuilder builder, SqlQualifiedName name, SqlEmitterContext context)
    {
        if (name.Parts.Count == 1 && !name.Parts[0].IsQuoted && BareFunctionName.IsMatch(name.Parts[0].Value))
        {
            builder.Append(name.Parts[0].Value.ToUpperInvariant());
            return;
        }

        FormatQualifiedName(ref builder, name, context);
    }

    private static readonly Regex BareFunctionName = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);

    public virtual void FormatQualifiedName(ref ValueStringBuilder builder, SqlQualifiedName name, SqlEmitterContext context)
    {
        int startIndex = name.Parts.Count == 4 ? 1 : 0;
        for (int i = startIndex; i < name.Parts.Count; i++)
        {
            if (i > startIndex) builder.Append('.');
            FormatIdentifier(ref builder, name.Parts[i], context);
        }
    }

    /// <summary>
    /// Emits a table reference for the backend target dialect. If the table reference is a 3-part name
    /// (catalog.schema.table), the catalog prefix is stripped to emit only schema.table.
    /// </summary>
    public virtual void FormatTableName(ref ValueStringBuilder builder, SqlQualifiedName name, SqlEmitterContext context)
    {
        int startIndex = name.Parts.Count == 3 ? 1 : 0;
        for (int i = startIndex; i < name.Parts.Count; i++)
        {
            if (i > startIndex) builder.Append('.');
            FormatIdentifier(ref builder, name.Parts[i], context);
        }
    }

    public abstract void FormatStringLiteral(ref ValueStringBuilder builder, string value, SqlEmitterContext context);

    public abstract void FormatBoolean(ref ValueStringBuilder builder, bool value, SqlEmitterContext context);

    public virtual void FormatLiteral(ref ValueStringBuilder builder, LiteralExpression lit, SqlEmitterContext context)
    {
        if (lit.Value == null || lit.Type == LiteralType.Null)
        {
            builder.Append("NULL");
            return;
        }

        if (context.IsBound && BindLiterals)
        {
            FormatBoundLiteral(ref builder, lit, context);
            return;
        }

        switch (lit.Type)
        {
            case LiteralType.Boolean:
                FormatBoolean(ref builder, (bool)lit.Value, context);
                break;
            case LiteralType.Integer:
                builder.Append(Convert.ToInt64(lit.Value, CultureInfo.InvariantCulture));
                break;
            case LiteralType.Decimal:
                builder.Append(Convert.ToString(lit.Value, CultureInfo.InvariantCulture));
                break;
            case LiteralType.String:
                FormatStringLiteral(ref builder, lit.Value.ToString() ?? string.Empty, context);
                break;
            case LiteralType.Binary:
                builder.Append(TrinoSqlEngine.Ast.SqlSafeTokens.EnsureBinaryLiteral(lit.Value.ToString() ?? string.Empty));
                break;
            default:
                builder.Append(lit.Value.ToString() ?? string.Empty);
                break;
        }
    }

    /// <summary>INV-4: a literal value reaches the database only as a bound parameter.</summary>
    private void FormatBoundLiteral(ref ValueStringBuilder builder, LiteralExpression lit, SqlEmitterContext context)
    {
        switch (lit.Type)
        {
            case LiteralType.Boolean:
            {
                // TRUE and FALSE are keywords (plan 3.4); the dialect spells them as a registered structural token.
                int start = builder.Length;
                FormatBoolean(ref builder, (bool)lit.Value!, context);
                context.RegisterInlineNumericPosition(start, builder.Length - start);
                break;
            }
            case LiteralType.Integer:
                builder.Append(BindInteger(Convert.ToInt64(lit.Value, CultureInfo.InvariantCulture), context));
                break;
            case LiteralType.Decimal:
            {
                decimal value = lit.Value switch
                {
                    decimal d => d,
                    double dbl => (decimal)dbl,
                    _ => decimal.Parse(Convert.ToString(lit.Value, CultureInfo.InvariantCulture)!, NumberStyles.Float, CultureInfo.InvariantCulture)
                };
                builder.Append(context.BindValue(value, SqlParameterType.Decimal, ParameterOrigin.QueryLiteral));
                break;
            }
            case LiteralType.String:
                builder.Append(context.BindValue(lit.Value!.ToString() ?? string.Empty, SqlParameterType.String, ParameterOrigin.QueryLiteral));
                break;
            case LiteralType.Binary:
                builder.Append(context.BindValue(ParseBinaryLiteral(lit.Value!.ToString() ?? string.Empty), SqlParameterType.Binary, ParameterOrigin.QueryLiteral));
                break;
            default:
                throw new NotSupportedException($"Unsupported literal type: {lit.Type}");
        }
    }

    private static byte[] ParseBinaryLiteral(string literal)
    {
        string checkedLiteral = TrinoSqlEngine.Ast.SqlSafeTokens.EnsureBinaryLiteral(literal);
        string hex = checkedLiteral[2..^1].Replace(" ", string.Empty, StringComparison.Ordinal);
        return Convert.FromHexString(hex);
    }

    /// <summary>Parses the value of a DATE/TIME/TIMESTAMP literal into a typed .NET value; unparsable values fail closed.</summary>
    protected static (object Value, SqlParameterType Type) ParseTypedLiteralValue(TypedLiteralExpression literal)
    {
        string text = literal.Value.Trim();
        switch (literal.Kind)
        {
            case TypedLiteralKind.Date when DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                return (date.ToDateTime(TimeOnly.MinValue), SqlParameterType.Date);
            case TypedLiteralKind.Time when TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time):
                return (time.ToTimeSpan(), SqlParameterType.Time);
            case TypedLiteralKind.Timestamp when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp):
                return (timestamp, SqlParameterType.Timestamp);
            default:
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"The {literal.Kind} literal value is not a valid {literal.Kind} value.");
        }
    }
}
