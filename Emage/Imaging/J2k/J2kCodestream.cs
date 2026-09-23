namespace Emage.Imaging.J2k;

public readonly record struct J2kComponent(byte SignedPrecision, byte SubDx, byte SubDy)
{
    public byte Precision => (byte)((SignedPrecision & 0x7F) + 1);
}

public readonly record struct J2kSegment(byte Code, long Offset, int Span);

public readonly record struct J2kPrecinctSize(byte Log2Width, byte Log2Height);

public sealed record J2kPacket
{
    public required int Layer { get; init; }
    public required long Offset { get; init; }
    public required long Length { get; init; }
}

public sealed class J2kTilePart
{
    public required int TileIndex { get; init; }
    public required uint Psot { get; init; }
    public required byte TPsot { get; init; }
    public required byte TNsot { get; init; }
    public List<J2kSegment> HeaderSegments { get; } = new();
    public List<J2kPacket> Packets { get; } = new();
}

public sealed class J2kCodestream
{
    public required string SourcePath { get; init; }
    public required uint Xsiz { get; init; }
    public required uint Ysiz { get; init; }
    public required uint XOsiz { get; init; }
    public required uint YOsiz { get; init; }
    public required uint XTsiz { get; init; }
    public required uint YTsiz { get; init; }
    public required uint XTOsiz { get; init; }
    public required uint YTOsiz { get; init; }
    public required byte Csiz { get; init; }
    public required J2kComponent[] Components { get; init; }
    public required byte Scod { get; init; }
    public required J2kProgressionOrder ProgressionOrder { get; init; }
    public required int NumLayers { get; init; }
    public required int NumResolutions { get; init; }
    public required J2kSegment[] MainSegments { get; init; }
    public required List<J2kTilePart> TileParts { get; init; }
    public required List<J2kPacket> Packets { get; init; }
    public required bool HasPoc { get; init; }
    public required bool HasPlt { get; init; }

    public bool HasSop => (Scod & 0x02) != 0;
    public bool HasEph => (Scod & 0x04) != 0;
    public int Width => (int)(Xsiz - XOsiz);
    public int Height => (int)(Ysiz - YOsiz);

    public int PacketsPerLayer => Packets.Count / Math.Max(1, NumLayers);
}