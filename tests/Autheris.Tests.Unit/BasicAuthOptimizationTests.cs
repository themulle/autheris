using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public sealed class BasicAuthOptimizationTests
{
    #region PasswordHasher Tests

    [Fact]
    public void PasswordHasher_Argon2id_GeneratesValidHashAndVerifies()
    {
        const string password = "CorrectHorseBatteryStaple!";
        var hash = PasswordHasher.HashPasswordArgon2id(password, memorySizeKb: 8192, iterations: 1, parallelism: 1);

        hash.ShouldStartWith("$argon2id$v=19$m=8192,t=1,p=1$");

        var isValid = PasswordHasher.VerifyPassword(password, hash, "alice", isDevelopment: false);
        isValid.ShouldBeTrue();

        var isInvalid = PasswordHasher.VerifyPassword("WrongPassword123", hash, "alice", isDevelopment: false);
        isInvalid.ShouldBeFalse();
    }

    [Fact]
    public void PasswordHasher_PBKDF2_GeneratesValidHashAndVerifies()
    {
        const string password = "P@ssw0rdPBKDF2Test!";
        var hash = PasswordHasher.HashPasswordPbkdf2(password, iterations: 10000);

        hash.ShouldStartWith("$pbkdf2$10000$");

        var isValid = PasswordHasher.VerifyPassword(password, hash, "alice", isDevelopment: false);
        isValid.ShouldBeTrue();

        var isInvalid = PasswordHasher.VerifyPassword("WrongPassword!", hash, "alice", isDevelopment: false);
        isInvalid.ShouldBeFalse();
    }

    [Fact]
    public void PasswordHasher_PlaintextAndSha256Fallback_VerifiesCorrectlyInDev()
    {
        // Plaintext in dev
        PasswordHasher.VerifyPassword("mySecret", "mySecret", "alice", isDevelopment: true).ShouldBeTrue();
        PasswordHasher.VerifyPassword("mySecret", "different", "alice", isDevelopment: true).ShouldBeFalse();

        // Plaintext in prod must fail
        PasswordHasher.VerifyPassword("mySecret", "mySecret", "alice", isDevelopment: false).ShouldBeFalse();

        // Unsalted SHA256 hex fallback
        using var sha256 = SHA256.Create();
        var shaHex = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes("shaSecret"))).ToLowerInvariant();
        PasswordHasher.VerifyPassword("shaSecret", shaHex, "alice", isDevelopment: true).ShouldBeTrue();
        PasswordHasher.VerifyPassword("wrongSecret", shaHex, "alice", isDevelopment: true).ShouldBeFalse();
        PasswordHasher.VerifyPassword("shaSecret", shaHex, "alice", isDevelopment: false).ShouldBeFalse();
    }

    [Fact]
    public void PasswordHasher_Argon2id_EnforcesParameterBounds()
    {
        const string pwd = "test";
        var validHash = PasswordHasher.HashPasswordArgon2id(pwd, memorySizeKb: 8192, iterations: 1, parallelism: 1);
        var parts = validHash.Split('$');
        // parts: ["", "argon2id", "v=19", "m=8192,t=1,p=1", salt, hash]

        // Below min memory (8192 KB)
        var lowMemHash = $"${parts[1]}${parts[2]}$m=4096,t=1,p=1${parts[4]}${parts[5]}";
        PasswordHasher.VerifyPassword(pwd, lowMemHash, "alice", isDevelopment: false).ShouldBeFalse();

        // Above max memory (262144 KB)
        var highMemHash = $"${parts[1]}${parts[2]}$m=524288,t=1,p=1${parts[4]}${parts[5]}";
        PasswordHasher.VerifyPassword(pwd, highMemHash, "alice", isDevelopment: false).ShouldBeFalse();

        // Below min time cost (1)
        var zeroTimeHash = $"${parts[1]}${parts[2]}$m=8192,t=0,p=1${parts[4]}${parts[5]}";
        PasswordHasher.VerifyPassword(pwd, zeroTimeHash, "alice", isDevelopment: false).ShouldBeFalse();

        // Above max parallelism (8)
        var highParallelHash = $"${parts[1]}${parts[2]}$m=8192,t=1,p=16${parts[4]}${parts[5]}";
        PasswordHasher.VerifyPassword(pwd, highParallelHash, "alice", isDevelopment: false).ShouldBeFalse();
    }

    [Fact]
    public void PasswordHasher_PBKDF2_EnforcesIterationBounds()
    {
        const string pwd = "test";
        var salt = Convert.ToBase64String(new byte[16]);
        var hash = Convert.ToBase64String(new byte[32]);

        // Below min iterations (10,000)
        var lowIterHash = $"$pbkdf2$5000${salt}${hash}";
        PasswordHasher.VerifyPassword(pwd, lowIterHash, "alice", isDevelopment: false).ShouldBeFalse();

        // Above max iterations (10,000,000)
        var highIterHash = $"$pbkdf2$20000000${salt}${hash}";
        PasswordHasher.VerifyPassword(pwd, highIterHash, "alice", isDevelopment: false).ShouldBeFalse();
    }

    #endregion

    #region BasicAuthAttemptGuard Tests

    [Fact]
    public void AttemptGuard_InMemory_LocksOutAfterMaxAttempts()
    {
        var options = new BasicAuthOptions { MaxFailedAttempts = 3, FailureWindowSeconds = 300 };
        var guard = new BasicAuthAttemptGuard(options);
        const string ip = "192.168.1.100";
        const string user = "alice";
        var key = BasicAuthAttemptGuard.BuildAttemptKey(user, ip);

        guard.IsLockedOut(key).ShouldBeFalse();
        guard.RecordFailure(key);
        guard.IsLockedOut(key).ShouldBeFalse();

        guard.RecordFailure(key);
        guard.IsLockedOut(key).ShouldBeFalse();

        guard.RecordFailure(key); // 3rd attempt
        guard.IsLockedOut(key).ShouldBeTrue();

        // Different user from same IP is not locked out
        var bobKey = BasicAuthAttemptGuard.BuildAttemptKey("bob", ip);
        guard.IsLockedOut(bobKey).ShouldBeFalse();

        // Reset on success
        guard.RecordSuccess(key);
        guard.IsLockedOut(key).ShouldBeFalse();
    }

    [Fact]
    public void AttemptGuard_WithDistributedCache_UsesCacheAndSurvivesExceptions()
    {
        var mockCache = Substitute.For<IDistributedCache>();
        mockCache.Get(Arg.Any<string>()).Throws(new InvalidOperationException("Redis connection failure"));

        var options = new BasicAuthOptions { MaxFailedAttempts = 3, FailureWindowSeconds = 300 };
        var guard = new BasicAuthAttemptGuard(options, null, mockCache);
        const string ip = "10.0.0.1";
        const string user = "charlie";
        var key = BasicAuthAttemptGuard.BuildAttemptKey(user, ip);

        // Even though IDistributedCache threw, the guard must not crash and fallback to memory
        guard.IsLockedOut(key).ShouldBeFalse();
        guard.RecordFailure(key);
        guard.RecordFailure(key);
        guard.RecordFailure(key);

        guard.IsLockedOut(key).ShouldBeTrue();
    }

    #endregion

    #region BasicAuthenticationHandler Integration Tests

    [Fact]
    public async Task Handler_AuthenticatesWithArgon2idSuccessfully()
    {
        const string user = "argoUser";
        const string pwd = "SuperSecretPassword123!";
        var argonHash = PasswordHasher.HashPasswordArgon2id(pwd, memorySizeKb: 8192, iterations: 1, parallelism: 1);

        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Users = [new BasicAuthUserConfig { Username = user, Password = argonHash, Roles = ["Admin"] }]
                }
            }
        };

        var handler = CreateHandler(options);
        var httpContext = new DefaultHttpContext();
        var rawCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pwd}"));
        httpContext.Request.Headers.Authorization = $"Basic {rawCredentials}";

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Basic, "Basic", typeof(BasicAuthenticationHandler)), httpContext);
        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal?.Identity?.Name.ShouldBe(user);
        result.Principal?.IsInRole("Admin").ShouldBeTrue();
    }

    [Fact]
    public async Task Handler_UsesClientIpResolver_ForLockoutTracking()
    {
        const string user = "testUser";
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    MaxFailedAttempts = 2,
                    FailureWindowSeconds = 300,
                    Users = [new BasicAuthUserConfig { Username = user, Password = "dev" }]
                }
            }
        };

        var ipResolver = Substitute.For<IClientIpResolver>();
        ipResolver.ResolveClientIp().Returns(IPAddress.Parse("203.0.113.195"));

        var scheme = new AuthenticationScheme(GatewayAuthSchemes.Basic, "Basic", typeof(BasicAuthenticationHandler));
        var wrongCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:wrongPassword"));

        DefaultHttpContext CreateContext()
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1"); // Ingress IP
            ctx.Request.Headers.Authorization = $"Basic {wrongCredentials}";
            return ctx;
        }

        // Fail 1
        var handler1 = CreateHandler(options, ipResolver);
        await handler1.InitializeAsync(scheme, CreateContext());
        var result1 = await handler1.AuthenticateAsync();
        result1.Succeeded.ShouldBeFalse();

        // Fail 2
        var handler2 = CreateHandler(options, ipResolver);
        await handler2.InitializeAsync(scheme, CreateContext());
        var result2 = await handler2.AuthenticateAsync();
        result2.Succeeded.ShouldBeFalse();

        // 3rd attempt -> locked out
        var handler3 = CreateHandler(options, ipResolver);
        await handler3.InitializeAsync(scheme, CreateContext());
        var result3 = await handler3.AuthenticateAsync();
        result3.Succeeded.ShouldBeFalse();

        // Verify resolver was called
        ipResolver.Received().ResolveClientIp();

        // Verify that the resolved IP was locked out, and NOT the ingress IP
        var guard = BasicAuthAttemptGuard.For(options.Authentication.BasicAuth);
        var resolvedKey = BasicAuthAttemptGuard.BuildAttemptKey(user, "203.0.113.195");
        var ingressKey = BasicAuthAttemptGuard.BuildAttemptKey(user, "10.0.0.1");

        guard.IsLockedOut(resolvedKey).ShouldBeTrue();
        guard.IsLockedOut(ingressKey).ShouldBeFalse();
    }

    [Fact]
    public async Task Handler_HandlesLargeHeaderSafely()
    {
        // Credentials larger than 2048 chars (stackalloc boundary)
        var longUser = new string('u', 1200);
        var longPass = new string('p', 1200);
        var rawCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{longUser}:{longPass}"));

        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Users = [new BasicAuthUserConfig { Username = "other", Password = "dev" }]
                }
            }
        };

        var handler = CreateHandler(options);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = $"Basic {rawCredentials}";

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Basic, "Basic", typeof(BasicAuthenticationHandler)), httpContext);
        var result = await handler.AuthenticateAsync();

        // Should safely fail authentication without throwing StackOverflowException
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Handler_UnknownUser_ExecutesDummyVerificationAndFails()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Users = [new BasicAuthUserConfig { Username = "realUser", Password = "$argon2id$v=19$m=8192,t=1,p=1$fakeSalt$fakeHash" }]
                }
            }
        };

        var handler = CreateHandler(options);
        var httpContext = new DefaultHttpContext();
        var rawCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("nonExistentUser:somePassword123"));
        httpContext.Request.Headers.Authorization = $"Basic {rawCredentials}";

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Basic, "Basic", typeof(BasicAuthenticationHandler)), httpContext);
        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure?.Message.ShouldContain("Invalid username or password");
    }

    private static BasicAuthenticationHandler CreateHandler(
        GatewayOptions options,
        IClientIpResolver? clientIpResolver = null)
    {
        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Development");

        var schemeMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemeMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        return new BasicAuthenticationHandler(
            schemeMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            Options.Create(options),
            mockEnv,
            clientIpResolver);
    }

    #endregion

    #region PasswordHashCli Tests

    [Fact]
    public void PasswordHashCli_GeneratesArgon2idAndPbkdf2Hashes()
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(stdout);

            var exitCode1 = PasswordHashCli.Run(["hash-password", "Secret123!", "--type", "argon2id", "--memory", "8192", "--iterations", "1", "--parallelism", "1"]);
            exitCode1.ShouldBe(0);
            var output1 = stdout.ToString();
            output1.ShouldContain("$argon2id$v=19$m=8192,t=1,p=1$");

            stdout.GetStringBuilder().Clear();

            var exitCode2 = PasswordHashCli.Run(["hash-password", "Secret123!", "--type", "pbkdf2", "--iterations", "15000"]);
            exitCode2.ShouldBe(0);
            var output2 = stdout.ToString();
            output2.ShouldContain("$pbkdf2$15000$");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    #endregion
}
