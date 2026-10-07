using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Emage.Imaging.J2k;
using Emage.OpenApi;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Emage.Tests;

public class MetadataTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("emage-header-metadata").FullName;
    public void Dispose() => Directory.Delete(_root, true);
    private static byte[] Fixture(string name) => File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static byte[] Embed(byte[] data, string json)
    {
        var parsed = J2kCodestreamParser.Parse(data, "fixture");
        int offset = checked((int)parsed.MainSegments.Last().Offset + parsed.MainSegments.Last().Span);
        var payload = Encoding.UTF8.GetBytes("EMAGE-META\0" + json);
        var result = new byte[data.Length + payload.Length + 6];
        data.AsSpan(0, offset).CopyTo(result);
        result[offset] = 0xff;
        result[offset + 1] = 0x64;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset + 2), (ushort)(payload.Length + 4));
        payload.CopyTo(result, offset + 6);
        data.AsSpan(offset).CopyTo(result.AsSpan(offset + payload.Length + 6));
        return result;
    }

    [Theory]
    [InlineData("sample.lrcp.j2k")]
    [InlineData("encrypted.j2k")]
    public void HeaderMetadata_IsOpaqueAndSurvivesEveryResolutionWindow(string fixture)
    {
        var data = Embed(Fixture(fixture), "{\"callerField\":{\"labels\":[1,2],\"mode\":\"opaque\"}}");
        var parsed = J2kCodestreamParser.Parse(data, fixture);
        for (int level = 0; level < parsed.NumLayers; ++level)
        {
            var prefix = J2kLayerExtractor.Slice(data, parsed, 0, level).WithEoc();
            var window = J2kCodestreamParser.Parse(prefix, "window");
            var meta = J2kMetadata.Read(prefix, window);
            Assert.Equal("opaque", meta!.Value.GetProperty("callerField").GetProperty("mode").GetString());
        }
    }

    [Theory]
    [InlineData("sample.lrcp.j2k")]
    [InlineData("encrypted.j2k")]
    public async Task UploadAndMeta_ReadFromImageHeadersWithoutSeparateFiles(string fixture)
    {
        var library = new J2kImageLibrary(_root);
        var handlers = new EndpointHandlers(library, "test-password");
        var upload = new DefaultHttpContext();
        upload.Request.Headers["X-Emage-Password"] = "test-password";
        var bytes = Embed(Fixture(fixture), "{\"callerField\":\"opaque\"}");
        upload.Request.Body = new MemoryStream(bytes);
        var result = await handlers.UploadImage(upload, "image.j2k");
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, "image.j2k")));
        Assert.Single(Directory.EnumerateFiles(_root));
        var restarted = new J2kImageLibrary(_root);
        Assert.True(restarted.TryGet("image.j2k", out var stored));
        Assert.Equal("opaque", stored.Metadata!.Value.GetProperty("callerField").GetString());
        var request = new DefaultHttpContext();
        request.Request.Headers["X-Emage-Layers"] = "meta";
        var meta = handlers.GetImage(request, "image.j2k");
        var json = JsonSerializer.SerializeToElement(((IValueHttpResult)meta).Value);
        Assert.Equal("opaque", json.GetProperty("metadata").GetProperty("callerField").GetString());
        Assert.True(restarted.TryDelete("image.j2k", out bool existed));
        Assert.True(existed);
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public void MissingOrMalformedGenericMetadata_IsIgnored()
    {
        foreach (var data in new[] { Fixture("sample.lrcp.j2k"), Embed(Fixture("sample.lrcp.j2k"), "invalid") })
            Assert.Null(J2kMetadata.Read(data, J2kCodestreamParser.Parse(data, "fixture")));
    }
}
