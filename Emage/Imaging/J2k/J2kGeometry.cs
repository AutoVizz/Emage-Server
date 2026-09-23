namespace Emage.Imaging.J2k;

public static class J2kGeometry
{
    public static long CeilDiv(long a, long b) => (a + b - 1) / b;

    public static long FloorDiv(long a, long b) => a >= 0 ? a / b : -CeilDiv(-a, b);

    public static long CeilDivPow2(long a, int power) => CeilDiv(a, 1L << power);

    public static long FloorDivPow2(long a, int power) => FloorDiv(a, 1L << power);

    public static (long X0, long Y0, long X1, long Y1) TileBounds(
        int tileX, int tileY, uint xsiz, uint ysiz, uint xosiz, uint yosiz, uint xtsiz, uint ytsiz,
        uint xtosiz, uint ytosiz)
    {
        long tx0 = xtosiz + (long)tileX * xtsiz;
        long ty0 = ytosiz + (long)tileY * ytsiz;
        long x0 = Math.Max(tx0, xosiz);
        long y0 = Math.Max(ty0, yosiz);
        long x1 = Math.Min(tx0 + xtsiz, xsiz);
        long y1 = Math.Min(ty0 + ytsiz, ysiz);
        return (x0, y0, x1, y1);
    }

    public static int TilesAcross(uint xsiz, uint xtosiz, uint xtsiz) => (int)CeilDiv(xsiz - xtosiz, xtsiz);
}