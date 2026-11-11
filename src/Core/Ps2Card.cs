namespace MemcardManager.Core;

/// <summary>
/// PS2 memory card image (PCSX2 .ps2, PS3 .vm2, OPL .vmc, MemCard PRO2/SD2PSX .mc2, raw .bin ...),
/// with or without ECC spare area, any size (8/16/32/64 MB...). Works fully in memory.
/// </summary>
public sealed class Ps2Card : MemoryCard
{
    public const string Magic = "Sony PS2 Memory Card Format ";
    const uint ChainEnd = 0xFFFFFFFF, AllocBit = 0x80000000, ClusterMask = 0x7FFFFFFF, FreeEnd = 0x7FFFFFFF;

    byte[] _img;
    int _pageSize, _ppc, _ppeb, _clusterSize, _spare, _rawPage;
    uint _clustersPerCard, _allocOffset, _allocEnd, _rootCluster, _gb1, _gb2;
    readonly uint[] _ifc = new uint[32];
    uint[] _fat = Array.Empty<uint>();
    uint[] _fatAbs = Array.Empty<uint>();
    bool[] _fatDirty = Array.Empty<bool>();
    int _allocLimit, _allocCursor;
    int _eccCorrected;
    List<CardEntry> _entries = new();
    readonly string _kind;

    public bool HasEcc { get; private set; }
    public int ClusterSize => _clusterSize;
    public override Platform Platform => Platform.PS2;
    public override string FormatName =>
        $"{_kind} – PS2, {(long)_clustersPerCard * _clusterSize / (1024 * 1024)} MB, {(HasEcc ? "mit ECC" : "ohne ECC")}";
    public override IReadOnlyList<CardEntry> Entries => _entries;
    public override long TotalBytes => (long)_allocLimit * _clusterSize;
    public override long FreeBytes => (long)CountFree() * _clusterSize;

    public static bool IsPs2Image(ReadOnlySpan<byte> d) => d.Length >= 0x154 && Bin.StartsWith(d, Magic);

    public Ps2Card(byte[] img, string path, string kind)
    {
        FilePath = path;
        _kind = kind;
        _img = img;
        if (!IsPs2Image(img)) throw new InvalidDataException("Keine PS2-Memory-Card (Superblock fehlt). Evtl. ist die Karte unformatiert.");
        var sb = img.AsSpan();
        _pageSize = Bin.U16(sb, 40);
        _ppc = Bin.U16(sb, 42);
        _ppeb = Bin.U16(sb, 44);
        _clustersPerCard = Bin.U32(sb, 48);
        _allocOffset = Bin.U32(sb, 52);
        _allocEnd = Bin.U32(sb, 56);
        _rootCluster = Bin.U32(sb, 60);
        _gb1 = Bin.U32(sb, 64);
        _gb2 = Bin.U32(sb, 68);
        for (int i = 0; i < 32; i++) _ifc[i] = Bin.U32(sb, 80 + i * 4);
        if (_pageSize < 512 || _pageSize > 8192 || _ppc < 1 || _ppc > 16 || _clustersPerCard == 0 || _clustersPerCard > 0x100000)
            throw new InvalidDataException("Superblock der PS2-Karte ist ungültig.");
        _clusterSize = _pageSize * _ppc;
        _spare = Bin.DivUp(_pageSize, 128) * 4;
        long pages = (long)_clustersPerCard * _ppc;
        if (img.Length >= pages * (_pageSize + _spare)) HasEcc = true;
        else if (img.Length >= pages * _pageSize) HasEcc = false;
        else
        {
            // Truncated image: decide by checking the ECC of the superblock page.
            HasEcc = false;
            if (img.Length >= _pageSize + _spare)
            {
                var page = img.AsSpan(0, _pageSize).ToArray();
                bool ok = true;
                for (int c = 0; c < _pageSize / 128 && ok; c++)
                    ok = Ecc.Check(page.AsSpan(c * 128, 128), img.AsSpan(_pageSize + c * 3, 3)) == 0;
                HasEcc = ok;
            }
            Warnings.Add("Kartenabbild ist kürzer als im Superblock angegeben.");
        }
        _rawPage = _pageSize + (HasEcc ? _spare : 0);

        int limit = (int)((long)Math.Min(_gb1, _gb2) * _ppeb / _ppc - _allocOffset);
        _allocLimit = (int)_allocEnd;
        if (limit > 0 && limit < _allocLimit) _allocLimit = limit;
        LoadFat();
        Reload();
        if (_eccCorrected > 0) Warnings.Add($"{_eccCorrected} Bitfehler per ECC korrigiert (nur beim Lesen).");
    }

    // ------------------------------------------------------------------ low level

    byte[] ReadCluster(uint abs)
    {
        var buf = new byte[_clusterSize];
        for (int p = 0; p < _ppc; p++)
        {
            long page = (long)abs * _ppc + p;
            long off = page * _rawPage;
            if (off + _pageSize > _img.Length) continue;
            var dst = buf.AsSpan(p * _pageSize, _pageSize);
            _img.AsSpan((int)off, _pageSize).CopyTo(dst);
            if (HasEcc && off + _rawPage <= _img.Length)
            {
                var spare = _img.AsSpan((int)off + _pageSize, _spare);
                for (int c = 0; c < _pageSize / 128; c++)
                {
                    var chunk = dst.Slice(c * 128, 128);
                    var saved = chunk.ToArray();
                    int r = Ecc.Check(chunk, spare.Slice(c * 3, 3));
                    if (r == 1 && !chunk.SequenceEqual(saved)) _eccCorrected++;
                    else if (r == 2) saved.CopyTo(chunk); // uncorrectable (e.g. erased page): use raw data
                }
            }
        }
        return buf;
    }

    void WriteCluster(uint abs, ReadOnlySpan<byte> data)
    {
        for (int p = 0; p < _ppc; p++)
        {
            long page = (long)abs * _ppc + p;
            long off = page * _rawPage;
            if (off + _rawPage > _img.Length) throw new IOException("Schreibversuch außerhalb des Kartenabbilds.");
            var src = data.Slice(p * _pageSize, _pageSize);
            src.CopyTo(_img.AsSpan((int)off, _pageSize));
            if (HasEcc)
            {
                var spare = _img.AsSpan((int)off + _pageSize, _spare);
                spare.Clear();
                Ecc.CalculatePage(src, spare);
            }
        }
    }

    byte[] ReadAlloc(uint n) => ReadCluster(n + _allocOffset);
    void WriteAlloc(uint n, ReadOnlySpan<byte> d) => WriteCluster(n + _allocOffset, d);

    void LoadFat()
    {
        int epc = _clusterSize / 4;
        int fatClusters = Bin.DivUp((int)_allocEnd, epc);
        if (fatClusters > 32 * epc) throw new InvalidDataException("FAT der PS2-Karte ist ungültig.");
        _fat = new uint[fatClusters * epc];
        _fatAbs = new uint[fatClusters];
        _fatDirty = new bool[fatClusters];
        var indCache = new Dictionary<uint, byte[]>();
        for (int k = 0; k < fatClusters; k++)
        {
            uint ind = _ifc[k / epc];
            if (ind >= _clustersPerCard) throw new InvalidDataException("Indirekte FAT der PS2-Karte ist ungültig.");
            if (!indCache.TryGetValue(ind, out var indData)) indCache[ind] = indData = ReadCluster(ind);
            uint abs = Bin.U32(indData, (k % epc) * 4);
            if (abs >= _clustersPerCard) throw new InvalidDataException("FAT-Verweis der PS2-Karte ist ungültig.");
            _fatAbs[k] = abs;
            var data = ReadCluster(abs);
            for (int j = 0; j < epc; j++) _fat[k * epc + j] = Bin.U32(data, j * 4);
        }
    }

    void SetFat(uint n, uint v)
    {
        _fat[n] = v;
        _fatDirty[n / (_clusterSize / 4)] = true;
    }

    void FlushFat()
    {
        int epc = _clusterSize / 4;
        var buf = new byte[_clusterSize];
        for (int k = 0; k < _fatAbs.Length; k++)
        {
            if (!_fatDirty[k]) continue;
            for (int j = 0; j < epc; j++) Bin.W32(buf, j * 4, _fat[k * epc + j]);
            WriteCluster(_fatAbs[k], buf);
            _fatDirty[k] = false;
        }
    }

    List<uint> Chain(uint first)
    {
        var list = new List<uint>();
        uint c = first;
        while (c != ChainEnd && c < _allocEnd && list.Count <= _allocEnd)
        {
            list.Add(c);
            uint v = _fat[c];
            if (v == ChainEnd || (v & AllocBit) == 0) break;
            c = v & ClusterMask;
        }
        return list;
    }

    byte[] ReadData(uint first, long length)
    {
        if (length <= 0) return Array.Empty<byte>();
        var res = new byte[length];
        var chain = Chain(first);
        long pos = 0;
        foreach (var c in chain)
        {
            if (pos >= length) break;
            var d = ReadAlloc(c);
            int n = (int)Math.Min(_clusterSize, length - pos);
            Array.Copy(d, 0, res, pos, n);
            pos += n;
        }
        if (pos < length) throw new InvalidDataException("Clusterkette ist kürzer als die Dateigröße (beschädigt).");
        return res;
    }

    List<Ps2DirEntry> ReadDir(uint first, uint count)
    {
        if (count > 100000) throw new InvalidDataException("Verzeichnis ist beschädigt.");
        var d = ReadData(first, (long)count * Ps2DirEntry.Size);
        var list = new List<Ps2DirEntry>((int)count);
        for (int i = 0; i < count; i++) list.Add(Ps2DirEntry.Parse(d.AsSpan(i * Ps2DirEntry.Size)));
        return list;
    }

    Ps2DirEntry RootDot() => Ps2DirEntry.Parse(ReadAlloc(_rootCluster));

    List<Ps2DirEntry> ReadRoot()
    {
        var dot = RootDot();
        if (dot.Name != "." || !dot.IsDir) throw new InvalidDataException("Hauptverzeichnis der Karte ist beschädigt.");
        return ReadDir(_rootCluster, dot.Length);
    }

    void WriteDirEntry(uint dirFirst, int index, Ps2DirEntry e)
    {
        int per = _clusterSize / Ps2DirEntry.Size;
        var chain = Chain(dirFirst);
        if (index / per >= chain.Count) throw new InvalidDataException("Verzeichniseintrag außerhalb der Clusterkette.");
        uint c = chain[index / per];
        var d = ReadAlloc(c);
        e.Write(d.AsSpan(index % per * Ps2DirEntry.Size));
        WriteAlloc(c, d);
    }

    int CountFree()
    {
        int n = 0;
        for (int i = 0; i < _allocLimit; i++) if ((_fat[i] & AllocBit) == 0) n++;
        return n;
    }

    uint Allocate()
    {
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = _allocCursor; i < _allocLimit; i++)
            {
                if ((_fat[i] & AllocBit) == 0)
                {
                    SetFat((uint)i, ChainEnd);
                    _allocCursor = i + 1;
                    return (uint)i;
                }
            }
            _allocCursor = 0;
        }
        throw new CardFullException("Nicht genug freier Speicher auf der Karte.");
    }

    uint AllocChain(int count)
    {
        uint first = Allocate(), prev = first;
        for (int i = 1; i < count; i++)
        {
            uint c = Allocate();
            SetFat(prev, c | AllocBit);
            prev = c;
        }
        return first;
    }

    void FreeChain(uint first)
    {
        foreach (var c in Chain(first)) SetFat(c, _fat[c] & ClusterMask);
        _allocCursor = 0;
    }

    // ------------------------------------------------------------------ high level

    void Reload()
    {
        var list = new List<CardEntry>();
        var root = ReadRoot();
        for (int i = 2; i < root.Count; i++)
        {
            var e = root[i];
            if (!e.Exists) continue;
            if (!e.IsDir)
            {
                Warnings.Add($"Datei im Hauptverzeichnis wird ignoriert: {e.Name}");
                continue;
            }
            try
            {
                var save = ReadSave(e);
                list.Add(new CardEntry
                {
                    Save = new SaveGame { Ps2 = save },
                    Size = (long)save.ClustersNeeded(_clusterSize) * _clusterSize,
                    Modified = e.Modified.ToLocal(),
                    Tag = i
                });
            }
            catch (Exception ex)
            {
                Warnings.Add($"Savegame '{e.Name}' ist beschädigt: {ex.Message}");
            }
        }
        _entries = list;
    }

    Ps2Save ReadSave(Ps2DirEntry de)
    {
        var s = new Ps2Save { DirName = de.Name, Mode = de.Mode, Created = de.Created, Modified = de.Modified, Attr = de.Attr };
        var ents = ReadDir(de.Cluster, de.Length);
        for (int i = 2; i < ents.Count; i++)
        {
            var e = ents[i];
            if (!e.Exists) continue;
            if (e.IsFile)
                s.Files.Add(new Ps2SaveFile
                {
                    Name = e.Name, Mode = e.Mode, Created = e.Created, Modified = e.Modified, Attr = e.Attr,
                    Data = ReadData(e.Cluster, e.Length)
                });
            else
                s.Warnings.Add($"Unterverzeichnis '{e.Name}' wird nicht unterstützt und ignoriert.");
        }
        return s;
    }

    public override bool Exists(string name) => ReadRoot().Skip(2).Any(e => e.Exists && e.Name == name);

    public override long SpaceNeeded(SaveGame save) =>
        save.Ps2 == null ? long.MaxValue : (long)save.Ps2.ClustersNeeded(_clusterSize) * _clusterSize;

    public override void Delete(CardEntry entry) => DeleteByName(entry.Name);

    void DeleteByName(string name)
    {
        var root = ReadRoot();
        int idx = -1;
        for (int i = 2; i < root.Count; i++)
            if (root[i].Exists && root[i].Name == name) { idx = i; break; }
        if (idx < 0) throw new InvalidOperationException($"Savegame '{name}' nicht gefunden.");
        FreeTree(root[idx], 0);
        var e = root[idx];
        e.Mode = (ushort)(e.Mode & ~Ps2DirEntry.DfExists);
        WriteDirEntry(_rootCluster, idx, e);
        FlushFat();
        MarkModified();
        Reload();
    }

    void FreeTree(Ps2DirEntry e, int depth)
    {
        if (e.IsDir && depth < 8)
        {
            var ents = ReadDir(e.Cluster, e.Length);
            for (int i = 2; i < ents.Count; i++)
                if (ents[i].Exists) FreeTree(ents[i], depth + 1);
            FreeChain(e.Cluster);
        }
        else if (e.Length > 0 && e.Cluster != ChainEnd)
        {
            FreeChain(e.Cluster);
        }
    }

    public override void Import(SaveGame save)
    {
        var s = save.Ps2 ?? throw new InvalidOperationException("PS1-Savegames können nicht auf eine PS2-Karte kopiert werden.");
        s.Validate();
        if (Exists(s.DirName)) throw new InvalidOperationException($"Savegame '{s.DirName}' existiert bereits auf der Karte.");

        int per = _clusterSize / Ps2DirEntry.Size;
        var root = ReadRoot();
        int slot = -1;
        for (int i = 2; i < root.Count; i++) if (!root[i].Exists) { slot = i; break; }
        int extra = 0;
        if (slot < 0)
        {
            slot = root.Count;
            if (root.Count % per == 0) extra = 1;
        }
        int need = s.ClustersNeeded(_clusterSize) + extra;
        if (need > CountFree())
            throw new CardFullException($"Nicht genug Platz für '{s.DirName}': benötigt {need} KB, frei {CountFree()} KB.");

        // Snapshot to roll back on any failure (keeps the in-memory card consistent).
        var imgBackup = (byte[])_img.Clone();
        var fatBackup = (uint[])_fat.Clone();
        var dirtyBackup = (bool[])_fatDirty.Clone();
        try
        {
            ImportCore(s, root, slot, extra, per);
            FlushFat();
        }
        catch
        {
            _img = imgBackup;
            _fat = fatBackup;
            _fatDirty = dirtyBackup;
            _allocCursor = 0;
            throw;
        }
        MarkModified();
        Reload();
    }

    void ImportCore(Ps2Save s, List<Ps2DirEntry> root, int slot, int extra, int per)
    {
        var created = s.Created.OrNow();
        var modified = s.Modified.OrNow();
        int dirLen = s.Files.Count + 2;
        int dirClusters = Bin.DivUp(dirLen, per);
        uint dirFirst = AllocChain(dirClusters);

        var dirBuf = new byte[dirClusters * _clusterSize];
        new Ps2DirEntry
        {
            Mode = Ps2DirEntry.DefaultDirMode, Length = 0, Created = created, Modified = modified,
            Cluster = _rootCluster, ParentEntry = (uint)slot, Name = "."
        }.Write(dirBuf.AsSpan(0));
        new Ps2DirEntry
        {
            Mode = Ps2DirEntry.DefaultDirMode, Length = 0, Created = created, Modified = modified,
            Cluster = 0, ParentEntry = 0, Name = ".."
        }.Write(dirBuf.AsSpan(Ps2DirEntry.Size));

        for (int i = 0; i < s.Files.Count; i++)
        {
            var f = s.Files[i];
            uint first = ChainEnd;
            if (f.Data.Length > 0)
            {
                int n = Bin.DivUp(f.Data.Length, _clusterSize);
                first = AllocChain(n);
                var chain = Chain(first);
                var cbuf = new byte[_clusterSize];
                for (int k = 0; k < n; k++)
                {
                    Array.Clear(cbuf);
                    int len = Math.Min(_clusterSize, f.Data.Length - k * _clusterSize);
                    Array.Copy(f.Data, k * _clusterSize, cbuf, 0, len);
                    WriteAlloc(chain[k], cbuf);
                }
            }
            new Ps2DirEntry
            {
                Mode = (ushort)((f.Mode & ~Ps2DirEntry.DfDir) | Ps2DirEntry.DfFile | Ps2DirEntry.DfExists),
                Length = (uint)f.Data.Length,
                Created = f.Created.OrNow(), Modified = f.Modified.OrNow(), Attr = f.Attr,
                Cluster = first, ParentEntry = 0, Name = f.Name
            }.Write(dirBuf.AsSpan((i + 2) * Ps2DirEntry.Size));
        }
        var dchain = Chain(dirFirst);
        for (int k = 0; k < dirClusters; k++) WriteAlloc(dchain[k], dirBuf.AsSpan(k * _clusterSize, _clusterSize));

        if (slot == root.Count)
        {
            if (extra == 1)
            {
                var rchain = Chain(_rootCluster);
                uint nc = Allocate();
                SetFat(rchain[^1], nc | AllocBit);
                WriteAlloc(nc, new byte[_clusterSize]);
            }
            var dot = root[0];
            dot.Length = (uint)(root.Count + 1);
            WriteDirEntry(_rootCluster, 0, dot);
        }
        WriteDirEntry(_rootCluster, slot, new Ps2DirEntry
        {
            Mode = (ushort)((s.Mode & ~Ps2DirEntry.DfFile) | Ps2DirEntry.DfDir | Ps2DirEntry.DfExists),
            Length = (uint)dirLen, Created = created, Modified = modified, Attr = s.Attr,
            Cluster = dirFirst, ParentEntry = 0, Name = s.DirName
        });
    }

    public byte[] BuildImage()
    {
        FlushFat();
        return (byte[])_img.Clone();
    }

    public override void Commit(string path)
    {
        var img = BuildImage();
        CardIO.SafeWrite(path, img, bytes => { _ = new Ps2Card(bytes, path, _kind); });
        FilePath = path;
        MarkSaved();
    }

    /// <summary>Integrity check: cross-linked / lost clusters and broken chains.</summary>
    public List<string> Check()
    {
        var msgs = new List<string>();
        var used = new bool[_allocEnd];
        void Mark(uint first, long len, string name)
        {
            if (len <= 0) return;
            var ch = Chain(first);
            if (ch.Count != Bin.DivUp(len, _clusterSize)) msgs.Add($"{name}: Clusterkette passt nicht zur Größe.");
            foreach (var c in ch)
            {
                if (used[c]) msgs.Add($"{name}: Cluster {c} mehrfach verwendet.");
                used[c] = true;
            }
        }
        var rootDot = RootDot();
        Mark(_rootCluster, rootDot.Length * 512L, "/");
        var root = ReadRoot();
        for (int i = 2; i < root.Count; i++)
        {
            var e = root[i];
            if (!e.Exists) continue;
            if (e.IsDir)
            {
                Mark(e.Cluster, e.Length * 512L, e.Name);
                var ents = ReadDir(e.Cluster, e.Length);
                for (int k = 2; k < ents.Count; k++)
                    if (ents[k].Exists && ents[k].IsFile) Mark(ents[k].Cluster, ents[k].Length, e.Name + "/" + ents[k].Name);
            }
            else Mark(e.Cluster, e.Length, e.Name);
        }
        int lost = 0;
        for (int i = 0; i < _allocLimit; i++) if ((_fat[i] & AllocBit) != 0 && !used[i]) lost++;
        if (lost > 0) msgs.Add($"{lost} verlorene Cluster (belegt, aber keiner Datei zugeordnet).");
        return msgs;
    }

    // ------------------------------------------------------------------ format

    /// <summary>Creates a freshly formatted PS2 card image (same layout as a PS2 BIOS / mymc format).</summary>
    public static byte[] Format(int sizeMb, bool withEcc)
    {
        const int pageSize = 512, ppeb = 16, cs = 1024, ppc = cs / pageSize, epc = cs / 4;
        int pagesPerCard = sizeMb * 1024 * 1024 / pageSize;
        int spare = withEcc ? 16 : 0;
        int rawPage = pageSize + spare;
        int clustersPerEb = ppeb / ppc;
        int ebPerCard = pagesPerCard / ppeb;
        int clustersInCard = pagesPerCard / ppc;
        uint gb1 = (uint)(ebPerCard - 1), gb2 = (uint)(ebPerCard - 2);
        int firstIfc = Bin.DivUp(0x2000, cs);
        int allocClusters = clustersInCard - (firstIfc + 2);
        int fatClusters = Bin.DivUp(allocClusters, epc);
        int ifcCount = Bin.DivUp(fatClusters, epc);
        if (ifcCount > 32) { ifcCount = 32; fatClusters = ifcCount * epc; }
        allocClusters = fatClusters * epc;
        int allocOffset = firstIfc + ifcCount + fatClusters;
        int allocEnd = (int)gb2 * clustersPerEb - allocOffset;
        if (allocEnd < 1) throw new ArgumentException("Karte zu klein.");

        var img = new byte[(long)pagesPerCard * rawPage];
        if (withEcc)
        {
            var zeroSpare = new byte[spare];
            Ecc.CalculatePage(new byte[pageSize], zeroSpare);
            for (long p = 0; p < pagesPerCard; p++) zeroSpare.CopyTo(img, p * rawPage + pageSize);
        }

        void WriteClusterRaw(int abs, byte[] data)
        {
            for (int p = 0; p < ppc; p++)
            {
                long off = ((long)abs * ppc + p) * rawPage;
                Array.Copy(data, p * pageSize, img, off, pageSize);
                if (withEcc)
                {
                    var sp = img.AsSpan((int)off + pageSize, spare);
                    sp.Clear();
                    Ecc.CalculatePage(data.AsSpan(p * pageSize, pageSize), sp);
                }
            }
        }

        // Indirect FAT clusters
        int firstFat = firstIfc + ifcCount;
        for (int i = 0; i < ifcCount; i++)
        {
            var buf = new byte[cs];
            for (int j = 0; j < epc; j++)
            {
                int idx = i * epc + j;
                Bin.W32(buf, j * 4, idx < fatClusters ? (uint)(firstFat + idx) : 0xFFFFFFFF);
            }
            WriteClusterRaw(firstIfc + i, buf);
        }
        // FAT
        for (int k = 0; k < fatClusters; k++)
        {
            var buf = new byte[cs];
            for (int j = 0; j < epc; j++)
            {
                int n = k * epc + j;
                uint v = n == 0 ? ChainEnd : n < allocEnd ? FreeEnd : ChainEnd;
                Bin.W32(buf, j * 4, v);
            }
            WriteClusterRaw(firstFat + k, buf);
        }
        // Root directory
        var now = Ps2Tod.Now;
        var rootBuf = new byte[cs];
        new Ps2DirEntry { Mode = 0x8427, Length = 2, Created = now, Modified = now, Cluster = 0, Name = "." }.Write(rootBuf.AsSpan(0));
        new Ps2DirEntry { Mode = 0xA426, Length = 0, Created = now, Modified = now, Cluster = 0, Name = ".." }.Write(rootBuf.AsSpan(512));
        WriteClusterRaw(allocOffset, rootBuf);
        // Superblock
        var sb = new byte[cs];
        Bin.WStr(sb.AsSpan(0, 28), Magic);
        Bin.WStr(sb.AsSpan(28, 12), "1.2.0.0");
        Bin.W16(sb, 40, pageSize);
        Bin.W16(sb, 42, ppc);
        Bin.W16(sb, 44, ppeb);
        Bin.W16(sb, 46, 0xFF00);
        Bin.W32(sb, 48, (uint)clustersInCard);
        Bin.W32(sb, 52, (uint)allocOffset);
        Bin.W32(sb, 56, (uint)allocEnd);
        Bin.W32(sb, 60, 0);
        Bin.W32(sb, 64, gb1);
        Bin.W32(sb, 68, gb2);
        for (int i = 0; i < 32; i++) Bin.W32(sb, 80 + i * 4, i < ifcCount ? (uint)(firstIfc + i) : 0);
        for (int i = 0; i < 32; i++) Bin.W32(sb, 208 + i * 4, 0xFFFFFFFF);
        sb[336] = 2;
        sb[337] = 0x2B;
        // only the first page holds the superblock; write it alone so page 1 stays zero
        Array.Copy(sb, 0, img, 0, pageSize);
        if (withEcc)
        {
            var sp = img.AsSpan(pageSize, spare);
            sp.Clear();
            Ecc.CalculatePage(sb.AsSpan(0, pageSize), sp);
        }
        // erase backup block 2 (as the PS2 does)
        long eb = (long)gb2 * ppeb * rawPage;
        img.AsSpan((int)eb, ppeb * rawPage).Fill(0xFF);
        return img;
    }
}
