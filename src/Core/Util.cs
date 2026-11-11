using System.Buffers.Binary;
using System.Text;

namespace MemcardManager.Core;

/// <summary>Little-endian binary helpers.</summary>
internal static class Bin
{
    public static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o, 2));
    public static short S16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt16LittleEndian(b.Slice(o, 2));
    public static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o, 4));
    public static float F32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.Slice(o, 4));
    public static void W16(Span<byte> b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(o, 2), v);
    public static void W32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(o, 4), v);

    /// <summary>Zero terminated byte string, decoded byte-preserving (Latin1).</summary>
    public static string ZStr(ReadOnlySpan<byte> b)
    {
        int i = b.IndexOf((byte)0);
        if (i >= 0) b = b[..i];
        return Encoding.Latin1.GetString(b);
    }

    /// <summary>Writes a string into a fixed field (zero padded, truncated).</summary>
    public static void WStr(Span<byte> field, string s)
    {
        field.Clear();
        var bytes = Encoding.Latin1.GetBytes(s);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
    }

    public static bool StartsWith(ReadOnlySpan<byte> b, string ascii)
    {
        if (b.Length < ascii.Length) return false;
        for (int i = 0; i < ascii.Length; i++)
            if (b[i] != (byte)ascii[i]) return false;
        return true;
    }

    public static int DivUp(int a, int b) => (a + b - 1) / b;
    public static long DivUp(long a, long b) => (a + b - 1) / b;
}

internal static class TextUtil
{
    public static readonly Encoding Sjis;

    static TextUtil()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Sjis = Encoding.GetEncoding(932, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>Decodes Shift-JIS text (PS1/PS2 titles), normalises full-width characters.</summary>
    public static string DecodeSjis(ReadOnlySpan<byte> b)
    {
        int i = b.IndexOf((byte)0);
        if (i >= 0) b = b[..i];
        string s = Sjis.GetString(b);
        s = s.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString();
    }

    public static byte[] EncodeSjis(string s) => Sjis.GetBytes(s);

    /// <summary>Makes a string usable as a Windows file name.</summary>
    public static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(invalid.Contains(c) || c < 32 ? '_' : c);
        string r = sb.ToString().Trim().TrimEnd('.');
        while (r.Contains("  ")) r = r.Replace("  ", " ");
        return r.Length == 0 ? "save" : (r.Length > 120 ? r[..120] : r);
    }
}

internal static class Crc32
{
    static readonly uint[] Table = MakeTable();

    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data, uint prev = 0)
    {
        uint crc = prev ^ 0xFFFFFFFFu;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}

/// <summary>PS2 time-of-day structure (stored in JST, UTC+9).</summary>
public struct Ps2Tod
{
    public byte Sec, Min, Hour, Day, Month;
    public ushort Year;

    public static Ps2Tod Read(ReadOnlySpan<byte> b, int o) => new()
    {
        Sec = b[o + 1], Min = b[o + 2], Hour = b[o + 3], Day = b[o + 4], Month = b[o + 5], Year = Bin.U16(b, o + 6)
    };

    public readonly void Write(Span<byte> b, int o)
    {
        b[o] = 0; b[o + 1] = Sec; b[o + 2] = Min; b[o + 3] = Hour; b[o + 4] = Day; b[o + 5] = Month;
        Bin.W16(b, o + 6, Year);
    }

    public readonly bool IsValid =>
        Year >= 1980 && Year <= 2200 && Month >= 1 && Month <= 12 && Day >= 1 && Day <= 31 && Hour < 24 && Min < 60 && Sec < 60;

    public readonly DateTime? ToLocal()
    {
        if (!IsValid) return null;
        try
        {
            var jst = new DateTime(Year, Month, Math.Min((int)Day, DateTime.DaysInMonth(Year, Month)), Hour, Min, Sec, DateTimeKind.Utc);
            return jst.AddHours(-9).ToLocalTime();
        }
        catch { return null; }
    }

    public static Ps2Tod FromDateTime(DateTime dt)
    {
        var j = dt.ToUniversalTime().AddHours(9);
        return new Ps2Tod
        {
            Sec = (byte)j.Second, Min = (byte)j.Minute, Hour = (byte)j.Hour, Day = (byte)j.Day, Month = (byte)j.Month,
            Year = (ushort)j.Year
        };
    }

    public static Ps2Tod Now => FromDateTime(DateTime.Now);

    public readonly Ps2Tod OrNow() => IsValid ? this : Now;
}

public enum Region { Unbekannt, USA, Europa, Japan, Korea, Asien, China }

public static class RegionUtil
{
    /// <summary>Derives the region from a save directory/file name like "BASLUS-20919...".</summary>
    public static Region FromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return Region.Unbekannt;
        string up = name.ToUpperInvariant();
        string code = up.Length >= 6 && up[0] == 'B' ? up.Substring(2, 4) : (up.Length >= 4 ? up[..4] : up);
        switch (code)
        {
            case "SLUS": case "SCUS": case "LSP0":
                return Region.USA;
            case "SLES": case "SCES": case "SCED": case "SLED":
                return Region.Europa;
            case "SLPS": case "SLPM": case "SCPS": case "SCPM": case "SIPS": case "PBPX": case "PCPX": case "PAPX": case "SLAJ":
                return Region.Japan;
            case "SCAJ":
                return Region.Asien;
            case "SLKA": case "SCKA":
                return Region.Korea;
            case "SCCS": case "SLCS":
                return Region.China;
        }
        if (up.Length >= 2 && up[0] == 'B')
        {
            return up[1] switch
            {
                'A' => Region.USA,
                'E' => Region.Europa,
                'I' => Region.Japan,
                'K' => Region.Korea,
                'H' => Region.Asien,
                'C' => Region.China,
                _ => Region.Unbekannt
            };
        }
        return Region.Unbekannt;
    }

    public static string Display(Region r) => r switch
    {
        Region.USA => "NTSC-U (USA)",
        Region.Europa => "PAL (Europa)",
        Region.Japan => "NTSC-J (Japan)",
        Region.Korea => "Korea",
        Region.Asien => "Asien",
        Region.China => "China",
        _ => "unbekannt"
    };

    /// <summary>Product code "SLUS-20919" extracted from a save name, or empty.</summary>
    public static string ProductCode(string name)
    {
        if (name.Length >= 12 && name[0] == 'B') return name.Substring(2, 10);
        return "";
    }
}
