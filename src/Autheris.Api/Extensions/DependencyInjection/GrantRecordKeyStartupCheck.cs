namespace Autheris.Api.Extensions.DependencyInjection;

using System.Text;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Grants.Records;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

public sealed class GrantRecordKeyStartupCheck(IGrantRecordSigner signer, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) { if (!IsDevOrTest(environment)) signer.EnsureSigningKeyAvailable(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static EcdsaGrantRecordSigner CreateSigner(IConfiguration configuration, IKeyVaultSecretProvider? secretProvider, IHostEnvironment environment)
    {
        var settings = new GrantRecordSigningSettings();
        configuration.GetSection(GrantRecordSigningSettings.SectionName).Bind(settings);
        var keyMaterial = !string.IsNullOrWhiteSpace(settings.SigningKeyVaultRef) && secretProvider is not null
            ? Encoding.UTF8.GetString(secretProvider.GetSecretBytes(settings.SigningKeyVaultRef))
            : settings.SigningKey;

        var previousKeys = settings.PreviousPublicKeys
            .Where(k => !string.IsNullOrWhiteSpace(k.KeyId) && !string.IsNullOrWhiteSpace(k.PublicKeyPem))
            .ToDictionary(k => k.KeyId, k => k.PublicKeyPem, StringComparer.Ordinal);

        ValidateAuditKeySeparation(keyMaterial, configuration, secretProvider);
        if (string.IsNullOrWhiteSpace(keyMaterial))
            return IsDevOrTest(environment)
                ? EcdsaGrantRecordSigner.CreateEphemeral(settings.KeyId, previousKeys)
                : EcdsaGrantRecordSigner.Unavailable("No grant record ECDSA signing key configured outside Development/Test.");

        return EcdsaGrantRecordSigner.FromPem(keyMaterial, settings.KeyId, previousKeys);
    }

    private static bool IsDevOrTest(IHostEnvironment env) =>
        env.IsDevelopment() || string.Equals(env.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase) || string.Equals(env.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);

    private static void ValidateAuditKeySeparation(string? signingMaterial, IConfiguration config, IKeyVaultSecretProvider? secretProvider)
    {
        if (string.IsNullOrWhiteSpace(signingMaterial)) return;
        foreach (var path in new[] { "Gateway:GovernanceDb:AuditHmacKey", "Gateway:Audit:AuditHmacKey" })
            if (config[path] is { Length: > 0 } raw) EcdsaGrantRecordSigner.EnsureSeparateFromAuditKey(signingMaterial, Encoding.UTF8.GetBytes(raw));
        if (config["Gateway:GovernanceDb:AuditHmacKeyVaultRef"] is { Length: > 0 } auditRef && secretProvider is not null)
        {
            try { EcdsaGrantRecordSigner.EnsureSeparateFromAuditKey(signingMaterial, secretProvider.GetSecretBytes(auditRef)); }
            catch (Exception ex) when (ex is not InvalidOperationException) { }
        }
    }
}
