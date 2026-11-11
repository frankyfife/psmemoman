namespace MemcardManager.Core;

public enum Ps1Container
{
    Raw,        // .mcr .mcd .mc .srm .bin .ps .vm1 (PS3) .ddf .mci .psm ...
    DexDrive,   // .gme (3904 byte header)
    Vgs,        // .vgs / .mem (Connectix VGS, 64 byte header)
    Vmp,        // .vmp (PSP / PS Vita, 0x80 byte header, signature recomputed on save)
    Generic     // unknown header preserved as-is
}

/// <summary>PlayStation 1 memory card (128 KB, 15 blocks) in any common container.</summary>
public sealed class Ps1Card : MemoryCard
{
    public const int CardSize = 131072, BlockSize = 8192, FrameSize = 128;
    const int GmeHeaderSize = 3904, VgsHeaderSize = 64, VmpHeaderSize = 0x80;

    byte[] _d;
    readonly byte[] _header;
    public Ps1Container Container { get; }
    List<CardEntry> _entries = new();
    readonly string _kindName;

    public override Platform Platform => Platform.PS1;
    public override string FormatName => $"{_kindName} – PS1, 128 KB (15 Blöcke)";
    public override IReadOnlyList<CardEntry> Entries => _entries;
    public override long TotalBytes => 15L * BlockSize;
    public override long FreeBytes => FreeSlots().Count * (long)BlockSize;

    /// <summary>Detects a PS1 card in the data; returns the container type and data offset.</summary>
    public static bool TryDetect(byte[] f, out Ps1Container c, out int offset)
    {
        c = Ps1Container.Raw;
        offset = 0;
        if (f.Length >= CardSize && Bin.StartsWith(f, "MC") && f.Length < CardSize + 64) { c = Ps1Container.Raw; return true; }
        if (f.Length >= GmeHeaderSize + CardSize && Bin.StartsWith(f, "123-456-STD")) { c = Ps1Container.DexDrive; offset = GmeHeaderSize; return true; }
        if (f.Length >= VgsHeaderSize + CardSize && Bin.StartsWith(f, "VgsM")) { c = Ps1Container.Vgs; offset = VgsHeaderSize; return true; }
        if (f.Length >= VmpHeaderSize + CardSize && f[0] == 0 && f[1] == (byte)'P' && f[2] == (byte)'M' && f[3] == (byte)'V')
        { c = Ps1Container.Vmp; offset = VmpHeaderSize; return true; }
        if (f.Length > CardSize && f.Length - CardSize <= 0x10000)
        {
            int o = f.Length - CardSize;
            if (f[o] == (byte)'M' && f[o + 1] == (byte)'C') { c = Ps1Container.Generic; offset = o; return true; }
        }
        if (f.Length == CardSize && LooksLikeDirectory(f)) { c = Ps1Container.Raw; return true; }
        return false;
    }

    static bool LooksLikeDirectory(byte[] d)
    {
        // card without "MC" magic (some emulators) but with plausible directory frames
        for (int i = 1; i < 16; i++)
        {
            byte s = d[i * FrameSize];
            if (s != 0xA0 && s != 0xA1 && s != 0xA2 && s != 0xA3 && s != 0x51 && s != 0x52 && s != 0x53) return false;
        }
        return true;
    }

    public Ps1Card(byte[] file, string path, string kindName)
    {
        FilePath = path;
        _kindName = kindName;
        if (!TryDetect(file, out var c, out int off)) throw new InvalidDataException("Keine PS1-Memory-Card erkannt.");
        Container = c;
        _header = file.AsSpan(0, off).ToArray();
        _d = file.AsSpan(off, CardSize).ToArray();
        if (c == Ps1Container.Vmp && !PsSign.VerifyVmp(file))
            Warnings.Add("VMP-Signatur ist ungültig – sie wird beim Speichern neu berechnet.");
        if (!Bin.StartsWith(_d, "MC")) Warnings.Add("Kartenkopf ('MC') fehlt – Karte evtl. unformatiert, wird beim Speichern ergänzt.");
        Reload();
    }

    public static byte[] FormatRaw()
    {
        var d = new byte[CardSize];
        d[0] = (byte)'M'; d[1] = (byte)'C';
        Checksum(d, 0);
        for (int i = 1; i < 16; i++)
        {
            int o = i * FrameSize;
            d[o] = 0xA0;
            d[o + 8] = 0xFF; d[o + 9] = 0xFF;
            Checksum(d, o);
        }
        for (int i = 16; i < 36; i++)
        {
            int o = i * FrameSize;
            Bin.W32(d, o, 0xFFFFFFFF);
            d[o + 8] = 0xFF; d[o + 9] = 0xFF;
            Checksum(d, o);
        }
        Array.Copy(d, 0, d, 63 * FrameSize, FrameSize); // write test frame
        return d;
    }

    static void Checksum(byte[] d, int frameOff)
    {
        byte x = 0;
        for (int i = 0; i < 127; i++) x ^= d[frameOff + i];
        d[frameOff + 127] = x;
    }

    int State(int slot) => _d[(slot + 1) * FrameSize];
    int Next(int slot) => Bin.U16(_d, (slot + 1) * FrameSize + 8);

    void Reload()
    {
        var list = new List<CardEntry>();
        for (int s = 0; s < 15; s++)
        {
            if (State(s) != 0x51) continue;
            int fo = (s + 1) * FrameSize;
            string name = Bin.ZStr(_d.AsSpan(fo + 0x0A, 21));
            var blocks = new List<int> { s };
            int cur = s, guard = 0;
            while (guard++ < 15)
            {
                int n = Next(cur);
                if (n == 0xFFFF || n > 14 || blocks.Contains(n)) break;
                if (State(n) != 0x52 && State(n) != 0x53) { Warnings.Add($"{name}: Blockkette ist beschädigt."); break; }
                blocks.Add(n);
                cur = n;
            }
            var data = new byte[blocks.Count * BlockSize];
            for (int i = 0; i < blocks.Count; i++) Array.Copy(_d, (blocks[i] + 1) * BlockSize, data, i * BlockSize, BlockSize);
            var save = new Ps1Save { FileName = name, Data = data };
            list.Add(new CardEntry { Save = new SaveGame { Ps1 = save }, Size = data.Length, Blocks = blocks.Count, Tag = blocks.ToArray() });
        }
        _entries = list;
    }

    List<int> FreeSlots()
    {
        // pristine free blocks first, deleted ones afterwards (keeps deleted saves recoverable as long as possible)
        var free = new List<int>();
        for (int s = 0; s < 15; s++) if (State(s) == 0xA0) free.Add(s);
        for (int s = 0; s < 15; s++) if (State(s) is 0xA1 or 0xA2 or 0xA3) free.Add(s);
        // also count frames with garbage state as unusable
        return free;
    }

    public override bool Exists(string name) => _entries.Any(e => e.Name == name);

    public override long SpaceNeeded(SaveGame save) =>
        save.Ps1 == null ? long.MaxValue : (long)save.Ps1.Blocks * BlockSize;

    public override void Delete(CardEntry entry)
    {
        if (entry.Tag is not int[] blocks) throw new InvalidOperationException();
        foreach (int s in blocks)
        {
            int o = (s + 1) * FrameSize;
            _d[o] = _d[o] switch { 0x51 => 0xA1, 0x52 => 0xA2, 0x53 => 0xA3, var x => x };
            Checksum(_d, o);
        }
        MarkModified();
        Reload();
    }

    public override void Import(SaveGame save)
    {
        var s = save.Ps1 ?? throw new InvalidOperationException("PS2-Savegames können nicht auf eine PS1-Karte kopiert werden.");
        s.Validate();
        if (Exists(s.FileName)) throw new InvalidOperationException($"Savegame '{s.FileName}' existiert bereits auf der Karte.");
        var free = FreeSlots();
        int n = s.Blocks;
        if (n > free.Count) throw new CardFullException($"Nicht genug Platz: benötigt {n} Blöcke, frei {free.Count}.");
        var use = free.Take(n).OrderBy(x => x).ToList();
        for (int i = 0; i < n; i++)
        {
            int slot = use[i];
            int o = (slot + 1) * FrameSize;
            Array.Clear(_d, o, FrameSize);
            _d[o] = (byte)(n == 1 || i == 0 ? 0x51 : i == n - 1 ? 0x53 : 0x52);
            if (i == 0)
            {
                Bin.W32(_d, o + 4, (uint)(n * BlockSize));
                Bin.WStr(_d.AsSpan(o + 0x0A, 20), s.FileName);
            }
            Bin.W16(_d, o + 8, i == n - 1 ? (ushort)0xFFFF : (ushort)use[i + 1]);
            Checksum(_d, o);
            Array.Copy(s.Data, i * BlockSize, _d, (slot + 1) * BlockSize, BlockSize);
        }
        if (!Bin.StartsWith(_d, "MC")) { _d[0] = (byte)'M'; _d[1] = (byte)'C'; Checksum(_d, 0); }
        MarkModified();
        Reload();
    }

    /// <summary>Raw 128 KB card data.</summary>
    public byte[] RawData => (byte[])_d.Clone();

    /// <summary>Serialises the card in the given container format.</summary>
    public byte[] Build(Ps1Container target)
    {
        byte[] header = target switch
        {
            Ps1Container.Raw => Array.Empty<byte>(),
            Ps1Container.DexDrive => BuildGmeHeader(Container == Ps1Container.DexDrive ? _header : null),
            Ps1Container.Vgs => Container == Ps1Container.Vgs ? _header : BuildVgsHeader(),
            Ps1Container.Generic => _header,
            _ => Array.Empty<byte>()
        };
        if (target == Ps1Container.Vmp) return PsSign.MakeVmp(_d);
        var res = new byte[header.Length + CardSize];
        header.CopyTo(res, 0);
        _d.CopyTo(res, header.Length);
        return res;
    }

    byte[] BuildGmeHeader(byte[]? old)
    {
        var h = new byte[GmeHeaderSize];
        if (old != null && old.Length == GmeHeaderSize) old.CopyTo(h, 0);
        Bin.WStr(h.AsSpan(0, 11), "123-456-STD");
        h[18] = 0x01; h[20] = 0x01; h[21] = (byte)'M';
        for (int s = 0; s < 15; s++)
        {
            h[22 + s] = _d[(s + 1) * FrameSize];
            h[38 + s] = _d[(s + 1) * FrameSize + 8];
        }
        return h;
    }

    static byte[] BuildVgsHeader()
    {
        var h = new byte[VgsHeaderSize];
        Bin.WStr(h.AsSpan(0, 4), "VgsM");
        h[4] = 1; h[8] = 1; h[12] = 1; h[17] = 2;
        return h;
    }

    public override void Commit(string path)
    {
        var bytes = Build(Container);
        CardIO.SafeWrite(path, bytes, b => { _ = new Ps1Card(b, path, _kindName); });
        FilePath = path;
        MarkSaved();
    }
}
