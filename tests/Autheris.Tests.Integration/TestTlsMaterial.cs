namespace Autheris.Tests.Integration;

using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// A throw-away CA and a PostgreSQL server certificate signed by it, generated in process for the shared test server. The Production-
/// environment hosts need a governance connection with <c>SSL Mode=VerifyCA</c> (ConnectionTlsPolicy); the CA file is the root certificate
/// they trust. Nothing here is a secret: the material lives only for the test process.
/// </summary>
internal sealed class TestTlsMaterial
{
    private TestTlsMaterial(string caPem, string serverCertPem, string serverKeyPem, string caFilePath)
    {
        CaPem = caPem;
        ServerCertPem = serverCertPem;
        ServerKeyPem = serverKeyPem;
        CaFilePath = caFilePath;
    }

    public string CaPem { get; }

    public string ServerCertPem { get; }

    public string ServerKeyPem { get; }

    /// <summary>File with the CA certificate (PEM) for the client's <c>Root Certificate</c> setting.</summary>
    public string CaFilePath { get; }

    public static TestTlsMaterial Create()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var until = now.AddDays(2);

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Autheris Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(now, until);

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.Parse("10.88.0.1"));
        serverRequest.CertificateExtensions.Add(san.Build());
        using var server = serverRequest.Create(ca, now, until, RandomNumberGenerator.GetBytes(16));

        var caPem = ca.ExportCertificatePem();
        var caFile = Path.Combine(Path.GetTempPath(), "autheris-test-ca-" + Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(caFile, caPem);
        return new TestTlsMaterial(caPem, server.ExportCertificatePem(), serverKey.ExportPkcs8PrivateKeyPem(), caFile);
    }
}
