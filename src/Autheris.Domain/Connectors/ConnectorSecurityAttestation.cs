namespace Autheris.Domain.Connectors;

/// <summary>
/// Typed security attestation reported by a connector or data-source executor indicating which
/// enforcement steps were already executed upstream so the gateway neither filters nor masks twice.
/// </summary>
public readonly record struct ConnectorSecurityAttestation(
    bool RlsPushdownExecuted = false,
    bool InDbColumnMaskingExecuted = false)
{
    public static bool MayAttest(Autheris.Domain.Model.TableMetadata metadata) =>
        metadata.Table?.DataSourceType != Autheris.Domain.Model.DataSourceType.HttpDeclarative;
}
