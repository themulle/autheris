namespace Autheris.Tests.Unit.Security;

using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// GQL-03: the page offset is bounded centrally in the execution service, so every resolver inherits the bound.
/// </summary>
public sealed class Gql03OffsetBoundTests
{
    [Theory]
    [Trait("Category", "Security")]
    [InlineData(-1)]
    [InlineData(2147483647)]
    public async Task GQL_03_OutOfRangeOffset_IsRejected(int offset)
    {
        var service = new GatewayExecutionService(
            Substitute.For<ITableMetadataRepository>(),
            Substitute.For<IConsentRepository>(),
            Substitute.For<IAuditLogRepository>(),
            Substitute.For<IConsentResolutionService>(),
            Substitute.For<IConsentCacheService>(),
            Substitute.For<IColumnMaskingProvider>(),
            options: Options.Create(new GatewayOptions()));

        await Should.ThrowAsync<GatewayInvalidQueryException>(
            () => service.ExecuteTableQueryAsync(null, new TableIdentifier("finance", "dbo", "t"), 10, offset));
    }
}
