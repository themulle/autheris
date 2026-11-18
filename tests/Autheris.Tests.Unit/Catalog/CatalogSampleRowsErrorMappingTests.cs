namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using Autheris.Api.Middleware;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#138 INC-1 P3: the <c>sample_rows</c> refusal of a non-Full shape (SEC-CAT138-15) is a client error (403), never a 500.</summary>
[Trait("Category", "Security")]
public sealed class CatalogSampleRowsErrorMappingTests
{
}
