using System.Security.Cryptography;

namespace MemcardManager.Core;

/// <summary>
/// Signature used by PSP/PS Vita virtual PS1 memory cards (.VMP) and PS3 single saves (.PSV):
/// an HMAC-SHA1 whose 20 byte key is derived from a salt seed via AES-128.
/// Algorithm as documented/implemented by MemcardRex (Shendo, GPL-3).
/// </summary>
public static class PsSign
{
    static readonly byte[] Key = { 0xAB, 0x5A, 0xBC, 0x9F, 0xC1, 0xF4, 0x9D, 0xE6, 0xA0, 0x51, 0xDB, 0xAE, 0xFA, 0x51, 0x88, 0x59 };
    static readonly byte[] Iv = { 0xB3, 0x0F, 0xFE, 0xED, 0xB7, 0xDC, 0x5E, 0xB7, 0x13, 0x3D, 0xA6, 0x0D, 0x1B, 0x6B, 0x2C, 0xDC };

    static byte[] EcbBlock(ReadOnlySpan<byte> block16, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        var outBuf = new byte[16];
        if (encrypt) aes.EncryptEcb(block16[..16], outBuf, PaddingMode.None);
        else aes.DecryptEcb(block16[..16], outBuf, PaddingMode.None);
        return outBuf;
    }

    /// <summary>HMAC-SHA1 over <paramref name="data"/> with the key derived from the 20 byte salt seed.</summary>
    public static byte[] Hmac(ReadOnlySpan<byte> data, ReadOnlySpan<byte> seed)
    {
        // 20 byte HMAC key: AES-decrypt(seed[0..16]) XOR IV  ‖  AES-encrypt(seed[0..16])[0..4] XOR seed[16..20]
        var dec = EcbBlock(seed[..16], false);
        var enc = EcbBlock(seed[..16], true);
        var key = new byte[64];
        for (int i = 0; i < 16; i++) key[i] = (byte)(dec[i] ^ Iv[i]);
        for (int i = 0; i < 4; i++) key[16 + i] = (byte)(enc[i] ^ seed[16 + i]);

        var inner = new byte[64 + data.Length];
        for (int i = 0; i < 64; i++) inner[i] = (byte)(key[i] ^ 0x36);
        data.CopyTo(inner.AsSpan(64));
        var h1 = SHA1.HashData(inner);
        var outer = new byte[64 + 20];
        for (int i = 0; i < 64; i++) outer[i] = (byte)(key[i] ^ 0x5C);
        h1.CopyTo(outer, 64);
        return SHA1.HashData(outer);
    }

    // ------------------------------------------------------------------ VMP (PSP / Vita)

    public const int VmpHeader = 0x80;

    /// <summary>Wraps a raw 128 KB PS1 card into a freshly signed VMP.</summary>
    public static byte[] MakeVmp(ReadOnlySpan<byte> rawCard)
    {
        var v = new byte[VmpHeader + Ps1Card.CardSize];
        v[1] = (byte)'P'; v[2] = (byte)'M'; v[3] = (byte)'V'; v[4] = 0x80;
        rawCard[..Ps1Card.CardSize].CopyTo(v.AsSpan(VmpHeader));
        var seed = SHA1.HashData(v);
        seed.CopyTo(v, 0x0C);
        Hmac(v, seed).CopyTo(v, 0x20);
        return v;
    }

    /// <summary>Checks the signature of an existing VMP file.</summary>
    public static bool VerifyVmp(byte[] vmp) => Verify(vmp, 0x0C, 0x20);

    static bool Verify(byte[] file, int seedOff, int sigOff)
    {
        if (file.Length < sigOff + 20) return false;
        var seed = file.AsSpan(seedOff, 20).ToArray();
        var sig = file.AsSpan(sigOff, 20).ToArray();
        var copy = (byte[])file.Clone();
        Array.Clear(copy, sigOff, 20);
        return Hmac(copy, seed).AsSpan().SequenceEqual(sig);
    }

    // ------------------------------------------------------------------ PSV (PS3)

    public static bool VerifyPsv(byte[] psv) => Verify(psv, 0x08, 0x1C);

    static void SignPsv(byte[] p)
    {
        Array.Clear(p, 0x08, 0x14);
        Array.Clear(p, 0x1C, 0x14);
        var seed = SHA1.HashData(p);
        seed.CopyTo(p, 0x08);
        Hmac(p, seed).CopyTo(p, 0x1C);
    }

    /// <summary>PS1 save as signed PS3 PSV (layout as written by the PS3 / MemcardRex).</summary>
    public static byte[] MakePs1Psv(Ps1Save s)
    {
        var p = new byte[0x84 + s.Data.Length];
        p[1] = (byte)'V'; p[2] = (byte)'S'; p[3] = (byte)'P';
        Bin.W32(p, 0x38, 0x14);
        Bin.W32(p, 0x3C, 1);
        Bin.W32(p, 0x40, (uint)s.Data.Length);
        Bin.W32(p, 0x44, 0x84);
        Bin.W32(p, 0x48, 0x200);
        Bin.W32(p, 0x5C, (uint)s.Data.Length);
        Bin.W16(p, 0x60, 0x9003);
        Bin.WStr(p.AsSpan(0x64, 20), s.FileName);
        s.Data.CopyTo(p, 0x84);
        SignPsv(p);
        return p;
    }

    /// <summary>PS2 save as signed PS3 PSV.</summary>
    public static byte[] MakePs2Psv(Ps2Save s)
    {
        int n = s.Files.Count;
        int headerEnd = 0xA0 + n * 60;
        long total = s.Files.Sum(f => (long)f.Data.Length);
        var p = new byte[headerEnd + total];
        p[1] = (byte)'V'; p[2] = (byte)'S'; p[3] = (byte)'P';
        Bin.W32(p, 0x38, 0x2C);
        Bin.W32(p, 0x3C, 2);
        Bin.W32(p, 0x40, (uint)total);

        var sys = s.IconSys;
        int pos = headerEnd;
        var positions = new int[n];
        for (int i = 0; i < n; i++) { positions[i] = pos; pos += s.Files[i].Data.Length; }
        int Find(string name)
        {
            for (int i = 0; i < n; i++) if (s.Files[i].Name == name) return i;
            return -1;
        }
        void Ref(int off, string? name)
        {
            int i = name == null ? -1 : Find(name);
            if (i < 0) return;
            Bin.W32(p, off, (uint)positions[i]);
            Bin.W32(p, off + 4, (uint)s.Files[i].Data.Length);
        }
        Ref(0x44, s.Find("icon.sys")?.Name);
        Ref(0x4C, sys?.IconNormal);
        Ref(0x54, sys?.IconCopy);
        Ref(0x5C, sys?.IconDelete);
        Bin.W32(p, 0x64, (uint)n);
        // directory info
        s.Created.OrNow().Write(p, 0x68);
        s.Modified.OrNow().Write(p, 0x70);
        Bin.W32(p, 0x78, (uint)(n + 2));
        Bin.W32(p, 0x7C, (uint)((s.Mode & ~Ps2DirEntry.DfFile) | Ps2DirEntry.DfDir | Ps2DirEntry.DfExists));
        Bin.WStr(p.AsSpan(0x80, 32), s.DirName);
        for (int i = 0; i < n; i++)
        {
            var f = s.Files[i];
            int o = 0xA0 + i * 60;
            f.Created.OrNow().Write(p, o);
            f.Modified.OrNow().Write(p, o + 8);
            Bin.W32(p, o + 16, (uint)f.Data.Length);
            Bin.W32(p, o + 20, (uint)((f.Mode & ~Ps2DirEntry.DfDir) | Ps2DirEntry.DfFile | Ps2DirEntry.DfExists));
            Bin.WStr(p.AsSpan(o + 24, 32), f.Name);
            Bin.W32(p, o + 56, (uint)positions[i]);
            f.Data.CopyTo(p, positions[i]);
        }
        SignPsv(p);
        return p;
    }
}
