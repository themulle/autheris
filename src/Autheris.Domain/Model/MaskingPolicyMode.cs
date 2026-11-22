namespace Autheris.Domain.Model;

public enum MaskingPolicyMode
{
    Default = 0,   // Default: configured column masking rules apply
    Unmasked = 1,  // Exception: all columns in plaintext (Clear)
    Strict = 2     // Mandatory: all sensitive columns strictly masked
}
