namespace MemcardManager.Core;

public enum Platform { PS1, PS2 }

/// <summary>PS2 memory card directory entry (512 bytes).</summary>
public sealed class Ps2DirEntry
{
    public const int Size = 512;
    public const ushort DfRead = 0x0001, DfWrite = 0x0002, DfExec = 0x0004, DfProtected = 0x0008, DfFile = 0x0010,
        DfDir = 0x0020, Df0400 = 0x0400, DfPocketStation = 0x0800, DfPsx = 0x1000, DfHidden = 0x2000, DfExists = 0x8000;
    public const ushort DefaultDirMode = 0x8427, DefaultFileMode = 0x8497;

    public ushort Mode;
    public ushort Unused;
    public uint Length;
    public Ps2Tod Created;
    public uint Cluster;
    public uint ParentEntry;
    public Ps2Tod Modified;
    public uint Attr;
    public string Name = "";

    public bool Exists => (Mode & DfExists) != 0;
    public bool IsDir => (Mode & (DfFile | DfDir | DfExists)) == (DfDir | DfExists);
    public bool IsFile => (Mode & (DfFile | DfDir | DfExists)) == (DfFile | DfExists);

    public static Ps2DirEntry Parse(ReadOnlySpan<byte> b) => new()
    {
        Mode = Bin.U16(b, 0),
        Unused = Bin.U16(b, 2),
        Length = Bin.U32(b, 4),
        Created = Ps2Tod.Read(b, 8),
        Cluster = Bin.U32(b, 16),
        ParentEntry = Bin.U32(b, 20),
        Modified = Ps2Tod.Read(b, 24),
        Attr = Bin.U32(b, 32),
        Name = Bin.ZStr(b.Slice(64, 448))
    };

    public void Write(Span<byte> b)
    {
        b[..Size].Clear();
        Bin.W16(b, 0, Mode);
        Bin.W16(b, 2, Unused);
        Bin.W32(b, 4, Length);
        Created.Write(b, 8);
        Bin.W32(b, 16, Cluster);
        Bin.W32(b, 20, ParentEntry);
        Modified.Write(b, 24);
        Bin.W32(b, 32, Attr);
        Bin.WStr(b.Slice(64, 32), Name);
    }

    public byte[] ToBytes()
    {
        var b = new byte[Size];
        Write(b);
        return b;
    }
}

public sealed class Ps2SaveFile
{
    public string Name = "";
    public ushort Mode = Ps2DirEntry.DefaultFileMode;
    public Ps2Tod Created = Ps2Tod.Now;
    public Ps2Tod Modified = Ps2Tod.Now;
    public uint Attr;
    public byte[] Data = Array.Empty<byte>();

    public Ps2SaveFile Clone() => new()
    {
        Name = Name, Mode = Mode, Created = Created, Modified = Modified, Attr = Attr, Data = (byte[])Data.Clone()
    };
}

/// <summary>A complete PS2 save (one directory on the card with its files).</summary>
public sealed class Ps2Save
{
    public string DirName = "";
    public ushort Mode = Ps2DirEntry.DefaultDirMode;
    public Ps2Tod Created = Ps2Tod.Now;
    public Ps2Tod Modified = Ps2Tod.Now;
    public uint Attr;
    public List<Ps2SaveFile> Files = new();
    public List<string> Warnings = new();

    public Ps2SaveFile? Find(string name) =>
        Files.FirstOrDefault(f => f.Name == name) ??
        Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    IconSys? _iconSys;
    bool _iconSysParsed;

    public IconSys? IconSys
    {
        get
        {
            if (!_iconSysParsed)
            {
                _iconSysParsed = true;
                var f = Find("icon.sys");
                if (f != null) _iconSys = IconSys.Parse(f.Data);
            }
            return _iconSys;
        }
    }

    public long DataSize => Files.Sum(f => (long)f.Data.Length);

    /// <summary>Card clusters needed: directory clusters plus file clusters.</summary>
    public int ClustersNeeded(int clusterSize = 1024)
    {
        int n = Bin.DivUp(Files.Count + 2, clusterSize / Ps2DirEntry.Size);
        foreach (var f in Files) n += Bin.DivUp(f.Data.Length, clusterSize);
        return n;
    }

    public string Title
    {
        get
        {
            var s = IconSys;
            if (s == null) return DirName;
            var (a, b) = s.Title();
            string t = (a.TrimEnd() + " " + b.Trim()).Trim();
            return t.Length == 0 ? DirName : t;
        }
    }

    public Ps2Save Clone()
    {
        var s = new Ps2Save { DirName = DirName, Mode = Mode, Created = Created, Modified = Modified, Attr = Attr };
        s.Files.AddRange(Files.Select(f => f.Clone()));
        return s;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DirName)) throw new InvalidDataException("Savegame hat keinen Verzeichnisnamen.");
        if (DirName.Length > 31) throw new InvalidDataException($"Verzeichnisname zu lang: {DirName}");
        if (DirName.Contains('/') || DirName == "." || DirName == "..") throw new InvalidDataException($"Ungültiger Verzeichnisname: {DirName}");
        var names = new HashSet<string>();
        foreach (var f in Files)
        {
            if (string.IsNullOrEmpty(f.Name) || f.Name.Length > 31 || f.Name.Contains('/'))
                throw new InvalidDataException($"Ungültiger Dateiname im Savegame: '{f.Name}'");
            if (!names.Add(f.Name)) throw new InvalidDataException($"Doppelter Dateiname im Savegame: {f.Name}");
        }
    }
}

/// <summary>PS2 icon.sys (964 bytes).</summary>
public sealed class IconSys
{
    public const int Size = 964;
    public ushort TitleOffset;
    public uint BgTransparency;
    public int[] BgColors = new int[16];
    public float[][] LightDir = new float[3][];
    public float[][] LightColor = new float[3][];
    public float[] Ambient = new float[4];
    public byte[] TitleBytes = new byte[68];
    public string IconNormal = "", IconCopy = "", IconDelete = "";

    public static IconSys? Parse(byte[] d)
    {
        if (d.Length < Size || !Bin.StartsWith(d, "PS2D")) return null;
        var s = new IconSys { TitleOffset = Bin.U16(d, 6), BgTransparency = Bin.U32(d, 12) };
        for (int i = 0; i < 16; i++) s.BgColors[i] = (int)Bin.U32(d, 16 + i * 4);
        for (int l = 0; l < 3; l++)
        {
            s.LightDir[l] = new float[4];
            s.LightColor[l] = new float[4];
            for (int k = 0; k < 4; k++)
            {
                s.LightDir[l][k] = Bin.F32(d, 80 + l * 16 + k * 4);
                s.LightColor[l][k] = Bin.F32(d, 128 + l * 16 + k * 4);
            }
        }
        for (int k = 0; k < 4; k++) s.Ambient[k] = Bin.F32(d, 176 + k * 4);
        Array.Copy(d, 192, s.TitleBytes, 0, 68);
        s.IconNormal = Bin.ZStr(d.AsSpan(260, 64));
        s.IconCopy = Bin.ZStr(d.AsSpan(324, 64));
        s.IconDelete = Bin.ZStr(d.AsSpan(388, 64));
        return s;
    }

    public (string, string) Title()
    {
        var tb = TitleBytes.AsSpan();
        int end = tb.IndexOf((byte)0);
        if (end < 0) end = tb.Length;
        int split = Math.Min(TitleOffset, end);
        return (TextUtil.DecodeSjis(tb[..split]), TextUtil.DecodeSjis(tb[split..end]));
    }
}

/// <summary>A PS1 save: the 21 byte file name plus the raw block data (n * 8 KB).</summary>
public sealed class Ps1Save
{
    public const int BlockSize = 8192;
    public string FileName = "";
    public byte[] Data = Array.Empty<byte>();
    public int Blocks => Bin.DivUp(Data.Length, BlockSize);

    public string Title
    {
        get
        {
            if (Data.Length < 0x44 || Data[0] != (byte)'S' || Data[1] != (byte)'C') return FileName;
            string t = TextUtil.DecodeSjis(Data.AsSpan(4, 64)).Trim();
            return t.Length == 0 ? FileName : t;
        }
    }

    public Ps1Save Clone() => new() { FileName = FileName, Data = (byte[])Data.Clone() };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FileName) || FileName.Length > 20)
            throw new InvalidDataException($"Ungültiger PS1-Dateiname: '{FileName}'");
        if (Data.Length == 0 || Data.Length % BlockSize != 0)
            throw new InvalidDataException("PS1-Savegame-Daten haben keine gültige Blockgröße (8 KB).");
        if (Blocks > 15) throw new InvalidDataException("PS1-Savegame ist größer als eine Memory Card (15 Blöcke).");
    }
}

/// <summary>A save of either platform.</summary>
public sealed class SaveGame
{
    public Ps2Save? Ps2 { get; init; }
    public Ps1Save? Ps1 { get; init; }
    public Platform Platform => Ps2 != null ? Platform.PS2 : Platform.PS1;
    public string Name => Ps2?.DirName ?? Ps1!.FileName;
    public string Title => Ps2?.Title ?? Ps1!.Title;
    public long Size => Ps2?.DataSize ?? Ps1!.Data.Length;
    public Region Region => RegionUtil.FromName(Name);

    public SaveGame Clone() => Ps2 != null ? new SaveGame { Ps2 = Ps2.Clone() } : new SaveGame { Ps1 = Ps1!.Clone() };

    /// <summary>
    /// Copy of this save under a new name. For PS2 saves, files inside that carry the old directory
    /// name (very common: "BESLES-53703kk/BESLES-53703kk") can be renamed along.
    /// </summary>
    public SaveGame Renamed(string newName, bool renameMatchingFiles)
    {
        var c = Clone();
        if (c.Ps2 != null)
        {
            string old = c.Ps2.DirName;
            c.Ps2.DirName = newName;
            if (renameMatchingFiles)
                foreach (var f in c.Ps2.Files.Where(f => f.Name == old)) f.Name = newName;
            c.Ps2.Validate();
        }
        else
        {
            c.Ps1!.FileName = newName;
            c.Ps1.Validate();
        }
        return c;
    }

    /// <summary>Replaces the region letter of a "B?SLxS-…" name (A = USA, E = Europe, I = Japan …).</summary>
    public static string WithRegionPrefix(string name, char region) =>
        name.Length >= 2 && name[0] == 'B' ? name[0] + region.ToString() + name[2..] : name;
}
