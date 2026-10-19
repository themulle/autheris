namespace Autheris.Infrastructure.Telemetry;

using System;
using System.IO;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Autheris.Domain.Options.Observability;

/// <summary>
/// Hardened HttpClient factory for OTLP export channels.
/// Enforces SEC-OT06-13: no auto-redirects, TLS &gt;= 1.2, no proxy by default, mTLS from mounted secrets.
/// </summary>
public static class OtlpHttpClientFactory
{
    public static Func<HttpClient> Create(TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var otlp = options.Otlp;

        return () =>
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = otlp.UseSystemProxy,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }
            };

            if (!string.IsNullOrWhiteSpace(otlp.ClientCertificatePath) && !string.IsNullOrWhiteSpace(otlp.ClientKeyPath))
            {
                if (File.Exists(otlp.ClientCertificatePath) && File.Exists(otlp.ClientKeyPath))
                {
                    var cert = X509Certificate2.CreateFromPemFile(otlp.ClientCertificatePath, otlp.ClientKeyPath);
                    handler.SslOptions.ClientCertificates = new X509CertificateCollection { cert };
                }
            }

            var client = new HttpClient(handler, disposeHandler: true);
            if (otlp.Timeout > TimeSpan.Zero)
            {
                client.Timeout = otlp.Timeout;
            }

            return client;
        };
    }
}
