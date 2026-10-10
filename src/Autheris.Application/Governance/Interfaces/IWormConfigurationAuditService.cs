namespace Autheris.Application.Governance.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Service responsible for calculating canonical configuration hashes, detecting governance policy drift,
/// and minting tamper-proof WORM configuration audit records.
/// </summary>
public interface IWormConfigurationAuditService
{
    /// <summary>
    /// Computes the deterministic SHA-256 hash of the specified classification options.
    /// </summary>
    string ComputeCanonicalHash(ClassificationOptions options);

    /// <summary>
    /// Evaluates active configuration on startup or dynamic reload and mints a WORM audit record if modified.
    /// </summary>
    ValueTask<WormConfigurationRecord?> AuditConfigurationSnapshotAsync(
        string trigger,
        string actorSid = "SYSTEM",
        CancellationToken ct = default);

    /// <summary>
    /// Gets the most recent verified configuration hash.
    /// </summary>
    string? CurrentConfigHash { get; }
}
