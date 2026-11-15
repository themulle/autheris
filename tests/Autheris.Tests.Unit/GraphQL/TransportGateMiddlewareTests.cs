namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Subscriptions;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Collection("CatalogRelation")]
[Trait("Category", "Security")]
public sealed class TransportGateMiddlewareTests
{
    private static async Task<(IRequestExecutor Executor, RecordingGate Gate, Action ResetResolverFlag, Func<bool> WasResolverCalled)> CreateTestExecutorAsync(
        params string[] deniedSources)
    {
        var gate = new RecordingGate(deniedSources);
        var resolverCalled = false;

        var services = new ServiceCollection();
        services.AddSingleton<IDataSourceTransportGate>(gate);

        var executor = await services
            .AddGraphQLServer()
            .AddQueryType(d =>
            {
                d.Name("Query");
                d.Field("sItems").Type<StringType>().Resolve(_ => { resolverCalled = true; return "ok-s"; })
                    .Extend().OnBeforeCreate(f => f.Features.Set(new TransportSourceFeature("src_s")));
                d.Field("sItemsConnection").Type<StringType>().Resolve(_ => { resolverCalled = true; return "ok-sc"; })
                    .Extend().OnBeforeCreate(f => f.Features.Set(new TransportSourceFeature("src_sc")));
                d.Field("tItems").Type<StringType>().Resolve(_ => { resolverCalled = true; return "ok-t"; })
                    .Extend().OnBeforeCreate(f => f.Features.Set(new TransportSourceFeature("src_t")));
                d.Field("_entities").Type<ListType<TestEntityUnion>>().Resolve(_ =>
                {
                    resolverCalled = true;
                    return Array.Empty<object>();
                });
                d.Field("entitiesNoFeature")
                    .Type<ListType<TestEntityWithoutFeatureUnion>>()
                    .Resolve(_ => { resolverCalled = true; return Array.Empty<object>(); });
            })
            .AddType<TestEntityWithFeatureType>()
            .AddType<TestEntityWithoutFeatureType>()
            .AddType<TestEntityUnion>()
            .AddType<TestEntityWithoutFeatureUnion>()
            .AddMutationType(d =>
            {
                d.Name("Mutation");
                d.Field("doIt").Type<StringType>().Resolve(_ => { resolverCalled = true; return "mutated"; })
                    .Extend().OnBeforeCreate(f => f.Features.Set(new TransportSourceFeature("src_mut")));
            })
            .AddSubscriptionType(d =>
            {
                d.Name("Subscription");
                d.Field("onIt").Type<StringType>()
                    .Subscribe(_ => SubscribeOnceAsync(() => resolverCalled = true))
                    .Resolve(ctx => ctx.GetEventMessage<string>())
                    .Extend().OnBeforeCreate(f => f.Features.Set(new TransportSourceFeature("src_sub")));
            })
            .UseExceptions()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseOperationCache()
            .UseOperationCompiler()
            .UseOperationVariableCoercion()
            .UseRequest<TransportGateMiddleware>()
            .UseOperationExecution()
            .BuildRequestExecutorAsync();

        return (executor, gate, () => resolverCalled = false, () => resolverCalled);
    }

    private static async IAsyncEnumerable<string> SubscribeOnceAsync(Action onSubscribe)
    {
        onSubscribe();
        yield return "evt";
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RootFieldNames_CollectsAliasesFragmentsAndInlineFragments()
    {
        var (executor, gate, _, _) = await CreateTestExecutorAsync();

        var result = await executor.ExecuteAsync("""
            query Q {
              a: sItems
              ...F
              ... on Query { tItems }
            }
            fragment F on Query { sItemsConnection }
            """);

        result.ExpectOperationResult().Errors.ShouldBeEmpty();
        var checkedSources = gate.Calls.SelectMany(c => c).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        checkedSources.ShouldBe(["src_s", "src_sc", "src_t"]);
    }

    [Fact]
    public async Task RootFieldNames_WithoutOperationName_CoversEveryQueryOperation_FailClosed()
    {
        var (executor, gate, _, wasCalled) = await CreateTestExecutorAsync("src_s");
        const string doc = "query A { sItems } query B { tItems }";

        // Multiple operations without operationName fail closed before any resolver runs
        var ambiguous = await executor.ExecuteAsync(doc);
        ambiguous.ExpectOperationResult().Errors.ShouldNotBeEmpty();
        wasCalled().ShouldBeFalse();

        // Selecting operation B evaluates only B's compiled root (src_t is allowed; A's denied src_s is ignored)
        var ok = await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument(doc).SetOperationName("B").Build());
        ok.ExpectOperationResult().Errors.ShouldBeEmpty();
        gate.Calls.ShouldHaveSingleItem().ShouldBe(["src_t"]);
    }

    [Fact]
    public async Task RootFieldNames_RecursiveFragments_Terminate()
    {
        var (executor, _, _, wasCalled) = await CreateTestExecutorAsync();

        var result = await executor.ExecuteAsync("query Q { ...A } fragment A on Query { sItems ...B } fragment B on Query { tItems ...A }");

        result.ExpectOperationResult().Errors.ShouldNotBeEmpty();
        wasCalled().ShouldBeFalse();
    }

    [Fact]
    public async Task Unknown_operation_name_is_denied_not_skipped()
    {
        var (executor, _, _, wasCalled) = await CreateTestExecutorAsync();

        var result = await executor.ExecuteAsync(
            OperationRequestBuilder.New()
                .SetDocument("query A { sItems }")
                .SetOperationName("DoesNotExist")
                .Build());

        result.ExpectOperationResult().Errors.ShouldNotBeEmpty();
        wasCalled().ShouldBeFalse();

        // Pipeline where document is parsed and operationName is unknown/uncompiled: TransportGateMiddleware still denies
        var uncompiledResolverCalled = false;
        var uncompiledExecutor = await new ServiceCollection()
            .AddSingleton<IDataSourceTransportGate>(new RecordingGate())
            .AddGraphQLServer()
            .AddQueryType(d => d.Field("sItems").Type<StringType>().Resolve(_ => { uncompiledResolverCalled = true; return "ok"; })
                .Extend().OnBeforeCreate(f => f.Features.Set(new TransportSourceFeature("src_s"))))
            .UseDefaultPipeline()
            .UseRequest<TransportGateMiddleware>(key: "PreValidateTransportGate", before: "DocumentValidationMiddleware")
            .BuildRequestExecutorAsync();

        var uncompiledResult = await uncompiledExecutor.ExecuteAsync(
            OperationRequestBuilder.New()
                .SetDocument("query A { sItems }")
                .SetOperationName("DoesNotExist")
                .Build());

        uncompiledResult.ExpectOperationResult().Errors.ShouldContain(e => e.Code == TransportNotAllowedException.Code);
        uncompiledResolverCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Fragment_spread_root_is_scanned_once_and_denied()
    {
        var (executor, gate, _, wasCalled) = await CreateTestExecutorAsync("src_s");

        var result = await executor.ExecuteAsync("""
            query Q {
              ...F
              ... on Query { sItems }
            }
            fragment F on Query { sItems }
            """);

        var errors = result.ExpectOperationResult().Errors;
        errors.ShouldNotBeNull();
        errors.ShouldContain(e => e.Code == TransportNotAllowedException.Code);
        gate.Calls.Count.ShouldBe(1);
        gate.Calls[0].ShouldBe(["src_s"]);
        wasCalled().ShouldBeFalse();
    }

    [Fact]
    public async Task Null_document_or_null_operation_is_denied()
    {
        // Before DocumentParserMiddleware: context.OperationDocumentInfo.Document is null and operation is null.
        var noDocResolverCalled = false;
        var noDocExecutor = await new ServiceCollection()
            .AddSingleton<IDataSourceTransportGate>(new RecordingGate())
            .AddGraphQLServer()
            .AddQueryType(d => d.Field("health").Type<StringType>().Resolve(_ => { noDocResolverCalled = true; return "ok"; }))
            .UseDefaultPipeline()
            .UseRequest<TransportGateMiddleware>(key: "PreParseTransportGate", before: "DocumentParserMiddleware")
            .BuildRequestExecutorAsync();

        var noDocResult = await noDocExecutor.ExecuteAsync("{ health }");
        noDocResult.ExpectOperationResult().Errors.ShouldContain(e => e.Code == TransportNotAllowedException.Code);
        noDocResolverCalled.ShouldBeFalse();

        // After DocumentParserMiddleware, before OperationCompilerMiddleware: document is parsed, compiled operation is null.
        var noOpResolverCalled = false;
        var noOpExecutor = await new ServiceCollection()
            .AddSingleton<IDataSourceTransportGate>(new RecordingGate())
            .AddGraphQLServer()
            .AddQueryType(d => d.Field("sItems").Type<StringType>().Resolve(_ => { noOpResolverCalled = true; return "ok"; })
                .Extend().OnBeforeCreate(f => f.Features.Set(new TransportSourceFeature("src_s"))))
            .UseDefaultPipeline()
            .UseRequest<TransportGateMiddleware>(key: "PreValidateTransportGate", before: "DocumentValidationMiddleware")
            .BuildRequestExecutorAsync();

        var noOpResult = await noOpExecutor.ExecuteAsync("{ sItems }");
        noOpResult.ExpectOperationResult().Errors.ShouldContain(e => e.Code == TransportNotAllowedException.Code);
        noOpResolverCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Skip_and_include_directives_evaluate_runtime_variables()
    {
        var (executor, gate, resetCalled, wasCalled) = await CreateTestExecutorAsync("src_s");
        const string query = "query Q($inc: Boolean!) { tItems sItems @include(if: $inc) }";

        // When $inc = false, denied root sItems is excluded by compiled operation flags
        var allowed = await executor.ExecuteAsync(
            OperationRequestBuilder.New()
                .SetDocument(query)
                .SetVariableValues("{\"inc\": false}")
                .Build());
        allowed.ExpectOperationResult().Errors.ShouldBeEmpty();
        gate.Calls.ShouldHaveSingleItem().ShouldBe(["src_t"]);

        // When $inc = true, denied root sItems IS included and rejected before resolver runs
        resetCalled();
        var denied = await executor.ExecuteAsync(
            OperationRequestBuilder.New()
                .SetDocument(query)
                .SetVariableValues("{\"inc\": true}")
                .Build());
        denied.ExpectOperationResult().Errors.ShouldContain(e => e.Code == TransportNotAllowedException.Code);
        wasCalled().ShouldBeFalse();
    }

    [Fact]
    public async Task Mutation_root_on_disallowed_source_is_denied()
    {
        var (executor, gate, _, wasCalled) = await CreateTestExecutorAsync("src_mut");

        var result = await executor.ExecuteAsync("mutation { doIt }");

        result.ExpectOperationResult().Errors.ShouldContain(e => e.Code == TransportNotAllowedException.Code);
        gate.Calls.ShouldHaveSingleItem().ShouldBe(["src_mut"]);
        wasCalled().ShouldBeFalse();
    }

    [Fact]
    public async Task Subscription_root_on_disallowed_source_is_denied()
    {
        var (executor, gate, _, wasCalled) = await CreateTestExecutorAsync("src_sub");

        var result = await executor.ExecuteAsync("subscription { onIt }");

        result.ExpectOperationResult().Errors.ShouldContain(e => e.Code == TransportNotAllowedException.Code);
        gate.Calls.ShouldHaveSingleItem().ShouldBe(["src_sub"]);
        wasCalled().ShouldBeFalse();
    }

    [Fact]
    public async Task Federation_entities_resolving_catalog_table_checks_or_denies()
    {
        var (executor, gate, _, wasCalled) = await CreateTestExecutorAsync("src_entity");

        // _entities resolving a catalog entity carrying TransportSourceFeature checks its logical source
        var deniedByGate = await executor.ExecuteAsync("query { _entities { ... on CatalogEntity { id } } }");
        deniedByGate.ExpectOperationResult().Errors.ShouldContain(e => e.Code == TransportNotAllowedException.Code);
        gate.Calls.ShouldHaveSingleItem().ShouldBe(["src_entity"]);
        wasCalled().ShouldBeFalse();

        // _entities resolving a catalog entity WITHOUT TransportSourceFeature fails closed in TransportGateMiddleware
        var services = new ServiceCollection();
        services.AddSingleton<IDataSourceTransportGate>(new RecordingGate()); // gate allows everything if reached
        var failClosedCalled = false;
        var entityExecutor = await services
            .AddGraphQLServer()
            .AddQueryType(d =>
            {
                d.Name("Query");
                d.Field("_entities")
                    .Type<ListType<TestEntityWithoutFeatureUnion>>()
                    .Resolve(_ => { failClosedCalled = true; return Array.Empty<object>(); });
            })
            .AddType<TestEntityWithoutFeatureType>()
            .AddType<TestEntityWithoutFeatureUnion>()
            .UseExceptions()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseOperationCache()
            .UseOperationCompiler()
            .UseOperationVariableCoercion()
            .UseRequest<TransportGateMiddleware>()
            .UseOperationExecution()
            .BuildRequestExecutorAsync();

        var missingFeature = await entityExecutor.ExecuteAsync("query { _entities { ... on UnattributedCatalogEntity { id } } }");
        missingFeature.ExpectOperationResult().Errors.ShouldContain(e => e.Code == TransportNotAllowedException.Code);
        failClosedCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Every_catalog_root_field_has_transport_feature()
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = new Table { IsActive = true, SourceName = "pg_sales", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
        var metaRepo = RepoStubProxy.Create<ITableMetadataRepository>(table);
        var relRepo = RepoStubProxy.Create<ITableRelationRepository>(table);

        var executor = await new ServiceCollection()
            .AddSingleton(metaRepo)
            .AddSingleton(relRepo)
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddSubscriptionType<Subscription>()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<ITableRelationRepository>(),
                graphQlOptions: new GraphQLOptions()))
            .BuildRequestExecutorAsync();

        var schema = executor.Schema;
        var operationRoots = new[]
        {
            schema.QueryType,
            schema.MutationType,
            schema.SubscriptionType
        }.Where(t => t is not null);

        var inspected = 0;
        foreach (var root in operationRoots)
        {
            foreach (var field in root!.Fields)
            {
                if (field.Features.Get<CatalogRootFieldFeature>() is not null)
                {
                    var feature = field.Features.Get<TransportSourceFeature>();
                    feature.ShouldNotBeNull($"Catalog root field {root.Name}.{field.Name} must declare TransportSourceFeature.");
                    feature.LogicalSource.ShouldNotBeNullOrWhiteSpace();
                    inspected++;
                }
            }
        }

        // query list + connection + aggregate
        inspected.ShouldBeGreaterThanOrEqualTo(3);

        // Catalog table object type itself must carry TransportSourceFeature for federation entity checks
        var entityType = schema.Types.GetType<IObjectTypeDefinition>("sales_dbo_orders");
        var typeFeature = entityType.Features.Get<TransportSourceFeature>();
        typeFeature.ShouldNotBeNull();
        typeFeature.LogicalSource.ShouldBe("pg_sales");
    }

    private sealed class RecordingGate(params string[] deniedSources) : IDataSourceTransportGate
    {
        private readonly HashSet<string> _denied = new(deniedSources, StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _checked = [];
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public IReadOnlyList<string> CheckedSources => _checked;
        public DataSourceTransportFlags Ingress => DataSourceTransportFlags.GraphQL;

        public Task EnsureAsync(string? dataSourceName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(dataSourceName))
            {
                throw new TransportNotAllowedException("unknown", DataSourceTransportFlags.GraphQL);
            }

            return EnsureAllAsync([dataSourceName], ct);
        }

        public Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default)
        {
            var list = dataSourceNames.Where(s => s is not null).Cast<string>().ToList();
            Calls.Add(list);
            foreach (var src in dataSourceNames)
            {
                if (string.IsNullOrWhiteSpace(src) || _denied.Contains(src))
                {
                    throw new TransportNotAllowedException(src ?? "unknown", DataSourceTransportFlags.GraphQL);
                }

                _checked.Add(src);
            }

            return Task.CompletedTask;
        }

        public Task EnsureCatalogSourcesAsync(IEnumerable<string?> catalogSourceNames, CancellationToken ct = default) =>
            EnsureAllAsync(catalogSourceNames, ct);

        public Task EnsureCatalogSourceAsync(string? catalogSourceName, CancellationToken ct = default) =>
            EnsureAsync(catalogSourceName, ct);

        public bool IsAllowed(string? dataSourceName) =>
            !string.IsNullOrWhiteSpace(dataSourceName) && !_denied.Contains(dataSourceName);
    }

    private sealed class TestEntityUnion : UnionType
    {
        protected override void Configure(IUnionTypeDescriptor descriptor)
        {
            descriptor.Name("TestEntity");
            descriptor.Type<TestEntityWithFeatureType>();
        }
    }

    private sealed class TestEntityWithoutFeatureUnion : UnionType
    {
        protected override void Configure(IUnionTypeDescriptor descriptor)
        {
            descriptor.Name("TestEntityWithoutFeature");
            descriptor.Type<TestEntityWithoutFeatureType>();
        }
    }

    private sealed class TestEntityWithFeatureType : ObjectType
    {
        protected override void Configure(IObjectTypeDescriptor descriptor)
        {
            descriptor.Name("CatalogEntity");
            descriptor.Field("id").Type<StringType>().Resolve("1");
            descriptor.Extend().OnBeforeCreate(d =>
            {
                d.Features.Set(new TransportSourceFeature("src_entity"));
            });
        }
    }

    private sealed class TestEntityWithoutFeatureType : ObjectType
    {
        protected override void Configure(IObjectTypeDescriptor descriptor)
        {
            descriptor.Name("UnattributedCatalogEntity");
            descriptor.Field("id").Type<StringType>().Resolve("1");
            descriptor.Extend().OnBeforeCreate(d =>
            {
                d.Features.Set(new CatalogRootFieldFeature(null!, null!));
            });
        }
    }

    private class RepoStubProxy : DispatchProxy
    {
        private sealed class RepoStubProxySubtype : RepoStubProxy;
        private IReadOnlyList<TableMetadata> _tables = [];

        public static T Create<T>(params TableMetadata[] tables) where T : class
        {
            var proxy = DispatchProxy.Create<T, RepoStubProxy>();
            ((RepoStubProxy)(object)proxy)._tables = tables;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var rt = targetMethod?.ReturnType;
            if (rt == typeof(Task<IReadOnlyList<TableMetadata>>))
            {
                return Task.FromResult(_tables);
            }

            if (rt == typeof(Task<IReadOnlyList<TableRelation>>))
            {
                return Task.FromResult<IReadOnlyList<TableRelation>>(Array.Empty<TableRelation>());
            }

            return rt is { IsValueType: true } && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
        }
    }
}
