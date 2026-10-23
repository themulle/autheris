using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Subscriptions;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>Issue #279: read-only tokens must not open GraphQL subscriptions; subscription switch.</summary>
public sealed class Issue279ReadOnlySubscriptionTests
{
    private const string FixedMessage = "This token only permits read access; subscriptions are rejected.";

    public sealed class Q
    {
        public string Ping() => "pong";
    }

    public sealed class M
    {
        public static int Calls { get; set; }
        public string Touch() { Calls++; return "t"; }
    }

    public sealed class S
    {
        public static int Calls { get; set; }

        [Subscribe]
        public string OnTick([EventMessage] string message) => message;

        [Subscribe(With = nameof(Open))]
        public string OnOpen([EventMessage] string message) => message;

        public ValueTask<ISourceStream<string>> Open()
        {
            Calls++;
            throw new InvalidOperationException("resolver must not run");
        }
    }

    private static ClaimsPrincipal Principal(bool readOnly, string? scope = null)
    {
        var id = new ClaimsIdentity("Test");
        id.AddClaim(new Claim(ClaimTypes.NameIdentifier, "user-1"));
        if (scope != null) id.AddClaim(new Claim("scp", scope));
        if (readOnly) TokenAccessScope.MarkReadOnly(id);
        return new ClaimsPrincipal(id);
    }

    private static async Task<IRequestExecutor> BuildAsync(bool withSubscription = true, bool withCostRule = false)
    {
        var b = new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<Q>()
            .AddMutationType<M>();
        if (withSubscription)
        {
            b.AddSubscriptionType<S>().AddInMemorySubscriptions();
        }
        if (withCostRule)
        {
            b.AddValidationRule<QueryCostAnalyzerRule>((_, _) => new QueryCostAnalyzerRule(maxAllowedCost: int.MaxValue));
        }
        return await b
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseRequest<ReadOnlyOperationMiddleware>()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseOperationExecution()
            .BuildRequestExecutorAsync();
    }

    private static async Task<OperationResult> RunAsync(IRequestExecutor ex, string doc, ClaimsPrincipal? p, string? name = null, bool viaContextData = true)
    {
        var rb = OperationRequestBuilder.New().SetDocument(doc);
        if (name != null) rb.SetOperationName(name);
        if (p != null && viaContextData) rb.AddGlobalState(nameof(ClaimsPrincipal), p);
        return (OperationResult)await ex.ExecuteAsync(rb.Build());
    }

    private static void ShouldBeRejected(OperationResult r)
    {
        r.Errors.ShouldNotBeNull();
        r.Errors.ShouldNotBeEmpty();
        r.Errors[0].Code.ShouldBe("READ_ONLY_TOKEN", string.Join(" | ", r.Errors.Select(e => e.Message)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyPrincipal_Subscription_IsRejectedWithReadOnlyToken_AndResolverNotInvoked()
    {
        S.Calls = 0;
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "subscription { onOpen }", Principal(true));
        ShouldBeRejected(r);
        S.Calls.ShouldBe(0); // SEC-279-8
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyPrincipal_Subscription_ErrorContainsOnlyFixedMessageAndCode()
    {
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "subscription Secret { onOpen }", Principal(true), "Secret");
        r.Errors!.Count.ShouldBe(1);
        r.Errors[0].Message.ShouldBe(FixedMessage);
        r.Errors[0].Code.ShouldBe("READ_ONLY_TOKEN");
        r.Errors[0].Extensions!.Keys.ShouldBe(["code"]); // SEC-279-3
        r.Errors[0].Path.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyPrincipal_MixedDocument_NoOperationName_IsRejected()
    {
        S.Calls = 0;
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "query A { ping } subscription B { onOpen }", Principal(true));
        r.Errors.ShouldNotBeNull(); // rejected (validation or middleware), never executed
        S.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyPrincipal_SubscriptionNamedInMultiOperationDocument_NoOperationName_IsRejected()
    {
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "subscription B { onOpen } query A { ping }", Principal(true));
        r.Errors.ShouldNotBeNull(); // rejected (validation or middleware), never executed // SEC-279-1
    }

    [Fact]
    public async Task ReadOnlyPrincipal_MixedDocument_OperationNameQuery_IsExecuted()
    {
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "query A { ping } subscription B { onOpen }", Principal(true), "A");
        (r.Errors?.Count ?? 0).ShouldBe(0, string.Join(" | ", r.Errors?.Select(e => e.Message) ?? []));
        r.Data.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyPrincipal_MixedDocument_OperationNameSubscription_IsRejected()
    {
        S.Calls = 0;
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "query A { ping } subscription B { onOpen }", Principal(true), "B");
        ShouldBeRejected(r);
        S.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyPrincipal_SubscriptionWithFragments_IsRejected()
    {
        S.Calls = 0;
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "subscription { ...F } fragment F on S { onOpen }", Principal(true));
        ShouldBeRejected(r); // SEC-279-1
        S.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyPrincipal_SubscriptionOperationName_NotMatchingAnyOperation_DoesNotExecute()
    {
        S.Calls = 0;
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "subscription B { onOpen }", Principal(true), "Missing");
        r.Errors.ShouldNotBeNull(); // SEC-279-1: nothing runs
        S.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyPrincipal_WithSelfServiceScope_Subscription_IsRejected()
    {
        S.Calls = 0;
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "subscription { onOpen }", Principal(true, "data.access.request"));
        ShouldBeRejected(r);
        S.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyPrincipal_SelfServiceDisabled_OptionsUnresolvable_Subscription_IsRejected()
    {
        // No IOptions<GatewayOptions> registered in this executor: the subscription check must not depend on it (SEC-279-2).
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "subscription { onOpen }", Principal(true));
        ShouldBeRejected(r);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadOnlyPrincipal_FromSocketContextData_Subscription_IsRejected()
    {
        // Principal only in ContextData (WebSocket path), no HttpContext.
        S.Calls = 0;
        var ex = await BuildAsync();
        var r = await RunAsync(ex, "subscription { onOpen }", Principal(true), viaContextData: true);
        ShouldBeRejected(r);
        S.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task RegularPrincipal_Subscription_IsNotRejectedByMiddleware()
    {
        var ex = await BuildAsync();
        var request = OperationRequestBuilder.New().SetDocument("subscription { onTick }")
            .AddGlobalState(nameof(ClaimsPrincipal), Principal(false)).Build();
        var result = await ex.ExecuteAsync(request);
        // Executed: a response stream, not a READ_ONLY_TOKEN rejection.
        (result is OperationResult op && op.Errors?.Any(e => e.Code == "READ_ONLY_TOKEN") == true).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Subscriptions_Disabled_ReadOnlyMutation_StillRejected()
    {
        M.Calls = 0;
        var ex = await BuildAsync(withSubscription: false);
        var r = await RunAsync(ex, "mutation { touch }", Principal(true));
        ShouldBeRejected(r); // SEC-279-5
        M.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("mutation M { touch }", "M", OperationType.Mutation, true)]
    [InlineData("mutation M { touch } query Q { ping }", null, OperationType.Mutation, true)]
    [InlineData("mutation M { touch } query Q { ping }", "Q", OperationType.Mutation, false)]
    [InlineData("subscription S { onTick }", null, OperationType.Subscription, true)]
    [InlineData("subscription S { onTick }", "X", OperationType.Subscription, false)]
    [InlineData("query Q { ping }", null, OperationType.Subscription, false)]
    [InlineData("subscription { ...F } fragment F on S { onTick }", null, OperationType.Subscription, true)]
    public void SelectsOperation_Matrix(string doc, string? name, OperationType type, bool expected)
    {
        ReadOnlyOperationMiddleware.SelectsOperation(Utf8GraphQLParser.Parse(doc), name, type).ShouldBe(expected);
    }

    [Fact]
    public void GraphQLSubscriptionOptions_DefaultsToEnabled()
    {
        new GraphQLOptions().Subscriptions.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void GatewayOptions_Bind_SubscriptionsSectionAbsent_EnabledTrue()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        GatewayServiceCollectionExtensions.BindGatewayOptions(cfg).GraphQL.Subscriptions.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void GatewayOptions_Bind_EnabledFalse_IsHonoured()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{GatewaySections.Root}:GraphQL:Subscriptions:Enabled"] = "false"
        }).Build();
        GatewayServiceCollectionExtensions.BindGatewayOptions(cfg).GraphQL.Subscriptions.Enabled.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GraphQLSubscriptionOptions_InvalidBool_FailsStartup()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{GatewaySections.Root}:GraphQL:Subscriptions:Enabled"] = "not-a-bool"
        }).Build();
        Should.Throw<InvalidOperationException>(() => GatewayServiceCollectionExtensions.BindGatewayOptions(cfg)); // SEC-279-5
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AddGatewaySubscriptions_Disabled_SchemaHasNoSubscriptionType_AndSubscriptionDocumentFailsValidation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<WebSocketAuthInterceptor>(_ => null!);
        var b = services.AddGraphQLServer().AddQueryType<Q>()
            .AddValidationRule<QueryCostAnalyzerRule>((_, _) => new QueryCostAnalyzerRule(maxAllowedCost: int.MaxValue));
        b.AddGatewaySubscriptions(new GraphQLOptions { Subscriptions = new GraphQLSubscriptionOptions { Enabled = false } });
        var ex = await b.BuildRequestExecutorAsync();

        ex.Schema.SubscriptionType.ShouldBeNull();
        var r = (OperationResult)await ex.ExecuteAsync("subscription { onTick }");
        r.Errors.ShouldNotBeNull();
        r.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AddGatewaySubscriptions_Enabled_SchemaHasSubscriptionType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<WebSocketAuthInterceptor>(_ => null!);
        var b = services.AddGraphQLServer().AddQueryType<Q>();
        b.AddGatewaySubscriptions(new GraphQLOptions { Subscriptions = new GraphQLSubscriptionOptions { Enabled = true } });
        var ex = await b.BuildRequestExecutorAsync();
        ex.Schema.SubscriptionType.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Subscriptions_Disabled_WebSocketAuthInterceptor_StillRegistered()
    {
        var resolved = false;
        var services = new ServiceCollection();
        services.AddSingleton<WebSocketAuthInterceptor>(_ =>
        {
            resolved = true;
            return (WebSocketAuthInterceptor)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebSocketAuthInterceptor));
        });
        var b = services.AddGraphQLServer().AddQueryType<Q>().AddApplicationService<WebSocketAuthInterceptor>();
        b.AddGatewaySubscriptions(new GraphQLOptions { Subscriptions = new GraphQLSubscriptionOptions { Enabled = false } });
        var ex = await b.BuildRequestExecutorAsync();
        var interceptorType = typeof(WebSocketAuthInterceptor).BaseType!.GetInterfaces().Single();

        ((Schema)ex.Schema).Services.GetService(interceptorType).ShouldNotBeNull();
        resolved.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryCostAnalyzerRule_SchemaWithoutSubscriptionType_SubscriptionDocument_DoesNotThrow()
    {
        var ex = await BuildAsync(withSubscription: false, withCostRule: true);
        var r = await RunAsync(ex, "subscription { onTick }", Principal(false));
        r.Errors.ShouldNotBeNull(); // SEC-279-6: standard validation error, no exception, never accepted
        r.Errors.ShouldNotBeEmpty();
    }
}
