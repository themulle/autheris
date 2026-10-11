namespace TrinoSqlEngine.Governance;

using System.Collections.Frozen;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>Typed row-level-security input. Replaces the string policy provider on the compiler path.</summary>
public interface IPolicyPredicateProvider
{
    /// <summary>Whether a policy predicate applies to <paramref name="table"/> (the tenant predicate applies regardless).</summary>
    bool ShouldApplyPolicy(TableIdentity table);

    /// <summary>The predicate. Deny-all is <see cref="PolicyPredicate.DenyAll"/>, never null.</summary>
    PolicyPredicate GetPredicate(TableIdentity table);
}

/// <summary>Typed column masks. Replaces the string masking provider on the compiler path.</summary>
public interface IColumnMaskProvider
{
    bool HasMask(TableIdentity table, string column);

    MaskSpec GetMask(TableIdentity table, string column);
}

public enum MaskKind { Nullify, Redact, PartialMask, Hmac, GeoJitter, Constant }

/// <summary>
/// Mask arguments. Everything except <paramref name="Decimals"/> is a bound parameter (INV-4); <paramref name="Decimals"/> is a
/// structural integer (a rounding scale) emitted inline. <paramref name="HmacKeyOuter"/> is the second HMAC pad of the SQL
/// Server expression (the inner and outer pad are derived from the same key).
/// </summary>
public sealed record MaskArguments(
    PolicyParameterExpression? Constant = null,
    PolicyParameterExpression? KeepPrefix = null,
    PolicyParameterExpression? KeepSuffix = null,
    PolicyParameterExpression? MaskChar = null,
    PolicyParameterExpression? HmacKey = null,
    int? Decimals = null,
    PolicyParameterExpression? HmacKeyOuter = null);

public sealed record MaskSpec(MaskKind Kind, MaskArguments Arguments, FrozenDictionary<string, PolicyValue> Parameters);
