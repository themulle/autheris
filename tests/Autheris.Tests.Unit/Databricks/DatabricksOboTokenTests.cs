namespace Autheris.Tests.Unit.Databricks;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Autheris.Tests.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.DatabricksTestSupport;

public sealed class DatabricksOboTokenTests
{
    private static string CreateMockJwt(string subject, DateTimeOffset expiresAt)
    {
        const string header = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0";
        var payloadJson = $"{{\"sub\":\"{subject}\",\"exp\":{expiresAt.ToUnixTimeSeconds()}}}";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{payload}.sig";
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-SIMPL-90")]
    public async Task Token_scoped_per_subject_audience_not_logged_expires_and_exchange_failure_fails_closed()
    {
        var initialTime = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(initialTime);

        var exchanges = new List<string>();
        var fakeHandler = new FakeHttpMessageHandler((req, _) =>
        {
            var body = req.Content is null ? string.Empty : req.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (req.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase))
            {
                if (body.Contains("fail-exchange"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"Bad token\"}", Encoding.UTF8, "application/json")
                    });
                }

                exchanges.Add(body);
                var token = $"downstream-{exchanges.Count}";
                var json = $"{{\"access_token\":\"{token}\",\"token_type\":\"Bearer\",\"expires_in\":300}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var http = new HttpClient(fakeHandler);
        var vault = Substitute.For<IKeyVaultSecretProvider>();
        vault.GetSecretBytes("kv://dbx-obo-secret").Returns(Encoding.UTF8.GetBytes("obo-secret-123"));

        var capturingLogger = new CapturingLogger<DatabricksOboTokenProvider>();
        using var cache = new DatabricksTokenCache(maxEntries: 100, timeProvider: time);

        var provider = new DatabricksOboTokenProvider(
            http,
            null,
            vault,
            Env("Production"),
            cache,
            capturingLogger,
            time);

        var dbOptions = new DatabricksDataSourceOptions
        {
            Host = DatabricksTestSupport.Host,
            WarehouseId = "wh-1",
            Auth = new DatabricksAuthOptions
            {
                Mode = DatabricksAuthMode.OAuthObo,
                Obo = new DatabricksOboOptions
                {
                    ClientId = "obo-client-id",
                    ClientSecretRef = "kv://dbx-obo-secret",
                    Scopes = "audience-1/.default"
                }
            }
        };

        var aliceJwt = CreateMockJwt("alice", initialTime.AddHours(1));
        var bobJwt = CreateMockJwt("bob", initialTime.AddHours(1));

        var aliceContext = new DelegationContext(CallerToken: aliceJwt, TenantId: new TenantId("tenant-1"));
        var bobContext = new DelegationContext(CallerToken: bobJwt, TenantId: new TenantId("tenant-1"));

        // 1. Token scoped per subject: Alice gets downstream-1, Bob gets downstream-2
        var aliceToken1 = await provider.AcquireTokenAsync(dbOptions, aliceContext, CancellationToken.None);
        aliceToken1.ShouldBe("downstream-1");

        var bobToken = await provider.AcquireTokenAsync(dbOptions, bobContext, CancellationToken.None);
        bobToken.ShouldBe("downstream-2");

        // Alice second call uses cache
        var aliceTokenCached = await provider.AcquireTokenAsync(dbOptions, aliceContext, CancellationToken.None);
        aliceTokenCached.ShouldBe("downstream-1");
        exchanges.Count.ShouldBe(2);

        // 2. Expiration: advance past token expiry (300 - 60 = 240s)
        time.Advance(TimeSpan.FromMinutes(5));
        var aliceTokenRefreshed = await provider.AcquireTokenAsync(dbOptions, aliceContext, CancellationToken.None);
        aliceTokenRefreshed.ShouldBe("downstream-3");

        // 3. Token never logged
        foreach (var message in capturingLogger.Messages)
        {
            message.ShouldNotContain("downstream-1");
            message.ShouldNotContain("downstream-2");
            message.ShouldNotContain("downstream-3");
            message.ShouldNotContain("obo-secret-123");
            message.ShouldNotContain(aliceJwt);
        }

        // 4. Exchange failure fails closed
        var failContext = new DelegationContext(CallerToken: "fail-exchange-assertion", TenantId: new TenantId("tenant-1"));
        var ex = await Should.ThrowAsync<DatabricksProviderException>(() =>
            provider.AcquireTokenAsync(dbOptions, failContext, CancellationToken.None));
        ex.Code.ShouldBe(DatabricksErrorCodes.AuthFailed);
        ex.Message.ShouldNotContain("fail-exchange");
        ex.Message.ShouldNotContain("obo-secret-123");
    }
}
