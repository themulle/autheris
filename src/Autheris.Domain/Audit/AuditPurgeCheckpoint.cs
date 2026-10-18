namespace Autheris.Domain.Audit;

using System;

/// <summary>
/// SEC-AUD161-02: Cryptographic checkpoint anchor representing a verified and signed historical prefix purge of the audit trail.
/// Verifiers start sequential hash-chain verification from FirstSurvivingSequence using SurvivingPrevHash.
/// </summary>
public sealed record AuditPurgeCheckpoint(
    long FirstSurvivingSequence,
    string SurvivingPrevHash,
    long PurgedCount,
    DateTimeOffset PurgedThroughUtc,
    DateTimeOffset CreatedAtUtc,
    string Signature,
    string? ExternalSignature = null);
