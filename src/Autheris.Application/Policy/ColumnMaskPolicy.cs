namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Interfaces;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// <see cref="IColumnMaskPolicy"/> over typed <see cref="MaskSpec"/> descriptors (WP-X0f). A mask is only ever built from a spec, never
/// from dialect text. The constructor fails closed when a spec references a parameter that it does not declare (a value would be
/// missing at bind time) or declares a parameter that is not of <see cref="ParameterOrigin.Mask"/> (INV-4: mask values are bound mask
/// parameters).
/// </summary>
public sealed class ColumnMaskPolicy : IColumnMaskPolicy
{
    private readonly Dictionary<string, MaskSpec> _masks;
    private readonly Lazy<string> _fingerprint;

    public ColumnMaskPolicy(IReadOnlyDictionary<string, MaskSpec> masks)
    {
        ArgumentNullException.ThrowIfNull(masks);
        _masks = new Dictionary<string, MaskSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, spec) in masks)
        {
            ArgumentNullException.ThrowIfNull(spec);
            Validate(column, spec);
            if (!_masks.TryAdd(column, spec))
            {
                throw new SecurityException("A column has more than one typed mask.");
            }
        }

        _fingerprint = new Lazy<string>(ComputeFingerprint);
    }

    public string Fingerprint => _fingerprint.Value;

    public IReadOnlyCollection<string> MaskedColumns => _masks.Keys;

    public bool HasMask(string column) => column is not null && _masks.ContainsKey(column);

    /// <summary>The typed specs by column (case-insensitive).</summary>
    public IReadOnlyDictionary<string, MaskSpec> Specs => _masks;

    public bool TryGetSpec(string column, out MaskSpec spec) => _masks.TryGetValue(column, out spec!);

    private static IEnumerable<(string Role, PolicyParameterExpression? Parameter)> Arguments(MaskArguments a)
    {
        yield return ("constant", a.Constant);
        yield return ("keepPrefix", a.KeepPrefix);
        yield return ("keepSuffix", a.KeepSuffix);
        yield return ("maskChar", a.MaskChar);
        yield return ("hmacKey", a.HmacKey);
        yield return ("hmacKeyOuter", a.HmacKeyOuter);
    }

    private static void Validate(string column, MaskSpec spec)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            throw new SecurityException("A typed mask needs a column name.");
        }

        foreach (var (_, parameter) in Arguments(spec.Arguments))
        {
            if (parameter is null)
            {
                continue;
            }

            if (parameter.Origin != ParameterOrigin.Mask || !spec.Parameters.ContainsKey(parameter.Name))
            {
                throw new SecurityException("A mask argument must be a bound mask parameter that the spec declares.");
            }
        }
    }

    private string ComputeFingerprint()
    {
        var text = new StringBuilder();
        foreach (var (column, spec) in _masks.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(column.ToUpperInvariant()).Append(':').Append(spec.Kind)
                .Append(":d").Append(spec.Arguments.Decimals?.ToString(CultureInfo.InvariantCulture) ?? "-");
            foreach (var (role, parameter) in Arguments(spec.Arguments))
            {
                if (parameter is not null)
                {
                    text.Append(':').Append(role).Append('=').Append(parameter.Name).Append('/').Append(spec.Parameters[parameter.Name].Type);
                }
            }

            text.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
