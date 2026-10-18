using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

namespace TrinoSqlEngine.Ast.Generators;

/// <summary>
/// REQ-SQL-130 (A-6, BC-130-3/11/12): the single interception point of <c>convert_timezone</c>. The call is selected by its normalized simple name
/// before the function name is formatted, so no spelling can become a pass-through call. Only <see cref="FormatConvertTimeZone"/> of a dialect
/// emits the conversion text; the zone arguments go through <see cref="AppendZoneArgument"/>, which accepts only a registry literal or a zone
/// parameter slot. This is a second, independent check next to the validator.
/// </summary>
public abstract partial class SqlDialectGeneratorBase
{
    /// <summary>True for the dialects that have a conversion form (SQL Server, PostgreSQL, DuckDB). Every other generator can never pass the name through.</summary>
    protected virtual bool EmitsTimeZoneConversion => false;

    private void GenerateTimeZoneCall(FunctionCallExpression fn, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        // Order: no form for the dialect (also when the validator was bypassed), no bound form, not validated, shape.
        if (!EmitsTimeZoneConversion) throw new SqlTimeZoneException(GovernedSqlErrorCodes.FunctionUnsupportedDialect);
        if (!context.IsBound) throw NoBoundForm("time zone conversion", TargetDialect);
        if (!context.TimeZoneCallsValidated) throw new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneArgument);
        if (fn.Name.Parts.Count != 1 || fn.Arguments.Count != 3 || fn.Distinct || fn.Filter is not null || fn.Window is not null || fn.OrderWithin is not null || fn.IsStar)
        {
            throw new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneArgument);
        }

        FormatConvertTimeZone(ref builder, fn, context);
    }

    /// <summary>The conversion of <c>convert_timezone(from_zone, to_zone, ts)</c> to the local wall-clock plain timestamp of <c>to_zone</c> (Q-130-2).</summary>
    protected virtual void FormatConvertTimeZone(ref ValueStringBuilder builder, FunctionCallExpression fn, SqlEmitterContext context) =>
        throw new SqlTimeZoneException(GovernedSqlErrorCodes.FunctionUnsupportedDialect);

    /// <summary>
    /// Emits one zone argument as a bound marker: a literal is looked up in the closed set and bound as the registry-owned dialect value; a synthetic
    /// parameter becomes a zone slot the binder maps. Anything else throws (a column, an expression, NULL, a positional parameter).
    /// </summary>
    protected void AppendZoneArgument(ref ValueStringBuilder builder, Expression zone, SqlEmitterContext context)
    {
        switch (zone)
        {
            case LiteralExpression { Type: LiteralType.String, Value: string text }:
                if (!TimeZoneRegistry.TryResolve(TargetDialect, text, out string mapped))
                {
                    throw new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneInvalid);
                }

                builder.Append(context.BindValue(mapped, SqlParameterType.String, ParameterOrigin.QueryLiteral));
                break;
            case ParameterReference { IsSynthetic: true } parameter:
                builder.Append(context.BindZoneClient(parameter));
                break;
            default:
                throw new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneArgument);
        }
    }
}
