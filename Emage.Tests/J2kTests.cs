using System.Diagnostics;
using Emage.Imaging.J2k;
using Xunit;

namespace Emage.Tests;

public class J2kTests : IDisposable
{
    private readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "emage-tests-" + Guid.NewGuid().ToString("N"));

    public J2kTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private string Fixture(string name) => Path.Combine(_fixtures, name);

    // Offset of a main-header marker segment (FF xx) in a slice.
    private static int MarkerOffset(byte[] slice, byte code)
    {
        for (int i = 2; i + 1 < slice.Length; ++i)
            if (slice[i] == 0xff && slice[i + 1] == code)
                return i;
        throw new InvalidOperationException($"slice has no FF{code:X2} marker");
    }

    // Start of the SOD marker segment's body, i.e. just past the marker.
    private static int SliceSodOffset(byte[] slice) => MarkerOffset(slice, 0x93) + 2;

    private J2kSlice Slice(string fixtureName, int first, int last)
    {
        byte[] data = File.ReadAllBytes(Fixture(fixtureName));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, fixtureName);
        J2kSlice slice = J2kLayerExtractor.Slice(data, cs, first, last);
        Assert.True(slice.Data.Length > 0);
        return slice;
    }

    [Fact]
    public void Parse_EncryptedJ2k_ReportsExpectedStructure()
    {
        byte[] data = File.ReadAllBytes(Fixture("encrypted.j2k"));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, "encrypted.j2k");

        Assert.Equal(224, cs.Width);
        Assert.Equal(225, cs.Height);
        Assert.Equal(3, cs.Csiz);
        Assert.All(cs.Components, c => Assert.Equal(8, c.Precision));
        Assert.All(cs.Components, c => Assert.Equal(1, c.SubDx));
        Assert.Equal(6, cs.NumLayers);
        Assert.Equal(6, cs.NumResolutions);
        Assert.Equal(J2kProgressionOrder.Lrcp, cs.ProgressionOrder);
        Assert.Single(cs.TileParts);
        Assert.Equal(36, cs.Packets.Count);
        // Groups are per resolution level, so 36 packets over 6 levels.
        Assert.Equal(6, cs.PacketsPerLayer);
        Assert.Contains(cs.MainSegments, s => s.Code == 0x65);
    }

    [Fact]
    public void FullSlice_OfPlaintext_DecodesIdentically()
    {
        if (!Decode(Fixture("sample.lrcp.j2k"), Path.Combine(_scratch, "ref.ppm"))) return;
        J2kSlice slice = Slice("sample.lrcp.j2k", 0, 5);
        string outPpm = Path.Combine(_scratch, "slice.ppm");
        if (!DecodeBytes(slice.WithEoc(), outPpm)) { Assert.Fail("opj_decompress failed on full slice"); return; }

        Assert.Equal(File.ReadAllBytes(Path.Combine(_scratch, "ref.ppm")), File.ReadAllBytes(outPpm));
    }

    [Fact]
    public void SopVariant_FullSlice_DecodesIdentically()
    {
        if (!Decode(Fixture("sample.sop.j2k"), Path.Combine(_scratch, "ref_sop.ppm"))) return;
        J2kSlice slice = Slice("sample.sop.j2k", 0, 5);
        string outPpm = Path.Combine(_scratch, "sop_slice.ppm");
        Assert.True(DecodeBytes(slice.WithEoc(), outPpm), "opj_decompress failed on SOP full slice");

        Assert.Equal(File.ReadAllBytes(Path.Combine(_scratch, "ref_sop.ppm")), File.ReadAllBytes(outPpm));
    }

    [Fact]
    public void PrefixSlice_Decodes()
    {
        J2kSlice slice = Slice("sample.lrcp.j2k", 0, 0);
        Assert.True(DecodeBytes(slice.WithEoc(), Path.Combine(_scratch, "l0.ppm")),
            "opj_decompress failed on layer-0 prefix slice");
    }

    [Fact]
    public void PrefixSlice_KeepsHeadersAndPatchesPsot_WithoutEoc()
    {
        J2kSlice slice = Slice("sample.lrcp.j2k", 0, 0);

        Assert.True(slice.IncludesHeaders);
        Assert.Equal(0xff, slice.Data[0]);
        Assert.Equal(0x4f, slice.Data[1]);
        Assert.False(slice.Data[^2] == 0xff && slice.Data[^1] == 0xd9);
        Assert.Single(slice.TilePsotOffsets);
        int psotOffset = slice.TilePsotOffsets[0];
        Assert.InRange(psotOffset, 0, slice.Data.Length - 4);
        Assert.Equal(0xff, slice.Data[psotOffset - 6]);
        Assert.Equal(0x90, slice.Data[psotOffset - 5]);

        // The header must still describe the whole image: a resolution's size is
        // derived from SIZ plus the COD decomposition count, so renumbering the
        // retained levels from 0 would decode a 0:0 window as a full-size image.
        J2kCodestream parsed = J2kCodestreamParser.Parse(slice.WithEoc(), "prefix");
        Assert.Equal(6, parsed.NumLayers);
        Assert.Equal(6, parsed.NumResolutions);
    }

    [Fact]
    public void PrefixSlice_PsotCoversTheShortenedBody()
    {
        J2kSlice slice = Slice("sample.lrcp.j2k", 0, 0);
        byte[] data = File.ReadAllBytes(Fixture("sample.lrcp.j2k"));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, "sample.lrcp.j2k");

        // Psot counts from the start of its own SOT to the end of the tile-part, so
        // for the single tile-part here it is everything from SOT onwards.
        int psotOffset = slice.TilePsotOffsets[0];
        uint psot = (uint)((slice.Data[psotOffset] << 24) | (slice.Data[psotOffset + 1] << 16) |
                           (slice.Data[psotOffset + 2] << 8) | slice.Data[psotOffset + 3]);
        Assert.Equal((uint)(slice.Data.Length - (psotOffset - 6)), psot);

        // And it must be shorter than the stored tile-part, i.e. the finer levels
        // really were left out.
        uint full = (uint)(cs.TileParts[0].Packets[^1].Offset + cs.TileParts[0].Packets[^1].Length);
        Assert.True(psot < full, $"window Psot {psot} should be below full body {full}");
    }

    [Fact]
    public void MidLayerSlice_ReturnsPacketsOnly()
    {
        byte[] data = File.ReadAllBytes(Fixture("sample.lrcp.j2k"));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, "sample.lrcp.j2k");

        J2kSlice slice = J2kLayerExtractor.Slice(data, cs, 1, 2);

        Assert.False(slice.IncludesHeaders);
        Assert.Empty(slice.TilePsotOffsets);
        Assert.NotEqual(0xff, slice.Data[0]);
        Assert.False(slice.Data[^2] == 0xff && slice.Data[^1] == 0xd9);

        var expected = new MemoryStream();
        foreach (J2kTilePart tp in cs.TileParts)
            foreach (J2kPacket p in tp.Packets)
                if (p.Layer >= 1 && p.Layer <= 2)
                    expected.Write(data, (int)p.Offset, (int)p.Length);
        Assert.Equal(expected.ToArray(), slice.Data);
    }

    [Fact]
    public void Rlcp_PrefixSlice_Decodes()
    {
        J2kSlice slice = Slice("sample.rlcp.j2k", 0, 0);
        Assert.True(DecodeBytes(slice.WithEoc(), Path.Combine(_scratch, "rlcp_l0.ppm")),
            "opj_decompress failed on RLCP layer-0 prefix slice");
    }

    [Fact]
    public void Rlcp_PrefixWindow_IsWellFormed()
    {
        // "Layers" addresses resolution levels, so a prefix window is a legitimate
        // coarse-to-fine progressive fetch rather than an error.
        byte[] data = File.ReadAllBytes(Fixture("sample.rlcp.j2k"));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, "sample.rlcp.j2k");
        Assert.Equal(6, cs.NumLayers);

        J2kSlice slice = J2kLayerExtractor.Slice(data, cs, 0, 2);

        // Retaining 3 of 6 levels must not renumber them: the decomposition count
        // stays 5, so the window still describes a 6-level image whose levels 0..2
        // are present. The client reduces by 3 halvings to read it.
        J2kCodestream parsed = J2kCodestreamParser.Parse(slice.WithEoc(), "win");
        Assert.Equal(6, parsed.NumResolutions);
        Assert.Equal(6, parsed.NumLayers);

        // The window body is shorter than the stored tile-part, so the packets of
        // the finer levels really were left out.
        int sod = SliceSodOffset(slice.Data);
        Assert.True(slice.Data.Length - sod - 2 < cs.TileParts[0].Psot,
            "window body should be shorter than the stored tile-part");

        // Every main-header segment is copied verbatim, so COD keeps the full
        // decomposition count and the full quality-layer count.
        int codOffset = MarkerOffset(slice.Data, 0x52);
        int decomposition = slice.Data[codOffset + 9];
        Assert.Equal(5, decomposition);
        int layersOffset = codOffset + 6;
        Assert.Equal(3, (slice.Data[layersOffset] << 8) | slice.Data[layersOffset + 1]);
    }

    [Fact]
    public void MidWindow_OmitsHeaders()
    {
        // A window that does not start at level 0 carries packets only; the client
        // supplies the header it already has from the first window.
        byte[] data = File.ReadAllBytes(Fixture("sample.rlcp.j2k"));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, "sample.rlcp.j2k");

        J2kSlice slice = J2kLayerExtractor.Slice(data, cs, 1, 2);

        Assert.False(slice.IncludesHeaders);
        Assert.Empty(slice.TilePsotOffsets);
    }

    [Fact]
    public void ResolutionWindow_OutOfRange_IsRejected()
    {
        byte[] data = File.ReadAllBytes(Fixture("sample.rlcp.j2k"));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, "sample.rlcp.j2k");

        Assert.Throws<J2kException>(() => J2kLayerExtractor.Slice(data, cs, 0, cs.NumLayers));
        Assert.Throws<J2kException>(() => J2kLayerExtractor.Slice(data, cs, 3, 1));
    }

    [Fact]
    public void NoplJ2k_ToleratedForInfo_ButSliceRejected()
    {
        byte[] data = File.ReadAllBytes(Fixture("sample.nopl.j2k"));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, "sample.nopl.j2k");

        Assert.False(cs.HasPlt);
        Assert.Equal(6, cs.NumLayers);
        Assert.Equal(6, cs.NumResolutions);
        Assert.Equal(J2kProgressionOrder.Lrcp, cs.ProgressionOrder);
        Assert.Empty(cs.Packets);
        Assert.True(string.IsNullOrWhiteSpace(cs.SourcePath) == false);

        J2kException ex = Assert.Throws<J2kException>(() => J2kLayerExtractor.Slice(data, cs, 0, 2));
        Assert.Contains("PLT", ex.Message);
    }

    [Fact]
    public void ImageLibrary_RejectsTraversal()
    {
        var library = new J2kImageLibrary(_fixtures);
        Assert.False(library.TryResolve("../secret.j2k", out _));
        Assert.False(library.TryResolve("..\\secret.j2k", out _));
        Assert.False(library.TryResolve("/etc/passwd", out _));
        Assert.True(library.TryResolve("sample.lrcp.j2k", out string fullPath));
        Assert.True(File.Exists(fullPath));
    }

    private bool Decode(string input, string output)
    {
        if (!TryFindOpjDecompress(out string exe)) return false;
        using var process = Start(exe, "-i", input, "-o", output);
        process.WaitForExit(15000);
        return process.ExitCode == 0 && File.Exists(output);
    }

    private bool DecodeBytes(byte[] j2k, string output)
    {
        if (!TryFindOpjDecompress(out string exe)) return false;
        string input = Path.Combine(_scratch, Path.GetFileNameWithoutExtension(output) + ".j2k");
        File.WriteAllBytes(input, j2k);
        using var process = Start(exe, "-i", input, "-o", output);
        process.WaitForExit(15000);
        return process.ExitCode == 0 && File.Exists(output);
    }

    private static Process Start(string fileName, params string[] args)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);
        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    private static bool TryFindOpjDecompress(out string exe)
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "opj_decompress"),
            "/usr/bin/opj_decompress",
            "/usr/local/bin/opj_decompress"
        };
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
            candidates = candidates
                .Concat(pathEnv.Split(Path.PathSeparator).Select(dir => Path.Combine(dir, "opj_decompress")))
                .ToArray();
        foreach (string candidate in candidates)
            if (File.Exists(candidate))
            {
                exe = candidate;
                return true;
            }
        exe = string.Empty;
        return false;
    }
}