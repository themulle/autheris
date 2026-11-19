namespace Autheris.Tests.Integration.Interop.Trino;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>
/// Starts the gateway on a real HTTPS loopback socket (Trino clients refuse credentials over plain HTTP) with the governed parity data
/// plus a 10,000 row table with one masked column and a table nobody may read. Official clients send an opaque bearer alias; a
/// test-only bridge maps it to the test principal in front of the real pipeline, so authorization, masking and the Trino adapter are the
/// production code paths. Opt-in only: the interop tests carry <c>Category=TrinoInterop</c> and run in the nightly workflow.
/// </summary>
public sealed class TrinoInteropGatewayFixture : RowFilterChannelParityTests.Fixture
{
    public const string ReaderToken = "interop-reader";
    public const string ReadOnlyToken = "interop-readonly";
    public const int LargeRowCount = 10_000;
    public const string LargeTable = "default.main.big_t";
    public const string DeniedTable = "default.main.vault_t";

    private static readonly TableIdentifier BigId = new("default", "main", "big_t");
    private static readonly TableIdentifier VaultId = new("default", "main", "vault_t");

    private X509Certificate2? _certificate;
    private int _port;

    /// <summary>The loopback port Kestrel bound (listening on port 0; valid after initialization).</summary>
    public int Port => _port != 0 ? _port : _port = new Uri(
        Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First(a => a.StartsWith("https://", StringComparison.Ordinal))).Port;

    /// <summary>Every request that carried an interop alias, with its final status (the clients ignore cancel responses).</summary>
    public InteropRequestLog Requests => Services.GetRequiredService<InteropRequestLog>();

    protected override void SeedExtraData(NpgsqlConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE big_t (id INTEGER PRIMARY KEY, label TEXT, secret TEXT);
            INSERT INTO big_t SELECT g, 'label-' || g, 'secret-' || g FROM generate_series(1, {LargeRowCount}) AS g;
            CREATE TABLE vault_t (id INTEGER PRIMARY KEY, note TEXT);
            INSERT INTO vault_t VALUES (1, 'closed');
            """;
        cmd.ExecuteNonQuery();
    }

    protected override async Task SeedExtraGovernanceAsync(IGovernanceRepository repo)
    {
        var big = await RegisterTableAsync(repo, BigId, ("id", "integer"), ("label", "varchar"), ("secret", "varchar"));
        await RegisterTableAsync(repo, VaultId, ("id", "integer"), ("note", "varchar"));
        await repo.CreateConsentAsync(new Consent
        {
            TableId = big.Table.Id,
            TableIdentifier = BigId,
            TenantId = new TenantId(Tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid(ReadersGroupSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = big.Columns.Select(c => new ConsentColumnRule
            {
                ColumnName = c.ColumnName,
                AccessLevel = c.ColumnName == "secret" ? ColumnAccessLevel.Mask : ColumnAccessLevel.Clear
            }).ToList(),
            RowFilters = []
        });
    }

    protected override void ConfigureHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Gateway:RowLimits:Trino:DefaultMaxRows", "20000");
        builder.UseSetting("Gateway:RowLimits:Trino:MaxAllowedRows", "20000");
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpContextAccessor();
            services.AddSingleton<InteropRequestLog>();
            services.AddTransient<IStartupFilter, BearerAliasBridgeStartupFilter>();
            var existing = services.Last(d => d.ServiceType == typeof(IClaimsTransformation));
            services.Remove(existing);
            services.AddTransient<IClaimsTransformation>(sp => new ReadOnlyMarkingTransformation(
                existing.ImplementationType is { } type
                    ? (IClaimsTransformation)ActivatorUtilities.CreateInstance(sp, type)
                    : existing.ImplementationFactory is { } factory
                        ? (IClaimsTransformation)factory(sp)
                        : (IClaimsTransformation)existing.ImplementationInstance!,
                sp.GetRequiredService<IHttpContextAccessor>(),
                sp.GetRequiredService<InteropRequestLog>()));
        });
    }

    protected override void ConfigureFactory(WebApplicationFactory<Program> factory)
    {
        _certificate = CreateSelfSignedCertificate();
        var certificate = _certificate;
        factory.UseKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    internal sealed class BearerAliasBridgeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                var alias = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization["Bearer ".Length..] : string.Empty;
                if (alias is ReaderToken or ReadOnlyToken)
                {
                    var headers = context.Request.Headers;
                    headers.Remove("Authorization");
                    headers["X-Test-User-Sid"] = DavidSid;
                    headers["X-Test-Roles"] = "Reader";
                    headers["X-Test-Group-Sids"] = ReadersGroupSid;
                    headers["X-Test-Tenant"] = Tenant;
                    if (alias == ReadOnlyToken)
                    {
                        context.Items[ReadOnlyMarkingTransformation.ItemKey] = true;
                    }

                    await nextMiddleware();
                    context.RequestServices.GetRequiredService<InteropRequestLog>()
                        .Add(alias, context.Request.Method, context.Request.Path.Value ?? string.Empty, context.Response.StatusCode);
                    return;
                }

                await nextMiddleware();
            });
            next(app);
        };
    }

    internal sealed class ReadOnlyMarkingTransformation(IClaimsTransformation inner, IHttpContextAccessor accessor, InteropRequestLog log) : IClaimsTransformation
    {
        public const string ItemKey = "interop.readonly";

        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var result = await inner.TransformAsync(principal);
            if (accessor.HttpContext?.Items.ContainsKey(ItemKey) == true && !result.IsReadOnly())
            {
                var identity = new ClaimsIdentity();
                TokenAccessScope.MarkReadOnly(identity);
                result.AddIdentity(identity);
                log.CountReadOnlyMark();
            }

            return result;
        }
    }
}

/// <summary>Thread-safe record of the requests the interop bridge served and how often the read-only marker was applied.</summary>
public sealed class InteropRequestLog
{
    private readonly ConcurrentQueue<(string Alias, string Method, string Path, int Status)> _entries = new();
    private int _readOnlyMarks;

    public int ReadOnlyMarks => Volatile.Read(ref _readOnlyMarks);

    public IReadOnlyList<(string Alias, string Method, string Path, int Status)> Entries => [.. _entries];

    internal void Add(string alias, string method, string path, int status) => _entries.Enqueue((alias, method, path, status));

    internal void CountReadOnlyMark() => Interlocked.Increment(ref _readOnlyMarks);
}
