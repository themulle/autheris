using System.Text;
using Autheris.Application.Common.Interfaces;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// In-memory <see cref="IKeyVaultSecretProvider"/> for unit tests. Returns seeded secrets by reference,
/// falls back to an optional default secret or missing-key handler, and otherwise returns an empty byte array
/// (matching an unconfigured passive test double).
/// </summary>
public sealed class InMemoryKeyVaultSecretProvider : IKeyVaultSecretProvider
{
    private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);
    private readonly List<string> _requestedRefs = [];
    private readonly List<string> _writtenRefs = [];
    private readonly byte[]? _defaultSecret;
    private readonly Func<string, Exception>? _onMissing;

    public IReadOnlyList<string> RequestedRefs
    {
        get { lock (_requestedRefs) { return _requestedRefs.ToArray(); } }
    }

    public IReadOnlyList<string> WrittenRefs
    {
        get { lock (_writtenRefs) { return _writtenRefs.ToArray(); } }
    }

    public bool ContainsSecret(string secretRef) => _secrets.ContainsKey(secretRef);

    public InMemoryKeyVaultSecretProvider()
    {
    }

    public InMemoryKeyVaultSecretProvider(byte[] defaultSecret)
    {
        ArgumentNullException.ThrowIfNull(defaultSecret);
        _defaultSecret = defaultSecret;
    }

    public InMemoryKeyVaultSecretProvider(string defaultUtf8Secret)
    {
        ArgumentNullException.ThrowIfNull(defaultUtf8Secret);
        _defaultSecret = Encoding.UTF8.GetBytes(defaultUtf8Secret);
    }

    public InMemoryKeyVaultSecretProvider(Func<string, Exception> onMissing)
    {
        ArgumentNullException.ThrowIfNull(onMissing);
        _onMissing = onMissing;
    }

    public InMemoryKeyVaultSecretProvider Add(string secretRef, byte[] secretBytes)
    {
        ArgumentNullException.ThrowIfNull(secretBytes);
        _secrets[secretRef] = secretBytes;
        return this;
    }

    public InMemoryKeyVaultSecretProvider Add(string secretRef, string utf8Secret)
        => Add(secretRef, Encoding.UTF8.GetBytes(utf8Secret));

    public byte[] GetSecretBytes(string secretRef)
    {
        lock (_requestedRefs) { _requestedRefs.Add(secretRef); }
        if (_secrets.TryGetValue(secretRef, out var value))
        {
            return value;
        }

        if (_defaultSecret is not null)
        {
            return _defaultSecret;
        }

        if (_onMissing is not null)
        {
            throw _onMissing(secretRef);
        }

        return [];
    }

    public void SetSecret(string secretRef, byte[] secretBytes)
    {
        lock (_writtenRefs) { _writtenRefs.Add(secretRef); }
        Add(secretRef, secretBytes);
    }
}
