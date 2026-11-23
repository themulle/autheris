namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class InternalAudienceTokenTests : IClassFixture<InternalAudienceTokenTests.HostFixture>
{
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("a-very-secret-test-key-of-at-least-256-bits-length!"));
    private const string EntraTenantId = "11111111-1111-1111-1111-111111111111";
    private const string ForeignIdpTenant = "99999999-9999-9999-9999-999999999999";
    private const string ClientAudience = "api://autheris-internal-test";
    private const string Issuer = $"https://login.microsoftonline.com/{EntraTenantId}/v2.0";

    private readonly WebApplicationFactory<Program> _factory;

    public InternalAudienceTokenTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            base.ConfigureWebHost(builder);
        }

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("InternalAudienceTokenTests"),
                ["Gateway:Tenancy:DefaultTenantId"] = "t1",
                ["Gateway:Governance:AccessPolicy:Internal:TrustedIssuerTenantIds:0"] = EntraTenantId,
                ["Gateway:Governance:AccessPolicy:Internal:RequireMemberIndicator"] = "true",
                ["Authentication:Realms:Realms:entra-workforce:Kind"] = "EntraWorkforce",
                ["Authentication:Realms:Realms:entra-workforce:Issuers:0"] = Issuer,
                ["Authentication:Realms:Realms:entra-workforce:Audiences:0"] = ClientAudience,
                ["Authentication:Realms:Realms:entra-workforce:FixedTenantId"] = "t1",
                ["Authentication:Realms:Realms:entra-workforce:GroupSource"] = "Token",
                ["Authentication:Realms:Realms:entra-workforce:Provisioning"] = "None"
            });
        }

        protected override void ConfigureHostServices(IServiceCollection services)
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                options.TokenValidationParameters.ValidIssuer = Issuer;
                options.TokenValidationParameters.ValidAudience = ClientAudience;
            });
        }
    }

    private static string MintJwt(IEnumerable<Claim> claims)
    {
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientAudience,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256Signature),
            Expires = DateTime.UtcNow.AddHours(1)
        };
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }

    [Fact]
    public async Task B2b_guest_token_not_internal()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var metaRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            var domainRepo = scope.ServiceProvider.GetRequiredService<IGovernanceDomainRepository>();
            var tableId = Guid.NewGuid();
            var id = new TableIdentifier("sales", "dbo", "internal_metrics");
            await metaRepo.UpsertTableMetadataAsync(new TableMetadata
            {
                Identifier = id,
                OwnerTenantId = "t1",
                Table = new Table
                {
                    Id = tableId,
                    SourceName = "sales",
                    SchemaName = "dbo",
                    TableName = "internal_metrics",
                    IsActive = true
                },
                Columns = [new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" }]
            });
            await domainRepo.SetTableProtectionAsync(new TenantId("t1"), tableId, new ObjectProtection(ProtectionClass.Internal, null), clearColumns: null, new Sid("S-1-5-21-ADM-1"));
        }

        var guestToken = MintJwt(
        [
            new Claim("oid", "44444444-4444-4444-4444-444444444444"),
            new Claim("tid", EntraTenantId),
            new Claim("azp", "b2b-test-client"),
            new Claim("scp", "read"),
            new Claim("acct", "1"),
            new Claim("idp", $"https://login.microsoftonline.com/{ForeignIdpTenant}/v2.0"),
            new Claim("upn", "guest_partner.com#EXT#@contoso.onmicrosoft.com")
        ]);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", guestToken);

        var res = await client.GetAsync("/api/v1/data/sales/dbo/internal_metrics");
        res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
