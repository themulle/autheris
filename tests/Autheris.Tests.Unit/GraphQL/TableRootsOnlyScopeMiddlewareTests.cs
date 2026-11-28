namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Tokens with a listed scope may only use catalog table roots plus __typename; everything known-disallowed is rejected before validation.</summary>
[Trait("Category", "Security")]
public sealed class TableRootsOnlyScopeMiddlewareTests
{
    private const string Rejected = "ROOT_FIELD_NOT_ALLOWED";
    private const string ScpClaim = "scp";
    private const string UriClaim = "http://schemas.microsoft.com/identity/claims/scope";

    private sealed class Counters
    {
        public int Resolved;
        public int MutationRuns;
        public int StreamOpens;
    }

    private sealed class RootQuery(Counters counters) : ObjectType
    {
        protected override void Configure(IObjectTypeDescriptor d)
        {
            d.Name("Query");
            foreach (var name in new[] { "orders", "orders_aggregate", "ordersConnection" })
            {
                d.Field(name).Type<StringType>().Resolve(_ => { counters.Resolved++; return "x"; })
                    .Extend().OnBeforeCreate(def => def.Features.Set(new CatalogRootFieldFeature(null!, null!)));
            }

            foreach (var name in new[] { "table", "catalog", "myAccess" })
            {
                d.Field(name).Type<StringType>().Argument("a", a => a.Type<StringType>())
                    .Resolve(_ => { counters.Resolved++; return "x"; });
            }
        }
    }

    private sealed class RootMutation(Counters counters) : ObjectType
    {
        protected override void Configure(IObjectTypeDescriptor d)
        {
            d.Name("Mutation");
            d.Field("touch").Type<StringType>().Resolve(_ => { counters.MutationRuns++; return "t"; });
        }
    }

    private sealed class RootSubscription(Counters counters) : ObjectType
    {
        protected override void Configure(IObjectTypeDescriptor d)
        {
            d.Name("Subscription");
            d.Field("onTick").Type<StringType>()
                .Resolve(ctx => ctx.GetEventMessage<string>())
                .Subscribe(_ => { counters.StreamOpens++; throw new InvalidOperationException("stream must not open"); });
        }
    }

    private static GatewayOptions Gateway(params string[] scopes) =>
        new() { GraphQL = new GraphQLOptions { TableRootsOnlyScopes = [.. scopes] } };

    private static async Task<IRequestExecutor> BuildAsync(Counters counters, GatewayOptions? options, bool cache = false, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        configure?.Invoke(services);
        if (options is not null)
        {
            services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(options));
        }

        var builder = services.AddGraphQLServer()
            .AddQueryType(new RootQuery(counters))
            .AddMutationType(new RootMutation(counters))
            .AddSubscriptionType(new RootSubscription(counters))
            .AddInMemorySubscriptions()
            .UseExceptions();
        if (cache)
        {
            builder.UseDocumentCache();
        }

        return await builder
            .UseDocumentParser()
            .UseRequest<TableRootsOnlyScopeMiddleware>()
            .UseDocumentValidation()
            .UseOperationCompiler()
            .UseOperationVariableCoercion()
            .UseOperationExecution()
            .BuildRequestExecutorAsync();
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
    {
        var id = new ClaimsIdentity("Test");
        id.AddClaim(new Claim(ClaimTypes.NameIdentifier, "user-1"));
        foreach (var (type, value) in claims)
        {
            id.AddClaim(new Claim(type, value));
        }

        return new ClaimsPrincipal(id);
    }

    private static ClaimsPrincipal Restricted() => Principal((ScpClaim, "Agent.Read"));

    private static async Task<OperationResult> RunAsync(IRequestExecutor ex, string doc, ClaimsPrincipal? principal, string? operationName = null)
    {
        var rb = OperationRequestBuilder.New().SetDocument(doc);
        if (operationName is not null)
        {
            rb.SetOperationName(operationName);
        }

        if (principal is not null)
        {
            rb.AddGlobalState(nameof(ClaimsPrincipal), principal);
        }

        return (OperationResult)await ex.ExecuteAsync(rb.Build());
    }

    private static void NoErrors(OperationResult r) => (r.Errors ?? []).ShouldBeEmpty();

    private static void ShouldBeRejectedAs(OperationResult r, string name)
    {
        r.Errors.ShouldNotBeNull();
        r.Errors.ShouldHaveSingleItem().Code.ShouldBe(Rejected);
        r.Errors[0].Message.ShouldBe($"Root field '{name}' is not allowed for this token; only table root fields are permitted.");
        r.Data.ShouldBeNull();
        r.ContextData.ShouldContainKeyAndValue(ExecutionContextData.HttpStatusCode, HttpStatusCode.Forbidden);
        r.ContextData.ShouldNotContainKey(ExecutionContextData.ValidationErrors);
    }

    private static async Task<OperationResult> Restricted_RunAsync(string doc, Counters? counters = null, string? op = null)
    {
        var ex = await BuildAsync(counters ?? new Counters(), Gateway("Agent.Read"));
        return await RunAsync(ex, doc, Restricted(), op);
    }

    [Fact]
    public async Task Restricted_scope_allows_list_aggregate_connection_and_typename_roots()
    {
        var counters = new Counters();
        var r = await Restricted_RunAsync("{ __typename orders orders_aggregate ordersConnection }", counters);
        NoErrors(r);
        counters.Resolved.ShouldBe(3);
    }

    [Fact]
    public async Task Restricted_scope_rejects_legacy_table_root() =>
        ShouldBeRejectedAs(await Restricted_RunAsync("{ table(a: \"x\") }"), "table");

    [Fact]
    public async Task Restricted_scope_rejects_catalog_root() =>
        ShouldBeRejectedAs(await Restricted_RunAsync("{ catalog }"), "catalog");

    [Fact]
    public async Task Restricted_scope_rejects_myAccess_root() =>
        ShouldBeRejectedAs(await Restricted_RunAsync("{ myAccess }"), "myAccess");

    [Theory]
    [InlineData("{ __schema { queryType { name } } }", "__schema")]
    [InlineData("{ __type(name: \"Query\") { name } }", "__type")]
    public async Task Restricted_scope_rejects_schema_and_type_introspection(string doc, string name) =>
        ShouldBeRejectedAs(await Restricted_RunAsync(doc), name);

    [Fact]
    public async Task Restricted_scope_rejects_mutation_without_running_resolver()
    {
        var counters = new Counters();
        ShouldBeRejectedAs(await Restricted_RunAsync("mutation { touch }", counters), "mutation");
        counters.MutationRuns.ShouldBe(0);
    }

    [Fact]
    public async Task Restricted_scope_rejects_subscription_without_opening_stream()
    {
        var counters = new Counters();
        ShouldBeRejectedAs(await Restricted_RunAsync("subscription { onTick }", counters), "subscription");
        counters.StreamOpens.ShouldBe(0);
    }

    [Fact]
    public async Task Mixed_table_and_catalog_roots_are_rejected_as_a_whole_without_data()
    {
        var counters = new Counters();
        ShouldBeRejectedAs(await Restricted_RunAsync("{ orders catalog }", counters), "catalog");
        counters.Resolved.ShouldBe(0);
    }

    [Theory]
    [InlineData("{ ...F } fragment F on Query { catalog }")]
    [InlineData("{ ... on Query { catalog } }")]
    [InlineData("{ orders ... on Query { ...F } } fragment F on Query { ... on Query { catalog } }")]
    public async Task Disallowed_root_via_fragment_spread_or_inline_fragment_is_rejected(string doc) =>
        ShouldBeRejectedAs(await Restricted_RunAsync(doc), "catalog");

    [Theory]
    [InlineData("query($s: Boolean!) { catalog @skip(if: $s) }")]
    [InlineData("query($i: Boolean!) { catalog @include(if: $i) }")]
    [InlineData("{ catalog @skip(if: true) }")]
    public async Task Skip_and_include_do_not_exempt_a_disallowed_root(string doc)
    {
        var ex = await BuildAsync(new Counters(), Gateway("Agent.Read"));
        var rb = OperationRequestBuilder.New().SetDocument(doc).AddGlobalState(nameof(ClaimsPrincipal), Restricted())
            .SetVariableValues(new Dictionary<string, object?> { ["s"] = true, ["i"] = false });
        ShouldBeRejectedAs((OperationResult)await ex.ExecuteAsync(rb.Build()), "catalog");
    }

    [Fact]
    public async Task Error_names_field_not_alias()
    {
        var r = await Restricted_RunAsync("{ safe: catalog }");
        ShouldBeRejectedAs(r, "catalog");
        r.Errors![0].Message.ShouldNotContain("safe");
    }

    [Fact]
    public async Task Unknown_root_without_disallowed_root_reaches_validation()
    {
        var r = await Restricted_RunAsync("{ unknownX }");
        r.Errors.ShouldNotBeNull();
        r.Errors.ShouldNotContain(e => e.Code == Rejected);
    }

    [Fact]
    public async Task Disallowed_root_answer_is_independent_of_other_root_existence()
    {
        var ex = await BuildAsync(new Counters(), Gateway("Agent.Read"));
        var unknown = await RunAsync(ex, "{ catalog unknownX }", Restricted());
        var existing = await RunAsync(ex, "{ catalog orders }", Restricted());
        unknown.ToJson().ShouldBe(existing.ToJson());
    }

    [Fact]
    public async Task Token_without_listed_scope_uses_all_roots()
    {
        var counters = new Counters();
        var ex = await BuildAsync(counters, Gateway("Agent.Read"));
        var r = await RunAsync(ex, "{ catalog table(a: \"1\") orders }", Principal((ScpClaim, "Other.Scope")));
        NoErrors(r);
        counters.Resolved.ShouldBe(3);
    }

    [Fact]
    public async Task Empty_setting_changes_nothing()
    {
        var ex = await BuildAsync(new Counters(), Gateway());
        var r = await RunAsync(ex, "{ catalog }", Restricted());
        NoErrors(r);
    }

    [Fact]
    public async Task Any_listed_scope_restricts_even_with_other_scopes()
    {
        var ex = await BuildAsync(new Counters(), Gateway("Other.Scope", "Agent.Read"));
        var r = await RunAsync(ex, "{ catalog }", Principal((ScpClaim, "Sql.Write Agent.Read data.access.request")));
        ShouldBeRejectedAs(r, "catalog");
    }

    [Theory]
    [InlineData(ScpClaim, "agent.READ")]
    [InlineData(UriClaim, "api://app/Agent.Read")]
    [InlineData("scope", "openid Agent.Read")]
    public async Task Scope_match_uses_bare_name_case_insensitive_across_scp_uri_and_scope_claims(string claimType, string value)
    {
        var ex = await BuildAsync(new Counters(), Gateway("api://app/Agent.Read"));
        ShouldBeRejectedAs(await RunAsync(ex, "{ catalog }", Principal((claimType, value))), "catalog");
    }

    [Fact]
    public async Task Principal_without_scope_claims_is_unrestricted()
    {
        var ex = await BuildAsync(new Counters(), Gateway("Agent.Read"));
        NoErrors((await RunAsync(ex, "{ catalog }", Principal())));
        NoErrors((await RunAsync(ex, "{ catalog }", null)));
    }

    private sealed class WithoutOptionsServices(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IOptions<GatewayOptions>) ? null : inner.GetService(serviceType);
    }

    [Fact]
    public async Task Missing_options_service_fails_closed()
    {
        var ex = await new ServiceCollection()
            .AddSingleton<IOptions<GatewayOptions>>(Options.Create(Gateway("Agent.Read")))
            .AddGraphQLServer()
            .AddQueryType(new RootQuery(new Counters()))
            .UseExceptions()
            .UseDocumentParser()
            // A request whose service provider carries no options registration restricts every caller.
            .UseRequest(next => ctx =>
            {
                ctx.RequestServices = new WithoutOptionsServices(ctx.RequestServices);
                return next(ctx);
            })
            .UseRequest<TableRootsOnlyScopeMiddleware>()
            .UseDocumentValidation()
            .UseOperationCompiler()
            .UseOperationVariableCoercion()
            .UseOperationExecution()
            .BuildRequestExecutorAsync();
        foreach (var principal in new[] { Principal(), null })
        {
            ShouldBeRejectedAs(await RunAsync(ex, "{ catalog }", principal), "catalog");
        }
    }

    [Fact]
    public async Task All_operations_are_checked_without_operation_name()
    {
        const string doc = "query A { orders } query B { catalog }";
        ShouldBeRejectedAs(await Restricted_RunAsync(doc), "catalog");
        // With an operation name only the named operation counts.
        var ok = await Restricted_RunAsync(doc, op: "A");
        NoErrors(ok);
        ShouldBeRejectedAs(await Restricted_RunAsync(doc, op: "B"), "catalog");
    }

    [Fact]
    public void Cyclic_fragment_spreads_terminate()
    {
        var document = Utf8GraphQLParser.Parse("{ ...A orders } fragment A on Query { ...B } fragment B on Query { ...A }");
        TableRootsOnlyScopeMiddleware.FindDisallowedRoot(document, null, SchemaOf()).ShouldBeNull();
    }

    private static ISchemaDefinition SchemaOf() =>
        BuildAsync(new Counters(), Gateway("Agent.Read")).GetAwaiter().GetResult().Schema;

    [Fact]
    public async Task Rejection_carries_403_status_and_no_validation_marker()
    {
        var r = await Restricted_RunAsync("{ catalog }");
        r.ContextData[ExecutionContextData.HttpStatusCode].ShouldBe(HttpStatusCode.Forbidden);
        r.ContextData.ContainsKey(ExecutionContextData.ValidationErrors).ShouldBeFalse();
    }

    [Fact]
    public async Task Rejection_also_applies_on_document_cache_hits()
    {
        var ex = await BuildAsync(new Counters(), Gateway("Agent.Read"), cache: true);
        NoErrors((await RunAsync(ex, "{ orders }", Restricted())));
        ShouldBeRejectedAs(await RunAsync(ex, "{ catalog }", Restricted()), "catalog");
        ShouldBeRejectedAs(await RunAsync(ex, "{ catalog }", Restricted()), "catalog");
        // The same cached document stays allowed for an unrestricted caller.
        NoErrors((await RunAsync(ex, "{ catalog }", Principal())));
    }

    private sealed class FixedHttpContextAccessor(ClaimsPrincipal user) : Microsoft.AspNetCore.Http.IHttpContextAccessor
    {
        public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; } =
            new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user };
    }

    [Fact]
    public async Task Principal_from_context_data_is_preferred_over_http_user()
    {
        var ex = await BuildAsync(new Counters(), Gateway("Agent.Read"), configure: s =>
            s.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(new FixedHttpContextAccessor(Principal())));
        ShouldBeRejectedAs(await RunAsync(ex, "{ catalog }", Restricted()), "catalog");

        var exRestrictedHttp = await BuildAsync(new Counters(), Gateway("Agent.Read"), configure: s =>
            s.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(new FixedHttpContextAccessor(Restricted())));
        NoErrors(await RunAsync(exRestrictedHttp, "{ catalog }", Principal()));
        ShouldBeRejectedAs(await RunAsync(exRestrictedHttp, "{ catalog }", null), "catalog");
    }

    [Fact]
    public async Task Operation_name_selects_which_operation_is_checked()
    {
        const string doc = "query A { orders } query B { catalog }";
        ShouldBeRejectedAs(await Restricted_RunAsync(doc, op: "B"), "catalog");
        NoErrors(await Restricted_RunAsync(doc, op: "A"));
        var counters = new Counters();
        var unmatched = await Restricted_RunAsync(doc, counters, op: "Nope");
        unmatched.Errors.ShouldNotContain(e => e.Code == Rejected);
        counters.Resolved.ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_double_underscore_root_is_not_matched_and_not_echoed()
    {
        var r = await Restricted_RunAsync("{ __secretThing }");
        r.Errors.ShouldNotBeNull();
        r.Errors.ShouldNotContain(e => e.Code == Rejected);
        TableRootsOnlyScopeMiddleware.FindDisallowedRoot(Utf8GraphQLParser.Parse("{ __secretThing }"), null, SchemaOf()).ShouldBeNull();
    }

    [Theory]
    [InlineData("{ leak1: catalog }")]
    [InlineData("{ unknownRoot catalog }")]
    [InlineData("{ ...LeakFrag } fragment LeakFrag on Query { catalog }")]
    [InlineData("{ ... on Query { catalog } }")]
    public async Task Message_never_contains_document_supplied_names(string doc)
    {
        var r = await Restricted_RunAsync(doc);
        ShouldBeRejectedAs(r, "catalog");
        foreach (var forbidden in new[] { "leak1", "unknownRoot", "LeakFrag" })
        {
            r.Errors![0].Message.ShouldNotContain(forbidden);
        }
    }

    [Fact]
    public async Task Unknown_names_in_mutation_documents_are_not_echoed()
    {
        var r = await Restricted_RunAsync("mutation { evilUnknownField }");
        ShouldBeRejectedAs(r, "mutation");
        r.Errors![0].Message.ShouldNotContain("evilUnknownField");
    }

    [Fact]
    public async Task Null_document_while_restricted_is_rejected()
    {
        // A pipeline without the document parser leaves the document unset; a restricted token must not slip through.
        var ex = await new ServiceCollection()
            .AddSingleton<IOptions<GatewayOptions>>(Options.Create(Gateway("Agent.Read")))
            .AddGraphQLServer()
            .AddQueryType(new RootQuery(new Counters()))
            .UseExceptions()
            .UseRequest<TableRootsOnlyScopeMiddleware>()
            .UseDocumentValidation()
            .UseOperationCompiler()
            .UseOperationVariableCoercion()
            .UseOperationExecution()
            .BuildRequestExecutorAsync();
        ShouldBeRejectedAs(await RunAsync(ex, "{ orders }", Restricted()), "operation");
        (await RunAsync(ex, "{ orders }", Principal())).Errors.ShouldNotContain(e => e.Code == Rejected);
    }

    [Fact]
    public void Fragment_fanout_is_linear()
    {
        var sb = new System.Text.StringBuilder("{ ...F0 }");
        const int n = 1000;
        for (var i = 0; i < n; i++)
        {
            sb.Append($" fragment F{i} on Query {{ ...F{i + 1} ...F{i + 2} }}");
        }

        sb.Append($" fragment F{n} on Query {{ orders }} fragment F{n + 1} on Query {{ orders }}");
        var document = Utf8GraphQLParser.Parse(sb.ToString());
        var schema = SchemaOf();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TableRootsOnlyScopeMiddleware.FindDisallowedRoot(document, null, schema).ShouldBeNull();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Registration_places_scope_check_between_document_parser_and_validation()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir.FullName, "src", "Autheris.Api", "Extensions", "GatewayServiceCollectionExtensions.GraphQL.cs"));
        var parser = source.IndexOf(".UseDocumentParser()", StringComparison.Ordinal);
        var scope = source.IndexOf("TableRootsOnlyScopeMiddleware", StringComparison.Ordinal);
        var validation = source.IndexOf(".UseDocumentValidation()", StringComparison.Ordinal);
        parser.ShouldBeGreaterThan(-1);
        scope.ShouldBeGreaterThan(parser);
        validation.ShouldBeGreaterThan(scope);
    }

    [Fact]
    public void Sanitizer_keeps_code_and_message_outside_development()
    {
        var filter = new ErrorSanitizingFilter(new Autheris.Tests.Common.TestHostEnvironment("Production"), NullLogger<ErrorSanitizingFilter>.Instance);
        var message = "Root field 'catalog' is not allowed for this token; only table root fields are permitted.";
        var error = ErrorBuilder.New().SetMessage(message).SetCode(Rejected).Build();
        var sanitized = filter.OnError(error);
        sanitized.Code.ShouldBe(Rejected);
        sanitized.Message.ShouldBe(message);
    }
}
