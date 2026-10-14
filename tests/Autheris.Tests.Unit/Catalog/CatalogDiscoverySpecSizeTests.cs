namespace Autheris.Tests.Unit.Catalog;

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogDiscoverySpecSizeTests
{
    /// <summary>Content without a known length, emulating a chunked response with no Content-Length header.</summary>
    private sealed class ChunkedContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context)
            => stream.CopyToAsync(target);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
    }

    [Fact]
    public async Task SpecUrl_BodyLargerThanCap_IsRejected()
    {
        using var stream = new MemoryStream(new byte[1025]);
        using var content = new ChunkedContent(stream);

        content.Headers.ContentLength.ShouldBeNull();
        await Should.ThrowAsync<ArgumentException>(() =>
            BoundedHttpContentReader.ReadStringAsync(content, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task SpecUrl_BodyExactlyAtCap_IsAccepted()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 1024)));
        using var content = new ChunkedContent(stream);

        var text = await BoundedHttpContentReader.ReadStringAsync(content, 1024, CancellationToken.None);
        text.Length.ShouldBe(1024);
    }

    [Fact]
    public async Task SpecUrl_DeclaredContentLengthLargerThanCap_IsRejectedBeforeReading()
    {
        using var content = new ByteArrayContent(new byte[2048]);

        await Should.ThrowAsync<ArgumentException>(() =>
            BoundedHttpContentReader.ReadStringAsync(content, 1024, CancellationToken.None));
    }

    [Fact]
    public void Options_NonPositiveCap_FailsValidation()
    {
        var validator = new CatalogDiscoveryOptionsValidator();
        validator.Validate(null, new CatalogDiscoveryOptions { MaxSpecDownloadBytes = 0 }).Failed.ShouldBeTrue();
        validator.Validate(null, new CatalogDiscoveryOptions()).Succeeded.ShouldBeTrue();
        new CatalogDiscoveryOptions().MaxSpecDownloadBytes.ShouldBe(10L * 1024 * 1024);
    }
}
