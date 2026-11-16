using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>
/// .NET 11 no longer downloads missing intermediate certificates on the TLS server side (AIA off) and caps the AIA fetches of
/// <see cref="X509Chain"/> on Linux. A certificate whose intermediate is neither presented nor trusted must therefore not build a
/// trusted chain: callers that map a client certificate to an identity must treat this as unauthenticated and never fall back to
/// a default identity. The test pins the platform behaviour the gateway's fail-closed assumption depends on (no AIA extension is
/// present, so the outcome does not depend on the network).
/// </summary>
[Trait("Category", "Security")]
public sealed class ChainWithoutIntermediateFailClosedTests
{
    private static (X509Certificate2 Root, X509Certificate2 Intermediate, X509Certificate2 Leaf) Pki()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest("CN=Test Intermediate", intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var intermediate = intermediateRequest
            .Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(20), [1, 2, 3, 4])
            .CopyWithPrivateKey(intermediateKey);

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest("CN=client.example.org", leafKey, HashAlgorithmName.SHA256);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        var leaf = leafRequest.Create(intermediate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), [5, 6, 7, 8]);

        return (root, intermediate, leaf);
    }

    private static X509Chain Chain(X509Certificate2 root)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = false;
        return chain;
    }

    [Fact]
    public void LeafWithoutIntermediate_DoesNotBuildATrustedChain()
    {
        var (root, _, leaf) = Pki();
        using var chain = Chain(root);

        chain.Build(leaf).ShouldBeFalse("a missing intermediate must not be fetched or invented");
        chain.ChainStatus.ShouldContain(static s => s.Status == X509ChainStatusFlags.PartialChain || s.Status == X509ChainStatusFlags.UntrustedRoot);
    }

    [Fact]
    public void LeafWithPresentedIntermediate_BuildsATrustedChain()
    {
        var (root, intermediate, leaf) = Pki();
        using var chain = Chain(root);
        chain.ChainPolicy.ExtraStore.Add(intermediate);

        chain.Build(leaf).ShouldBeTrue(string.Join(", ", chain.ChainStatus.Select(static s => s.Status)));
    }
}
