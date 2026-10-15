namespace Autheris.Application.Streaming.Interfaces;

using System;

/// <summary>Resolves the key-encryption keys of the CDC stream (PLAN-CDC-99 D-A8, SEC-CDC99-14). Key bytes are never logged.</summary>
public interface ICdcKeyEncryptionKeyProvider
{
    string? ActiveKekId { get; }

    /// <summary>False means the KEK is unavailable (fail closed).</summary>
    bool TryGetKek(string kekId, out ReadOnlyMemory<byte> key);
}
