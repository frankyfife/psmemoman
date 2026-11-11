namespace MemcardManager.Core;

/// <summary>Hamming ECC as used in the spare area of PS2 memory card pages (3 bytes per 128 byte chunk).</summary>
internal static class Ecc
{
    static readonly byte[] Parity = new byte[256];
    static readonly byte[] ColumnMask = new byte[256];

    static Ecc()
    {
        for (int b = 0; b < 256; b++)
        {
            int a = b ^ (b >> 1);
            a ^= a >> 2;
            a ^= a >> 4;
            Parity[b] = (byte)(a & 1);
        }
        int[] cp = { 0x55, 0x33, 0x0F, 0x00, 0xAA, 0xCC, 0xF0 };
        for (int b = 0; b < 256; b++)
        {
            int m = 0;
            for (int i = 0; i < cp.Length; i++) m |= Parity[b & cp[i]] << i;
            ColumnMask[b] = (byte)m;
        }
    }

    public static void Calculate(ReadOnlySpan<byte> chunk, Span<byte> ecc)
    {
        int cp = 0x77, lp0 = 0x7F, lp1 = 0x7F;
        for (int i = 0; i < chunk.Length; i++)
        {
            byte b = chunk[i];
            cp ^= ColumnMask[b];
            if (Parity[b] != 0)
            {
                lp0 ^= ~i;
                lp1 ^= i;
            }
        }
        ecc[0] = (byte)(cp & 0xFF);
        ecc[1] = (byte)(lp0 & 0x7F);
        ecc[2] = (byte)(lp1 & 0xFF);
    }

    /// <summary>Writes ECC for a whole page into the spare area.</summary>
    public static void CalculatePage(ReadOnlySpan<byte> page, Span<byte> spare)
    {
        int chunks = Bin.DivUp(page.Length, 128);
        for (int i = 0; i < chunks; i++)
        {
            int start = i * 128;
            int len = Math.Min(128, page.Length - start);
            Calculate(page.Slice(start, len), spare.Slice(i * 3, 3));
        }
    }

    /// <summary>0 = ok, 1 = corrected, 2 = uncorrectable. Data may be fixed in place.</summary>
    public static int Check(Span<byte> chunk, ReadOnlySpan<byte> ecc)
    {
        Span<byte> c = stackalloc byte[3];
        Calculate(chunk, c);
        if (c[0] == ecc[0] && c[1] == ecc[1] && c[2] == ecc[2]) return 0;
        int cpDiff = (c[0] ^ ecc[0]) & 0x77;
        int lp0Diff = (c[1] ^ ecc[1]) & 0x7F;
        int lp1Diff = (c[2] ^ ecc[2]) & 0x7F;
        int lpComp = lp0Diff ^ lp1Diff;
        int cpComp = (cpDiff >> 4) ^ (cpDiff & 0x07);
        if (lpComp == 0x7F && cpComp == 0x07)
        {
            if (lp1Diff < chunk.Length) chunk[lp1Diff] ^= (byte)(1 << (cpDiff >> 4));
            return 1;
        }
        if ((cpDiff == 0 && lp0Diff == 0 && lp1Diff == 0) ||
            System.Numerics.BitOperations.PopCount((uint)lpComp) + System.Numerics.BitOperations.PopCount((uint)cpComp) == 1)
            return 1; // error in the ECC bytes themselves
        return 2;
    }
}
