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

    bool FrameChecksumOk(int frame)
    {
        int o = frame * FrameSize;
        byte x = 0;
        for (int i = 0; i < 127; i++) x ^= _d[o + i];
        return x == _d[o + 127];
    }

    /// <summary>Follows a block chain starting at a first block. states = allowed states of the follow-up blocks.</summary>
    (List<int> blocks, bool broken) FollowChain(int first, int midState, int lastState)
    {
        var blocks = new List<int> { first };
        int cur = first;
        bool broken = false;
        for (int guard = 0; guard < 15; guard++)
        {
            int n = Next(cur);
            if (n == 0xFFFF) break;
            if (n > 14 || blocks.Contains(n) || (State(n) != midState && State(n) != lastState)) { broken = true; break; }
            blocks.Add(n);
            cur = n;
        }
        // the last block of a multi-block save must carry the "last" state
        if (!broken && blocks.Count > 1 && State(blocks[^1]) != lastState) broken = true;
        return (blocks, broken);
    }

    SaveGame BuildSave(int first, List<int> blocks)
    {
        int fo = (first + 1) * FrameSize;
        string name = Bin.ZStr(_d.AsSpan(fo + 0x0A, 21));
        var data = new byte[blocks.Count * BlockSize];
        for (int i = 0; i < blocks.Count; i++) Array.Copy(_d, (blocks[i] + 1) * BlockSize, data, i * BlockSize, BlockSize);
        return new SaveGame { Ps1 = new Ps1Save { FileName = name, Data = data } };
    }

    void Reload()
    {
        var list = new List<CardEntry>();
        for (int s = 0; s < 15; s++)
        {
            if (State(s) != 0x51) continue;
            var (blocks, broken) = FollowChain(s, 0x52, 0x53);
            var g = BuildSave(s, blocks);
            uint sizeField = Bin.U32(_d, (s + 1) * FrameSize + 4);
            int expected = sizeField > 0 && sizeField % BlockSize == 0 ? (int)(sizeField / BlockSize) : blocks.Count;
            var problems = new List<string>();
            if (broken) problems.Add("Blockkette beschädigt");
            if (expected != blocks.Count) problems.Add($"{blocks.Count} von {expected} Blöcken vorhanden");
            if (g.Ps1!.Data[0] != 'S' || g.Ps1.Data[1] != 'C') problems.Add("Kopfdaten (SC) fehlen");
            if (problems.Count > 0) { g.Damaged = true; g.Problem = string.Join(", ", problems); }
            else if (blocks.Any(b => !FrameChecksumOk(b + 1))) g.Problem = "Prüfsumme im Verzeichnis falsch (reparierbar)";
            list.Add(new CardEntry { Save = g, Size = g.Ps1.Data.Length, Blocks = blocks.Count, Tag = blocks.ToArray() });
        }
        _entries = list;

        // deleted, but still completely recoverable saves
        var deleted = new List<CardEntry>();
        for (int s = 0; s < 15; s++)
        {
            if (State(s) != 0xA1) continue;
            var (blocks, broken) = FollowChain(s, 0xA2, 0xA3);
            uint sizeField = Bin.U32(_d, (s + 1) * FrameSize + 4);
            if (broken || sizeField != blocks.Count * (uint)BlockSize) continue;
            var g = BuildSave(s, blocks);
            if (g.Ps1!.FileName.Length == 0 || g.Ps1.Data[0] != 'S' || g.Ps1.Data[1] != 'C') continue;
            deleted.Add(new CardEntry { Save = g, Size = g.Ps1.Data.Length, Blocks = blocks.Count, Tag = blocks.ToArray() });
        }
        _deleted = deleted;
    }

    List<CardEntry> _deleted = new();
    public override IReadOnlyList<CardEntry> DeletedSaves => _deleted;

    public override void Undelete(CardEntry entry)
    {
        if (entry.Tag is not int[] blocks) throw new InvalidOperationException();
        if (Exists(entry.Name)) throw new InvalidOperationException($"Ein Savegame „{entry.Name}“ ist bereits vorhanden.");
        foreach (int s in blocks)
        {
            int st = State(s);
            if (st is not (0xA1 or 0xA2 or 0xA3)) throw new InvalidOperationException("Die Blöcke wurden inzwischen neu belegt.");
        }
        foreach (int s in blocks)
        {
            int o = (s + 1) * FrameSize;
            _d[o] = _d[o] switch { 0xA1 => 0x51, 0xA2 => 0x52, 0xA3 => 0x53, var x => x };
            Checksum(_d, o);
        }
        MarkModified();
        Reload();
    }

    /// <summary>Blocks used by active saves (to find orphans).</summary>
    HashSet<int> ActiveBlocks() => _entries.SelectMany(e => (int[])e.Tag!).ToHashSet();

    public override List<CardIssue> Diagnose()
    {
        var list = new List<CardIssue>();
        if (Container == Ps1Container.Vmp && Warnings.Any(w => w.Contains("Signatur")))
            list.Add(new CardIssue(IssueLevel.Warning, "VMP-Signatur ungültig – PSP/Vita lehnen die Karte so ab. Beim Speichern wird sie neu berechnet.", true));
        if (!Bin.StartsWith(_d, "MC")) list.Add(new CardIssue(IssueLevel.Warning, "Kartenkopf „MC“ fehlt.", true));
        else if (!FrameChecksumOk(0)) list.Add(new CardIssue(IssueLevel.Warning, "Prüfsumme des Kartenkopfs falsch.", true));
        int badSum = Enumerable.Range(1, 15).Count(f => !FrameChecksumOk(f));
        if (badSum > 0) list.Add(new CardIssue(IssueLevel.Warning, $"{badSum} Verzeichniseinträge mit falscher Prüfsumme (manche Konsolen/Emulatoren zeigen diese Saves nicht an).", true));
        var active = ActiveBlocks();
        int orphans = Enumerable.Range(0, 15).Count(s => State(s) is 0x52 or 0x53 && !active.Contains(s));
        if (orphans > 0) list.Add(new CardIssue(IssueLevel.Warning, $"{orphans} verwaiste Blöcke (gehören zu keinem Save) – können freigegeben werden.", true));
        int invalid = Enumerable.Range(0, 15).Count(s => State(s) is not (0x51 or 0x52 or 0x53 or 0xA0 or 0xA1 or 0xA2 or 0xA3));
        if (invalid > 0) list.Add(new CardIssue(IssueLevel.Warning, $"{invalid} Blöcke mit ungültigem Status – können als frei markiert werden.", true));
        if (_deleted.Count > 0)
            list.Add(new CardIssue(IssueLevel.Info, $"{_deleted.Count} gelöschte Savegame(s) noch vollständig vorhanden – Wiederherstellung möglich: " +
                                                   string.Join(", ", _deleted.Select(d => d.Name))));
        AddSaveIssues(list);
        return list;
    }

    public override List<string> Repair()
    {
        var report = new List<string>();
        if (!Bin.StartsWith(_d, "MC")) { _d[0] = (byte)'M'; _d[1] = (byte)'C'; report.Add("Kartenkopf „MC“ ergänzt."); }
        var active = ActiveBlocks();
        int orphans = 0, invalid = 0;
        for (int s = 0; s < 15; s++)
        {
            int o = (s + 1) * FrameSize;
            int st = _d[o];
            if (st is 0x52 or 0x53 && !active.Contains(s))
            {
                _d[o] = (byte)(st == 0x52 ? 0xA2 : 0xA3); // "deleted": free, data kept
                orphans++;
            }
            else if (st is not (0x51 or 0x52 or 0x53 or 0xA0 or 0xA1 or 0xA2 or 0xA3))
            {
                Array.Clear(_d, o, FrameSize);
                _d[o] = 0xA0;
                _d[o + 8] = 0xFF; _d[o + 9] = 0xFF;
                invalid++;
            }
        }
        if (orphans > 0) report.Add($"{orphans} verwaiste Blöcke freigegeben (Daten bleiben bis zum Überschreiben erhalten).");
        if (invalid > 0) report.Add($"{invalid} Blöcke mit ungültigem Status als frei markiert.");
        int fixedSums = 0;
        for (int f = 0; f < 16; f++)
        {
            if (FrameChecksumOk(f)) continue;
            Checksum(_d, f * FrameSize);
            fixedSums++;
        }
        if (fixedSums > 0) report.Add($"{fixedSums} Prüfsummen korrigiert.");
        if (Container == Ps1Container.Vmp && Warnings.RemoveAll(w => w.Contains("Signatur")) > 0)
            report.Add("VMP-Signatur wird beim Speichern neu berechnet.");
        if (report.Count > 0) { MarkModified(); Reload(); }
        return report;
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
