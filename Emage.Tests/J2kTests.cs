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
        Assert.Equal(2, cs.NumLayers);
        Assert.Equal(6, cs.NumResolutions);
        Assert.Equal(J2kProgressionOrder.Lrcp, cs.ProgressionOrder);
        Assert.Single(cs.TileParts);
        Assert.Equal(36, cs.Packets.Count);
        Assert.Equal(18, cs.PacketsPerLayer);
        Assert.Contains(cs.MainSegments, s => s.Code == 0x65);
    }

    [Fact]
    public void FullSlice_OfPlaintext_DecodesIdentically()
    {
        if (!Decode(Fixture("sample.lrcp.j2k"), Path.Combine(_scratch, "ref.ppm"))) return;
        J2kSlice slice = Slice("sample.lrcp.j2k", 0, 2);
        string outPpm = Path.Combine(_scratch, "slice.ppm");
        if (!DecodeBytes(slice.WithEoc(), outPpm)) { Assert.Fail("opj_decompress failed on full slice"); return; }

        Assert.Equal(File.ReadAllBytes(Path.Combine(_scratch, "ref.ppm")), File.ReadAllBytes(outPpm));
    }

    [Fact]
    public void SopVariant_FullSlice_DecodesIdentically()
    {
        if (!Decode(Fixture("sample.sop.j2k"), Path.Combine(_scratch, "ref_sop.ppm"))) return;
        J2kSlice slice = Slice("sample.sop.j2k", 0, 2);
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
    public void PrefixSlice_ExposesLayerCountOffset_WithoutEoc()
    {
        J2kSlice slice = Slice("sample.lrcp.j2k", 0, 0);

        Assert.True(slice.IncludesHeaders);
        Assert.InRange(slice.LayerCountOffset, 0, slice.Data.Length - 2);
        Assert.Equal(0, slice.Data[slice.LayerCountOffset]);
        Assert.Equal(1, slice.Data[slice.LayerCountOffset + 1]);
        Assert.Equal(0xff, slice.Data[0]);
        Assert.Equal(0x4f, slice.Data[1]);
        Assert.False(slice.Data[^2] == 0xff && slice.Data[^1] == 0xd9);
        Assert.Single(slice.TilePsotOffsets);
        Assert.InRange(slice.TilePsotOffsets[0], 0, slice.Data.Length - 4);
        Assert.Equal(0xff, slice.Data[slice.TilePsotOffsets[0] - 6]);
        Assert.Equal(0x90, slice.Data[slice.TilePsotOffsets[0] - 5]);

        J2kCodestream parsed = J2kCodestreamParser.Parse(slice.WithEoc(), "prefix");
        Assert.Equal(1, parsed.NumLayers);
    }

    [Fact]
    public void MidLayerSlice_ReturnsPacketsOnly()
    {
        byte[] data = File.ReadAllBytes(Fixture("sample.lrcp.j2k"));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, "sample.lrcp.j2k");

        J2kSlice slice = J2kLayerExtractor.Slice(data, cs, 1, 2);

        Assert.False(slice.IncludesHeaders);
        Assert.Equal(-1, slice.LayerCountOffset);
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
    public void Rlcp_MidLayers_IsRejected()
    {
        byte[] data = File.ReadAllBytes(Fixture("sample.rlcp.j2k"));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, "sample.rlcp.j2k");
        Assert.Throws<J2kException>(() => J2kLayerExtractor.Slice(data, cs, 1, 2));
    }

    [Fact]
    public void NoplJ2k_ToleratedForInfo_ButSliceRejected()
    {
        byte[] data = File.ReadAllBytes(Fixture("sample.nopl.j2k"));
        J2kCodestream cs = J2kCodestreamParser.Parse(data, "sample.nopl.j2k");

        Assert.False(cs.HasPlt);
        Assert.Equal(3, cs.NumLayers);
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