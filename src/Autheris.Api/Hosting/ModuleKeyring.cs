namespace Autheris.Api.Hosting;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

/// <summary>
/// Owns the ECDSA P-256 public keys that may sign module manifests, indexed by key id.
/// </summary>
public sealed class ModuleKeyring : IDisposable
{
    private readonly List<ECDsa> _owned;

    internal ModuleKeyring(IReadOnlyDictionary<string, ECDsa> keys, List<ECDsa> owned)
    {
        Keys = keys;
        _owned = owned;
    }

    public IReadOnlyDictionary<string, ECDsa> Keys { get; }

    public void Dispose()
    {
        foreach (var key in _owned)
        {
            key.Dispose();
        }
    }
}
