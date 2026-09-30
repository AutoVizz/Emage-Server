namespace Emage.Imaging.J2k;

public sealed record J2kSlice(byte[] Data, bool IncludesHeaders, int[] TilePsotOffsets)
{
    public static readonly byte[] Eoc = { 0xff, 0xd9 };

    public byte[] WithEoc()
    {
        byte[] result = new byte[Data.Length + 2];
        Buffer.BlockCopy(Data, 0, result, 0, Data.Length);
        result[^2] = Eoc[0];
        result[^1] = Eoc[1];
        return result;
    }
}

public static class J2kLayerExtractor
{
    private const byte Soc = 0x4f, Tlm = 0x55, Plm = 0x57, Plt = 0x58, Ppm = 0x60, Ppt = 0x61,
        Sot = 0x90, Sod = 0x93;

    // Emit the packets of resolution levels `firstLayer..lastLayer` as a codestream.
    //
    // A resolution level's pixel geometry comes from SIZ together with COD's
    // decomposition count, not from where its packets sit in the body, so the main
    // headers are copied verbatim — including that count. Renumbering the retained
    // levels from 0 rescales the image instead of coarsening it: a 0:0 window of a
    // 512x512/6-level codestream would decode as 512x512 rather than 16x16. The
    // window simply carries fewer packets, and a decoder reduces by
    // `NumResolutions - 1 - lastLayer` halvings to read it.
    public static J2kSlice Slice(byte[] data, J2kCodestream codestream, int firstLayer, int lastLayer)
    {
        if (firstLayer < 0 || lastLayer >= codestream.NumLayers || firstLayer > lastLayer)
            throw new J2kException(
                $"Layer range {firstLayer}:{lastLayer} is outside 0..{codestream.NumLayers - 1}");
        if (codestream.HasPoc)
            throw new J2kException("Codestream uses POC; layer slicing is not supported");
        if (!codestream.HasPlt)
            throw new J2kException(
                "Codestream has no PLT marker; packet boundaries are unavailable, layer slicing is not supported");

        using var output = new MemoryStream();
        var psotOffsets = new List<int>();

        if (firstLayer == 0)
        {
            output.WriteByte(0xff);
            output.WriteByte(Soc);

            foreach (J2kSegment seg in codestream.MainSegments)
            {
                switch (seg.Code)
                {
                    case Tlm:
                    case Plm:
                    case Ppm:
                        break;
                    default:
                        output.Write(data, (int)seg.Offset, seg.Span);
                        break;
                }
            }

            foreach (J2kTilePart tilePart in codestream.TileParts)
            {
                long tileStart = output.Position;
                output.WriteByte(0xff);
                output.WriteByte(Sot);
                WriteBe16(output, 10);
                WriteBe16(output, tilePart.TileIndex);
                psotOffsets.Add((int)output.Position);
                WriteBe32(output, 0);
                output.WriteByte(tilePart.TPsot);
                output.WriteByte(tilePart.TNsot);

                foreach (J2kSegment seg in tilePart.HeaderSegments)
                {
                    if (seg.Code is Plt or Ppt) continue;
                    output.Write(data, (int)seg.Offset, seg.Span);
                }

                output.WriteByte(0xff);
                output.WriteByte(Sod);

                foreach (J2kPacket packet in tilePart.Packets)
                {
                    if (packet.Layer > lastLayer) continue;
                    output.Write(data, (int)packet.Offset, (int)packet.Length);
                }

                long tileEnd = output.Position;
                output.Position = tileStart + 6;
                WriteBe32(output, (uint)(tileEnd - tileStart));
                output.Position = output.Length;
            }
        }
        else
        {
            foreach (J2kTilePart tilePart in codestream.TileParts)
                foreach (J2kPacket packet in tilePart.Packets)
                {
                    if (packet.Layer < firstLayer || packet.Layer > lastLayer) continue;
                    output.Write(data, (int)packet.Offset, (int)packet.Length);
                }
        }

        return new J2kSlice(output.ToArray(), firstLayer == 0, psotOffsets.ToArray());
    }

    private static void WriteBe16(MemoryStream output, int value)
    {
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }

    private static void WriteBe32(MemoryStream output, uint value)
    {
        output.WriteByte((byte)(value >> 24));
        output.WriteByte((byte)(value >> 16));
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }
}