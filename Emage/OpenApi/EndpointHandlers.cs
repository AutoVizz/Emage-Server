using System.Security.Cryptography;
using System.Text;
using Emage.Imaging.J2k;

namespace Emage.OpenApi;

public sealed class EndpointHandlers
{
    private readonly J2kImageLibrary _library;
    private readonly string? _uploadPassword;

    public EndpointHandlers(J2kImageLibrary library, string? uploadPassword)
    {
        _library = library;
        _uploadPassword = uploadPassword;
    }

    public IResult GetImage(HttpContext http, string? path)
    {
        if (!_library.TryGet(path ?? string.Empty, out var image))
            return Results.NotFound("Image not found or is not a supported JPEG 2000 codestream.");

        string raw = http.Request.Headers["X-Emage-Layers"].ToString();

        if (raw.Equals("meta", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new
            {
                layers = image.Codestream.NumLayers,
                width = image.Codestream.Width,
                height = image.Codestream.Height,
                bytes = image.Data.Length
            });

        if (!TryParseRange(raw, image.Codestream.NumLayers, out int first, out int last))
            return Results.BadRequest(
                "Request header 'X-Emage-Layers' must be 'n:m', 'n', 'all', or 'meta' within the available layers.");

        try
        {
            J2kSlice slice = J2kLayerExtractor.Slice(image.Data, image.Codestream, first, last);
            http.Response.Headers["X-Emage-Framing"] = first == 0 ? "headers+packets" : "packets";
            http.Response.Headers["X-Emage-Layers"] = $"{first}:{last}";
            http.Response.Headers["X-Emage-TotalLayers"] = image.Codestream.NumLayers.ToString();
            http.Response.Headers["X-Emage-RemainingLayers"] = (image.Codestream.NumLayers - last - 1).ToString();
            if (slice.TilePsotOffsets.Length > 0)
                http.Response.Headers["X-Emage-TilePsotOffsets"] = string.Join(',', slice.TilePsotOffsets);
            // A window that starts at level 0 carries the main headers, so it is a
            // codestream in its own right and is terminated with EOC. Without it
            // OpenJPEG does not report a truncated tile-part, it keeps asking for
            // the next tile-part and never returns.
            byte[] payload = slice.IncludesHeaders ? slice.WithEoc() : slice.Data;
            return Results.Bytes(payload, "image/jp2");
        }
        catch (J2kException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    public async Task<IResult> UploadImage(HttpContext http, string? path)
    {
        var feature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is not null) feature.MaxRequestBodySize = null;

        if (string.IsNullOrEmpty(_uploadPassword) ||
            !FixedTimeEquals(_uploadPassword, http.Request.Headers["X-Emage-Password"].ToString()))
            return Results.Unauthorized();

        if (!_library.TryResolve(path ?? string.Empty, out string fullPath))
            return Results.BadRequest("Invalid image path.");

        byte[] data;
        using (var buffer = new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(buffer);
            data = buffer.ToArray();
        }

        if (data.Length < 2)
            return Results.BadRequest("Upload body is empty.");
        if (data[^2] != 0xff || data[^1] != 0xd9)
            return Results.BadRequest(
                "Upload must be the entire image (a full codestream ending with EOC), not a layer slice.");

        J2kCodestream parsed;
        try
        {
            parsed = J2kCodestreamParser.Parse(data, fullPath);
        }
        catch (J2kException ex)
        {
            return Results.BadRequest($"Not a valid JPEG 2000 codestream: {ex.Message}");
        }

        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(fullPath, data);

        // The cache holds both the parsed structure and the raw bytes, so a
        // re-upload to an existing path would otherwise keep serving the
        // previous version indefinitely. Evict only after the write succeeded.
        _library.Invalidate(path ?? string.Empty);

        return Results.Ok(new
        {
            stored = path,
            layers = parsed.NumLayers,
            width = parsed.Width,
            height = parsed.Height,
            bytes = data.Length
        });
    }

    public IResult ListImages()
    {
        var list = new List<object>();
        foreach (var file in _library.EnumerateImages().OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            string relative = file.RelativePath.Replace('\\', '/');
            if (_library.TryGet(relative, out var image))
                list.Add(new
                {
                    path = relative,
                    bytes = file.Length,
                    layers = image.Codestream.NumLayers,
                    width = image.Codestream.Width,
                    height = image.Codestream.Height
                });
            else
                list.Add(new { path = relative, bytes = file.Length });
        }
        return Results.Json(list);
    }

    public IResult DeleteImage(HttpContext http, string? path)
    {
        if (string.IsNullOrEmpty(_uploadPassword) ||
            !FixedTimeEquals(_uploadPassword, http.Request.Headers["X-Emage-Password"].ToString()))
            return Results.Unauthorized();

        if (!_library.TryDelete(path ?? string.Empty, out bool existed))
            return Results.BadRequest("Invalid image path.");

        return existed
            ? Results.Ok(new { deleted = path })
            : Results.NotFound("Image not found.");
    }

    public IResult GetImageInfo(string? path)
    {
        if (!_library.TryGet(path ?? string.Empty, out var image))
            return Results.NotFound("Image not found or is not a supported JPEG 2000 codestream.");

        var codestream = image.Codestream;
        var packetByLayer = codestream.Packets
            .GroupBy(p => p.Layer)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                layer = g.Key,
                size = g.Sum(p => p.Length)
            });

        return Results.Json(new
        {
            width = codestream.Width,
            height = codestream.Height,
            components = codestream.Components.Select(c => new
            {
                precision = c.Precision,
                dx = c.SubDx,
                dy = c.SubDy
            }),
            tileSize = new { width = codestream.XTsiz, height = codestream.YTsiz },
            tiles = codestream.TileParts.Select(tp => tp.TileIndex).Distinct().Count(),
            layers = codestream.NumLayers,
            progression = codestream.ProgressionOrder.ToString(),
            scod = codestream.Scod,
            sop = codestream.HasSop,
            eph = codestream.HasEph,
            pltDebug = codestream.HasPlt,
            packets = codestream.Packets.Count,
            packetsPerLayer = codestream.PacketsPerLayer,
            bytes = image.Data.Length,
            packetsByLayer = packetByLayer
        });
    }

    private static bool TryParseRange(string raw, int maxLayers, out int first, out int last)
    {
        first = 0;
        last = maxLayers - 1;
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            return maxLayers >= 1;
        string[] parts = raw.Split(':', 2);
        if (!int.TryParse(parts[0], out first) || first < 0) return false;
        if (parts.Length == 1) last = first;
        else if (!int.TryParse(parts[1], out last) || last < first) return false;
        return last < maxLayers;
    }

    private static bool FixedTimeEquals(string expected, string given)
    {
        if (expected.Length != given.Length) return false;
        byte[] a = Encoding.UTF8.GetBytes(expected);
        byte[] b = Encoding.UTF8.GetBytes(given);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}