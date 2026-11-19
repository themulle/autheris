using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Common;
using Autheris.Domain.Security;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using NSubstitute.ExceptionExtensions;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Streaming.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class BasicAuthLockoutAtomicityTests
{
    [Fact]
    public async Task SEC_12H_05_BasicAuthAttemptGuard_UsesAtomicLuaScript_WhenRedisConnected()
    {
        var redis = Substitute.For<StackExchange.Redis.IConnectionMultiplexer>();
        var db = Substitute.For<StackExchange.Redis.IDatabase>();
        redis.IsConnected.Returns(true);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var options = new BasicAuthOptions
        {
            Enabled = true,
            MaxFailedAttempts = 5,
            FailureWindowSeconds = 60
        };

        var guard = new BasicAuthAttemptGuard(options, redis: redis);
        await guard.RecordFailureAsync("TESTUSER|127.0.0.1");

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(s => s.Contains("redis.call('INCR'") && s.Contains("redis.call('EXPIRE'")),
            Arg.Is<StackExchange.Redis.RedisKey[]>(keys => keys.Length == 2 && keys[0].ToString() == "autheris:fail:{TESTUSER|127.0.0.1}"),
            Arg.Is<StackExchange.Redis.RedisValue[]>(vals => vals.Length == 2 && (long)vals[0] == 60 && (int)vals[1] == 5));
    }
}
