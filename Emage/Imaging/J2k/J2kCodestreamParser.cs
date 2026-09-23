namespace Emage.Imaging.J2k;

public static class J2kCodestreamParser
{
    private const byte Soc = 0x4f, Siz = 0x51, Cod = 0x52, Coc = 0x53, Tlm = 0x55, Plm = 0x57, Plt = 0x58,
        Poc = 0x5f, Ppm = 0x60, Ppt = 0x61, Sot = 0x90, Sod = 0x93, Eoc = 0xd9;

    public static J2kCodestream Parse(byte[] data, string sourcePath)
    {
        if (data.Length < 2 || data[0] != 0xff || data[1] != Soc)
            throw new J2kException("Not a JPEG 2000 codestream");

        var main = new List<J2kSegment>();
        bool hasPoc = false;
        int sizOffset = -1, codOffset = -1;
        long pos = 2;
        while (pos + 1 < data.Length && data[pos] == 0xff)
        {
            byte m = data[pos + 1];
            if (m == Sot || m == Eoc) break;
            int len = Be16(data, pos + 2);
            main.Add(new J2kSegment(m, pos, 2 + len));
            if (m == Siz) sizOffset = (int)pos;
            else if (m == Cod) codOffset = (int)pos;
            else if (m == Poc) hasPoc = true;
            pos += 2 + len;
        }

        if (sizOffset < 0 || codOffset < 0)
            throw new J2kException("Missing SIZ or COD marker");

        (uint xsiz, uint ysiz, uint xosiz, uint yosiz, uint xtsiz, uint ytsiz, uint xtosiz, uint ytosiz, J2kComponent[] comps) = ReadSiz(data, sizOffset);
        (byte scod, J2kProgressionOrder progression, int layers, int numRes, J2kPrecinctSize[] precs) = ReadCod(data, codOffset);
        var cocPrecincts = ReadCoc(data, main, numRes);

        if (layers < 1 || numRes < 1)
            throw new J2kException("Invalid COD parameters");

        var tileParts = new List<J2kTilePart>();
        while (pos + 1 < data.Length && data[pos] == 0xff && data[pos + 1] == Sot)
        {
            J2kTilePart tilePart = ParseTilePart(data, pos, xsiz, ysiz, xosiz, yosiz, xtsiz, ytsiz, xtosiz, ytosiz,
                comps, numRes, layers, progression, cocPrecincts, precs);
            tileParts.Add(tilePart);
            if (tilePart.HeaderSegments.Any(s => s.Code == Poc)) hasPoc = true;
            pos += tilePart.Psot;
            if (tilePart.Psot == 0) break;
        }

        if (tileParts.Count == 0)
            throw new J2kException("No tile parts found");

        var packets = new List<J2kPacket>();
        foreach (J2kTilePart tp in tileParts)
            packets.AddRange(tp.Packets);

        return new J2kCodestream
        {
            SourcePath = sourcePath,
            Xsiz = xsiz, Ysiz = ysiz, XOsiz = xosiz, YOsiz = yosiz, XTsiz = xtsiz, YTsiz = ytsiz,
            XTOsiz = xtosiz, YTOsiz = ytosiz,
            Csiz = (byte)comps.Length,
            Components = comps,
            Scod = scod,
            ProgressionOrder = progression,
            NumLayers = layers,
            NumResolutions = numRes,
            MainSegments = main.ToArray(),
            TileParts = tileParts,
            Packets = packets,
            HasPoc = hasPoc,
            HasPlt = tileParts.Any(tp => tp.HeaderSegments.Any(s => s.Code == Plt))
        };
    }

    private static (uint, uint, uint, uint, uint, uint, uint, uint, J2kComponent[]) ReadSiz(byte[] data, int offset)
    {
        long p = offset + 4 + 2;
        uint xsiz = Be32(data, p), ysiz = Be32(data, p + 4), xosiz = Be32(data, p + 8), yosiz = Be32(data, p + 12),
            xtsiz = Be32(data, p + 16), ytsiz = Be32(data, p + 20), xtosiz = Be32(data, p + 24), ytosiz = Be32(data, p + 28);
        int csiz = Be16(data, p + 32);
        var comps = new J2kComponent[csiz];
        long c = p + 34;
        for (int i = 0; i < csiz; i++)
        {
            comps[i] = new J2kComponent(data[c], data[c + 1], data[c + 2]);
            c += 3;
        }
        return (xsiz, ysiz, xosiz, yosiz, xtsiz, ytsiz, xtosiz, ytosiz, comps);
    }

    private static (byte, J2kProgressionOrder, int, int, J2kPrecinctSize[]) ReadCod(byte[] data, int offset)
    {
        long p = offset + 4;
        byte scod = data[p];
        int progression = data[p + 1];
        if (progression is < 0 or > 4)
            throw new J2kException($"Unsupported progression order {progression}");
        int layers = Be16(data, p + 2);
        int numRes = data[p + 5] + 1;
        var precs = new J2kPrecinctSize[numRes];
        if ((scod & 0x01) != 0)
        {
            for (int r = 0; r < numRes; r++)
            {
                byte pr = data[p + 10 + r];
                precs[r] = new J2kPrecinctSize((byte)(pr >> 4), (byte)(pr & 0x0f));
            }
        }
        else
        {
            for (int r = 0; r < numRes; r++)
                precs[r] = new J2kPrecinctSize(15, 15);
        }
        return (scod, (J2kProgressionOrder)progression, layers, numRes, precs);
    }

    private static Dictionary<byte, J2kPrecinctSize[]> ReadCoc(byte[] data, List<J2kSegment> main, int numRes)
    {
        var result = new Dictionary<byte, J2kPrecinctSize[]>();
        foreach (J2kSegment seg in main.Where(s => s.Code == Coc))
        {
            long q = seg.Offset + 4;
            int compNo = Be16(data, q);
            byte scoc = data[q + 2];
            int numResComp = data[q + 3] + 1;
            int count = Math.Min(numResComp, numRes);
            var precs = new J2kPrecinctSize[count];
            if ((scoc & 0x01) != 0)
            {
                long start = q + 7;
                for (int r = 0; r < count; r++)
                {
                    byte b = data[start + r];
                    precs[r] = new J2kPrecinctSize((byte)(b >> 4), (byte)(b & 0x0f));
                }
            }
            else
            {
                for (int r = 0; r < count; r++)
                    precs[r] = new J2kPrecinctSize(15, 15);
            }
            result[(byte)compNo] = precs;
        }
        return result;
    }

    private static J2kTilePart ParseTilePart(byte[] data, long sot, uint xsiz, uint ysiz, uint xosiz,
        uint yosiz, uint xtsiz, uint ytsiz, uint xtosiz, uint ytosiz, J2kComponent[] comps, int numRes, int layers,
        J2kProgressionOrder progression, Dictionary<byte, J2kPrecinctSize[]> cocPrecincts, J2kPrecinctSize[] codPrecincts)
    {
        int lsot = Be16(data, sot + 2);
        int tileIndex = Be16(data, sot + 4);
        uint psot = Be32(data, sot + 6);
        byte tpsot = data[sot + 10], tnsot = data[sot + 11];

        long end;
        if (psot == 0)
        {
            end = FindEoc(data, sot + 2 + lsot);
            if (end < 0) throw new J2kException("Unterminated tile-part");
        }
        else
        {
            end = sot + psot;
        }

        var header = new List<J2kSegment>();
        long headerPos = sot + 2 + lsot;
        while (headerPos + 1 < end && data[headerPos] == 0xff)
        {
            byte hm = data[headerPos + 1];
            if (hm == Sod) break;
            int hl = Be16(data, headerPos + 2);
            header.Add(new J2kSegment(hm, headerPos, 2 + hl));
            headerPos += 2 + hl;
        }

        long sodOffset = headerPos;
        long bodyLength = end - (sodOffset + 2);
        if (bodyLength < 0)
            throw new J2kException("Corrupt tile-part length");

        bool hasPlt = header.Any(s => s.Code == Plt);
        int[] pltLengths = hasPlt ? ReadPlt(data, header) : [];

        var tilePart = new J2kTilePart
        {
            TileIndex = tileIndex,
            Psot = psot,
            TPsot = tpsot,
            TNsot = tnsot
        };
        foreach (J2kSegment seg in header)
            tilePart.HeaderSegments.Add(seg);

        if (!hasPlt) return tilePart;

        long packetSum = 0;
        foreach (int len in pltLengths) packetSum += len;
        if (packetSum != bodyLength)
            throw new J2kException($"PLT packet sizes ({packetSum}) do not cover SOD payload ({bodyLength})");

        var counts = PrecinctCountsPerResComp(tileIndex, xsiz, ysiz, xosiz, yosiz, xtsiz, ytsiz, xtosiz, ytosiz,
            comps, numRes, cocPrecincts, codPrecincts);
        var combos = OrderedCombos(progression, numRes, comps.Length, counts, layers);
        if (combos.Length != pltLengths.Length)
            throw new J2kException(
                $"Packet count from PLT ({pltLengths.Length}) does not match progression ({combos.Length})");

        long offset = sodOffset + 2;
        for (int i = 0; i < combos.Length; i++)
        {
            int len = pltLengths[i];
            tilePart.Packets.Add(new J2kPacket
            {
                Layer = combos[i].Layer,
                Offset = offset,
                Length = len
            });
            offset += len;
        }

        return tilePart;
    }

    private static long[,] PrecinctCountsPerResComp(int tileIndex, uint xsiz, uint ysiz, uint xosiz,
        uint yosiz, uint xtsiz, uint ytsiz, uint xtosiz, uint ytosiz, J2kComponent[] comps, int numRes,
        Dictionary<byte, J2kPrecinctSize[]> cocPrecincts, J2kPrecinctSize[] codPrecincts)
    {
        int tw = J2kGeometry.TilesAcross(xsiz, xtosiz, xtsiz);
        int tileX = tileIndex % tw;
        int tileY = tileIndex / tw;
        var (tx0, ty0, tx1, ty1) = J2kGeometry.TileBounds(tileX, tileY, xsiz, ysiz, xosiz, yosiz, xtsiz, ytsiz, xtosiz, ytosiz);

        long[,] counts = new long[numRes, comps.Length];
        for (int comp = 0; comp < comps.Length; comp++)
        {
            J2kPrecinctSize[] precise = cocPrecincts.TryGetValue((byte)comp, out var list) ? list : codPrecincts;
            for (int res = 0; res < numRes; res++)
            {
                J2kPrecinctSize size = res < precise.Length ? precise[res] : codPrecincts[res];
                counts[res, comp] = PrecinctCount(tx0, ty0, tx1, ty1, comps[comp].SubDx, comps[comp].SubDy, numRes, res, size);
            }
        }
        return counts;
    }

    private static long PrecinctCount(long tileX0, long tileY0, long tileX1, long tileY1, byte subDx, byte subDy,
        int numRes, int resNo, J2kPrecinctSize size)
    {
        long tcX0 = J2kGeometry.CeilDiv(tileX0, subDx);
        long tcY0 = J2kGeometry.CeilDiv(tileY0, subDy);
        long tcX1 = J2kGeometry.CeilDiv(tileX1, subDx);
        long tcY1 = J2kGeometry.CeilDiv(tileY1, subDy);
        int level = numRes - 1 - resNo;
        long rx0 = J2kGeometry.CeilDivPow2(tcX0, level);
        long ry0 = J2kGeometry.CeilDivPow2(tcY0, level);
        long rx1 = J2kGeometry.CeilDivPow2(tcX1, level);
        long ry1 = J2kGeometry.CeilDivPow2(tcY1, level);
        if (rx0 == rx1 || ry0 == ry1) return 0;
        long pw = J2kGeometry.CeilDivPow2(rx1, size.Log2Width) - J2kGeometry.FloorDivPow2(rx0, size.Log2Width);
        long ph = J2kGeometry.CeilDivPow2(ry1, size.Log2Height) - J2kGeometry.FloorDivPow2(ry0, size.Log2Height);
        return pw * ph;
    }

    private static (int Layer, int Res, int Comp, int Prec)[] OrderedCombos(J2kProgressionOrder order, int numRes,
        int comps, long[,] counts, int layers)
    {
        var list = new List<(int Layer, int Res, int Comp, int Prec)>();
        for (int layer = 0; layer < layers; layer++)
            for (int res = 0; res < numRes; res++)
                for (int comp = 0; comp < comps; comp++)
                    for (int prec = 0; prec < counts[res, comp]; prec++)
                        list.Add((layer, res, comp, prec));
        list.Sort((a, b) => CompareByOrder(a, b, order));
        return list.ToArray();
    }

    private static int CompareByOrder((int Layer, int Res, int Comp, int Prec) a, (int Layer, int Res, int Comp, int Prec) b,
        J2kProgressionOrder order)
    {
        foreach (Offset field in FieldsFor(order))
        {
            int va = FieldValue(a, field), vb = FieldValue(b, field);
            int cmp = va.CompareTo(vb);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private enum Offset { Layer, Res, Comp, Prec }

    private static Offset[] FieldsFor(J2kProgressionOrder order) => order switch
    {
        J2kProgressionOrder.Lrcp => new[] { Offset.Layer, Offset.Res, Offset.Comp, Offset.Prec },
        J2kProgressionOrder.Rlcp => new[] { Offset.Res, Offset.Layer, Offset.Comp, Offset.Prec },
        J2kProgressionOrder.Rpcl => new[] { Offset.Res, Offset.Prec, Offset.Comp, Offset.Layer },
        J2kProgressionOrder.Pcrl => new[] { Offset.Prec, Offset.Comp, Offset.Res, Offset.Layer },
        _ => new[] { Offset.Comp, Offset.Res, Offset.Prec, Offset.Layer }
    };

    private static int FieldValue((int Layer, int Res, int Comp, int Prec) combo, Offset field) => field switch
    {
        Offset.Layer => combo.Layer,
        Offset.Res => combo.Res,
        Offset.Comp => combo.Comp,
        _ => combo.Prec
    };

    private static int[] ReadPlt(byte[] data, List<J2kSegment> header)
    {
        var result = new List<int>();
        foreach (J2kSegment seg in header.Where(s => s.Code == Plt))
        {
            long q = seg.Offset + 4;
            long endSeg = seg.Offset + seg.Span;
            q += 1;
            int value = 0;
            while (q < endSeg)
            {
                byte b = data[q];
                q++;
                value = (value << 7) | (b & 0x7f);
                if ((b & 0x80) == 0)
                {
                    result.Add(value);
                    value = 0;
                }
            }
        }
        return result.ToArray();
    }

    private static long FindEoc(byte[] data, long start)
    {
        for (long i = start; i + 1 < data.Length; i++)
            if (data[i] == 0xff && data[i + 1] == Eoc)
                return i;
        return -1;
    }

    private static int Be16(byte[] d, long o) => (d[o] << 8) | d[o + 1];

    private static uint Be32(byte[] d, long o) =>
        ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
}