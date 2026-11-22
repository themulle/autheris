using System.Text;

namespace Autheris.Domain.Security;

/// <summary>Canonical protection classification hierarchy; parsing and normalization fail closed to <see cref="Confidential"/>.</summary>
public enum ProtectionClass
{
    Public = 0,
    Internal = 1,
    Confidential = 2,
    Secret = 3
}

public static class ProtectionClassExtensions
{
    public static bool IsStricterThan(this ProtectionClass candidate, ProtectionClass baseline) =>
        NormalizeOrConfidential(candidate) > NormalizeOrConfidential(baseline);

    public static ProtectionClass Max(ProtectionClass a, ProtectionClass b)
    {
        var left = NormalizeOrConfidential(a);
        var right = NormalizeOrConfidential(b);
        return left >= right ? left : right;
    }

    public static bool TryParse(string? value, out ProtectionClass result)
    {
        var span = value is null ? default : value.AsSpan().Trim();
        if (Ascii.EqualsIgnoreCase(span, "PUBLIC")) { result = ProtectionClass.Public; return true; }
        if (Ascii.EqualsIgnoreCase(span, "INTERNAL")) { result = ProtectionClass.Internal; return true; }
        if (Ascii.EqualsIgnoreCase(span, "CONFIDENTIAL")) { result = ProtectionClass.Confidential; return true; }
        if (Ascii.EqualsIgnoreCase(span, "SECRET")) { result = ProtectionClass.Secret; return true; }

        result = ProtectionClass.Confidential;
        return false;
    }

    public static ProtectionClass ParseOrConfidential(string? value) =>
        TryParse(value, out var parsed) ? parsed : ProtectionClass.Confidential;

    public static ProtectionClass NormalizeOrConfidential(this ProtectionClass value) =>
        value is >= ProtectionClass.Public and <= ProtectionClass.Secret ? value : ProtectionClass.Confidential;

    public static string ToCanonicalName(this ProtectionClass value) =>
        NormalizeOrConfidential(value) switch
        {
            ProtectionClass.Public => "PUBLIC",
            ProtectionClass.Internal => "INTERNAL",
            ProtectionClass.Confidential => "CONFIDENTIAL",
            ProtectionClass.Secret => "SECRET",
            _ => "CONFIDENTIAL"
        };
}
