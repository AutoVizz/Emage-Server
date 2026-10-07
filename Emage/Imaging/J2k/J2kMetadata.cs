using System.Text;
using System.Text.Json;

namespace Emage.Imaging.J2k;

// Caller-defined metadata in a binary COM marker. The server recognizes only
// this generic envelope; it never interprets SEC, IV, key, or encryption state.
public static class J2kMetadata
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("EMAGE-META\0");

    public static JsonElement? Read(byte[] data, J2kCodestream codestream)
    {
        foreach (var segment in codestream.MainSegments)
        {
            if (segment.Code != 0x64 || segment.Span < 6 + Prefix.Length) continue;
            int offset = checked((int)segment.Offset);
            if (data[offset + 4] != 0 || data[offset + 5] != 0) continue;
            if (!data.AsSpan(offset + 6, Prefix.Length).SequenceEqual(Prefix)) continue;
            try
            {
                using var doc = JsonDocument.Parse(data.AsMemory(offset + 6 + Prefix.Length,
                    segment.Span - 6 - Prefix.Length));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return null;
    }
}
