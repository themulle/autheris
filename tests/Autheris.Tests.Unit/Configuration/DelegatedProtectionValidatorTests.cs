namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Governance.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DelegatedProtectionValidatorTests
{
    private static GatewayOptions BuildOptions(EnforcementMode mode) =>
        new()
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["uc-prod"] = new()
                    {
                        Provider = "Databricks",
                        ConnectionString = "Host=example.invalid;HttpPath=/sql/1.0/warehouses/x",
                        EnforcementMode = mode
                    }
                }
            }
        };

    private static EffectiveProtection Protection(ProtectionClass tableClass, string? maskCategory = null) =>
        new(
            Container: new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            OwnerGroupSid: null,
            TableClass: tableClass,
            DeclaredTableClass: tableClass,
            TableMaskCategory: maskCategory,
            Columns: new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
            Visibility: CatalogVisibilityMode.OnRequest);

    [Fact]
    public void Confidential_on_delegated_refused()
    {
        var options = BuildOptions(EnforcementMode.Delegated);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayStartupValidator.ValidateDelegatedProtection(
                options,
                [("uc-prod", Protection(ProtectionClass.Confidential))]));

        ex.Message.ShouldContain(DelegatedProtectionValidator.ErrorCode);

        Should.Throw<ValidationException>(() =>
            DelegatedProtectionValidator.Validate(
                EnforcementMode.Delegated,
                "uc-prod",
                ProtectionClass.Secret,
                maskCategory: null));
    }

    [Fact]
    public void Pii_on_delegated_refused()
    {
        var options = BuildOptions(EnforcementMode.Delegated);

        var columnMaskProtection = new EffectiveProtection(
            Container: new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            OwnerGroupSid: null,
            TableClass: ProtectionClass.Internal,
            DeclaredTableClass: ProtectionClass.Internal,
            TableMaskCategory: null,
            Columns: new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new(ProtectionClass.Internal, "email")
            },
            Visibility: CatalogVisibilityMode.OnRequest);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayStartupValidator.ValidateDelegatedProtection(options, [("uc-prod", columnMaskProtection)]));

        ex.Message.ShouldContain(DelegatedProtectionValidator.ErrorCode);
    }

    [Fact]
    public void Defence_in_depth_allows()
    {
        var options = BuildOptions(EnforcementMode.DelegatedDefenceInDepth);

        var ex = Record.Exception(() =>
            GatewayStartupValidator.ValidateDelegatedProtection(
                options,
                [("uc-prod", Protection(ProtectionClass.Secret, maskCategory: "full"))]));

        ex.ShouldBeNull();
    }
}
