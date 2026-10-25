namespace Autheris.Application.Streaming.Interfaces;

using System;

/// <summary>Outcome of a KEK lookup. Only <see cref="Unknown"/> is a proven permanent loss of the id; <see cref="Unavailable"/> may be transient.</summary>
public enum CdcKekLookup
{
    /// <summary>The key was resolved at full length.</summary>
    Available = 0,

    /// <summary>The id is not configured (KEK removed from configuration).</summary>
    Unknown = 1,

    /// <summary>The id is configured but cannot be resolved now (secret provider failure, short or reserved secret, empty id).</summary>
    Unavailable = 2
}

/// <summary>Resolves the key-encryption keys of the CDC stream (PLAN-CDC-99 D-A8, SEC-CDC99-14). Key bytes are never logged.</summary>
public interface ICdcKeyEncryptionKeyProvider
{
    string? ActiveKekId { get; }

    /// <summary>Resolves a KEK and distinguishes a removed id from a transient failure (INF-13). The key is set only when Available.</summary>
    CdcKekLookup ResolveKek(string kekId, out ReadOnlyMemory<byte> key);

    /// <summary>False means the KEK is unknown or unavailable (fail closed).</summary>
    bool TryGetKek(string kekId, out ReadOnlyMemory<byte> key) => ResolveKek(kekId, out key) == CdcKekLookup.Available;
}
