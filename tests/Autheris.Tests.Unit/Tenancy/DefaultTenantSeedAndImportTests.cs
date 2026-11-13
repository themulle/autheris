using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Tenancy;

/// <summary>DT-A-9 (WP-DT-2a): seeds and importers use the configured default tenant and never invent one.</summary>
public sealed class DefaultTenantSeedAndImportTests
{
    [Fact]
    public void OpenMetadata_Entry_Without_Mapping_Uses_Default_Tenant_Else_Is_Skipped()
    {
        var options = new OpenMetadataOptions();

        OpenMetadataTableIdentity.TryResolveTenant("svc", "db", options, out var withDefault, out _, new TenantId("tenant-d")).ShouldBeTrue();
        withDefault.Value.ShouldBe("tenant-d");

        OpenMetadataTableIdentity.TryResolveTenant("svc", "db", options, out _, out var reason).ShouldBeFalse();
        reason.ShouldContain("no tenant mapping");

        new OpenMetadataOptions().DefaultTenantId.ShouldBeNull();
    }
}
