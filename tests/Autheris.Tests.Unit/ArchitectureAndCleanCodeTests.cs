namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Procedures.Tools;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class ArchitectureAndCleanCodeTests
{
    [Fact]
    public void DefaultSqlSecurityValidator_ValidPredicate_Passes()
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.NotThrow(() => validator.ValidatePredicateSql("status = 'ACTIVE' AND age >= 18", "testField"));
    }

    [Theory]
    [InlineData("1=1; DROP TABLE users;")]
    [InlineData("status = 'ACTIVE' -- comment")]
    [InlineData("status = 'ACTIVE' /* inline comment */")]
    [InlineData("status = @@version")]
    [InlineData("status = 'ACTIVE\0'")]
    public void DefaultSqlSecurityValidator_UnsafePredicate_ThrowsArgumentException(string unsafePredicate)
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.Throw<ArgumentException>(() => validator.ValidatePredicateSql(unsafePredicate, "testField"));
    }

    [Theory]
    [InlineData("created_at DESC")]
    [InlineData("customer_id ASC, order_date DESC")]
    [InlineData("users.first_name ASC")]
    public void DefaultSqlSecurityValidator_ValidOrderBy_Passes(string validOrderBy)
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.NotThrow(() => validator.ValidateOrderBySql(validOrderBy, "orderByField"));
    }

    [Theory]
    [InlineData("created_at; DROP TABLE users")]
    [InlineData("created_at -- comment")]
    [InlineData("created_at /* block */ DESC")]
    [InlineData("(SELECT password FROM users) ASC")]
    public void DefaultSqlSecurityValidator_UnsafeOrderBy_ThrowsArgumentException(string unsafeOrderBy)
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.Throw<ArgumentException>(() => validator.ValidateOrderBySql(unsafeOrderBy, "orderByField"));
    }

    [Theory]
    [InlineData("p0")]
    [InlineData("@tenantId")]
    [InlineData("user_id_123")]
    public void DefaultSqlSecurityValidator_ValidParameterName_Passes(string paramName)
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.NotThrow(() => validator.ValidateParameterName(paramName, "paramField"));
    }

    [Theory]
    [InlineData("@param;DROP TABLE")]
    [InlineData("param--comment")]
    [InlineData("123badStart")]
    [InlineData("@param with spaces")]
    public void DefaultSqlSecurityValidator_InvalidParameterName_ThrowsArgumentException(string invalidParam)
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.Throw<ArgumentException>(() => validator.ValidateParameterName(invalidParam, "paramField"));
    }

    [Fact]
    public void DiContainer_ResolvesISqlSecurityValidatorAndIProcedureYamlGenerator()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions
                {
                    Enabled = true,
                    AllowedSchemas = ["api"]
                }
            }
        };

        services.AddSingleton(Options.Create(options));
        services.AddLogging();
        services.AddSingleton<TrinoSqlEngine.ISqlEngine, TrinoSqlEngine.FastSqlEngine>();
        services.AddSingleton<ISqlSecurityValidator, DefaultSqlSecurityValidator>();
        services.AddSingleton<IProcedureYamlGenerator, ProcedureYamlGenerator>();

        var provider = services.BuildServiceProvider();

        provider.GetService<ISqlSecurityValidator>().ShouldNotBeNull();
        provider.GetService<IProcedureYamlGenerator>().ShouldNotBeNull();
    }
}
