namespace TrinoSqlEngine.Governance;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// A distinct column-access signature's unioned row predicate for per-cell query compilation.
/// </summary>
public sealed record PerCellSignatureSpec(PolicyPredicate Scope, bool IsUnfiltered = false);

/// <summary>
/// Signature index lists determining when a row-dependent column is projected clear versus masked.
/// Signatures in neither list fall through to NULL.
/// </summary>
public sealed record PerCellColumnRule(
    ImmutableArray<int> ClearSignatures,
    ImmutableArray<int> MaskedSignatures);

/// <summary>
/// Typed per-cell policy for a single table: ordered signature predicates, varying columns' branch assignments,
/// and optional mandatory/narrowing predicates applied on top of the signature union.
/// </summary>
public sealed record PerCellTablePolicy
{
    public PerCellTablePolicy(
        IReadOnlyList<PerCellSignatureSpec> signatures,
        IReadOnlyDictionary<string, PerCellColumnRule> columns,
        PolicyPredicate? narrowingPredicate = null)
    {
        Signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        NarrowingPredicate = narrowingPredicate;
        AnySignatureUnfiltered = signatures.Any(static s => s.IsUnfiltered);
        Fingerprint = ComputeFingerprint(signatures, columns, narrowingPredicate);
    }

    public IReadOnlyList<PerCellSignatureSpec> Signatures { get; }
    public IReadOnlyDictionary<string, PerCellColumnRule> Columns { get; }
    public PolicyPredicate? NarrowingPredicate { get; }
    public bool AnySignatureUnfiltered { get; }
    public string Fingerprint { get; }

    private static string ComputeFingerprint(
        IReadOnlyList<PerCellSignatureSpec> signatures,
        IReadOnlyDictionary<string, PerCellColumnRule> columns,
        PolicyPredicate? narrowingPredicate)
    {
        var sb = new StringBuilder();
        sb.Append(signatures.Count).Append(':');
        for (int i = 0; i < signatures.Count; i++)
        {
            sb.Append(signatures[i].IsUnfiltered ? 'U' : 'F')
              .Append(':')
              .Append(signatures[i].Scope.Fingerprint)
              .Append(';');
        }

        sb.Append('|').Append(narrowingPredicate?.Fingerprint ?? "-").Append('|');
        foreach (var (name, rule) in columns.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(name).Append('=')
              .Append(string.Join(',', rule.ClearSignatures))
              .Append('/')
              .Append(string.Join(',', rule.MaskedSignatures))
              .Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>Provides per-cell policies for tables that require row-dependent column masking.</summary>
public interface IPerCellColumnProvider
{
    PerCellTablePolicy? GetPolicy(TableIdentity table);
}

/// <summary>Empty per-cell provider for compiles without row-dependent columns.</summary>
public sealed class EmptyPerCellColumnProvider : IPerCellColumnProvider
{
    public static readonly EmptyPerCellColumnProvider Instance = new();

    public PerCellTablePolicy? GetPolicy(TableIdentity table) => null;
}
