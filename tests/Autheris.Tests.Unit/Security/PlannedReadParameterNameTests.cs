namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Federation.Parameters;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Shouldly;
using TrinoSqlEngine.Analysis;
using Xunit;

/// <summary>SEC-PG-17..19 (PARITY-GAP-017): the pushed-predicate parameter names are admitted on a planned table read only, anchored.</summary>
public sealed class PlannedReadParameterNameTests
{
    private static IReadOnlyDictionary<string, object?> One(string name) => new Dictionary<string, object?> { [name] = 1 };

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("@p_pd_0")]
    [InlineData("p_pd_12")]
    [InlineData("@p_pd_3_0")]
    [InlineData("@P_PD_7_12")]
    public void PlannedRead_PushedPredicateParameter_IsAccepted(string name) =>
        Should.NotThrow(() => GovernedSqlExecutionService.EnsureClientParameterNames(One(name), plannedTableRead: true));

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("@p_pd_x")]
    [InlineData("@p_pd_1_")]
    [InlineData("@p_pd_")]
    [InlineData("@p_pd_1;--")]
    [InlineData("@p_pd_12345")]
    [InlineData("@p_pd_1_2_3")]
    public void PlannedRead_PdNameNotMatchingPattern_Rejected(string name) =>
        Should.Throw<WebSqlPolicyException>(() => GovernedSqlExecutionService.EnsureClientParameterNames(One(name), plannedTableRead: true))
            .Message.ShouldBe("A request parameter uses a reserved gateway parameter name.");

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("@__gql_x")]
    [InlineData("@p_rls_0")]
    [InlineData("@rls_0")]
    [InlineData("@__param_0")]
    [InlineData("@__rls_ks0")]
    public void PlannedRead_OtherReservedPrefixes_StillRejected(string name) =>
        Should.Throw<WebSqlPolicyException>(() => GovernedSqlExecutionService.EnsureClientParameterNames(One(name), plannedTableRead: true));

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("@p_pd_0")]
    [InlineData("@p_pd_3_0")]
    public void NonPlannedRequest_PdParameter_Rejected(string name)
    {
        Should.Throw<WebSqlPolicyException>(() => GovernedSqlExecutionService.EnsureClientParameterNames(One(name)));
        Should.Throw<WebSqlPolicyException>(() => GovernedSqlExecutionService.EnsureClientParameterNames(One(name), plannedTableRead: false));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ClientSql_PdParameter_StillRejected()
    {
        Should.Throw<WebSqlPolicyException>(() => FederatedParameterContract.ValidateParameterName("@p_pd_0"));
        Should.Throw<System.Security.SecurityException>(() => SqlParameterExtractor.EnsureValidParameterName("@p_pd_0"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GovernedSqlQueryRequest_PlannedTableRead_NotBindableFromApiDtos()
    {
        // SEC-PG-18: no JSON constructor on the request and no Autheris.Api type exposes the flag or the request record as a member.
        typeof(GovernedSqlQueryRequest).GetConstructors().ShouldNotContain(c => c.IsDefined(typeof(JsonConstructorAttribute), false));

        var api = typeof(Autheris.Api.Extensions.DependencyInjection.GatewaySqlEngineServiceExtensions).Assembly;
        var offenders = api.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
            .Where(p => p.Name == "PlannedTableRead" || p.PropertyType == typeof(GovernedSqlQueryRequest))
            .Select(p => p.DeclaringType!.FullName + "." + p.Name)
            .ToList();
        offenders.ShouldBeEmpty();
    }
}
