namespace Autheris.Application.Interfaces;

/// <summary>
/// Provider to resolve secrets from Azure Key Vault, HashiCorp Vault, or environment configurations.
/// </summary>
public interface IKeyVaultSecretProvider
{
    byte[] GetSecretBytes(string secretRef);
    void SetSecret(string secretRef, byte[] secretBytes) => throw new System.NotSupportedException();
}
