namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Autheris.Api.Security;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.Application.Identity.Validators;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class StaticJwksKeySourceTests : IDisposable
{
    private const string Issuer = "https://issuer.example";
    private const string Audience = "autheris";

    private readonly string _dir = Directory.CreateTempSubdirectory("jwks-test").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static RSA NewRsa(int bits = 2048) => RSA.Create(bits);

    private static Dictionary<string, object?> RsaJwk(RSA rsa, string? kid, string? alg = "RS256")
    {
        var p = rsa.ExportParameters(false);
        var jwk = new Dictionary<string, object?>
        {
            ["kty"] = "RSA",
            ["n"] = Base64UrlEncoder.Encode(p.Modulus),
            ["e"] = Base64UrlEncoder.Encode(p.Exponent),
            ["use"] = "sig"
        };
        if (kid is not null) jwk["kid"] = kid;
        if (alg is not null) jwk["alg"] = alg;
        return jwk;
    }

    private string Write(params Dictionary<string, object?>[] keys) => WriteRaw(JsonSerializer.Serialize(new { keys }));

    private string WriteRaw(string json, string name = "jwks.json")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static StaticJwksKeySource Source(string path, FakeTimeProvider? time = null) =>
        new(path, TimeSpan.FromMinutes(5), time ?? new FakeTimeProvider(), NullLogger.Instance);

    [Fact]
    public void Jwks_MissingFile_FailsStartup() =>
        Should.Throw<StaticJwksException>(() => Source(Path.Combine(_dir, "nope.json")));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"keys\":[]}")]
    [InlineData("{\"keys\":{}}")]
    public void Jwks_EmptyOrInvalid_FailsStartup(string json) =>
        Should.Throw<StaticJwksException>(() => Source(WriteRaw(json)));

    [Fact]
    public void Jwks_OversizeFile_Rejected()
    {
        var path = WriteRaw(new string(' ', StaticJwksKeySource.MaxFileBytes + 1));

        Should.Throw<StaticJwksException>(() => Source(path));
    }

    [Fact]
    public void Jwks_TooManyKeys_Rejected()
    {
        using var rsa = NewRsa();
        var keys = Enumerable.Range(0, StaticJwksKeySource.MaxKeys + 1).Select(i => RsaJwk(rsa, $"k{i}")).ToArray();

        Should.Throw<StaticJwksException>(() => Source(Write(keys)));
        Source(Write([.. keys.Take(StaticJwksKeySource.MaxKeys)])).KeyCount.ShouldBe(StaticJwksKeySource.MaxKeys);
    }

    [Theory]
    [InlineData("d")]
    [InlineData("p")]
    [InlineData("q")]
    public void Jwks_PrivateKeyMaterial_Rejected(string member)
    {
        using var rsa = NewRsa();
        var jwk = RsaJwk(rsa, "k1");
        jwk[member] = "AQAB";

        Should.Throw<StaticJwksException>(() => Source(Write(jwk)));
    }

    [Fact]
    public void Jwks_SymmetricKey_Rejected()
    {
        var oct = new Dictionary<string, object?> { ["kty"] = "oct", ["kid"] = "k1", ["k"] = Base64UrlEncoder.Encode(new byte[32]) };
        var octNoK = new Dictionary<string, object?> { ["kty"] = "oct", ["kid"] = "k1" };

        Should.Throw<StaticJwksException>(() => Source(Write(oct)));
        Should.Throw<StaticJwksException>(() => Source(Write(octNoK)));
    }

    [Fact]
    public void Jwks_RsaBelow2048_Rejected()
    {
        using var small = NewRsa(1024);

        Should.Throw<StaticJwksException>(() => Source(Write(RsaJwk(small, "k1"))));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    public void Jwks_AlgNoneOrHs256_Rejected(string alg)
    {
        using var rsa = NewRsa();

        Should.Throw<StaticJwksException>(() => Source(Write(RsaJwk(rsa, "k1", alg))));
    }

    [Fact]
    public void Jwks_MissingOrDuplicateKid_Rejected()
    {
        using var rsa = NewRsa();

        Should.Throw<StaticJwksException>(() => Source(Write(RsaJwk(rsa, null))));
        Should.Throw<StaticJwksException>(() => Source(Write(RsaJwk(rsa, "k1"), RsaJwk(rsa, "k1"))));
    }

    [Fact]
    public void Jwks_EcKeys_AllowedCurvesOnly()
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p521 = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        Dictionary<string, object?> Ec(ECDsa key, string crv)
        {
            var p = key.ExportParameters(false);
            return new() { ["kty"] = "EC", ["kid"] = crv, ["crv"] = crv, ["x"] = Base64UrlEncoder.Encode(p.Q.X), ["y"] = Base64UrlEncoder.Encode(p.Q.Y) };
        }

        Source(Write(Ec(p256, "P-256"))).KeyCount.ShouldBe(1);
        Should.Throw<StaticJwksException>(() => Source(Write(Ec(p521, "P-521"))));
    }

    [Fact]
    public void Jwks_WithMetadataAddress_Fails()
    {
        var options = new IdentityRealmsOptions
        {
            Realms = new(StringComparer.OrdinalIgnoreCase)
            {
                ["workload"] = new IdentityRealmConfigOptions
                {
                    Kind = IdentityRealmKind.GenericOidc,
                    Issuers = [Issuer],
                    Audiences = [Audience],
                    MetadataAddress = "https://issuer.example/.well-known/openid-configuration",
                    Jwks = new StaticJwksSource { File = Path.Combine(_dir, "jwks.json") }
                }
            }
        };

        new SubjectRoleOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();

        options.Realms["workload"].MetadataAddress = null;
        new SubjectRoleOptionsValidator().Validate(null, options).Failed.ShouldBeFalse();

        options.Realms["workload"].Jwks!.File = "relative/jwks.json";
        new SubjectRoleOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Jwks_Rotation_NewKidAccepted_OldKidRemovedAfterReload()
    {
        using var oldKey = NewRsa();
        using var newKey = NewRsa();
        var time = new FakeTimeProvider();
        var path = Write(RsaJwk(oldKey, "old"));
        var source = Source(path, time);
        source.ResolveKeys("old").Count.ShouldBe(1);

        Write(RsaJwk(newKey, "new"));
        time.Advance(TimeSpan.FromMinutes(6));

        source.ResolveKeys("new").Count.ShouldBe(1);
        source.ResolveKeys("old").ShouldBeEmpty();
    }

    [Fact]
    public void Jwks_ReloadFailure_KeepsLastGoodSet()
    {
        using var rsa = NewRsa();
        var time = new FakeTimeProvider();
        var path = Write(RsaJwk(rsa, "k1"));
        var source = Source(path, time);

        File.WriteAllText(path, "garbage");
        time.Advance(TimeSpan.FromMinutes(6));
        source.ResolveKeys("k1").Count.ShouldBe(1);

        File.Delete(path);
        time.Advance(TimeSpan.FromMinutes(6));
        source.ResolveKeys("k1").Count.ShouldBe(1);
    }

    [Fact]
    public void Jwks_UnknownKidReload_RateLimited()
    {
        using var rsa = NewRsa();
        using var added = NewRsa();
        var time = new FakeTimeProvider();
        var path = Write(RsaJwk(rsa, "k1"));
        var source = Source(path, time);

        time.Advance(TimeSpan.FromSeconds(31));
        source.ResolveKeys("later").ShouldBeEmpty();
        Write(RsaJwk(rsa, "k1"), RsaJwk(added, "later"));

        time.Advance(TimeSpan.FromSeconds(5));
        source.ResolveKeys("later").ShouldBeEmpty();

        time.Advance(TimeSpan.FromSeconds(30));
        source.ResolveKeys("later").Count.ShouldBe(1);
    }

    [Fact]
    public void Jwks_NoHttpRequestMade()
    {
        using var rsa = NewRsa();
        var requests = 0;
        using var handler = new CountingHandler(() => requests++);
        var path = Write(RsaJwk(rsa, "k1"));

        var source = Source(path);
        source.ResolveKeys("unknown");

        requests.ShouldBe(0);
        typeof(StaticJwksKeySource).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .ShouldNotContain(f => typeof(HttpClient).IsAssignableFrom(f.FieldType));
    }

    private static string Token(RSA rsa, string kid, string issuer = Issuer, string audience = Audience, TimeSpan? lifetime = null)
    {
        var now = DateTime.UtcNow;
        var expires = now + (lifetime ?? TimeSpan.FromMinutes(10));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = lifetime is { } l && l < TimeSpan.Zero ? now.AddHours(-2) : now.AddMinutes(-1),
            Expires = expires,
            Claims = new Dictionary<string, object> { ["sub"] = "system:serviceaccount:data:dbt-sync" },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = kid }, SecurityAlgorithms.RsaSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private async Task<bool> Validates(string token, RSA signer, string kidInFile = "k1")
    {
        var path = Write(RsaJwk(signer, kidInFile));
        var options = new JwtBearerOptions();
        var env = new ProductionEnvironment();
        var realm = new IdentityRealmConfigOptions { Kind = IdentityRealmKind.GenericOidc, Issuers = [Issuer], Audiences = [Audience] };
        GatewayServiceCollectionExtensions.ConfigureGenericOidcJwtBearer(options, realm, Source(path), env);
        var parameters = options.TokenValidationParameters.Clone();
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        return result.IsValid;
    }

    [Fact]
    public async Task Jwt_ValidToken_Accepted_WithStaticJwks()
    {
        using var rsa = NewRsa();

        (await Validates(Token(rsa, "k1"), rsa)).ShouldBeTrue();
    }

    [Fact]
    public async Task Jwt_WrongAudience_WrongIssuer_Expired_Rejected_WithStaticJwks()
    {
        using var rsa = NewRsa();
        using var other = NewRsa();

        (await Validates(Token(rsa, "k1", audience: "other"), rsa)).ShouldBeFalse();
        (await Validates(Token(rsa, "k1", issuer: "https://evil.example"), rsa)).ShouldBeFalse();
        (await Validates(Token(rsa, "k1", lifetime: TimeSpan.FromMinutes(-60)), rsa)).ShouldBeFalse();
        (await Validates(Token(rsa, "unknown-kid"), rsa)).ShouldBeFalse();
        (await Validates(Token(other, "k1"), rsa)).ShouldBeFalse();
    }

    [Fact]
    public void UnconfiguredIssuer_SelectsNoRealmScheme_SoItIsRejected()
    {
        using var rsa = NewRsa();
        var map = new GenericOidcSchemeMap();
        map.Add(Issuer, GenericOidcSchemeMap.SchemeName("workload"));
        var options = new GatewayOptions();

        GatewayAuthSchemes.SelectJwtScheme(Token(rsa, "k1"), options, map).ShouldBe(GenericOidcSchemeMap.SchemeName("workload"));
        GatewayAuthSchemes.SelectJwtScheme(Token(rsa, "k1", issuer: "https://evil.example"), options, map).ShouldBe(GatewayAuthSchemes.JwtBearer);
        GatewayAuthSchemes.SelectJwtScheme("not-a-jwt", options, map).ShouldBe(GatewayAuthSchemes.JwtBearer);

        // The fallback scheme has no issuer for it: Entra and AD FS are disabled, so no ValidIssuers exist.
        var fallback = new JwtBearerOptions();
        fallback.TokenValidationParameters.ValidIssuers.ShouldBeNull();
    }

    private static IdentityRealmConfigOptions Realm(bool optIn) => new()
    {
        Kind = IdentityRealmKind.GenericOidc,
        Issuers = [Issuer],
        Audiences = [Audience],
        UserKeyClaim = "sub",
        SubjectRoles = optIn ? [new SubjectRoleMapping { Subject = "a:b", Roles = ["TenantAdmin"] }] : []
    };

    private static (GenericOidcSchemeMap Map, IdentityRealmsOptions Options) RegisterRealm(IdentityRealmConfigOptions realm, bool withConfiguration)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        if (withConfiguration)
        {
            var values = new Dictionary<string, string?> { ["Authentication:Realms:Realms:wl:Kind"] = "GenericOidc", ["Authentication:Realms:Realms:wl:Issuers:0"] = Issuer };
            if (realm.SubjectRoles.Count > 0)
            {
                values["Authentication:Realms:Realms:wl:SubjectRoles:0:Subject"] = "a:b";
                values["Authentication:Realms:Realms:wl:SubjectRoles:0:Roles:0"] = "TenantAdmin";
            }

            services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build());
        }

        services.AddGatewayAuth(new GatewayOptions(), new ProductionEnvironment());
        var map = services.BuildServiceProvider().GetRequiredService<GenericOidcSchemeMap>();
        return (map, new IdentityRealmsOptions { Realms = new(StringComparer.OrdinalIgnoreCase) { ["wl"] = realm } });
    }

    [Fact]
    public void GenericOidcRealm_WithoutSubjectRolesOrJwks_GetsNoRealmScheme()
    {
        var (map, options) = RegisterRealm(Realm(optIn: false), withConfiguration: true);

        map.IsEmpty.ShouldBeTrue();
        GatewayAuthSchemes.SelectJwtScheme(Token(NewRsa(), "k1"), new GatewayOptions(), map).ShouldBe(GatewayAuthSchemes.JwtBearer);
        new GenericOidcSchemeCoverageValidator(map).Validate(null, options).Failed.ShouldBeFalse();
    }

    [Fact]
    public void GenericOidcRealm_WithSubjectRoles_GetsRealmScheme()
    {
        var (map, options) = RegisterRealm(Realm(optIn: true), withConfiguration: true);

        map.TryGetScheme(Issuer, out var scheme).ShouldBeTrue();
        scheme.ShouldBe(GenericOidcSchemeMap.SchemeName("wl"));
        new GenericOidcSchemeCoverageValidator(map).Validate(null, options).Failed.ShouldBeFalse();
    }

    [Fact]
    public void GenericOidcRealm_WithSubjectRoles_ButNoRegistrationConfiguration_FailsStartup()
    {
        var (map, options) = RegisterRealm(Realm(optIn: true), withConfiguration: false);

        new GenericOidcSchemeCoverageValidator(map).Validate(null, options).Failed.ShouldBeTrue();
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Autheris";
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class CountingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage());
        }
    }
}
