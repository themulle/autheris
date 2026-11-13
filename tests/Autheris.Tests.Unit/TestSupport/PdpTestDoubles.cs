namespace Autheris.Tests.Unit.TestSupport;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Interfaces;
using NSubstitute;

/// <summary>Decision point doubles for consumers that only need an unconstrained allow (or a fixed deny).</summary>
internal static class PdpTestDoubles
{
    public static IAccessDecisionPoint AllowAll()
    {
        var pdp = Substitute.For<IAccessDecisionPoint>();
        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(TableAccessDecision.Allowed(
                ci.Arg<AccessRequest>().Object.Identifier,
                new Dictionary<string, ColumnAccessLevel>(),
                hasUnconstrainedColumnAllow: true)));
        return pdp;
    }

    /// <summary>A decision point answering from a function of the request (keeps the request for assertions through <c>Received</c>).</summary>
    public static IAccessDecisionPoint From(Func<AccessRequest, TableAccessDecision> decide)
    {
        var pdp = Substitute.For<IAccessDecisionPoint>();
        pdp.DecideAsync(Arg.Any<AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(decide(ci.Arg<AccessRequest>())));
        return pdp;
    }

    /// <summary>A catalog that knows every table as an active one.</summary>
    public static ITableMetadataRepository AnyTable()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata { Identifier = ci.Arg<TableIdentifier>(), Table = new Table { IsActive = true } });
        return repo;
    }

    /// <summary>Request services of an endpoint test whose caller is allowed to read every table.</summary>
    public static IServiceProvider AllowAllRequestServices()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, AllowAll());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, AnyTable());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<TrinoSqlEngine.ISqlEngine>(services, new TrinoSqlEngine.FastSqlEngine());
        return Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
    }
}
